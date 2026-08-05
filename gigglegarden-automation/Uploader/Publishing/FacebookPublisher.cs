using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

// Facebook Reels via the Meta Graph API's Video Reels endpoint. Requires a
// Facebook Page and a Page access token with publish_video permission.
//
// Unlike Instagram's container-then-publish shape, Reels on a Page use a
// three-phase flow against the same endpoint: upload_phase=start (returns a
// video_id + resumable upload_url) -> POST the bytes to upload_url ->
// upload_phase=finish with video_state=PUBLISHED. No publicly reachable file
// URL is required, same as Instagram.
sealed class FacebookPublisher(FacebookConfig cfg, Logger log) : IPublisher
{
    public const int CaptionLimit = 2200;

    public string Platform => Platforms.Facebook;
    public bool Enabled => cfg.Enabled;
    public int MaxPerRun => cfg.MaxUploadsPerRun;

    private string Base => $"https://graph.facebook.com/{cfg.GraphVersion}";

    public bool Accepts(Sidecar sidecar, out string? reason)
    {
        if (!sidecar.IsVertical)
        {
            reason = "Facebook Reels only accepts the 9:16 render.";
            return false;
        }

        reason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.PageId) || string.IsNullOrWhiteSpace(cfg.AccessToken))
            return PublishResult.Fatal("Facebook PageId or AccessToken is not configured.");

        using var http = MetaGraph.CreateClient(log, "facebook");

        try
        {
            var caption = request.Sidecar.CaptionFor(Platforms.Facebook, CaptionLimit);

            // 1. Start phase — allocates a video_id and a resumable upload_url.
            using var start = await MetaGraph.PostFormAsync(http, $"{Base}/{cfg.PageId}/video_reels",
            [
                new("upload_phase", "start"),
                new("access_token", cfg.AccessToken),
            ], ct);

            var videoId = MetaGraph.RequireString(start.RootElement, "video_id");
            var uploadUrl = MetaGraph.RequireString(start.RootElement, "upload_url");

            log.Info($"  [facebook] video {videoId}, uploading {Text.Bytes(request.SizeBytes)}");

            // 2. Binary upload
            await MetaGraph.UploadBinaryAsync(http, uploadUrl, cfg.AccessToken, request.VideoPath, ct);

            // 3. Wait for transcode
            await MetaGraph.WaitForStatusAsync(http,
                $"{Base}/{videoId}?fields=status&access_token={Uri.EscapeDataString(cfg.AccessToken)}",
                root =>
                {
                    var phase = root.TryGetProperty("status", out var st) &&
                                st.TryGetProperty("video_status", out var vs)
                        ? vs.GetString() ?? ""
                        : "";
                    return (phase == "ready", phase is "error" or "failed", phase);
                },
                cfg.ProcessingTimeoutSeconds, log, "facebook", ct);

            // 4. Finish phase — publishes the reel to the Page.
            using var finish = await MetaGraph.PostFormAsync(http, $"{Base}/{cfg.PageId}/video_reels",
            [
                new("upload_phase", "finish"),
                new("video_id", videoId),
                new("video_state", "PUBLISHED"),
                new("description", caption),
                new("access_token", cfg.AccessToken),
            ], ct);

            var success = finish.RootElement.TryGetProperty("success", out var ok) &&
                          ok.ValueKind != System.Text.Json.JsonValueKind.False;
            if (!success)
                return PublishResult.Retry("Facebook finish phase reported success=false.");

            return PublishResult.Ok(videoId, $"https://www.facebook.com/{cfg.PageId}/videos/{videoId}");
        }
        catch (MetaGraphException ex)
        {
            return ex.Error.Permanent
                ? PublishResult.Fatal($"Facebook (code {ex.Error.Code}/{ex.Error.SubCode}): {ex.Message}")
                : PublishResult.Retry($"Facebook (code {ex.Error.Code}): {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return PublishResult.Retry($"Facebook transport error: {ex.Message}");
        }
    }
}
