using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

// TikTok Content Posting API v2.
//
// Uses /post/publish/inbox/video/init (video.upload scope — works for any
// registered app) unless cfg.DirectPost is true, in which case it uses
// /post/publish/video/init (video.publish scope, requires an audited app —
// see the comment on TikTokConfig.DirectPost). Either way: init returns an
// upload_url, PUT the file to it, then poll publish status.
//
// TikTok invalidates the previous refresh token on every refresh and issues
// a new one, so cfg.TokenStorePath is rewritten on every publish call. The
// store must be seeded once with an initial refresh token obtained through
// TikTok's OAuth consent flow before this publisher can run — see the README.
sealed class TikTokPublisher(TikTokConfig cfg, Logger log) : IPublisher
{
    private const string ApiBase = "https://open.tiktokapis.com/v2";

    public string Platform => Platforms.TikTok;
    public bool Enabled => cfg.Enabled;
    public int MaxPerRun => cfg.MaxUploadsPerRun;

    public bool Accepts(Sidecar sidecar, string videoPath, out string? reason)
    {
        if (!sidecar.IsVertical)
        {
            reason = "TikTok only accepts the 9:16 render.";
            return false;
        }

        reason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ClientKey) || string.IsNullOrWhiteSpace(cfg.ClientSecret))
            return PublishResult.Fatal("TikTok ClientKey or ClientSecret is not configured.");

        if (!File.Exists(cfg.TokenStorePath))
            return PublishResult.Fatal(
                $"No TikTok refresh token at {cfg.TokenStorePath}. Complete the one-time TikTok OAuth " +
                "consent flow (see README) before this publisher can run.");

        using var http = new HttpClient(new RetryHandler(log: m => log.Info($"  [tiktok] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(30),
        };

        try
        {
            var accessToken = await RefreshAccessTokenAsync(http, ct);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Idempotency: a previous run may have called init and died before
            // the sidecar was saved. If that publish is live in the inbox (or
            // still processing), reuse/wait for it — do not create a second draft.
            if (!string.IsNullOrWhiteSpace(request.ExistingId))
            {
                var prior = await CheckPriorPublishAsync(http, request.ExistingId, ct);
                if (prior is not null) return prior;
                // null = the prior publish is gone/failed — fall through to a fresh init.
            }

            var caption = request.Sidecar.CaptionFor(Platforms.TikTok, 2200);
            var info = new FileInfo(request.VideoPath);

            // TikTok's inbox/draft endpoint (source_info only - the flow Sandbox/unaudited
            // apps are restricted to) has no title/caption field at all; that only exists
            // on the Direct Post endpoint, which needs an audited production app. There is
            // no way to carry the caption through the API here, so it's written to a
            // companion file for a quick copy-paste when you open the draft in the app.
            var captionFile = Path.ChangeExtension(request.VideoPath, ".tiktok-caption.txt");
            await File.WriteAllTextAsync(captionFile, caption, ct);

            var initEndpoint = cfg.DirectPost
                ? $"{ApiBase}/post/publish/video/init/"
                : $"{ApiBase}/post/publish/inbox/video/init/";

            // TikTok accepts the file in chunks: init declares chunk_size and
            // total_chunk_count, then each chunk goes up as its own PUT with a
            // Content-Range header. Verified live 2026-11-13: chunk_size must
            // be 10 MB (1, 2 and 4 MB are rejected "chunk size is invalid")
            // and total_chunk_count must be floor(video_size / chunk_size) —
            // ceil is rejected "total chunk count is invalid". The last chunk
            // absorbs the remainder, so it can be larger than chunk_size.
            // A single 64 MB PUT dies on this VM (the egress proxy throttles
            // sustained uploads and breaks long streams), so chunking is what
            // makes a large upload possible at all. Files at or under the
            // chunk size go up as one chunk, exactly as the demo did.
            const long chunkSize = 10L * 1024 * 1024;
            var totalChunks = info.Length <= chunkSize
                ? 1
                : (int)(info.Length / chunkSize);
            var initChunkSize = totalChunks == 1 ? info.Length : chunkSize;

            object initPayload = cfg.DirectPost
                ? new
                {
                    post_info = new
                    {
                        title = caption,
                        privacy_level = cfg.PrivacyLevel,
                        disable_duet = cfg.DisableDuet,
                        disable_comment = cfg.DisableComment,
                        disable_stitch = cfg.DisableStitch,
                    },
                    source_info = new { source = "FILE_UPLOAD", video_size = info.Length, chunk_size = initChunkSize, total_chunk_count = totalChunks },
                }
                : new
                {
                    source_info = new { source = "FILE_UPLOAD", video_size = info.Length, chunk_size = initChunkSize, total_chunk_count = totalChunks },
                };

            using var initResp = await http.PostAsync(initEndpoint,
                new StringContent(JsonSerializer.Serialize(initPayload), Encoding.UTF8, "application/json"), ct);
            using var initJson = await ReadTikTokResponseAsync(initResp, ct);

            var data = initJson.RootElement.GetProperty("data");
            var publishId = data.GetProperty("publish_id").GetString()!;
            var uploadUrl = data.GetProperty("upload_url").GetString()!;

            // The publish_id exists from this point on. Record it immediately
            // (sidecar.SaveAsync) so a VM restart mid-upload can resume/check
            // this publish instead of starting a second one.
            if (request.OnPlatformIdAsync is not null)
                await request.OnPlatformIdAsync(publishId);

            log.Info($"  [tiktok] publish {publishId}, uploading {Text.Bytes(info.Length)} in {totalChunks} chunks");
            log.Info($"  [tiktok] caption (also saved to {Path.GetFileName(captionFile)}) - copy-paste into the draft:\n{caption}");

            // The last chunk absorbs the remainder, so it can be larger than
            // chunk_size; size the buffer for it.
            var maxChunkBytes = totalChunks == 1
                ? info.Length
                : Math.Max(chunkSize, info.Length - (totalChunks - 1) * chunkSize);
            var buffer = new byte[maxChunkBytes];
            await using var stream = File.OpenRead(request.VideoPath);
            long offset = 0;
            for (var chunkIndex = 1; chunkIndex <= totalChunks; chunkIndex++)
            {
                var toRead = chunkIndex == totalChunks
                    ? (int)(info.Length - offset)
                    : (int)chunkSize;
                var read = 0;
                while (read < toRead)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, toRead - read), ct);
                    if (n == 0) throw new IOException("Unexpected end of file while reading upload chunk.");
                    read += n;
                }

                Exception? lastError = null;
                var sent = false;
                for (var attempt = 1; attempt <= 3 && !sent; attempt++)
                {
                    try
                    {
                        using var chunkContent = new ByteArrayContent(buffer, 0, toRead);
                        chunkContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                        chunkContent.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + toRead - 1, info.Length);

                        using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        chunkCts.CancelAfter(TimeSpan.FromMinutes(5));

                        using var putResp = await http.PutAsync(uploadUrl, chunkContent, chunkCts.Token);
                        if (!putResp.IsSuccessStatusCode)
                            throw new TikTokApiException(
                                $"TikTok chunk upload failed ({(int)putResp.StatusCode}): {Text.Tail(await putResp.Content.ReadAsStringAsync(chunkCts.Token), 400)}",
                                Permanent: false);
                        sent = true;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or TikTokApiException)
                    {
                        lastError = ex;
                        if (attempt < 3)
                        {
                            log.Info($"  [tiktok] chunk {chunkIndex}/{totalChunks} attempt {attempt}/3 failed ({ex.Message}); retrying.");
                            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                        }
                    }
                }

                if (!sent)
                    return PublishResult.Retry($"TikTok chunk {chunkIndex}/{totalChunks} failed after 3 attempts: {lastError?.Message}");

                offset += toRead;
                log.Info($"  [tiktok] uploaded chunk {chunkIndex}/{totalChunks} ({Text.Bytes(offset)}/{Text.Bytes(info.Length)})");
            }

            return await WaitForPublishAsync(http, publishId, ct);
        }
        catch (TikTokApiException ex)
        {
            return ex.Permanent ? PublishResult.Fatal(ex.Message) : PublishResult.Retry(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return PublishResult.Retry($"TikTok transport error: {ex.Message}");
        }
        // A 200 response whose body doesn't have the shape this client expects (a
        // TikTok API change, a proxy/CDN error page returned with a success status,
        // etc.) previously escaped uncaught here and crashed the whole video's
        // processing instead of just this one target - see the crash-retry path in
        // Uploader/Program.cs, which exists to bound retries for exactly this kind
        // of unexpected failure.
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return PublishResult.Retry($"TikTok returned an unexpected response shape: {ex.Message}");
        }
    }

    private async Task<PublishResult> WaitForPublishAsync(HttpClient http, string publishId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(cfg.ProcessingTimeoutSeconds);
        var delay = TimeSpan.FromSeconds(5);

        while (true)
        {
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(20, delay.TotalSeconds * 1.5));

            using var statusResp = await http.PostAsync($"{ApiBase}/post/publish/status/fetch/",
                new StringContent(JsonSerializer.Serialize(new { publish_id = publishId }), Encoding.UTF8, "application/json"), ct);
            using var statusJson = await ReadTikTokResponseAsync(statusResp, ct);

            var data = statusJson.RootElement.GetProperty("data");
            var status = data.GetProperty("status").GetString() ?? "";

            // SEND_TO_USER_INBOX is the terminal success state for the draft
            // (video.upload-only) flow; PUBLISH_COMPLETE is terminal success
            // for an audited Direct Post app.
            if (status is "PUBLISH_COMPLETE" or "SEND_TO_USER_INBOX")
                return PublishResult.Ok(publishId);

            if (status == "FAILED")
            {
                // B20: a single FAILED poll used to return Fatal, permanently
                // killing the target — inconsistent with
                // CheckPriorPublishAsync, which treats a FAILED prior publish
                // as "start fresh". A transient TikTok processing failure
                // must not be terminal: Retry, so the next run re-checks
                // (and re-inits if the publish is still FAILED).
                var reason = data.TryGetProperty("fail_reason", out var fr) ? fr.GetString() : "unknown";
                log.Info($"  [tiktok] processing failed ({reason}); retrying next run instead of abandoning.");
                return PublishResult.Retry($"TikTok processing failed: {reason}");
            }

            if (DateTimeOffset.UtcNow > deadline)
                return PublishResult.Retry($"TikTok still processing after {cfg.ProcessingTimeoutSeconds}s (last status: {status}).");

            log.Info($"  [tiktok] processing… {status}");
        }
    }

    // Ask TikTok what happened to a publish_id recorded by a previous run that
    // died before the sidecar was saved. Live -> reuse it (no second draft);
    // still processing -> wait; failed -> null (fresh init). A lookup failure
    // is tri-state like YouTube's: only a 4xx rejection (Permanent) means the
    // publish_id is invalid/gone and a fresh init is safe; a 5xx, rate limit,
    // or transport failure means the check itself failed, so retry instead of
    // risking a second draft in the inbox.
    private async Task<PublishResult?> CheckPriorPublishAsync(HttpClient http, string publishId, CancellationToken ct)
    {
        try
        {
            using var statusResp = await http.PostAsync($"{ApiBase}/post/publish/status/fetch/",
                new StringContent(JsonSerializer.Serialize(new { publish_id = publishId }), Encoding.UTF8, "application/json"), ct);
            using var statusJson = await ReadTikTokResponseAsync(statusResp, ct);

            var status = statusJson.RootElement.GetProperty("data").GetProperty("status").GetString() ?? "";
            if (status is "PUBLISH_COMPLETE" or "SEND_TO_USER_INBOX")
                return PublishResult.Ok(publishId);
            if (status == "FAILED")
            {
                log.Info($"  [tiktok] previously recorded publish {publishId} failed; starting a fresh upload.");
                return null;
            }
            return PublishResult.Retry($"TikTok publish {publishId} from a previous run is still {status}; waiting before retrying.");
        }
        catch (Exception ex) when (ex is TikTokApiException or HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            if (ex is TikTokApiException apiEx && apiEx.Permanent)
            {
                log.Info($"  [tiktok] previously recorded publish {publishId} is invalid/gone ({ex.Message}); starting a fresh upload.");
                return null;
            }
            // R7: an InvalidOperationException here is a malformed response
            // shape (e.g. GetProperty on a non-object "data") — the check
            // itself failed, not the publish. Retry instead of letting it
            // escape to the uploader's per-video catch, which would burn a
            // crash retry and eventually move the video to failed/.
            log.Info($"  [tiktok] could not verify prior publish {publishId} ({ex.Message}); waiting before retrying.");
            return PublishResult.Retry($"Could not verify previously recorded TikTok publish {publishId}; not creating a second draft blind.");
        }
    }

    // TikTok invalidates the previous refresh token on every use, so the new
    // one must be persisted immediately — losing it means redoing consent.
    private async Task<string> RefreshAccessTokenAsync(HttpClient http, CancellationToken ct)
    {
        var stored = JsonSerializer.Deserialize<TikTokTokenFile>(
            await File.ReadAllTextAsync(cfg.TokenStorePath, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"{cfg.TokenStorePath} is not valid TikTok token JSON.");

        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_key"] = cfg.ClientKey,
            ["client_secret"] = cfg.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = stored.RefreshToken,
        });

        JsonDocument json;
        try
        {
            using var resp = await http.PostAsync($"{ApiBase}/oauth/token/", body, ct);
            json = await ReadTikTokResponseAsync(resp, ct);
        }
        catch (TikTokApiException ex)
        {
            // A rejected refresh token is not a transient blip — the stored token
            // is dead and every later run will fail auth the same way until a human
            // re-authorizes. Say exactly that, instead of a generic API error.
            throw new TikTokApiException(
                "TikTok refresh token rejected (invalid/expired) — token invalid, re-authorize: " +
                "run the one-time TikTok OAuth consent flow to obtain a new refresh token (see README). " +
                $"Original error: {ex.Message}",
                Permanent: true);
        }

        using (json)
        {
            var root = json.RootElement;
            var accessToken = root.GetProperty("access_token").GetString()!;
            var newRefreshToken = root.GetProperty("refresh_token").GetString()!;

            // Keep the previous refresh token as a backup until the new one is
            // confirmed written: if the write below is interrupted, the .bak still
            // holds the last known-good token file (the old token is already
            // invalidated server-side by this refresh, so this is belt-and-braces
            // for a torn write, not a substitute for re-authorizing).
            var backup = cfg.TokenStorePath + ".bak";
            try { File.Copy(cfg.TokenStorePath, backup, overwrite: true); }
            catch (Exception ex) { log.Warn($"  [tiktok] could not back up the previous refresh token ({ex.Message})."); }

            var temp = cfg.TokenStorePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new TikTokTokenFile(newRefreshToken)), ct);
            File.Move(temp, cfg.TokenStorePath, overwrite: true);

            return accessToken;
        }
    }

    private static async Task<JsonDocument> ReadTikTokResponseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);

        if (doc.RootElement.TryGetProperty("error", out var error) &&
            error.TryGetProperty("code", out var code) &&
            code.GetString() is { Length: > 0 } codeStr && codeStr != "ok")
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : codeStr;
            doc.Dispose();
            throw new TikTokApiException($"TikTok error ({codeStr}): {message}",
                Permanent: resp.StatusCode is not System.Net.HttpStatusCode.TooManyRequests
                           && (int)resp.StatusCode is >= 400 and < 500);
        }

        if (!resp.IsSuccessStatusCode)
        {
            doc.Dispose();
            throw new TikTokApiException($"TikTok HTTP {(int)resp.StatusCode}: {Text.Tail(body, 300)}",
                Permanent: resp.StatusCode is not System.Net.HttpStatusCode.TooManyRequests
                           && (int)resp.StatusCode is >= 400 and < 500);
        }

        return doc;
    }

    private sealed record TikTokTokenFile(string RefreshToken);
}

sealed class TikTokApiException(string message, bool Permanent) : Exception(message)
{
    public bool Permanent { get; } = Permanent;
}
