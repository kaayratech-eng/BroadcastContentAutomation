using System.Text.Json;
using GiggleGarden.Shared;

// Pexels stock photo/video search - the "context/b-roll moment" visual source for the
// stock-footage/Remotion pipeline (Deliverable 6, tasks/todo.md). Pexels' license
// permits commercial/monetized use with no attribution required (checked live before
// adopting - see todo.md), so this queries and downloads with no human review step,
// unlike the manual Gemini character-art loop it sits alongside. Wikimedia/Archive.org
// stay out of this client entirely for that reason - they're manual-approval only.
class StockFootageClient(GenConfig cfg) : IStockFootageClient
{
    private const string VideoSearchUrl = "https://api.pexels.com/videos/search";
    private const string PhotoSearchUrl = "https://api.pexels.com/v1/search";

    // Tries a video match first (Remotion can pan/crop real footage), falls back to a
    // photo if no video matches - some scene concepts (an abstract idea, a rare
    // historical object) turn up stills far more often than footage. outputPathWithoutExtension
    // gets ".mp4" or ".jpg" appended depending on which kind actually matched, since the
    // caller can't know in advance which one it'll get. Returns null (not a throw) when
    // Pexels simply has no match for the query - a missed b-roll shot is not fatal to
    // the run the way a failed image *generation* would be.
    public async Task<string?> DownloadBestMatchAsync(string query, string outputPathWithoutExtension, ImageClient.Orientation orientation)
    {
        var detailed = await DownloadBestMatchDetailedAsync(query, outputPathWithoutExtension, orientation);
        return detailed?.Path;
    }

    public async Task<StockDownload?> DownloadBestMatchDetailedAsync(string query, string outputPathWithoutExtension, ImageClient.Orientation orientation, IReadOnlySet<long>? excludeVideoIds = null)
    {
        var video = await TryDownloadVideoAsync(query, outputPathWithoutExtension + ".mp4", orientation, excludeVideoIds);
        if (video is not null) return video;
        return await TryDownloadPhotoAsync(query, outputPathWithoutExtension + ".jpg", orientation);
    }

    // Pure video selection (testable without the network): prefer the
    // highest-resolution mp4 that matches the requested orientation and
    // isn't absurdly large (cap at ~1080p) — Pexels returns every encode
    // of every candidate clip, not just one file per video. Frame rate
    // first (2026-11-13: a 24fps clip in the 30fps composition plays back
    // with 3:2 judder — duplicated frames alternating with double-steps —
    // which reads as shaking. 30fps is native; 60fps decimates evenly to
    // 30; 25fps judders less than 24fps), then area. Clips in
    // excludeVideoIds are skipped outright (Rishi's reuse rule — never
    // the same clip twice within < 4 scenes); when the best clip is
    // excluded, the next-best clip wins instead of the same footage
    // under a different filename.
    internal static (string? Link, long? VideoId, string? PageUrl) PickBestVideoFile(
        JsonElement videos, ImageClient.Orientation orientation, IReadOnlySet<long>? excludeVideoIds = null)
    {
        string? bestLink = null;
        long? bestVideoId = null;
        string? bestPageUrl = null;
        long bestArea = 0;
        var bestFpsScore = -1;
        foreach (var clip in videos.EnumerateArray())
        {
            var videoId = clip.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64()
                : (long?)null;
            if (videoId is not null && excludeVideoIds is not null && excludeVideoIds.Contains(videoId.Value))
                continue;
            if (!clip.TryGetProperty("video_files", out var files)) continue;
            foreach (var file in files.EnumerateArray())
            {
                if (file.GetProperty("file_type").GetString() != "video/mp4") continue;
                var w = file.GetProperty("width").GetInt32();
                var h = file.GetProperty("height").GetInt32();
                if (w <= 0 || h <= 0) continue;
                var matchesOrientation = orientation == ImageClient.Orientation.Portrait ? h > w : w >= h;
                if (!matchesOrientation) continue;

                var fps = file.TryGetProperty("fps", out var fpsEl) && fpsEl.ValueKind == JsonValueKind.Number
                    ? fpsEl.GetDouble()
                    : 0;
                var fpsScore = Math.Abs(fps - 30) < 0.5 || Math.Abs(fps - 29.97) < 0.5 ? 3
                    : Math.Abs(fps - 60) < 1 ? 3
                    : Math.Abs(fps - 25) < 0.5 ? 2 : 1;

                var area = (long)w * h;
                if (area <= 1920L * 1080 && (fpsScore > bestFpsScore || (fpsScore == bestFpsScore && area > bestArea)))
                {
                    bestFpsScore = fpsScore;
                    bestArea = area;
                    bestLink = file.GetProperty("link").GetString();
                    bestVideoId = videoId;
                    bestPageUrl = clip.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                        ? urlEl.GetString()
                        : null;
                }
            }
        }
        return (bestLink, bestVideoId, bestPageUrl);
    }

