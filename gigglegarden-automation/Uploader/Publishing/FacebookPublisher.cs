using System.Net.Http.Headers;
using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

// Regular Facebook Page video post via the Graph API's /{page-id}/videos endpoint.
//
// Originally used the Video Reels endpoint (upload_phase=start/finish), but that
// requires the Page to be eligible for Meta's Reels processing pipeline — a new
// or low-activity Page (or certain Page categories) can sit at
// processing_phase="not_started" indefinitely with no error ever surfacing, which
// is exactly what happened on this Page. The plain video-post endpoint predates
// Reels, has no such eligibility gate, and works for any Page with publish_video
// access — the tradeoff is the post lands on the Videos tab/feed rather than the
// Reels surface.
sealed class FacebookPublisher(FacebookConfig cfg, Logger log) : IPublisher
{
    public const int CaptionLimit = 2200;

    public string Platform => Platforms.Facebook;
    public bool Enabled => cfg.Enabled;
    public int MaxPerRun => cfg.MaxUploadsPerRun;

    private string Base => $"https://graph.facebook.com/{cfg.GraphVersion}";

    public bool Accepts(Sidecar sidecar, out string? reason)
    {
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

            log.Info($"  [facebook] uploading {Text.Bytes(request.SizeBytes)} to Page video post");

            await using var stream = File.OpenRead(request.VideoPath);
            using var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");

            using var form = new MultipartFormDataContent
            {
                { new StringContent(caption), "description" },
                { new StringContent(cfg.AccessToken), "access_token" },
                { fileContent, "source", Path.GetFileName(request.VideoPath) },
            };

            using var upload = await MetaGraph.PostMultipartAsync(http, $"{Base}/{cfg.PageId}/videos", form, ct);
            var videoId = MetaGraph.RequireString(upload.RootElement, "id");

            log.Info($"  [facebook] video {videoId} uploaded, waiting for processing");

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
