// Pexels stock photo/video search via the vault-backed helper (set up
// 2026-11-11). Same contract and selection logic as StockFootageClient -
// video first (highest-resolution mp4 that matches the orientation and
// isn't absurdly large, capped at ~1080p), photo fallback - but the
// authenticated searches go through the pexels_search.py helper, which
// performs the authd surrogate exchange for the custom.pexels key. No key
// value is handled here. The media links themselves are public CDN URLs,
// so the downloads are plain unauthenticated GETs, exactly as in the
// direct client. Used whenever GenConfig.PexelsApiKey holds no real key
// (see the selection in Program.cs RunVisualLoopAsync).
using System.Text.Json;
using GiggleGarden.Shared;

class StockFootageHelperClient(GenConfig cfg) : IStockFootageClient
{
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

    private async Task<StockDownload?> TryDownloadVideoAsync(string query, string outputPath, ImageClient.Orientation orientation, IReadOnlySet<long>? excludeVideoIds = null)
    {
        var json = await SearchAsync("search-video", query, orientation, perPage: 5);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("videos", out var videos) || videos.GetArrayLength() == 0)
        {
            // L51: see StockFootageClient — name the query, the
            // orientation and the reason at the null branch.
            Console.WriteLine($"  {PrepContext.Prefix}Pexels {query} {orientation}: no video results — returning null");
            return null;
        }

        string? bestLink = null;
        long? bestVideoId = null;
        string? bestPageUrl = null;
        long bestArea = 0;
        foreach (var clip in videos.EnumerateArray())
        {
            var videoId = clip.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64()
                : (long?)null;
            // Rishi's reuse rule: never the same clip twice within < 4
            // scenes — an excluded clip's files never enter the race.
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

                var area = (long)w * h;
                if (area <= 1920L * 1080 && area > bestArea)
                {
                    bestArea = area;
                    bestLink = file.GetProperty("link").GetString();
                    bestVideoId = videoId;
                    bestPageUrl = clip.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                        ? urlEl.GetString()
                        : null;
                }
            }
        }
        if (bestLink is null)
        {
            Console.WriteLine($"  {PrepContext.Prefix}Pexels {query} {orientation}: no file matches orientation — returning null");
            return null;
        }

        await DownloadAsync(bestLink, outputPath);
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            Console.WriteLine($"  {PrepContext.Prefix}Pexels {query} {orientation}: empty download — returning null");
            return null;
        }
        return new StockDownload(outputPath, "video", bestVideoId, null, bestPageUrl, null);
    }

    private async Task<StockDownload?> TryDownloadPhotoAsync(string query, string outputPath, ImageClient.Orientation orientation)
    {
        var json = await SearchAsync("search-photo", query, orientation, perPage: 1);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("photos", out var photos) || photos.GetArrayLength() == 0)
        {
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

        await DownloadAsync(link, outputPath);
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            Console.WriteLine($"  {PrepContext.Prefix}Pexels {query} {orientation}: empty download — returning null");
            return null;
        }
        return new StockDownload(outputPath, "photo", null, photoId, pageUrl, alt);
    }

    private async Task<string> SearchAsync(string command, string query, ImageClient.Orientation orientation, int perPage)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.PexelsHelperPath, command,
            "--query", query,
            "--orientation", orientation == ImageClient.Orientation.Portrait ? "portrait" : "landscape",
            "--per-page", perPage.ToString(),
        ], timeoutMinutes: 2);
        if (result.ExitCode != 0)
            throw new Exception($"Pexels helper {command} failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 500)}");
        return result.Stdout;
    }

    private static async Task DownloadAsync(string url, string outputPath)
    {
        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  {PrepContext.Prefix}[pexels] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        await File.WriteAllBytesAsync(outputPath, await http.GetByteArrayAsync(url));
    }
}
