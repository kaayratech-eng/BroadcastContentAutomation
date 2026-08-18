using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

// Vidu image-to-video (https://platform.vidu.com/docs/image-to-video).
//
// The submitted image becomes the clip's FIRST FRAME, which is what makes
// last-frame chaining work: scene N's final frame is scene N+1's input, so the
// character stays on-model across a whole video without paying for the much
// pricier reference-to-video mode.
//
// Generation is asynchronous. With OffPeak enabled (half price) Vidu only
// guarantees delivery within 48 hours, so submission and collection are separate
// pipeline phases - see Program.cs --prep / --assemble. Tasks that miss the
// window are auto-cancelled by Vidu and the credits refunded, so a lost task
// costs latency rather than money.
class ViduClient(GenConfig cfg) : IVideoProvider
{
    private const string SubmitUrl = "https://api.vidu.com/ent/v2/img2video";
    private static string TaskUrl(string taskId) => $"https://api.vidu.com/ent/v2/tasks/{taskId}/creations";

    // Vidu's own cap for the Q2 family. Clips are sized to their scene's narration,
    // so a longer line is split across scenes upstream rather than clipped here.
    public const int MaxClipSeconds = 10;

    public sealed record Submission(string TaskId, int Credits);

    public sealed record Result(string State, string? Url, string? Error)
    {
        public bool Succeeded => State == "success" && !string.IsNullOrEmpty(Url);
        // "failed" is Vidu's terminal error state; a task auto-cancelled after the
        // off-peak window reports as cancelled. Either way there is nothing to wait for.
        public bool IsTerminal => Succeeded || State is "failed" or "cancelled";
    }

    private HttpClient CreateClient() =>
        new(new RetryHandler(log: m => Console.WriteLine($"  [vidu] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

    public async Task<Submission> SubmitAsync(
        string startFramePath, string prompt, double narrationSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.ViduApiKey))
            throw new InvalidOperationException("ViduApiKey is not configured.");
        if (!File.Exists(startFramePath))
            throw new FileNotFoundException($"Vidu start frame not found: {startFramePath}", startFramePath);

        // Round up so the clip always covers its narration; ffmpeg trims the tail
        // during assembly rather than letting audio run past the picture.
        var duration = Math.Clamp((int)Math.Ceiling(narrationSeconds), 1, MaxClipSeconds);

        var bytes = await File.ReadAllBytesAsync(startFramePath, ct);
        var mediaType = Path.GetExtension(startFramePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png",
        };

        var body = JsonSerializer.Serialize(new
        {
            model = cfg.ViduModel,
            images = new[] { $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}" },
            prompt,
            duration,
            resolution = cfg.ViduResolution,
            off_peak = cfg.ViduOffPeak,
            // Narration comes from Azure TTS and background music from assets/music.mp3,
            // both mixed in during assembly. Generated audio here would fight both, in a
            // language we don't control, and desync the subtitle timing.
            audio = false,
            bgm = false,
        });

        using var http = CreateClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Token {cfg.ViduApiKey}");

        using var resp = await http.PostAsync(SubmitUrl, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Vidu submit failed ({(int)resp.StatusCode}): {Text.Tail(json, 500)}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var taskId = root.TryGetProperty("task_id", out var id) ? id.GetString() : null;
        if (string.IsNullOrEmpty(taskId))
            throw new Exception($"Vidu returned no task_id: {Text.Tail(json, 500)}");

        var credits = root.TryGetProperty("credits", out var c) && c.TryGetInt32(out var credit) ? credit : 0;
        return new Submission(taskId, credits);
    }

    public async Task<Result> PollAsync(string taskId, CancellationToken ct = default)
    {
        using var http = CreateClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Token {cfg.ViduApiKey}");

        using var resp = await http.GetAsync(TaskUrl(taskId), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Vidu poll failed ({(int)resp.StatusCode}): {Text.Tail(json, 400)}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
        var error = root.TryGetProperty("err_msg", out var e) ? e.GetString() : null;

        // The clean render and a watermarked variant are returned side by side;
        // "url" is the unwatermarked one and the only one fit to publish.
        string? url = null;
        if (root.TryGetProperty("creations", out var creations) &&
            creations.ValueKind == JsonValueKind.Array &&
            creations.GetArrayLength() > 0 &&
            creations[0].TryGetProperty("url", out var u))
        {
            url = u.GetString();
        }

        return new Result(state, url, string.IsNullOrWhiteSpace(error) ? null : error);
    }

    // Download URLs are pre-signed and expire, so fetch straight after a successful
    // poll rather than persisting the URL for later.
    public async Task DownloadAsync(string url, string outputPath, CancellationToken ct = default)
    {
        using var http = CreateClient();

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Vidu download failed ({(int)resp.StatusCode}) for {Path.GetFileName(outputPath)}.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(outputPath);
        await stream.CopyToAsync(file, ct);

        if (new FileInfo(outputPath).Length == 0)
            throw new Exception($"Vidu returned an empty clip for {Path.GetFileName(outputPath)}.");
    }
}
