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

    public bool Accepts(Sidecar sidecar, string videoPath, out string? reason)
    {
        if (!sidecar.IsVertical)
        {
            reason = "Instagram Stories only accepts the 9:16 render.";
            return false;
        }

        // Instagram Stories are capped at 60s. The Shorts this pipeline makes
        // are 78-81s, so without this gate every Story burns a full container +
        // upload + transcode wait before Meta rejects it — and a rejection used
        // to be terminal (see MetaGraph.ParseError, code -1). Gate here, not in
        // PublishAsync, so the target is marked Skipped with a clear reason
        // instead of Failed/Abandoned. Facebook Story is NOT gated: it published
        // fine at 78.8s, so no cap is assumed for it.
        //
        // Story cut (Rishi 2026-11-26): finish-chunks.py writes a first-60s
        // sibling cut for over-60s Shorts, and the dispatcher hands the
        // cut's path here. The sidecar still carries the FULL Short's
        // duration, so the gate must probe the actual file when the path
        // is a cut — trusting the sidecar would skip every cut.
        var duration = StoryCut.IsStoryCut(videoPath)
            ? MediaProbe.TryGetDurationSeconds(videoPath, log)
            : sidecar.DurationSeconds ?? MediaProbe.TryGetDurationSeconds(videoPath, log);
        if (duration.HasValue && duration.Value > 60)
        {
            reason = $"Instagram Stories are capped at 60s; this video is {duration.Value:0.#}s — skipping the Story target.";
            return false;
        }
        if (!duration.HasValue)
            log.Warn($"  [instagram_story] could not determine duration for {Path.GetFileName(videoPath)}; attempting anyway.");

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
            // Idempotency: a previous run may have published the Story and died
            // before the sidecar was saved. If the recorded media is live,
            // reuse it — do not post a second Story.
            if (!string.IsNullOrWhiteSpace(request.ExistingId))
            {
                var lookup = await MetaGraph.MediaExistsAsync(http, Base, request.ExistingId, cfg.AccessToken, ct, log);
                if (lookup == MediaLookup.Found)
                    return PublishResult.Ok(request.ExistingId);
                if (lookup == MediaLookup.Unknown)
                    return PublishResult.Retry(
                        $"Could not verify previously recorded Instagram Story media {request.ExistingId}; " +
                        "not re-publishing blind to avoid a duplicate Story.");
                log.Info($"  [instagram_story] previously recorded media {request.ExistingId} not found; uploading fresh.");
            }

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

            // The Story is live NOW. Record the media id before the permalink
            // lookup, so a VM restart in that window cannot post a second Story.
            if (request.OnPlatformIdAsync is not null)
            {
                try
                {
                    await request.OnPlatformIdAsync(mediaId);
                }
                catch (Exception ex)
                {
                    // L13: the Story is live; only the id-save failed. The
                    // callback already set the id in memory, and Program.cs
                    // saves the sidecar again after PublishAsync returns —
                    // log it and continue as a success.
                    log.Error($"  [instagram_story] Story {mediaId} is live but the id could not be recorded ({ex.GetType().Name}: {ex.Message}); continuing — the sidecar save after publish will persist it.");
                }
            }

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
        catch (Exception ex)
        {
            // L13: anything outside the known shapes used to escape to the
            // per-video catch and burn a crash retry. Return a target-level
            // Retry instead; a Story already live is re-verified via
            // ExistingId on the next run.
            return PublishResult.Retry(
                $"Instagram Story unexpected error ({ex.GetType().Name}): {ex.Message}; target will retry, video already live will be re-verified via ExistingId");
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
        catch (Exception ex)
        {
            // Stories don't expose a permalink the way Reels do; a missing one here
            // is expected, and must not turn a successful publish into a retry.
            // L74: log the failure anyway — an empty Permalink in the
            // analytics log needs an explanation, not silence.
            log.Info($"  [instagram-story] permalink fetch failed for media {mediaId}: {ex.Message}; permalink left empty, upload already recorded");
            return null;
        }
    }
}
