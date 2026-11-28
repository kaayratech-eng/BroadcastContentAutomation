using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

// YouTube Data API v3. Accepts both aspects — YouTube auto-detects Shorts
// from the vertical render's dimensions/duration, there is no separate
// Shorts upload endpoint to target.
sealed class YouTubePublisher(YouTubeConfig cfg, Logger log) : IPublisher
{
    public string Platform => Platforms.YouTube;
    public bool Enabled => cfg.Enabled;
    public int MaxPerRun => cfg.MaxUploadsPerRun;

    // Authorization refreshes silently (no browser) once TokenCapture has run once;
    // built lazily and reused across every video in a run rather than per upload.
    private YouTubeService? _service;
    private UserCredential? _credential;

    public bool Accepts(Sidecar sidecar, string videoPath, out string? reason)
    {
        reason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ClientSecretPath) || string.IsNullOrWhiteSpace(cfg.TokenStorePath))
            return PublishResult.Fatal("YouTube ClientSecretPath or TokenStorePath is not configured.");

        try
        {
            var youtube = await GetServiceAsync();

            // Idempotency: a previous run may have uploaded the video and died
            // before the sidecar was saved. If the recorded video is live,
            // reuse it — do not create a second public video.
            if (!string.IsNullOrWhiteSpace(request.ExistingId))
            {
                var lookup = await TryFindVideoAsync(youtube, request.ExistingId, ct);
                if (lookup == VideoLookup.Found)
                    return PublishResult.Ok(request.ExistingId, $"https://youtu.be/{request.ExistingId}");
                if (lookup == VideoLookup.Unknown)
                    return PublishResult.Retry(
                        $"Could not verify previously recorded YouTube video {request.ExistingId}; " +
                        "not re-uploading blind to avoid a duplicate.");
                log.Info($"  [youtube] previously recorded video {request.ExistingId} not found; uploading fresh.");
            }

            var video = new Video
            {
                Snippet = new VideoSnippet
                {
                    Title = request.Sidecar.Title,
                    Description = request.Sidecar.Description,
                    Tags = request.Sidecar.Tags,
                    CategoryId = cfg.CategoryId,
                    DefaultLanguage = Text.LanguageTag(request.Sidecar.Language),
                    DefaultAudioLanguage = Text.LanguageTag(request.Sidecar.Language),
                },
                Status = new VideoStatus
                {
                    PrivacyStatus = cfg.PrivacyStatus,
                    SelfDeclaredMadeForKids = request.Sidecar.MadeForKids,
                    MadeForKids = request.Sidecar.MadeForKids,
                },
            };

            using var fs = new FileStream(request.VideoPath, FileMode.Open, FileAccess.Read);
            var insert = youtube.Videos.Insert(video, "snippet,status", fs, "video/mp4");
            insert.ChunkSize = ResumableUpload.MinimumChunkSize * 4;

            string? uploadError = null;
            insert.ProgressChanged += p =>
            {
                if (p.Status == UploadStatus.Failed)
                    uploadError = p.Exception?.Message;
            };

            // The VM's uplink to Google can stall mid-chunk (the default 100s
            // HttpClient timeout killed the first teaser upload at exactly 100s).
            // Re-calling UploadAsync on the SAME insert resumes the resumable
            // session from the last server-confirmed chunk instead of starting
            // over, so a stalled chunk costs one retry, not the whole file.
            IUploadProgress? result = null;
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                result = await insert.UploadAsync(ct);
                if (result.Status == UploadStatus.Completed) break;
                uploadError = result.Exception?.Message ?? uploadError;
                log.Warn($"  [youtube] upload attempt {attempt}/4 for {Path.GetFileName(request.VideoPath)} stalled ({uploadError}); resuming from last confirmed chunk.");
            }
            if (result is null || result.Status != UploadStatus.Completed)
                return PublishResult.Retry($"Upload did not complete: {uploadError ?? result?.Exception?.Message}");

            // A completed upload with no video id in the response is not a
            // publish — dereferencing ResponseBody.Id would throw a
            // NullReferenceException into the crash-retry path. Treat it as a
            // clean retry instead; the resumable session may still be
            // recoverable, and no id means nothing was recorded as live.
            var id = insert.ResponseBody?.Id;
            if (string.IsNullOrWhiteSpace(id))
                return PublishResult.Retry("YouTube upload completed but the API returned no video id; not recording a publish.");

            // The video is live NOW. Record the id (sidecar.SaveAsync) before
            // the playlist/thumbnail/caption steps, so a VM restart in that
            // window cannot re-upload a second copy — those steps are
            // post-publish niceties, not part of the publish itself.
            if (request.OnPlatformIdAsync is not null)
            {
                try
                {
                    await request.OnPlatformIdAsync(id);
                }
                catch (Exception ex)
                {
                    // L13: the video is live; only the id-save failed. The
                    // callback already set the id in memory, and Program.cs
                    // saves the sidecar again after PublishAsync returns —
                    // so log it and continue as a success instead of
                    // reporting a live video as a publish failure.
                    log.Error($"  [youtube] video {id} is live but the id could not be recorded ({ex.GetType().Name}: {ex.Message}); continuing — the sidecar save after publish will persist it.");
                }
            }

            log.Info($"  [youtube] uploaded https://youtu.be/{id} ({cfg.PrivacyStatus})");
            if (!string.IsNullOrWhiteSpace(cfg.PlaylistId))
            {
                try
                {
                    var item = new PlaylistItem
                    {
                        Snippet = new PlaylistItemSnippet { PlaylistId = cfg.PlaylistId, ResourceId = new ResourceId { Kind = "youtube#video", VideoId = id } },
                    };
                    await youtube.PlaylistItems.Insert(item, "snippet").ExecuteAsync(ct);
                    log.Info("  [youtube] added to playlist");
                }
                catch (Exception ex) { log.Error($"  [youtube] playlist add failed for {id} (video still published): {ex.Message}"); }
            }

            await TrySetThumbnailAsync(youtube, id, request.VideoPath, request.Sidecar, ct);
            await TryUploadCaptionsAsync(youtube, id, request.VideoPath, request.Sidecar.Language, ct);
            await TryAddShortLinkToLongFormAsync(youtube, id, request, ct);
            // Fix #2: the Short's "Full story here →" comment. Best-effort
            // like the steps above — a failure here never fails the publish.
            // The reconciler re-attempts it on later runs while the Short is
            // still in the watch dir (long-form not published yet, or the
            // token predating the force-ssl scope).
            await EnsurePinnedCommentAsync(youtube, request.VideoPath, request.Sidecar, ct, justPublishedShortId: id);

            return PublishResult.Ok(id, $"https://youtu.be/{id}");
        }
        catch (Google.GoogleApiException ex)
        {
            var code = (int)ex.HttpStatusCode;
            return code is >= 400 and < 500 and not 429
                ? PublishResult.Fatal($"YouTube (HTTP {code}): {ex.Message}")
                : PublishResult.Retry($"YouTube (HTTP {code}): {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return PublishResult.Retry($"YouTube transport error: {ex.Message}");
        }
        catch (Exception ex)
        {
            // L13: anything outside the two known shapes (an
            // InvalidOperationException from credential setup, a
            // NullReferenceException, a JsonException) used to escape to the
            // per-video catch and burn a crash retry. Return a target-level
            // Retry instead; a video already live is re-verified via
            // ExistingId on the next run.
            return PublishResult.Retry(
                $"YouTube unexpected error ({ex.GetType().Name}): {ex.Message}; target will retry, video already live will be re-verified via ExistingId");
        }
    }

    // Custom thumbnail is the single biggest click-through lever on YouTube, so VideoGen
    // generates one for every landscape render; a failure here must not turn an
    // otherwise successful video publish into a retry/abandon. Note: custom thumbnails
    // require a phone-verified channel - Google rejects them with 403 otherwise, which
    // is a one-time manual step in YouTube Studio, not a bug in this code.
    //
    // Thumbnails.Set has no metadata body, so the client library's resumable-upload
    // initiation POST carries an empty body and Google answers HTTP 411 Length
    // Required (verified live 2026-11-13: the same initiation fails for thumbnails
    // while Videos.Insert/Captions.Insert work, because their initiation carries the
    // JSON metadata). A plain media upload (uploadType=media) sets Content-Length
    // and reaches Google's real authorization check instead.
    // A previously recorded video id is verified against the live API before
    // any new upload: Found = reuse it, NotFound = it was deleted (upload
    // fresh), Unknown = the check itself failed (retry later, never upload
    // blind — a second public video is worse than a delayed one).
    private enum VideoLookup { Found, NotFound, Unknown }

    private async Task<VideoLookup> TryFindVideoAsync(YouTubeService youtube, string videoId, CancellationToken ct)
    {
        try
        {
            var list = youtube.Videos.List("status");
            list.Id = videoId;
            var res = await list.ExecuteAsync(ct);
            return res.Items.Count > 0 ? VideoLookup.Found : VideoLookup.NotFound;
        }
        catch (Google.GoogleApiException ex) when ((int)ex.HttpStatusCode == 404)
        {
            return VideoLookup.NotFound;
        }
        catch (Exception ex)
        {
            log.Warn($"  [youtube] could not verify previously recorded video {videoId} ({ex.Message}).");
            return VideoLookup.Unknown;
        }
    }

    private async Task TrySetThumbnailAsync(YouTubeService youtube, string videoId, string videoPath, Sidecar sidecar, CancellationToken ct)
    {
        var thumbPath = ResolveThumbnailPath(videoPath, sidecar);
        if (thumbPath is null)
        {
            // Was a silent return: every Short published with YouTube's
            // auto-picked frame and nothing in the log said why. Say it.
            log.Warn($"  [youtube] no custom thumbnail found for {Path.GetFileName(videoPath)} " +
                     "(checked the video folder, sidecar ThumbnailPath, and the landscape sibling); " +
                     "publishing with YouTube's auto-picked frame.");
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(thumbPath, ct);
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            // youtube.HttpClient carries the OAuth credential, so this request is signed.
            using var response = await youtube.HttpClient.PostAsync(
                $"https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId={Uri.EscapeDataString(videoId)}&uploadType=media",
                content, ct);
            response.EnsureSuccessStatusCode();
            // Name the thumbnail file, not just the fact one was set: the
            // run log is the proof a second Short used its OWN thumbnail
            // (<slug>-en-short2.thumb.jpg) rather than YouTube's auto frame.
            log.Info($"  [youtube] custom thumbnail set ({thumbPath})");
        }
        catch (Exception ex)
        {
            log.Error($"  [youtube] thumbnail upload failed for {videoId} (video still published): {ex.Message}");
        }
    }

    // One-off live re-thumbnailing (fix #3): set a chosen thumbnail
    // variant on an already-live video, e.g. after Rishi picks one of
    // the three variants the --make-thumbnails run produced. Unlike the
    // private TrySetThumbnailAsync above (best-effort inside a publish,
    // errors swallowed, thumbnail resolved from a video path), this one
    // takes the thumbnail file directly and reports success/failure so
    // the --set-thumbnail CLI mode can exit non-zero on failure. The
    // video is verified to exist first — setting a thumbnail on a wrong
    // or deleted id must fail loudly, never silently no-op.
    public async Task<(bool Ok, string? Error)> SetThumbnailAsync(string videoId, string thumbPath, CancellationToken ct)
    {
        if (!File.Exists(thumbPath))
        {
            var missingError = $"thumbnail file not found: {thumbPath}";
            log.Error($"  [youtube] {missingError}");
            return (false, missingError);
        }

        try
        {
            var youtube = await GetServiceAsync();
            return await SetThumbnailWithServiceAsync(youtube, videoId, thumbPath, ct);
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            log.Error($"  [youtube] thumbnail set failed for {videoId}: {error}");
            return (false, error);
        }
    }

    // L26: the lookup + thumbnails.set POST, taking the service so tests
    // can drive it with a canned HttpClient. Returns the API reason on
    // failure instead of a bare bool, so the --set-thumbnail call site
    // can log it at Error level (the old bool lost the reason entirely).
    internal async Task<(bool Ok, string? Error)> SetThumbnailWithServiceAsync(
        YouTubeService youtube, string videoId, string thumbPath, CancellationToken ct)
    {
        if (await TryFindVideoAsync(youtube, videoId, ct) != VideoLookup.Found)
        {
            var error = $"video {videoId} not found on YouTube — thumbnail not set.";
            log.Error($"  [youtube] {error}");
            return (false, error);
        }

        var bytes = await File.ReadAllBytesAsync(thumbPath, ct);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        // Same plain-media upload as TrySetThumbnailAsync above: the
        // client library's resumable initiation POST carries an empty
        // body and Google answers HTTP 411; uploadType=media sets
        // Content-Length and reaches the real authorization check.
        using var response = await youtube.HttpClient.PostAsync(
            $"https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId={Uri.EscapeDataString(videoId)}&uploadType=media",
            content, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Read the API error body instead of EnsureSuccessStatusCode's
            // generic message, so the caller logs the real reason (e.g.
            // a 403 quota/authorization message), not just the status.
            var body = await response.Content.ReadAsStringAsync(ct);
            var error = $"YouTube thumbnails.set returned {(int)response.StatusCode} for {videoId}: {Text.Tail(body, 300)}";
            log.Error($"  [youtube] thumbnail set failed for {videoId}: {error}");
            return (false, error);
        }
        log.Info($"  [youtube] custom thumbnail set for https://youtu.be/{videoId}");
        return (true, null);
    }

    // VideoGen writes .thumb.jpg only for the landscape render, so a Short has
    // none next to it — but the landscape sibling's story art (the deliberately
    // selected scene-1 picture, not the title card) is the right thumbnail for
    // the Short too. Fall back through: video folder, sidecar ThumbnailPath,
    // landscape sibling ("<base>-short.mp4" -> "<base>.thumb.jpg", including the
    // done/<base>/ copy after the long-form has moved), then any
    // .thumb.jpg in the folder sharing the video's base-name prefix.
    //
    // The direct check below already covers a second Short's OWN thumbnail:
    // finish writes "<slug>-en-short2.thumb.jpg" next to the short2 mp4, and
    // Path.ChangeExtension maps "-short2.mp4" to exactly that file — so no
    // special-casing is needed here. Pinned by SecondShortUploadTests.
    private static string? ResolveThumbnailPath(string videoPath, Sidecar sidecar)
    {
        var direct = Path.ChangeExtension(videoPath, ".thumb.jpg");
        if (File.Exists(direct)) return direct;

        if (!string.IsNullOrWhiteSpace(sidecar.ThumbnailPath) && File.Exists(sidecar.ThumbnailPath))
            return sidecar.ThumbnailPath;

        var fileName = Path.GetFileName(videoPath);
        if (fileName.EndsWith("-short.mp4", StringComparison.OrdinalIgnoreCase))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(videoPath) ?? "",
                fileName[..^"-short.mp4".Length] + ".thumb.jpg");
            if (File.Exists(sibling)) return sibling;

            // 2026-11-22 (video #3): the long-form publishes first and is
            // MOVED to done/<base>/ before the Short is processed, so the
            // sibling .thumb.jpg is no longer next to the Short. Check the
            // done folder too, or the Short ships with YouTube's auto frame.
            var baseName = fileName[..^"-short.mp4".Length];
            var doneSibling = Path.Combine(Path.GetDirectoryName(videoPath) ?? "",
                "done", baseName, baseName + ".thumb.jpg");
            if (File.Exists(doneSibling)) return doneSibling;
        }

        var dir = Path.GetDirectoryName(videoPath);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            // Last resort: a .thumb.jpg in the same folder for the SAME
            // video. B14: the old scan matched bidirectionally on prefix,
            // so a "-short-v2" thumb matched a "-short" video (and any
            // other video sharing a prefix). Now the "-short" / "-short-vN"
            // suffix is stripped from BOTH names and the stripped bases
            // must be EQUAL — same video, any cut, never another video.
            var prefix = StripShortSuffix(Path.GetFileNameWithoutExtension(videoPath));
            return Directory.EnumerateFiles(dir, "*.thumb.jpg")
                .FirstOrDefault(f =>
                    string.Equals(StripShortSuffix(Path.GetFileNameWithoutExtension(f)), prefix, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    // "slug-en-short" -> "slug-en"; anything else unchanged. Used by the
    // thumbnail prefix scan (B14): stripping ONLY the exact "-short"
    // suffix means a "-short-v2" thumb no longer matches a "-short"
    // video (its base keeps "-short-v2"), while a Short still falls back
    // to its own long-form's thumb and vice versa.
    internal static string StripShortSuffix(string baseName)
    {
        if (baseName.EndsWith("-short", StringComparison.OrdinalIgnoreCase))
            return baseName[..^"-short".Length];
        return baseName;
    }

    // Real caption track alongside the burned-in drawtext subtitles - gives platforms
    // and accessibility tools indexable/toggleable caption text. Best-effort for the
    // same reason as the thumbnail above.
    private async Task TryUploadCaptionsAsync(YouTubeService youtube, string videoId, string videoPath, string language, CancellationToken ct)
    {
        var srtPath = Path.ChangeExtension(videoPath, ".srt");
        if (!File.Exists(srtPath)) return;

        try
        {
            var caption = new Caption
            {
                Snippet = new CaptionSnippet
                {
                    VideoId = videoId,
                    Language = Text.LanguageTag(language),
                    Name = "",
                    IsDraft = false,
                },
            };

            using var stream = new FileStream(srtPath, FileMode.Open, FileAccess.Read);
            var insert = youtube.Captions.Insert(caption, "snippet", stream, "application/octet-stream");
            await insert.UploadAsync(ct);
            log.Info("  [youtube] captions uploaded");
        }
        catch (Exception ex)
        {
            log.Error($"  [youtube] caption upload failed for {videoId} (video still published): {ex.Message}");
        }
    }

    // Long-form -> Short cross-link (Rishi 2026-11-22, review SEO #3). When a
    // Short publishes, its long-form (uploaded first, so already live) gets a
    // "Watch the 60-second version" line appended to its YouTube description.
    // The Short's own "Watch the full video" line is baked in at its upload
    // (AppendTrendlineFooterAsync), so this is the only direction that needs a
    // post-publish update. Best-effort like the thumbnail/captions: a failure
    // here must not turn a successful Short publish into a retry. The update
    // goes through EnsureLinkInDescriptionAsync, which reads YouTube's live
    // description first (the source of truth) and only changes the
    // description — the live title/tags/category are echoed back, never the
    // sidecar's stale values, so a manual Studio edit is never clobbered
    // (review R4). The same helper backs the per-run reconciliation below,
    // which covers the Short-first ordering this one-shot call cannot.
    private async Task TryAddShortLinkToLongFormAsync(YouTubeService youtube, string shortId, PublishRequest request, CancellationToken ct)
    {
        if (!request.Sidecar.Channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase))
            return;
        if (!request.Sidecar.IsVertical)
            return;

        var fileName = Path.GetFileNameWithoutExtension(request.VideoPath);
        if (!fileName.EndsWith("-short", StringComparison.OrdinalIgnoreCase))
            return;
        var longFormSlug = fileName[..^"-short".Length];
        var channelDir = Path.GetDirectoryName(request.VideoPath);
        if (channelDir is null)
            return;

        // The long-form was uploaded first and moved to done/<slug>/<slug>.json;
        // the watch-dir copy is the fallback for one that has not moved yet.
        Sidecar? longForm = null;
        string? longFormVideoPath = null;
        foreach (var candidate in new[]
        {
            Path.Combine(channelDir, "done", longFormSlug, longFormSlug + ".mp4"),
            Path.Combine(channelDir, longFormSlug + ".mp4"),
        })
        {
            // L82: wire the helper's log sink into the run log.
            longForm = await Sidecar.LoadAsync(candidate, msg => log.Warn(msg));
            if (longForm is not null) { longFormVideoPath = candidate; break; }
        }
        if (longForm is null || longFormVideoPath is null)
        {
            log.Info("  [youtube] no long-form sidecar found; skipping the Short cross-link update.");
            return;
        }
        if (!longForm.Publications.TryGetValue(Platforms.YouTube, out var pub)
            || pub.Status != PublishStatus.Published
            || string.IsNullOrWhiteSpace(pub.Id))
        {
            log.Info("  [youtube] long-form is not published on YouTube yet; skipping the Short cross-link update.");
            return;
        }

        await EnsureLinkInDescriptionAsync(youtube, longFormVideoPath, longForm,
            ShortVersionLinkLine(shortId), $"https://youtu.be/{shortId}", ct);
    }

    // Cross-link line formats (single source of truth — Program.cs builds
    // the Short's "Watch the full video" footer line from the same helper,
    // so the footer and the reconciliation can never drift apart).
    // Emoji-prefixed (Rishi 2026-11-25: descriptions must ALWAYS be catchy
    // and carry emojis wherever possible) — the emoji sits BEFORE the
    // wording, so the plain "Watch the …: https://youtu.be/…" substring
    // stays intact for the existing tests and for
    // EnsureLinkInDescriptionAsync's Contains(linkUrl) check, and a
    // pre-emoji line already live on YouTube is still recognised by its URL.
    internal static string FullVideoLinkLine(string longFormId) => $"🎬 Watch the full video: https://youtu.be/{longFormId}";
    internal static string ShortVersionLinkLine(string shortId) => $"⚡ Watch the 60-second version: https://youtu.be/{shortId}";

    // Fix #2 (Rishi 2026-11-26, Shorts→long-form bridge): the Short's
    // top-level comment. Exact wording, single source of truth — the
    // uploader posts this verbatim, never a generated variant.
    internal static string FullStoryCommentText(string longFormId) => $"Full story here → https://youtu.be/{longFormId}";

    // Fix #2: post the "Full story here →" comment on a Short, best-effort.
    // The Short's description link is not clickable in the Shorts feed and
    // the end card has no tap target there, so the comment is the second
    // clickable route to the long-form (the first is the Related Video link,
    // set in Studio — the API has no endpoint for it).
    //
    // PINNING VERDICT (verified 2026-11-26, do not re-litigate by guessing):
    // the YouTube Data API v3 has NO endpoint that pins a comment. The
    // comments resource exposes only list/insert/update/delete/
    // setModerationStatus and commentThreads only list/insert — confirmed
    // against the installed Google.Apis.YouTube.v3 1.68.0 client library and
    // the API reference. Pinning is a Studio-only action (video → Comments
    // → ⋮ → Pin, ~30s per Short). What the API CAN do is post the comment
    // as the channel owner, which carries the owner badge and typically
    // bubbles near the top of the comment list — so this posts it and logs
    // the Studio pin step as the remaining manual action. Do NOT attempt
    // to "pin" via setModerationStatus: that only sets heldForReview /
    // published / rejected, it does not pin anything.
    //
    // Runs at Short publish time AND from the reconciler on every later run
    // while the Short is still in the watch dir, so a first attempt missed
    // (long-form not published yet, or the token predating the force-ssl
    // scope) heals itself on a later run. One call per Short ever:
    // Sidecar.PinnedCommentPosted is set only after a successful insert, so
    // retries never spam duplicate comments. A failure here NEVER fails the
    // publish or the reconciliation — it logs and returns false.
    internal async Task<bool> EnsurePinnedCommentAsync(YouTubeService youtube, string videoPath, Sidecar sidecar, CancellationToken ct,
        string? justPublishedShortId = null)
    {
        // L33: the Short id, declared outside the try so the catch lines
        // can name it too. At publish time PublishAsync passes it
        // directly: the sidecar's Publication status is only set to
        // Published by Program.cs AFTER PublishAsync returns, so reading
        // the sidecar here would always no-op at publish time. The
        // reconciler path (later runs) reads it from the sidecar.
        string? shortId = justPublishedShortId;
        try
        {
            // L33: the early returns below used to be silent — the caller
            // could not distinguish "not a Short" from "already posted".
            // One Info line each, naming the video file + Short id (when
            // known) + reason + next action.
            if (!sidecar.Channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase))
            {
                log.Info($"  [youtube] skipping Short comment for {Path.GetFileName(videoPath)}: channel is '{sidecar.Channel}', not trendlinenow; never applicable");
                return false;
            }
            if (!sidecar.IsVertical)
            {
                log.Info($"  [youtube] skipping Short comment for {Path.GetFileName(videoPath)}: not a vertical Short; never applicable");
                return false;
            }
            if (sidecar.PinnedCommentPosted)
            {
                var postedId = sidecar.Publications.TryGetValue(Platforms.YouTube, out var postedPub) ? postedPub.Id : null;
                log.Info($"  [youtube] skipping Short comment for {Path.GetFileName(videoPath)} (Short {postedId ?? "unknown id"}): comment already posted; will not re-post");
                return false;
            }

            // The Short's own YouTube id (see above): the reconciler path
            // reads it from the sidecar when PublishAsync did not pass it.
            if (shortId is null)
            {
                if (!sidecar.Publications.TryGetValue(Platforms.YouTube, out var pub)
                    || pub.Status != PublishStatus.Published
                    || string.IsNullOrWhiteSpace(pub.Id))
                {
                    log.Info($"  [youtube] skipping Short comment for {Path.GetFileName(videoPath)}: Short is not published on YouTube yet; will retry on reconciler runs");
                    return false;
                }
                shortId = pub.Id;
            }

            var fileName = Path.GetFileNameWithoutExtension(videoPath);
            if (!fileName.EndsWith("-short", StringComparison.OrdinalIgnoreCase))
            {
                log.Info($"  [youtube] skipping Short comment for {Path.GetFileName(videoPath)} (Short {shortId}): not a Short filename; never applicable");
                return false;
            }

            var sibling = await FindSiblingSidecarAsync(videoPath, wantShort: false, msg => log.Warn(msg));
            if (sibling is null)
            {
                log.Info("  [youtube] no long-form sidecar found; skipping the Short comment.");
                return false;
            }
            var (longForm, _) = sibling.Value;
            if (!longForm.Publications.TryGetValue(Platforms.YouTube, out var lfPub)
                || lfPub.Status != PublishStatus.Published
                || string.IsNullOrWhiteSpace(lfPub.Id))
            {
                log.Info("  [youtube] long-form is not published on YouTube yet; skipping the Short comment.");
                return false;
            }

            var thread = new CommentThread
            {
                Snippet = new CommentThreadSnippet
                {
                    VideoId = shortId,
                    TopLevelComment = new Comment
                    {
                        Snippet = new CommentSnippet { TextOriginal = FullStoryCommentText(lfPub.Id) },
                    },
                },
            };
            // commentThreads.insert costs ~50 quota units. One call per
            // Short ever (see PinnedCommentPosted above).
            await youtube.CommentThreads.Insert(thread, "snippet").ExecuteAsync(ct);

            // Record success only after the insert returned, so a failure
            // above leaves the flag false and a later run retries instead of
            // believing the comment is posted.
            sidecar.PinnedCommentPosted = true;
            // L82: wire the helper's log sink into the run log.
            await sidecar.SaveAsync(videoPath, msg => log.Warn(msg));
            log.Info($"  [youtube] posted the \"Full story here\" comment on the Short {shortId} " +
                     "(pin it in YouTube Studio → Comments → ⋮ → Pin; the API cannot pin).");
            return true;
        }
        catch (Google.GoogleApiException ex) when ((int)ex.HttpStatusCode == 403)
        {
            // The usual cause: the stored token was captured before the
            // youtube.force-ssl scope was added (fix #2). Re-capture the
            // token via TokenCapture; the next run then posts the comment.
            // Never a publish failure — the video is already live.
            log.Error($"  [youtube] Short comment not posted for {shortId} (HTTP 403 — the YouTube token lacks the " +
                      "youtube.force-ssl scope; re-authorize via TokenCapture and the next run will post it).");
            return false;
        }
        catch (Exception ex)
        {
            log.Error($"  [youtube] Short comment failed for {shortId} (video still published): {ex.Message}");
            return false;
        }
    }

    // R3 (fix sequence #2, ITEM 5): reconcile the Short<->long-form
    // cross-link on EVERY uploader run instead of once. The one-shot paths
    // (footer at resolve time, TryAddShortLinkToLongFormAsync at Short
    // publish) both silently no-op when the other side is not Published
    // yet, and the footer is then persisted without the link and never
    // revisited — a Short-first ordering left NEITHER direction linked.
    // Program.cs calls this after a video's targets are processed; it only
    // patches descriptions of already-published videos, never re-uploads
    // or re-publishes anything, and is a no-op when both links are live.
    public async Task ReconcileCrossLinksAsync(string videoPath, Sidecar sidecar, CancellationToken ct)
    {
        // L34: the early returns below used to be silent — a reconcile
        // that no-ops every run left no trace of why.
        if (!sidecar.Channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase))
        {
            log.Info($"  [youtube] cross-link skipped for {Path.GetFileName(videoPath)}: channel is '{sidecar.Channel}', not trendlinenow; never applicable");
            return;
        }
        if (!sidecar.Publications.TryGetValue(Platforms.YouTube, out var pub)
            || pub.Status != PublishStatus.Published
            || string.IsNullOrWhiteSpace(pub.Id))
        {
            log.Info($"  [youtube] cross-link skipped for {Path.GetFileName(videoPath)}: not published on YouTube yet; will retry next run");
            return;
        }

        YouTubeService youtube;
        try
        {
            youtube = await GetServiceAsync();
        }
        catch (Exception ex)
        {
            log.Warn($"  [youtube] cross-link reconciliation skipped for {Path.GetFileName(videoPath)} (could not authorize YouTube: {ex.Message}); descriptions unchanged, will retry next run");
            return;
        }

        try
        {
            await ReconcileCrossLinksWithServiceAsync(youtube, videoPath, sidecar, ct);
        }
        catch (Exception ex)
        {
            // Best-effort: a reconciliation failure must never fail the
            // video's run or move it to failed/.
            log.Warn($"  [youtube] cross-link reconciliation failed for {Path.GetFileName(videoPath)} ({pub.Id}) (descriptions unchanged): {ex.Message}; will retry next run");
        }
    }

    // Core reconciliation, taking the service so tests can drive it with a
    // canned HttpClient (the sandbox blocks direct TCP from .NET, and the
    // live path needs OAuth). Both directions, from either side: a Short
    // reconciles its own "Watch the full video" line and its long-form's
    // "Watch the 60-second version" line; a long-form does the same pair
    // the other way round. Either side publishing later heals the pair.
    internal async Task ReconcileCrossLinksWithServiceAsync(YouTubeService youtube, string videoPath, Sidecar sidecar, CancellationToken ct)
    {
        // L34: same silent early returns as ReconcileCrossLinksAsync above
        // (this overload is the testable core; the same skip must be
        // visible whichever entry point runs).
        if (!sidecar.Channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase))
        {
            log.Info($"  [youtube] cross-link skipped for {Path.GetFileName(videoPath)}: channel is '{sidecar.Channel}', not trendlinenow; never applicable");
            return;
        }
        if (!sidecar.Publications.TryGetValue(Platforms.YouTube, out var pub)
            || pub.Status != PublishStatus.Published
            || string.IsNullOrWhiteSpace(pub.Id))
        {
            log.Info($"  [youtube] cross-link skipped for {Path.GetFileName(videoPath)}: not published on YouTube yet; will retry next run");
            return;
        }
        var thisId = pub.Id;

        if (sidecar.IsVertical)
        {
            var sibling = await FindSiblingSidecarAsync(videoPath, wantShort: false, msg => log.Warn(msg));
            if (sibling is null)
            {
                log.Info("  [youtube] no long-form sidecar found; cross-link reconciliation skipped for now.");
                return;
            }
            var (longForm, longFormPath) = sibling.Value;
            if (!longForm.Publications.TryGetValue(Platforms.YouTube, out var lfPub)
                || lfPub.Status != PublishStatus.Published
                || string.IsNullOrWhiteSpace(lfPub.Id))
            {
                log.Info("  [youtube] long-form is not published on YouTube yet; cross-link reconciliation skipped for now.");
                return;
            }
            await EnsureLinkInDescriptionAsync(youtube, videoPath, sidecar,
                FullVideoLinkLine(lfPub.Id), $"https://youtu.be/{lfPub.Id}", ct);
            await EnsureLinkInDescriptionAsync(youtube, longFormPath, longForm,
                ShortVersionLinkLine(thisId), $"https://youtu.be/{thisId}", ct);
            // Fix #2: the Short's "Full story here →" comment, retried on
            // every run until posted (first attempt may have missed because
            // the long-form was not published yet, or the token predated the
            // force-ssl scope). Best-effort; never fails the reconciliation.
            await EnsurePinnedCommentAsync(youtube, videoPath, sidecar, ct);
        }
        else
        {
            var sibling = await FindSiblingSidecarAsync(videoPath, wantShort: true, msg => log.Warn(msg));
            if (sibling is null)
            {
                log.Info("  [youtube] no Short sidecar found; cross-link reconciliation skipped for now.");
                return;
            }
            var (shortSidecar, shortPath) = sibling.Value;
            if (!shortSidecar.Publications.TryGetValue(Platforms.YouTube, out var shPub)
                || shPub.Status != PublishStatus.Published
                || string.IsNullOrWhiteSpace(shPub.Id))
            {
                log.Info("  [youtube] Short is not published on YouTube yet; cross-link reconciliation skipped for now.");
                return;
            }
            await EnsureLinkInDescriptionAsync(youtube, shortPath, shortSidecar,
                FullVideoLinkLine(thisId), $"https://youtu.be/{thisId}", ct);
            await EnsureLinkInDescriptionAsync(youtube, videoPath, sidecar,
                ShortVersionLinkLine(shPub.Id), $"https://youtu.be/{shPub.Id}", ct);
        }
    }

    // The sibling sidecar of a video: "<slug>-en.mp4" <-> "<slug>-en-short.mp4"
    // (or "<slug>-en-short2.mp4" for the fix-#5 second Short), in the watch
    // dir or already moved to done/<slug>/. Returns null when the sibling
    // video has not been rendered/published yet.
    private static async Task<(Sidecar Sidecar, string VideoPath)?> FindSiblingSidecarAsync(string videoPath, bool wantShort, Action<string>? log = null)
    {
        var fileName = Path.GetFileNameWithoutExtension(videoPath);
        var channelDir = Path.GetDirectoryName(videoPath);
        if (channelDir is null)
            return null;

        string siblingSlug;
        if (wantShort)
        {
            // A Short (main or second) has no Short sibling to find.
            if (ShortVideo.IsShort(videoPath))
                return null;
            siblingSlug = fileName + ShortVideo.Suffix;
        }
        else
        {
            // Fix #5: a second Short ("-short2") is also a Short — its
            // long-form sibling is the same "<slug>-en".
            var longFormSlug = ShortVideo.LongFormSlug(fileName);
            if (longFormSlug is null)
                return null;
            siblingSlug = longFormSlug;
        }

        foreach (var candidate in new[]
        {
            Path.Combine(channelDir, siblingSlug + ".mp4"),
            Path.Combine(channelDir, "done", siblingSlug, siblingSlug + ".mp4"),
        })
        {
            // L82: wire the helper's log sink into the run log.
            var sibling = await Sidecar.LoadAsync(candidate, log);
            if (sibling is not null)
                return (sibling, candidate);
        }
        return null;
    }

    // YouTube's live snippet for a video id, or null when the video is gone
    // or the read failed (the caller then leaves the description alone).
    private async Task<VideoSnippet?> FetchLiveSnippetAsync(YouTubeService youtube, string videoId, CancellationToken ct)
    {
        try
        {
            var list = youtube.Videos.List("snippet");
            list.Id = videoId;
            var res = await list.ExecuteAsync(ct);
            if (res.Items.Count == 0)
            {
                log.Info($"  [youtube] video {videoId} no longer on YouTube; skipping the description update.");
                return null;
            }
            return res.Items[0].Snippet;
        }
        catch (Exception ex)
        {
            log.Error($"  [youtube] could not read the live description for {videoId} (description unchanged): {ex.Message}");
            return null;
        }
    }

    // Ensure a video's live YouTube description carries linkLine, and keep
    // its sidecar in sync. Idempotent: the link URL is the marker — when
    // it is already live, no update is sent (and a sidecar that is behind
    // is synced from the live description instead). Only the description
    // value ever changes: videos.update replaces the whole snippet, so the
    // live title/tags/category/language are echoed back verbatim — sending
    // a description-only body would wipe them, and re-sending the sidecar's
    // stale values would clobber a manual Studio edit (review R4). Returns
    // true only when the live description was actually updated.
    internal async Task<bool> EnsureLinkInDescriptionAsync(YouTubeService youtube, string videoPath, Sidecar sidecar,
        string linkLine, string linkUrl, CancellationToken ct)
    {
        // L34: the false returns below used to be silent — a reconcile
        // that no-ops every run left no trace of why.
        if (!sidecar.Publications.TryGetValue(Platforms.YouTube, out var pub)
            || pub.Status != PublishStatus.Published
            || string.IsNullOrWhiteSpace(pub.Id))
        {
            log.Info($"  [youtube] cross-link skipped for {Path.GetFileName(videoPath)}: not published on YouTube yet; will retry next run");
            return false;
        }

        var live = await FetchLiveSnippetAsync(youtube, pub.Id, ct);
        if (live is null)
        {
            log.Info($"  [youtube] cross-link skipped for {Path.GetFileName(videoPath)} ({pub.Id}): could not read the live description; descriptions unchanged, will retry next run");
            return false;
        }
        var liveDescription = live.Description ?? "";

        if (liveDescription.Contains(linkUrl, StringComparison.Ordinal))
        {
            // Already live. Sync the sidecar so a later run agrees, then stop.
            if (!sidecar.Description.Contains(linkUrl, StringComparison.Ordinal))
            {
                sidecar.Description = liveDescription;
                // L82: wire the helper's log sink into the run log.
                await sidecar.SaveAsync(videoPath, msg => log.Warn(msg));
                log.Info($"  [youtube] synced the sidecar description for {Path.GetFileName(videoPath)} (cross-link already live).");
            }
            return false;
        }

        var newDescription = liveDescription.TrimEnd() + "\n\n" + linkLine;
        try
        {
            var update = new Video
            {
                Id = pub.Id,
                Snippet = new VideoSnippet
                {
                    Title = live.Title,
                    Description = newDescription,
                    Tags = live.Tags,
                    CategoryId = live.CategoryId,
                    DefaultLanguage = live.DefaultLanguage,
                    DefaultAudioLanguage = live.DefaultAudioLanguage,
                },
            };
            await youtube.Videos.Update(update, "snippet").ExecuteAsync(ct);
        }
        catch (Exception ex)
        {
            log.Error($"  [youtube] cross-link update failed for {pub.Id} (description unchanged on YouTube): {ex.Message}");
            return false;
        }

        // Update succeeded on YouTube first; only then record it, so a
        // failure above leaves the sidecar without the link and a later run
        // retries instead of believing it is done.
        sidecar.Description = newDescription;
        // L82: wire the helper's log sink into the run log.
        await sidecar.SaveAsync(videoPath, msg => log.Warn(msg));
        log.Info($"  [youtube] added the cross-link to the description of {pub.Id} ({linkUrl}).");
        return true;
    }

    private async Task<YouTubeService> GetServiceAsync()
    {
        if (_service is not null) return _service;

        var credential = await GetCredentialAsync();

        _service = new YouTubeService(new Google.Apis.Services.BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "GiggleGarden Uploader",
        });
        // The Google client ships a 100s HttpClient timeout; a single slow or
        // stalled chunk on this VM's uplink aborts the whole upload at 100s.
        // 10 minutes per chunk request is ample even at very low throughput
        // (a 1MB chunk at 10 minutes is ~1.7 KB/s), and the resume loop above
        // recovers if a chunk still fails.
        _service.HttpClient.Timeout = TimeSpan.FromMinutes(10);
        return _service;
    }

    // The one credential for this channel, shared by the Data API service
    // above and the Analytics API service below (fix #4). Extracted from
    // GetServiceAsync so the scope list lives in exactly one place.
    public async Task<UserCredential> GetCredentialAsync()
    {
        if (_credential is not null) return _credential;

        using var stream = new FileStream(cfg.ClientSecretPath, FileMode.Open, FileAccess.Read);
        _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            // yt-analytics.readonly must match TokenCapture so a token
            // captured with analytics scope is not downgraded on refresh.
            // youtube.force-ssl (fix #2, 2026-11-26) is the scope
            // commentThreads.insert needs for the Short's "Full story here"
            // comment. A token captured before this scope was added 403s on
            // the comment call until it is re-captured — the comment step
            // degrades gracefully to a log line in that case (see
            // EnsurePinnedCommentAsync); it never fails the publish.
            // Full `youtube` scope added 2026-11-26 (fix #4 follow-up from
            // fix #3): TokenCapture already lists it, and this array had
            // only four scopes — a fresh capture through the Uploader path
            // would have silently downgraded the token and broken playlist
            // linking (playlists.insert needs the full scope). A stored
            // token is reused as-is, so this changes no live behaviour.
            new[] { YouTubeService.Scope.YoutubeUpload, YouTubeService.Scope.YoutubeReadonly, "https://www.googleapis.com/auth/yt-analytics.readonly", "https://www.googleapis.com/auth/youtube.force-ssl", "https://www.googleapis.com/auth/youtube" },
            "gigglegarden-channel-owner",                        // must match TokenCapture
            CancellationToken.None,
            new FileDataStore(cfg.TokenStorePath, fullPath: true));
        return _credential;
    }
}
