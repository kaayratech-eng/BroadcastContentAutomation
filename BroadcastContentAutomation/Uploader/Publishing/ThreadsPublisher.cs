using System.Text.Json;
using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

// Threads API (https://developers.facebook.com/docs/threads) — a separate Meta
// "Threads" app/use-case from the Instagram Graph API, with its own OAuth app,
// user access token, and Threads user ID (see ThreadsConfig). MetaGraph's generic
// PostFormAsync/GetAsync helpers are reused since the JSON error/response shape
// matches graph.facebook.com; UploadBinaryAsync is NOT reused, because Threads has
// no resumable-upload endpoint at all.
//
// Publish is three steps: create a media container with a public video_url ->
// poll the container's status until FINISHED -> threads_publish. Text posts are
// capped at 500 chars (not Instagram's 2200) and there is no `share_to_feed`
// concept — Threads has one feed, period.
sealed class ThreadsPublisher(ThreadsConfig cfg, Logger log) : IPublisher
{
    public const int CaptionLimit = 500;

    public string Platform => Platforms.Threads;
    public bool Enabled => cfg.Enabled;
    public int MaxPerRun => cfg.MaxUploadsPerRun;

    private string Base => $"https://graph.threads.com/{cfg.GraphVersion}";

    public bool Accepts(Sidecar sidecar, out string? reason)
    {
        if (!sidecar.IsVertical)
        {
            reason = "Threads posts use the 9:16 render, same as Instagram.";
            return false;
        }

        reason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.UserId) || string.IsNullOrWhiteSpace(cfg.AccessToken))
            return PublishResult.Fatal("Threads UserId or AccessToken is not configured.");

        if (string.IsNullOrWhiteSpace(cfg.PublicVideoBaseUrl))
            return PublishResult.Fatal(
                "Threads PublicVideoBaseUrl is not configured — Threads has no direct-upload endpoint, " +
                "it downloads the video itself from a public URL you supply. Host the render somewhere " +
                "reachable over HTTPS and point PublicVideoBaseUrl at it.");

        var videoUrl = $"{cfg.PublicVideoBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(Path.GetFileName(request.VideoPath))}";

        using var http = MetaGraph.CreateClient(log, "threads");

        try
        {
            var caption = request.Sidecar.CaptionFor(Platforms.Threads, CaptionLimit);

            log.Info($"  [threads] container from {videoUrl}");

            // 1. Container — Threads fetches the video itself, no bytes cross this call.
            using var container = await MetaGraph.PostFormAsync(http, $"{Base}/{cfg.UserId}/threads",
            [
                new("media_type", "VIDEO"),
                new("video_url", videoUrl),
                new("text", caption),
                new("access_token", cfg.AccessToken),
            ], ct);

            var containerId = MetaGraph.RequireString(container.RootElement, "id");

            // 2. Poll container status. Docs: query ~once/min, no more than 5 min —
            // WaitForStatusAsync's own backoff (5s, growing to 20s) is faster than
            // that, which just means we notice FINISHED sooner; it doesn't violate
            // the guidance, which is a ceiling on total polling time, not a floor
            // between polls.
            await MetaGraph.WaitForStatusAsync(http,
                $"{Base}/{containerId}?fields=status,error_message&access_token={Uri.EscapeDataString(cfg.AccessToken)}",
                root =>
                {
                    var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                    var detail = root.TryGetProperty("error_message", out var em) ? em.GetString() ?? status : status;
                    return (status == "FINISHED", status is "ERROR" or "EXPIRED", detail);
                },
                cfg.ProcessingTimeoutSeconds, log, "threads", ct);

            // 3. Publish
            using var published = await MetaGraph.PostFormAsync(http, $"{Base}/{cfg.UserId}/threads_publish",
            [
                new("creation_id", containerId),
                new("access_token", cfg.AccessToken),
            ], ct);

            var mediaId = MetaGraph.RequireString(published.RootElement, "id");
            return PublishResult.Ok(mediaId, null);   // Threads has no permalink field on the publish response
        }
        catch (MetaGraphException ex)
        {
            return ex.Error.Permanent
                ? PublishResult.Fatal($"Threads (code {ex.Error.Code}/{ex.Error.SubCode}): {ex.Message}")
                : PublishResult.Retry($"Threads (code {ex.Error.Code}): {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return PublishResult.Retry($"Threads transport error: {ex.Message}");
        }
    }
}
