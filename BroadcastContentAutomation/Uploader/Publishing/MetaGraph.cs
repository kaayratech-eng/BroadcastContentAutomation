using System.Net.Http.Headers;
using System.Text.Json;
using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

// Shared Meta Graph plumbing for Instagram Reels and Facebook Reels.
//
// Both use the same two-part shape: create a container/session on graph.facebook.com,
// then POST the raw bytes to rupload.facebook.com. That binary path is why neither
// platform needs a publicly reachable file URL — the older `video_url` parameter,
// which does, is not used here.
static class MetaGraph
{
    public const long MaxReelBytes = 1024L * 1024 * 1024;   // 1 GB, both surfaces
    private const long LargeUploadWarningBytes = 30L * 1024 * 1024;

    public sealed record GraphError(int Code, int SubCode, string Message, bool Permanent);

    public static HttpClient CreateClient(Logger log, string tag) =>
        new(new RetryHandler(log: m => log.Info($"  [{tag}] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(30),
        };

    public static async Task<JsonDocument> PostFormAsync(
        HttpClient http, string url, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        var resp = await http.PostAsync(url, content, ct);
        return await ReadAsync(resp, ct);
    }

    public static async Task<JsonDocument> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        var resp = await http.GetAsync(url, ct);
        return await ReadAsync(resp, ct);
    }

    public static async Task<JsonDocument> PostMultipartAsync(
        HttpClient http, string url, MultipartFormDataContent content, CancellationToken ct)
    {
        var resp = await http.PostAsync(url, content, ct);
        return await ReadAsync(resp, ct);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            throw new MetaGraphException(new GraphError((int)resp.StatusCode, 0,
                $"Non-JSON response ({(int)resp.StatusCode}): {Text.Tail(body, 300)}", Permanent: false));
        }

        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var parsed = ParseError(error);
            doc.Dispose();
            throw new MetaGraphException(parsed);
        }

        if (!resp.IsSuccessStatusCode)
        {
            // Meta answers a transcode that fell over with a bare 400 carrying
            // debug_info.type = ProcessingFailedError and no `error` object, and it
            // self-reports as retriable:false. Empirically that is wrong: the same
            // encoder settings that fail one minute upload cleanly the next. Treating
            // it as permanent abandoned a perfectly valid render after one attempt,
            // so it is retried like any other transient fault.
            var processingFailed = body.Contains("ProcessingFailedError", StringComparison.Ordinal);

            doc.Dispose();
            throw new MetaGraphException(new GraphError((int)resp.StatusCode, 0,
                $"HTTP {(int)resp.StatusCode}: {Text.Tail(body, 300)}",
                Permanent: !processingFailed && (int)resp.StatusCode is >= 400 and < 500 and not 429));
        }

