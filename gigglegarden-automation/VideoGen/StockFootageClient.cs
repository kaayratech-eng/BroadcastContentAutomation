using System.Text.Json;
using GiggleGarden.Shared;

// Pexels stock photo/video search - the "context/b-roll moment" visual source for the
// stock-footage/Remotion pipeline (Deliverable 6, tasks/todo.md). Pexels' license
// permits commercial/monetized use with no attribution required (checked live before
// adopting - see todo.md), so this queries and downloads with no human review step,
// unlike the manual Gemini character-art loop it sits alongside. Wikimedia/Archive.org
// stay out of this client entirely for that reason - they're manual-approval only.
class StockFootageClient(GenConfig cfg)
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
        var video = await TryDownloadVideoAsync(query, outputPathWithoutExtension + ".mp4", orientation);
        if (video is not null) return video;
        return await TryDownloadPhotoAsync(query, outputPathWithoutExtension + ".jpg", orientation);
    }

    private async Task<string?> TryDownloadVideoAsync(string query, string outputPath, ImageClient.Orientation orientation)
    {
        using var http = NewClient();
        var url = $"{VideoSearchUrl}?query={Uri.EscapeDataString(query)}&per_page=5&orientation={PexelsOrientation(orientation)}";
        var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Pexels video search failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 500)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("videos", out var videos) || videos.GetArrayLength() == 0)
            return null;

        // Prefer the highest-resolution mp4 file that actually matches the requested
        // orientation and isn't absurdly large (cap at ~1080p) - Pexels returns every
        // encode of every candidate clip, not just one file per video.
        string? bestLink = null;
        long bestArea = 0;
        foreach (var clip in videos.EnumerateArray())
        {
            if (!clip.TryGetProperty("video_files", out var files)) continue;
            foreach (var file in files.EnumerateArray())
            {
                if (file.GetProperty("file_type").GetString() != "video/mp4") continue;
                var w = file.GetProperty("width").GetInt32();
                var h = file.GetProperty("height").GetInt32();
                if (w <= 0 || h <= 0) continue;
                var matchesOrientation = orientation == ImageClient.Orientation.Portrait ? h > w : w >= h;
                if (!matchesOrientation) continue;

                var area = (long)w * h;
                if (area <= 1920L * 1080 && area > bestArea)
                {
                    bestArea = area;
                    bestLink = file.GetProperty("link").GetString();
                }
            }
        }
        if (bestLink is null) return null;

        await File.WriteAllBytesAsync(outputPath, await http.GetByteArrayAsync(bestLink));
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
    }

    private async Task<string?> TryDownloadPhotoAsync(string query, string outputPath, ImageClient.Orientation orientation)
    {
        using var http = NewClient();
        var url = $"{PhotoSearchUrl}?query={Uri.EscapeDataString(query)}&per_page=1&orientation={PexelsOrientation(orientation)}";
        var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Pexels photo search failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 500)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("photos", out var photos) || photos.GetArrayLength() == 0)
            return null;

        var link = photos[0].GetProperty("src").GetProperty("large2x").GetString();
        if (string.IsNullOrEmpty(link)) return null;

        await File.WriteAllBytesAsync(outputPath, await http.GetByteArrayAsync(link));
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
    }

    private HttpClient NewClient()
    {
        var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  [pexels] {m}")))
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
