using System.Text.Json;
using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

// Instagram Reels via the Instagram Graph API.
//
// Requires: an Instagram Business or Creator account linked to a Facebook Page, a
// Meta app with instagram_content_publish granted, and a Page access token.
//
// Publish is four steps: create a resumable container -> POST the bytes to
// rupload.facebook.com -> poll until the container finishes transcoding ->
// media_publish. Reels are capped at 50 published posts per 24 hours per account.
sealed class InstagramPublisher(InstagramConfig cfg, Logger log) : IPublisher
{
    public const int CaptionLimit = 2200;

    public string Platform => Platforms.Instagram;
    public bool Enabled => cfg.Enabled;
    public int MaxPerRun => cfg.MaxUploadsPerRun;

    private string Base => $"https://graph.facebook.com/{cfg.GraphVersion}";

    public bool Accepts(Sidecar sidecar, string videoPath, out string? reason)
    {
        if (!sidecar.IsVertical)
        {
            reason = "Instagram Reels only accepts the 9:16 render.";
            return false;
        }

        reason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.IgUserId) || string.IsNullOrWhiteSpace(cfg.AccessToken))
            return PublishResult.Fatal("Instagram IgUserId or AccessToken is not configured.");

        using var http = MetaGraph.CreateClient(log, "instagram");

        try
        {
            // Idempotency: a previous run may have published the Reel and died
            // before the sidecar was saved. If the recorded media is live,
            // reuse it — do not post a second Reel.
            if (!string.IsNullOrWhiteSpace(request.ExistingId))
            {
                var lookup = await MetaGraph.MediaExistsAsync(http, Base, request.ExistingId, cfg.AccessToken, ct);
                if (lookup == MediaLookup.Found)
                    return PublishResult.Ok(request.ExistingId);
                if (lookup == MediaLookup.Unknown)
                    return PublishResult.Retry(
                        $"Could not verify previously recorded Instagram media {request.ExistingId}; " +
                        "not re-publishing blind to avoid a duplicate Reel.");
                log.Info($"  [instagram] previously recorded media {request.ExistingId} not found; uploading fresh.");
            }

            // Pre-flight: fail fast on a bad encode instead of burning a
            // container + a 26 MB upload + a transcode wait before Meta
            // rejects it. A file that fails this check will not pass on retry,
            // so it is Fatal, not Retry.
            var preflight = PreflightCheck(request.VideoPath);
            if (preflight is not null)
                return PublishResult.Fatal(preflight);

            var caption = request.Sidecar.CaptionFor(Platforms.Instagram, CaptionLimit);

            // 1. Container with upload_type=resumable — this is what lets us send the
            //    file directly instead of hosting it at a public URL.
            using var container = await MetaGraph.PostFormAsync(http, $"{Base}/{cfg.IgUserId}/media",
            [
                new("media_type", "REELS"),
                new("upload_type", "resumable"),
                new("caption", caption),
                new("share_to_feed", cfg.ShareToFeed ? "true" : "false"),
                new("access_token", cfg.AccessToken),
            ], ct);

            var containerId = MetaGraph.RequireString(container.RootElement, "id");

            var uploadUrl = container.RootElement.TryGetProperty("uri", out var uri)
                ? uri.GetString()!
                : $"https://rupload.facebook.com/ig-api-upload/{cfg.GraphVersion}/{containerId}";

            log.Info($"  [instagram] container {containerId}, uploading {Text.Bytes(request.SizeBytes)}");

            // 2. Binary upload
            await MetaGraph.UploadBinaryAsync(http, uploadUrl, cfg.AccessToken, request.VideoPath, ct,
                m => log.Info($"  [instagram] {m}"));

            // 3. Wait for transcode
            await MetaGraph.WaitForStatusAsync(http,
                $"{Base}/{containerId}?fields=status_code,status&access_token={Uri.EscapeDataString(cfg.AccessToken)}",
                root =>
                {
                    var code = root.TryGetProperty("status_code", out var sc) ? sc.GetString() ?? "" : "";
                    var detail = root.TryGetProperty("status", out var st) ? st.GetString() ?? code : code;
                    return (code == "FINISHED", code is "ERROR" or "EXPIRED", detail);
                },
                cfg.ProcessingTimeoutSeconds, log, "instagram", ct);

            // 4. Publish
            using var published = await MetaGraph.PostFormAsync(http, $"{Base}/{cfg.IgUserId}/media_publish",
            [
                new("creation_id", containerId),
                new("access_token", cfg.AccessToken),
            ], ct);

            var mediaId = MetaGraph.RequireString(published.RootElement, "id");

            // The Reel is live NOW. Record the media id before the permalink
            // lookup, so a VM restart in that window cannot post a second Reel.
            if (request.OnPlatformIdAsync is not null)
                await request.OnPlatformIdAsync(mediaId);

            var permalink = await TryPermalinkAsync(http, mediaId, ct);

            return PublishResult.Ok(mediaId, permalink);
        }
        catch (MetaGraphException ex)
        {
            return ex.Error.Permanent
                ? PublishResult.Fatal($"Instagram (code {ex.Error.Code}/{ex.Error.SubCode}): {ex.Message}")
                : PublishResult.Retry($"Instagram (code {ex.Error.Code}): {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return PublishResult.Retry($"Instagram transport error: {ex.Message}");
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
            // The post is live; a missing permalink is cosmetic and must not
            // turn a successful publish into a retry that double-posts.
            return null;
        }
    }

    // Encoding pre-flight (fail fast, before any bytes are sent): the pipeline
    // renders H.264/AAC 1080x1920 30fps, and a file that is not that will never
    // transcode no matter how often it is retried. Returns the Fatal error
    // message, or null when the file passes (or cannot be probed — a missing
    // ffprobe must not block an upload by itself, so that case only warns).
    private string? PreflightCheck(string videoPath)
    {
        var probe = MediaProbe.Probe(videoPath, log);
        if (probe is null)
        {
            log.Warn($"  [instagram] pre-flight probe unavailable for {Path.GetFileName(videoPath)}; uploading without it.");
            return null;
        }

        if (probe.DurationSeconds <= 0)
            return $"Instagram pre-flight: {Path.GetFileName(videoPath)} has no readable duration — refusing to upload a broken file.";
        if (!string.Equals(probe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
            return $"Instagram pre-flight: video codec is {probe.VideoCodec ?? "unknown"}, expected H.264 — refusing to upload a file Meta will not transcode.";
        if (probe.Width != 1080 || probe.Height != 1920)
            return $"Instagram pre-flight: dimensions are {probe.Width}x{probe.Height}, expected 1080x1920 for a Reel — refusing to upload.";
        if (probe.Fps is < 29 or > 31)
            return $"Instagram pre-flight: frame rate is {probe.Fps:0.##} fps, expected ~30 — refusing to upload.";
        if (probe.AudioCodec is not null && !string.Equals(probe.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase))
            return $"Instagram pre-flight: audio codec is {probe.AudioCodec}, expected AAC — refusing to upload a file Meta will not transcode.";
        if (probe.AudioCodec is null)
            log.Warn($"  [instagram] pre-flight: no audio stream found in {Path.GetFileName(videoPath)}; uploading anyway.");

        return null;
    }
}
