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
            doc.Dispose();
            throw new MetaGraphException(new GraphError((int)resp.StatusCode, 0,
                $"HTTP {(int)resp.StatusCode}: {Text.Tail(body, 300)}", Permanent: (int)resp.StatusCode is >= 400 and < 500 and not 429));
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

    // Meta's resumable protocol: a single POST of the whole file with an offset and
    // file_size header. Not retried by RetryHandler (the body is far over its cap),
    // so failures here surface as retryable and the whole publish is re-attempted
    // on the next run.
    public static async Task UploadBinaryAsync(
        HttpClient http, string uploadUrl, string accessToken, string filePath, CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        if (info.Length > MaxReelBytes)
            throw new MetaGraphException(new GraphError(0, 0,
                $"File is {Text.Bytes(info.Length)}; Meta's limit is {Text.Bytes(MaxReelBytes)}.", Permanent: true));

        await using var stream = File.OpenRead(filePath);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = info.Length;

        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");
        request.Headers.TryAddWithoutValidation("offset", "0");
        request.Headers.TryAddWithoutValidation("file_size", info.Length.ToString());

        var resp = await http.SendAsync(request, ct);
        using var doc = await ReadAsync(resp, ct);

        if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new MetaGraphException(new GraphError(0, 0, "Upload reported success=false.", Permanent: false));
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
