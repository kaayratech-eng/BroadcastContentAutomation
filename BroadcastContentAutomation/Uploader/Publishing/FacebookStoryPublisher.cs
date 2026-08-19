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

    public bool Accepts(Sidecar sidecar, out string? reason)
    {
        if (!sidecar.IsVertical)
        {
            reason = "Facebook Stories only accepts the 9:16 render.";
            return false;
        }

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
}