    private async Task<StockDownload?> TryDownloadVideoAsync(string query, string outputPath, ImageClient.Orientation orientation, IReadOnlySet<long>? excludeVideoIds = null)
    {
        using var http = NewClient();
        var url = $"{VideoSearchUrl}?query={Uri.EscapeDataString(query)}&per_page=5&orientation={PexelsOrientation(orientation)}";
        var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Pexels video search failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 500)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("videos", out var videos) || videos.GetArrayLength() == 0)
        {
            // L51: "no results" vs "results but none match orientation" vs
            // "empty download" were indistinguishable to the caller — name
            // the query, the orientation and the reason at the null branch.
            Console.WriteLine($"  {PrepContext.Prefix}Pexels {query} {orientation}: no video results — returning null");
            return null;
        }

        var (bestLink, bestVideoId, bestPageUrl) = PickBestVideoFile(videos, orientation, excludeVideoIds);
        if (bestLink is null)
        {
            Console.WriteLine($"  {PrepContext.Prefix}Pexels {query} {orientation}: no file matches orientation — returning null");
            return null;
        }

        await File.WriteAllBytesAsync(outputPath, await http.GetByteArrayAsync(bestLink));
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            Console.WriteLine($"  {PrepContext.Prefix}Pexels {query} {orientation}: empty download — returning null");
            return null;
        }
        return new StockDownload(outputPath, "video", bestVideoId, null, bestPageUrl, null);
    }

    private async Task<StockDownload?> TryDownloadPhotoAsync(string query, string outputPath, ImageClient.Orientation orientation)
    {
        using var http = NewClient();
        var url = $"{PhotoSearchUrl}?query={Uri.EscapeDataString(query)}&per_page=1&orientation={PexelsOrientation(orientation)}";
        var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Pexels photo search failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 500)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("photos", out var photos) || photos.GetArrayLength() == 0)
        {
            // L51: see TryDownloadVideoAsync.
            Console.WriteLine($"  {PrepContext.Prefix}Pexels {query} {orientation}: no photo results — returning null");
            return null;
        }

        var photo = photos[0];
        var link = photo.GetProperty("src").GetProperty("large2x").GetString();
        if (string.IsNullOrEmpty(link))
        {
            Console.WriteLine($"  {PrepContext.Prefix}Pexels {query} {orientation}: empty photo link — returning null");
            return null;
        }
        var photoId = photo.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
            ? idEl.GetInt64()
            : (long?)null;
        var pageUrl = photo.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
            ? urlEl.GetString()
            : null;
        var alt = photo.TryGetProperty("alt", out var altEl) && altEl.ValueKind == JsonValueKind.String
            ? altEl.GetString()
            : null;

        await File.WriteAllBytesAsync(outputPath, await http.GetByteArrayAsync(link));
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            Console.WriteLine($"  {PrepContext.Prefix}Pexels {query} {orientation}: empty download — returning null");
            return null;
        }
        return new StockDownload(outputPath, "photo", null, photoId, pageUrl, alt);
    }

    private HttpClient NewClient()
    {
        var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  {PrepContext.Prefix}[pexels] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        // Pexels uses the raw key as the header value - no "Bearer " prefix.
        http.DefaultRequestHeaders.Add("Authorization", cfg.PexelsApiKey);
        return http;
    }

    private static string PexelsOrientation(ImageClient.Orientation orientation) =>
        orientation == ImageClient.Orientation.Portrait ? "portrait" : "landscape";
}
