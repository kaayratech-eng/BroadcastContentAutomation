using System.Text.Json;
using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

// Instagram Stories via the Instagram Graph API — same container/upload/publish
// shape as InstagramPublisher, with media_type=STORIES instead of REELS.
//
// Two differences from Reels, per Meta's documented Stories publishing flow:
// there is no `caption` parameter (Stories publish with no text), and a
// published Story has no stable permalink the way a Reel does — TryPermalinkAsync
// coming back empty here is expected, not an error. Not live-tested yet.
sealed class InstagramStoryPublisher(InstagramConfig cfg, Logger log) : IPublisher
{
    public string Platform => Platforms.InstagramStory;
    public bool Enabled => cfg.Enabled;
    public int MaxPerRun => cfg.MaxUploadsPerRun;

    private string Base => $"https://graph.facebook.com/{cfg.GraphVersion}";

    public bool Accepts(Sidecar sidecar, out string? reason)
    {
        if (!sidecar.IsVertical)
        {
            reason = "Instagram Stories only accepts the 9:16 render.";
            return false;
        }

        reason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.IgUserId) || string.IsNullOrWhiteSpace(cfg.AccessToken))
            return PublishResult.Fatal("Instagram IgUserId or AccessToken is not configured.");

        using var http = MetaGraph.CreateClient(log, "instagram_story");

        try
        {
            // 1. Container with upload_type=resumable, no caption (Stories don't take one).
            using var container = await MetaGraph.PostFormAsync(http, $"{Base}/{cfg.IgUserId}/media",
            [
                new("media_type", "STORIES"),
                new("upload_type", "resumable"),
                new("access_token", cfg.AccessToken),
            ], ct);

            var containerId = MetaGraph.RequireString(container.RootElement, "id");

            var uploadUrl = container.RootElement.TryGetProperty("uri", out var uri)
                ? uri.GetString()!
                : $"https://rupload.facebook.com/ig-api-upload/{cfg.GraphVersion}/{containerId}";

            log.Info($"  [instagram_story] container {containerId}, uploading {Text.Bytes(request.SizeBytes)}");

            // 2. Binary upload
            await MetaGraph.UploadBinaryAsync(http, uploadUrl, cfg.AccessToken, request.VideoPath, ct,
                m => log.Info($"  [instagram_story] {m}"));

            // 3. Wait for transcode
            await MetaGraph.WaitForStatusAsync(http,
                $"{Base}/{containerId}?fields=status_code,status&access_token={Uri.EscapeDataString(cfg.AccessToken)}",
                root =>
                {
                    var code = root.TryGetProperty("status_code", out var sc) ? sc.GetString() ?? "" : "";
                    var detail = root.TryGetProperty("status", out var st) ? st.GetString() ?? code : code;
                    return (code == "FINISHED", code is "ERROR" or "EXPIRED", detail);
                },
                cfg.ProcessingTimeoutSeconds, log, "instagram_story", ct);

            // 4. Publish
            using var published = await MetaGraph.PostFormAsync(http, $"{Base}/{cfg.IgUserId}/media_publish",
            [
                new("creation_id", containerId),
                new("access_token", cfg.AccessToken),
            ], ct);

            var mediaId = MetaGraph.RequireString(published.RootElement, "id");
            var permalink = await TryPermalinkAsync(http, mediaId, ct);

            return PublishResult.Ok(mediaId, permalink);
        }
        catch (MetaGraphException ex)
        {
            return ex.Error.Permanent
                ? PublishResult.Fatal($"Instagram Story (code {ex.Error.Code}/{ex.Error.SubCode}): {ex.Message}")
                : PublishResult.Retry($"Instagram Story (code {ex.Error.Code}): {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return PublishResult.Retry($"Instagram Story transport error: {ex.Message}");
        }
    }

    private async Task<string?> TryPermalinkAsync(HttpClient http, string mediaId, CancellationToken ct)
    {
        try
        {
            using var doc = await MetaGraph.GetAsync(http,
                $"{Base}/{mediaId}?fields=permalink&access_token={Uri.EscapeDataString(cfg.AccessToken)}", ct);
            return doc.RootElement.TryGetProperty("permalink", out var p) ? p.GetString() : null;
        }
        catch (Exception)
        {
            // Stories don't expose a permalink the way Reels do; a missing one here
            // is expected, and must not turn a successful publish into a retry.
            return null;
        }
    }
}
