// Stock-footage lookup contract, shared by the direct client (config key)
// and the vault-backed helper client (see StockFootageHelperClient).
// Returns the downloaded file's path, or null when Pexels has no match -
// a missed b-roll shot is not fatal to the run.

// Rich result for a stock download: the file path plus the Pexels
// provenance the quality gate and the per-job manifest need (Ronaldo
// 2026-11-25: scene 4's hockey clip was unauditable — nothing recorded
// which video Pexels returned or why).
internal sealed record StockDownload(
    string Path,
    string Kind,          // "video" or "photo"
    long? VideoId,
    long? PhotoId,
    string? PageUrl,      // pexels.com page URL (its slug is the only
                          // subject signal the video API returns — tags
                          // are always empty, no alt text)
    string? Alt);         // photo alt text (video search returns none)

interface IStockFootageClient
{
    Task<string?> DownloadBestMatchAsync(string query, string outputPathWithoutExtension, ImageClient.Orientation orientation);

    // Detailed download: the same selection, but returns the provenance
    // and refuses clips whose video id is in excludeVideoIds — Rishi's
    // reuse rule (2026-11-25 23:49): never the same clip twice within
    // < 4 scenes. The caller (RunVisualLoopAsync) passes the ids used
    // by the previous three scenes, from pexels-video-used.json.
    Task<StockDownload?> DownloadBestMatchDetailedAsync(string query, string outputPathWithoutExtension, ImageClient.Orientation orientation, IReadOnlySet<long>? excludeVideoIds = null);
}
