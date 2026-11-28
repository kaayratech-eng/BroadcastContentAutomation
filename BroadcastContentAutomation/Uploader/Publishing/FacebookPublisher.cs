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

    public bool Accepts(Sidecar sidecar, string videoPath, out string? reason)
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
            // Idempotency: a previous run may have uploaded the video and died
            // before the sidecar was saved. If the recorded video is live,
            // reuse it — do not post a second video to the Page.
            if (!string.IsNullOrWhiteSpace(request.ExistingId))
            {
                var prior = await CheckPriorVideoAsync(http, request.ExistingId, ct);
                if (prior is not null) return prior;
                // null = the prior video is gone/failed — fall through to a fresh upload.
            }

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

            // The video is uploaded NOW. Record the id before the processing
            // poll, so a VM restart while waiting cannot post a second video.
            if (request.OnPlatformIdAsync is not null)
            {
                try
                {
                    await request.OnPlatformIdAsync(videoId);
                }
                catch (Exception ex)
                {
                    // L13: the video is live; only the id-save failed. The
                    // callback already set the id in memory, and Program.cs
                    // saves the sidecar again after PublishAsync returns —
                    // log it and continue as a success.
                    log.Error($"  [facebook] video {videoId} is live but the id could not be recorded ({ex.GetType().Name}: {ex.Message}); continuing — the sidecar save after publish will persist it.");
                }
            }

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
        catch (Exception ex)
        {
            // L13: anything outside the known shapes used to escape to the
            // per-video catch and burn a crash retry. Return a target-level
            // Retry instead; a video already live is re-verified via
            // ExistingId on the next run.
            return PublishResult.Retry(
                $"Facebook unexpected error ({ex.GetType().Name}): {ex.Message}; target will retry, video already live will be re-verified via ExistingId");
        }
    }

    // Ask Facebook what happened to a video id recorded by a previous run that
    // died before the sidecar was saved. Ready -> reuse it; failed processing
    // -> fresh upload; still processing -> wait (Retry, burns an attempt);
    // empty/missing status -> Defer (wait WITHOUT burning an attempt — no
    // state was reported at all, so the video may be fine and the attempt
    // budget must not be spent on a lookup that said nothing); not found
    // -> fresh upload; lookup failure -> Retry (never publish blind).
    private async Task<PublishResult?> CheckPriorVideoAsync(HttpClient http, string videoId, CancellationToken ct)
    {
        try
        {
            using var doc = await MetaGraph.GetAsync(http,
                $"{Base}/{Uri.EscapeDataString(videoId)}?fields=status&access_token={Uri.EscapeDataString(cfg.AccessToken)}", ct);
            var phase = doc.RootElement.TryGetProperty("status", out var st) && st.TryGetProperty("video_status", out var vs)
                ? vs.GetString() ?? ""
                : "";
            if (phase == "ready")
                return PublishResult.Ok(videoId, $"https://www.facebook.com/{cfg.PageId}/videos/{videoId}");
            if (phase is "error" or "failed")
            {
                log.Info($"  [facebook] previously recorded video {videoId} failed processing ({phase}); uploading fresh.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(phase))
            {
                // B19: an empty/missing status means Facebook reported no
                // state at all — not "processing", not "failed", nothing.
                // Returning Retry here burned one of the 3 attempts on every
                // run and landed a healthy video Abandoned. Defer instead:
                // the next run re-checks, and the attempt budget is untouched.
                log.Info($"  [facebook] previously recorded video {videoId} returned no processing status; deferring without burning an attempt.");
                return PublishResult.Defer($"Facebook video {videoId} from a previous run returned no processing status; waiting before retrying without burning an attempt.");
            }
            return PublishResult.Retry($"Facebook video {videoId} from a previous run is still processing (status: {phase}); waiting before retrying.");
        }
        catch (MetaGraphException ex) when (ex.Error.Code is 100 or 404)
        {
            // Graph error 100 / HTTP 404 = the video is gone (deleted), so a
            // fresh upload is safe.
            log.Info($"  [facebook] previously recorded video {videoId} is gone ({ex.Message}); uploading fresh.");
            return null;
        }
        catch (Exception ex)
        {
            // The lookup itself failed (transient 500, timeout, transport) —
            // the prior video may still be live, so retry instead of risking
            // a duplicate post to the Page.
            log.Info($"  [facebook] could not verify prior video {videoId} ({ex.Message}); waiting before retrying.");
            return PublishResult.Retry($"Could not verify previously recorded Facebook video {videoId}; not re-uploading blind to avoid a duplicate.");
        }
    }
}