        return doc;
    }

    public static string RequireString(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) && value.GetString() is { Length: > 0 } text)
            return text;

        throw new MetaGraphException(new GraphError(0, 0,
            $"Response did not include '{property}'.", Permanent: false));
    }

    private static GraphError ParseError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
        var sub = error.TryGetProperty("error_subcode", out var s) ? s.GetInt32() : 0;
        var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        if (error.TryGetProperty("error_user_msg", out var um) && um.GetString() is { Length: > 0 } userMessage)
            message = $"{message} — {userMessage}";

        // 4 / 17 / 32 / 613 are rate limits; 1 and 2 are transient platform faults.
        // 190 (bad token) and 100 (bad parameter) will never succeed on retry.
        var transient = code is 1 or 2 or 4 or 17 or 32 or 341 or 613;
        return new GraphError(code, sub, message, Permanent: !transient);
    }

    // Meta's resumable protocol, as the server actually enforces it (verified
    // live 2026-11-13): a POST must span from its `offset` to the END of the
    // file. A shorter "chunk" is answered 206 PartialRequestError and its
    // bytes are DISCARDED — the server's accepted offset stays 0, so a later
    // chunk at a higher offset is rejected 412 OffsetInvalidError ("Maximum
    // accepted offset: 0"). Sending the file in fixed 4 MB chunks therefore
    // can never complete. The working shape is: POST the REMAINDER
    // [offset, EOF) in one stream; if that stream stalls or breaks, ask the
    // server where it got to (a POST at an overshoot offset is rejected 412
    // and the message names the maximum accepted offset), then re-POST the
    // shorter remainder from there. Each attempt is a fresh connection, so a
    // stalled stream costs one retry, not the whole file.
    public static async Task UploadBinaryAsync(
        HttpClient http, string uploadUrl, string accessToken, string filePath, CancellationToken ct,
        Action<string>? log = null)
    {
        var info = new FileInfo(filePath);
        if (info.Length > MaxReelBytes)
            throw new MetaGraphException(new GraphError(0, 0,
                $"File is {Text.Bytes(info.Length)}; Meta's limit is {Text.Bytes(MaxReelBytes)}.", Permanent: true));

        var total = info.Length;
        long offset = 0;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var remaining = total - offset;
            try
            {
                await using var stream = File.OpenRead(filePath);
                stream.Seek(offset, SeekOrigin.Begin);
                using var content = new StreamContent(stream);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentLength = remaining;

                using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
                request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");
                request.Headers.TryAddWithoutValidation("offset", offset.ToString());
                request.Headers.TryAddWithoutValidation("file_size", total.ToString());

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromMinutes(10));

                var resp = await http.SendAsync(request, attemptCts.Token);
                using var doc = await ReadAsync(resp, attemptCts.Token);

                if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                    throw new MetaGraphException(new GraphError(0, 0, "Upload reported success=false.", Permanent: false));

                log?.Invoke($"  uploaded {Text.Bytes(total)} (offset {Text.Bytes(offset)} -> done)");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or MetaGraphException)
            {
                // The stream broke before the server had the whole remainder.
                // Ask the server how much it kept, then resume from there.
                var probed = await ProbeAcceptedOffsetAsync(http, uploadUrl, accessToken, total, ct);
                if (probed.HasValue)
                {
                    if (probed.Value >= total)
                    {
                        log?.Invoke("  server already holds the whole file; treating the upload as done.");
                        return;
                    }
                    if (probed.Value > offset)
                    {
                        log?.Invoke($"  attempt {attempt} stalled ({ex.Message}); server kept {Text.Bytes(probed.Value)}, resuming from there.");
                        offset = probed.Value;
                        continue;
                    }
                }
                log?.Invoke($"  attempt {attempt} failed ({ex.Message}); retrying the remainder from {Text.Bytes(offset)}.");
                if (attempt < 5)
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }

        throw new MetaGraphException(new GraphError(0, 0,
            $"Upload did not complete after 5 attempts (server holds {Text.Bytes(offset)} of {Text.Bytes(total)}).", Permanent: false));
    }

    // Ask the rupload endpoint how much of the file it already holds: a POST
    // whose offset overshoots what the server has is rejected 412, and the
    // error message names the maximum accepted offset. A 1-byte body keeps
    // the probe cheap; the server validates the offset before reading it.
    private static async Task<long?> ProbeAcceptedOffsetAsync(
        HttpClient http, string uploadUrl, string accessToken, long total, CancellationToken ct)
    {
        try
        {
            using var content = new ByteArrayContent(new byte[] { 0 });
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = 1;

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
            request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");
            request.Headers.TryAddWithoutValidation("offset", total.ToString());
            request.Headers.TryAddWithoutValidation("file_size", total.ToString());

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromMinutes(2));

            var resp = await http.SendAsync(request, probeCts.Token);
            var body = await resp.Content.ReadAsStringAsync(probeCts.Token);
            var m = System.Text.RegularExpressions.Regex.Match(body, @"Maximum accepted offset: (\d+)");
            if (m.Success && long.TryParse(m.Groups[1].Value, out var accepted))
                return accepted;
            if (resp.IsSuccessStatusCode)
                return total;
            return null;
        }
        catch
        {
            return null;
        }
    }

    // Both surfaces process asynchronously; publishing before the transcode finishes
    // fails with a misleading "media not ready" error.
    public static async Task<string> WaitForStatusAsync(
        HttpClient http, string statusUrl, Func<JsonElement, (bool Done, bool Failed, string Detail)> read,
        int timeoutSeconds, Logger log, string tag, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        var delay = TimeSpan.FromSeconds(5);
        var lastDetail = "unknown";

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(20, delay.TotalSeconds * 1.5));

            using var doc = await GetAsync(http, statusUrl, ct);
            var (done, failed, detail) = read(doc.RootElement);
            lastDetail = detail;

            if (done) return detail;
            if (failed) throw new MetaGraphException(new GraphError(0, 0, $"Processing failed: {detail}", Permanent: true));

            log.Info($"  [{tag}] processing… {detail}");
        }

        throw new MetaGraphException(new GraphError(0, 0,
            $"Still processing after {timeoutSeconds}s (last status: {lastDetail}).", Permanent: false));
    }
}

sealed class MetaGraphException(MetaGraph.GraphError error) : Exception(error.Message)
{
    public MetaGraph.GraphError Error { get; } = error;
}
