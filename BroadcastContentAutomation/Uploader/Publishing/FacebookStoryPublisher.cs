using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

// Facebook Page Stories via the Graph API's /{page-id}/video_stories endpoint —
// a different endpoint from both FacebookPublisher's plain /{page-id}/videos and
// the abandoned Reels endpoint, but the same two-phase upload_phase=start/finish
// shape Meta uses for Reels (see the comment on FacebookPublisher for why Reels
// was abandoned on this Page). Page Stories may hit the same eligibility gating
// Reels did — not live-tested yet, watch for a stuck/silent processing_phase on
// first real run.
sealed class FacebookStoryPublisher(FacebookConfig cfg, Logger log) : IPublisher
{
    public string Platform => Platforms.FacebookStory;
    public bool Enabled => cfg.Enabled;
    public int MaxPerRun => cfg.MaxUploadsPerRun;

    private string Base => $"https://graph.facebook.com/{cfg.GraphVersion}";

    public bool Accepts(Sidecar sidecar, string videoPath, out string? reason)
    {
        if (!sidecar.IsVertical)
        {
            reason = "Facebook Stories only accepts the 9:16 render.";
            return false;
        }

        // No duration gate here, unlike Instagram Stories: Facebook Story
        // published fine at 78.8s, so no 60s cap is assumed for it.
        reason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.PageId) || string.IsNullOrWhiteSpace(cfg.AccessToken))
            return PublishResult.Fatal("Facebook PageId or AccessToken is not configured.");

        using var http = MetaGraph.CreateClient(log, "facebook_story");

        try
        {
            // Idempotency: a previous run may have finished the story upload
            // and died before the sidecar was saved. If the recorded video is
            // live, reuse it — do not post a second Story. (Same shape as
            // FacebookPublisher's check; a lookup failure means "retry", never
            // "upload fresh" — a transient failure on the verification GET must
            // not cause a duplicate Story.)
            if (!string.IsNullOrWhiteSpace(request.ExistingId))
            {
                var prior = await CheckPriorVideoAsync(http, request.ExistingId, ct);
                if (prior is not null) return prior;
                // null = the prior video is gone/failed — fall through to a fresh upload.
            }

            // 1. Start an upload session.
            using var start = await MetaGraph.PostFormAsync(http, $"{Base}/{cfg.PageId}/video_stories",
            [
                new("upload_phase", "start"),
                new("access_token", cfg.AccessToken),
            ], ct);

            var videoId = MetaGraph.RequireString(start.RootElement, "video_id");
            var uploadUrl = start.RootElement.TryGetProperty("upload_url", out var uri)
                ? uri.GetString()!
                : $"https://rupload.facebook.com/video-upload/{cfg.GraphVersion}/{videoId}";

            log.Info($"  [facebook_story] session {videoId}, uploading {Text.Bytes(request.SizeBytes)}");

            // 2. Binary upload
            await MetaGraph.UploadBinaryAsync(http, uploadUrl, cfg.AccessToken, request.VideoPath, ct,
                m => log.Info($"  [facebook_story] {m}"));

            // 3. Finish — publishes the story.
            using var finish = await MetaGraph.PostFormAsync(http, $"{Base}/{cfg.PageId}/video_stories",
            [
                new("upload_phase", "finish"),
                new("video_id", videoId),
                new("access_token", cfg.AccessToken),
            ], ct);

            var postId = finish.RootElement.TryGetProperty("post_id", out var pid) ? pid.GetString() : videoId;

            // The story is published NOW. Record the video id before the
            // processing poll, so a VM restart while waiting cannot post a
            // second Story.
            if (request.OnPlatformIdAsync is not null)
                await request.OnPlatformIdAsync(videoId);

            // 4. Poll until the story finishes processing — finish alone only
            // confirms the upload session closed, not that the story is live
            // (same shape as FacebookPublisher's plain video post). Returning
            // Ok straight after finish recorded a story as Published even when
            // it later failed processing, and it was never revisited.
            await MetaGraph.WaitForStatusAsync(http,
                $"{Base}/{videoId}?fields=status&access_token={Uri.EscapeDataString(cfg.AccessToken)}",
                root =>
                {
                    // Stories may not expose a status object at all; finish
                    // already confirmed the publish, so an absent status means
                    // "done" rather than a poll timeout.
                    if (!root.TryGetProperty("status", out var st)) return (true, false, "no status reported");
                    var phase = st.TryGetProperty("video_status", out var vs) ? vs.GetString() ?? "" : "";
                    return (phase == "ready", phase is "error" or "failed", phase);
                },
                cfg.ProcessingTimeoutSeconds, log, "facebook_story", ct);

            return PublishResult.Ok(videoId, postId is { Length: > 0 } ? $"https://www.facebook.com/{postId}" : null);
        }
        catch (MetaGraphException ex)
        {
            return ex.Error.Permanent
                ? PublishResult.Fatal($"Facebook Story (code {ex.Error.Code}/{ex.Error.SubCode}): {ex.Message}")
                : PublishResult.Retry($"Facebook Story (code {ex.Error.Code}): {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return PublishResult.Retry($"Facebook Story transport error: {ex.Message}");
        }
    }

    // Same prior-video check as FacebookPublisher: ready -> reuse the story,
    // failed processing -> fresh upload, still processing -> wait (Retry,
    // burns an attempt), empty/missing status -> Defer (wait WITHOUT burning
    // an attempt — B19, see FacebookPublisher), not found -> fresh upload,
    // lookup failure -> Retry (never publish blind).
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
                log.Info($"  [facebook_story] previously recorded video {videoId} failed processing ({phase}); uploading fresh.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(phase))
            {
                // B19: no status reported at all — Defer, don't Retry: a
                // Retry here would burn an attempt on every run and abandon
                // a healthy story. (The post-upload poll in PublishAsync
                // already treats an absent status as done; this is the
                // prior-video check only.)
                log.Info($"  [facebook_story] previously recorded video {videoId} returned no processing status; deferring without burning an attempt.");
                return PublishResult.Defer($"Facebook Story {videoId} from a previous run returned no processing status; waiting before retrying without burning an attempt.");
            }
            return PublishResult.Retry($"Facebook Story {videoId} from a previous run is still processing (status: {phase}); waiting before retrying.");
        }
        catch (MetaGraphException ex) when (ex.Error.Code is 100 or 404)
        {
            // Graph error 100 / HTTP 404 = the video is gone (deleted), so a
            // fresh upload is safe.
            log.Info($"  [facebook_story] previously recorded video {videoId} is gone ({ex.Message}); uploading fresh.");
            return null;
        }
        catch (Exception ex)
        {
            // The lookup itself failed (transient 500, timeout, transport) —
            // the prior video may still be live, so retry instead of risking
            // a duplicate Story.
            log.Info($"  [facebook_story] could not verify prior video {videoId} ({ex.Message}); waiting before retrying.");
            return PublishResult.Retry($"Could not verify previously recorded Facebook Story video {videoId}; not re-uploading blind to avoid a duplicate.");
        }
    }
}
