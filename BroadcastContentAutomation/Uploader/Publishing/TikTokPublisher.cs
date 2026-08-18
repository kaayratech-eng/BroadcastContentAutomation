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

    public bool Accepts(Sidecar sidecar, out string? reason)
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
                    source_info = new { source = "FILE_UPLOAD", video_size = info.Length, chunk_size = info.Length, total_chunk_count = 1 },
                }
                : new
                {
                    source_info = new { source = "FILE_UPLOAD", video_size = info.Length, chunk_size = info.Length, total_chunk_count = 1 },
                };

            using var initResp = await http.PostAsync(initEndpoint,
                new StringContent(JsonSerializer.Serialize(initPayload), Encoding.UTF8, "application/json"), ct);
            using var initJson = await ReadTikTokResponseAsync(initResp, ct);

            var data = initJson.RootElement.GetProperty("data");
            var publishId = data.GetProperty("publish_id").GetString()!;
            var uploadUrl = data.GetProperty("upload_url").GetString()!;

            log.Info($"  [tiktok] publish {publishId}, uploading {Text.Bytes(info.Length)}");
            log.Info($"  [tiktok] caption saved to {Path.GetFileName(captionFile)} - copy-paste it when you open the draft in the app");

            await using (var stream = File.OpenRead(request.VideoPath))
            using (var content = new StreamContent(stream))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                content.Headers.ContentRange = new ContentRangeHeaderValue(0, info.Length - 1, info.Length);

                using var putResp = await http.PutAsync(uploadUrl, content, ct);
                if (!putResp.IsSuccessStatusCode)
                    return PublishResult.Retry(
                        $"TikTok upload failed ({(int)putResp.StatusCode}): {Text.Tail(await putResp.Content.ReadAsStringAsync(ct), 400)}");
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
                var reason = data.TryGetProperty("fail_reason", out var fr) ? fr.GetString() : "unknown";
                return PublishResult.Fatal($"TikTok processing failed: {reason}");
            }

            if (DateTimeOffset.UtcNow > deadline)
                return PublishResult.Retry($"TikTok still processing after {cfg.ProcessingTimeoutSeconds}s (last status: {status}).");

            log.Info($"  [tiktok] processing… {status}");
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

        using var resp = await http.PostAsync($"{ApiBase}/oauth/token/", body, ct);
        using var json = await ReadTikTokResponseAsync(resp, ct);
        var root = json.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()!;
        var newRefreshToken = root.GetProperty("refresh_token").GetString()!;

        var temp = cfg.TokenStorePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new TikTokTokenFile(newRefreshToken)), ct);
        File.Move(temp, cfg.TokenStorePath, overwrite: true);

        return accessToken;
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
