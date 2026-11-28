// Spotlight Now Daily Uploader (.NET 8)
// Watches D:\Business\Videos for .mp4 files and publishes each one to every
// platform its sidecar declares as a target (YouTube always; Instagram,
// Facebook, TikTok for vertical shorts once those platforms are enabled and
// configured). Publishing is per-target and resumable: a video only moves to
// \done once every target has reached a terminal state (see Sidecar.cs).
//
// Metadata resolution order per video:
//   1. Sidecar JSON  (myvideo.mp4 -> myvideo.json)  — used as-is if present
//   2. Auto-generate — pulls trending kids' video data from the YouTube API,
//      sends it + the video filename to a free Groq/Gemini API, receives
//      title/description/tags in English + Hindi + Punjabi. Defaults to a
//      YouTube-only target since the source video's aspect ratio is unknown.
//
// Scheduled via Windows Task Scheduler (see README).

using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;
using GiggleGarden.Uploader;
using GiggleGarden.Uploader.Publishing;
using Microsoft.Extensions.Configuration;

// L25: the config load used to throw before any logger existed, so a bad
// appsettings.json left no entry in any upload-*.log — only a console
// stack trace. Load the config first; on failure log through a fallback
// logger (the configured log dir is unknown while the config is bad) and
// exit 1 instead of throwing.
AppConfig cfg;
try
{
    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile("appsettings.Local.json", optional: true)
        .AddEnvironmentVariables("GIGGLE_")
        .Build();

    cfg = config.Get<AppConfig>() ?? throw new InvalidOperationException("appsettings.json missing or invalid");
    // L84: validate required fields now, before the lock/scan — a named
    // field error here exits 1 instead of dying later in an unrelated error.
    cfg.Validate();
}
catch (Exception ex)
{
    var fallbackLog = new Logger(Path.Combine(AppContext.BaseDirectory, "logs"));
    fallbackLog.Error("appsettings.json missing or invalid; fix appsettings.json and re-run", ex);
    return 1;
}

var log = new Logger(cfg.LogDirectory);

// B28: single-instance lock — two concurrent uploader runs can both read
// Pending and both publish (the ExistingId check cannot help: neither has
// saved an id yet). Held for the whole run; a second process exits
// without touching anything. The lock is kernel-held, so a killed run
// releases it automatically.
using var instanceLock = SingleInstanceLock.TryAcquire(Path.Combine(cfg.LogDirectory, "uploader.lock"));
if (instanceLock is null)
{
    // L1: name the holder (pid + age) so a bounce is greppable and a
    // human can tell whether the holder is alive or stale.
    var holder = SingleInstanceLock.ReadHolder(Path.Combine(cfg.LogDirectory, "uploader.lock"));
    if (holder is not null)
    {
        var age = DateTimeOffset.UtcNow - holder.StartedUtc;
        log.Warn($"uploader lock held by pid {holder.Pid} (age {age.TotalSeconds:0}s, since {holder.StartedUtc:O}); exiting without publishing, will retry on next scheduled tick");
    }
    else
    {
        log.Warn("uploader lock held (holder info unavailable); exiting without publishing, will retry on next scheduled tick");
    }
    // Exit code 2 (documented): lock bounce. Not a publish failure, but
    // the run did nothing — exiting 0 would be indistinguishable from a
    // clean run (this is how the 19:54 EST Messi retry bounce looked
    // like success). Schedulers treat 2 as "busy, retry later".
    return 2;
}

var setThumbIdx = Array.IndexOf(args, "--set-thumbnail");
if (setThumbIdx >= 0)
{
    // One-off live re-thumbnailing (fix #3): set a chosen thumbnail
    // variant on a live video via thumbnails.set (TrySetThumbnailAsync).
    // The parent runs this AFTER Rishi picks a variant; it never runs
    // as part of a publish run and never re-uploads the video.
    // Usage: dotnet Uploader.dll --set-thumbnail <videoId> <imagePath>
    if (setThumbIdx + 2 >= args.Length)
    {
        log.Error("Usage: Uploader.dll --set-thumbnail <videoId> <imagePath>");
        return 1;
    }
    var videoId = args[setThumbIdx + 1];
    var imagePath = args[setThumbIdx + 2];
    if (!File.Exists(imagePath))
    {
        log.Error($"--set-thumbnail: no such image file: {imagePath}");
        return 1;
    }
    if (!cfg.Channels.TryGetValue("trendlinenow", out var channelConfig))
    {
        log.Error("--set-thumbnail: channel 'trendlinenow' is not configured.");
        return 1;
    }
    var youtube = new YouTubePublisher(channelConfig.YouTube, log);
    var (ok, error) = await youtube.SetThumbnailAsync(videoId, imagePath, CancellationToken.None);
    if (ok)
        log.Info($"--set-thumbnail: set thumbnail for https://youtu.be/{videoId} from {imagePath}.");
    else
        // L26: a failure used to be logged at Info with no reason (the
        // API error lived only inside the publisher, behind a bare bool),
        // so an ERROR-grep missed it. Log the reason at Error level.
        log.Error($"--set-thumbnail: FAILED to set thumbnail for https://youtu.be/{videoId}: {error}; thumbnail unchanged, retry with a valid image/video id");
    return ok ? 0 : 1;
}

var analyticsIdx = Array.IndexOf(args, "--analytics-pull");
if (analyticsIdx >= 0)
{
    // Weekly analytics pull (fix #4): real numbers into analytics.jsonl
    // from the YouTube Analytics + Data APIs, plus the weekly brief.
    // Usage: dotnet Uploader.dll --analytics-pull
    if (!cfg.Channels.TryGetValue("trendlinenow", out var analyticsChannel))
    {
        log.Error("--analytics-pull: channel 'trendlinenow' is not configured.");
        return 1;
    }
    var pull = new AnalyticsPull(analyticsChannel.YouTube, log);
    var pulled = await pull.RunAsync(cfg, CancellationToken.None);
    return pulled ? 0 : 1;
}

try
{
    var videosWithNoPublish = await RunAsync(cfg, log);
    if (videosWithNoPublish > 0)
    {
        // A run that publishes nothing is a failure, not a success — the exit
        // code must say so, so the scheduler/monitoring can tell it apart from
        // a clean run (previously every run exited 0, even one where every
        // target was Abandoned). The detailed per-video summary (which videos
        // + why, L28) is logged by RunAsync itself before it returns.
        return 1;
    }
    return 0;
}
catch (Exception ex)
{
    log.Error($"FATAL: {ex}");
    return 1;
}

// Returns the number of videos that finished processing with no published
// target on any platform (used for the exit code — see Main above).
static async Task<int> RunAsync(AppConfig cfg, Logger log)
{
    var videosWithNoPublish = 0;
    // L28: the videos + first error behind the count above, for the
    // end-of-run summary — a monitor reading the summary line alone must
    // be able to tell which videos failed and why.
    var noPublishDetails = new List<(string File, string Reason)>();
    if (!string.IsNullOrWhiteSpace(cfg.TopicHistoryDirectory))
        TopicHistory.DirectoryOverride = cfg.TopicHistoryDirectory;
    if (cfg.Channels.Count == 0)
        throw new InvalidOperationException("appsettings.json has no Channels configured.");

    // Every channel we might discover a video under - the configured channels plus
    // DefaultChannel (covers a manually dropped video with no channel signal of
    // its own, in case DefaultChannel isn't itself a Channels key).
    var channels = cfg.Channels.Keys.Append(cfg.DefaultChannel)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    foreach (var channel in channels)
    {
        Directory.CreateDirectory(cfg.DoneDirFor(channel));
        Directory.CreateDirectory(cfg.FailedDirFor(channel));
    }

    // One publisher set per channel — each channel carries its own credentials,
    // so two channels can publish in the same run without sharing tokens.
    var publishersByChannel = cfg.Channels.ToDictionary(
        kv => kv.Key,
        kv => (IReadOnlyDictionary<string, IPublisher>)new IPublisher[]
        {
            new YouTubePublisher(kv.Value.YouTube, log),
            new InstagramPublisher(kv.Value.Instagram, log),
            new FacebookPublisher(kv.Value.Facebook, log),
            new TikTokPublisher(kv.Value.TikTok, log),
            new InstagramStoryPublisher(kv.Value.Instagram, log),
            new FacebookStoryPublisher(kv.Value.Facebook, log),
        }.ToDictionary(p => p.Platform, StringComparer.OrdinalIgnoreCase),
        StringComparer.OrdinalIgnoreCase);

    // Quota guard, tracked per (channel, platform) across the whole run rather
    // than per video — a channel/platform pair that hits its MaxPerRun stops
    // accepting new dispatches, but earlier videos and the other channel are
    // unaffected.
    var dispatchedThisRun = new Dictionary<(string Channel, string Platform), int>();
    int Dispatched(string channel, string platform) =>
        dispatchedThisRun.TryGetValue((channel, platform), out var n) ? n : 0;

    // Fix #5: today's YouTube publish count for the second-Short quota
    // guard, loaded lazily on first need (the analytics log is small, and
    // most runs have no second Short at all). One row per YouTube publish,
    // dated by PublishedAt (UTC); see YouTubeShort2QuotaAllows below.
    List<GiggleGarden.Shared.AnalyticsEntry>? analyticsToday = null;
    async Task<int> YouTubePublishesTodayAsync()
    {
        if (analyticsToday is null)
        {
            try
            {
                var entries = await GiggleGarden.Shared.AnalyticsLog.LoadAsync(
                    Path.Combine(cfg.WatchDirectory, "analytics"),
                    msg => log.Warn(msg));
                var today = DateTime.UtcNow.Date;
                analyticsToday = entries.Where(e => e.PublishedAt.UtcDateTime.Date == today).ToList();
            }
            catch (Exception ex)
            {
                // Fail open: a missing/unreadable analytics log must not
                // block the second Short — the API's own 403 quotaExceeded
                // is the backstop, and the run continues.
                log.Warn($"  [youtube] second-Short quota check: could not read today's analytics rows ({ex.Message}); assuming 0 published today");
                analyticsToday = [];
            }
        }
        return analyticsToday.Count;
    }

    // Which channel a video belongs to is now the folder it was physically found
    // in (WatchDirectory\<channel>\*.mp4), not sidecar.Channel - in every real
    // generated-content case they already agree (VideoGen always writes
    // Channel = profile.Id into the same folder it renders to), and this makes
    // the channel available even before a sidecar is resolved.
    var allFiles = channels
        .Select(channel => (Channel: channel, Dir: Path.Combine(cfg.WatchDirectory, channel)))
        .Where(c => Directory.Exists(c.Dir))
        .SelectMany(c => Directory.GetFiles(c.Dir, "*.mp4").Select(p => (Path: p, c.Channel)))
        // Story cuts (Rishi 2026-11-26) are derivatives of a Short, not
        // videos: they have no sidecar, and a trendlinenow file without
        // one is moved straight to failed/ (B21). Never scan them in.
        .Where(v => !StoryCut.IsStoryCut(v.Path))
        .ToList();

    // B27: files younger than MinFileAgeSeconds are skipped, and each one
    // is logged by name — the old silent Where() made an all-too-new run
    // just print "No videos to process." with no hint a fresh render was
    // sitting in the watch dir.
    var agedFiles = FilterByMinAge(allFiles, cfg.MinFileAgeSeconds, DateTime.UtcNow,
            msg => log.Info($"  [scan] {msg}"));
    var tooNewCount = allFiles.Count - agedFiles.Count;

    // L29: a video inside its crash-retry backoff used to be filtered
    // silently, so a run looked empty with no hint why. Log each one
    // with its backoff expiry, and make the empty-run line state the
    // breakdown (ready / too-new / in backoff).
    var inBackoffCount = 0;
    var videos = new List<(string Path, string Channel)>();
    foreach (var v in agedFiles)
    {
        if (IsPastCrashRetryBackoff(v.Path, cfg, log))
            videos.Add(v);
        else
            inBackoffCount++;
    }
    videos = videos
        // Long-forms before their Shorts (Rishi 2026-11-22, review SEO #3):
        // a Short's "Watch the full video" footer needs the long-form's
        // YouTube id, which only exists once the long-form is published.
        // The vertical cut is rendered (and therefore created) first, so
        // plain creation-time order would upload the Short before its
        // long-form and the cross-link could never be filled in. Fix #5:
        // the second Short ("-short2") orders with the Shorts, for the
        // same reason.
        .OrderBy(v => ShortVideo.IsShort(v.Path) ? 1 : 0)
        .ThenBy(v => File.GetCreationTimeUtc(v.Path))
        .ToList();

    if (videos.Count == 0)
    {
        log.Info($"No videos to process. (0 ready, {tooNewCount} too-new, {inBackoffCount} in crash-retry backoff)");
        return videosWithNoPublish;
    }

    log.Info($"Found {videos.Count} video(s).");

    foreach (var (path, channel) in videos)
    {
        // L23: the step + platform/target in progress, so the per-video
        // catch can name them (a SaveAsync failure after a successful
        // publish used to look identical to a publish failure). Declared
        // outside the try so the catch can read them.
        var step = "sidecar load";
        string? stepTarget = null;

        try
        {
            if (!publishersByChannel.TryGetValue(channel, out var publishersByPlatform))
            {
                log.Error($"Failed {Path.GetFileName(path)}: channel \"{channel}\" has no entry under Channels in appsettings.json.");
                TryMoveWithSiblings(path, cfg.FailedDirFor(channel), log, "failed");
                continue;
            }

            var sidecar = await ResolveSidecarAsync(path, channel, publishersByChannel, cfg, log);

            if (cfg.RequireApproval && sidecar.Approved != true)
            {
                // Human-in-the-loop gate: write the generated sidecar next to
                // the video and skip. You review, set "approved": true, next
                // run publishes it.
                var sidecarPath = Sidecar.PathFor(path);
                if (!File.Exists(sidecarPath))
                {
                    step = "sidecar save";
                    await SaveSidecarAsync(sidecar, path, log, "awaiting approval", stepTarget);
                    log.Info($"Awaiting approval: {Path.GetFileName(sidecarPath)} written. " +
                             "Review it, set \"approved\": true, and it publishes next run.");
                }
                else
                {
                    log.Info($"Still awaiting approval: {Path.GetFileName(sidecarPath)}");
                }
                continue;
            }

            // Second-Short 12h hold (Rishi 2026-11-27 18:19 EST — cadence:
            // something every 12h: long-form + main Short together, 2nd
            // Short 12h later, next story 12h after that). The hold is
            // anchored off the MAIN Short's live publish time, not the
            // finish-written PublishAfterUtc — that value is only the
            // fallback when the main Short's sidecar cannot be found. A
            // held file is skipped whole — no target is dispatched, nothing
            // is marked Skipped/Failed, no MaxPerRun slot or YouTube
            // dispatch count is consumed, and the file stays in the watch
            // dir so the next run picks it up once the hold has passed. The
            // long-form and main Short are never held (this branch is
            // short2-only). Checked here, before the per-target loop, so a
            // held file costs one log line, not one per target — and the
            // `continue` skips the whole per-video tail below (including
            // the no-publish -> failed/ path), so a hold is never mistaken
            // for a failure. The fix-#5 quota guard below runs after this
            // check; a held file never reaches it.
            if (ShortVideo.IsSecondShort(path))
            {
                var mainShortSidecar = await FindMainShortSidecarAsync(path, msg => log.Warn(msg));
                // Re-anchor (rules-agent handoff, Rishi 2026-11-27): when
                // the main Short IS published, the governing hold is its
                // live publish time + 12h — not the finish-written
                // provisional (finish + 12h), which goes stale when the
                // main Short goes live late. Rewrite the short2 sidecar's
                // stored PublishAfterUtc to the computed anchor and save
                // it, but only when it differs — so the sidecar always
                // shows the governing hold. Runs on the same run as the
                // hold evaluation, whether the file ends up held or
                // released.
                if (ReanchorPublishAfterUtc(sidecar, mainShortSidecar))
                {
                    step = "sidecar save";
                    await SaveSidecarAsync(sidecar, path, log, "re-anchor publish hold", stepTarget);
                    log.Info($"  [{channel}] re-anchored {Path.GetFileName(path)} publish hold to {sidecar.PublishAfterUtc!.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC (main Short's publish time + 12h).");
                }
                var holdReason = SecondShortHoldReason(mainShortSidecar, sidecar.PublishAfterUtc, DateTimeOffset.UtcNow);
                if (holdReason is not null)
                {
                    log.Info($"  [{channel}] holding {Path.GetFileName(path)}: {holdReason}.");
                    continue;
                }
                if (mainShortSidecar is null)
                    log.Info($"  [{channel}] no main-Short sidecar found for {Path.GetFileName(path)} — no hold anchor; proceeding (fallback PublishAfterUtc is null or past).");
            }

            foreach (var target in sidecar.OutstandingTargets().ToList())
            {
                stepTarget = target;

                if (!publishersByPlatform.TryGetValue(target, out var publisher) || !publisher.Enabled)
                {
                    sidecar.PublicationFor(target).Status = PublishStatus.Skipped;
                    // L27: this used to be silent — a disabled platform
                    // looked identical to "never a target". Name the
                    // platform, the video, and the next action. The reason
                    // is also recorded on the publication so the L28
                    // end-of-run summary can name it.
                    sidecar.PublicationFor(target).Error = "platform disabled in appsettings.json";
                    log.Info($"  [{channel}/{target}] skipping {Path.GetFileName(path)}: platform disabled in appsettings.json; target Skipped, video continues to next target");
                    step = "sidecar save";
                    await SaveSidecarAsync(sidecar, path, log, "disabled target", target);
                    continue;
                }

                if (Dispatched(channel, publisher.Platform) >= publisher.MaxPerRun)
                {
                    log.Info($"  [{channel}/{publisher.Platform}] run quota reached ({publisher.MaxPerRun}); deferring {Path.GetFileName(path)} to next run.");
                    continue;
                }

                // Fix #5 quota guard: the second Short is a bonus dispatch
                // (~1,700 YouTube units), sent only when the day's headroom
                // allows it. A skip is a deferral, not a failure: the target
                // stays outstanding and retries next run, and the other
                // platforms still publish. The main Short and long-form are
                // never gated here — only the bonus second Short.
                if (target.Equals(Platforms.YouTube, StringComparison.OrdinalIgnoreCase)
                    && ShortVideo.IsSecondShort(path))
                {
                    var todayRows = await YouTubePublishesTodayAsync();
                    var runDispatches = Dispatched(channel, Platforms.YouTube);
                    if (!YouTubeShort2QuotaAllows(todayRows, runDispatches))
                    {
                        var estimated = EstimateYouTubeQuotaUnits(todayRows + runDispatches + 1);
                        sidecar.PublicationFor(target).Status = PublishStatus.Skipped;
                        sidecar.PublicationFor(target).Error = $"YouTube daily quota headroom insufficient for the second Short (~{estimated} units with this dispatch, over the 10,000/day quota); deferred to next run";
                        log.Info($"  [{channel}/youtube] skipping second Short {Path.GetFileName(path)}: YouTube daily quota headroom insufficient (~{estimated} units with this dispatch vs 10,000/day: {todayRows} publish(es) logged today + {runDispatches} dispatch(es) this run); target Skipped, will retry next run; other platforms unaffected");
                        step = "sidecar save";
                        await SaveSidecarAsync(sidecar, path, log, "quota-guard skip", target);
                        continue;
                    }
                }

                // Story cut (Rishi 2026-11-26): when the Short runs over
                // 60s, finish writes a first-60s sibling cut; the
                // instagram_story target uploads the cut instead of the
                // full Short. No cut (older video, or the cut failed at
                // finish time) keeps the old behaviour — the publisher's
                // gate skips the target. The sidecar still belongs to the
                // Short: only the file handed to Accepts/PublishAsync
                // changes, never the sidecar path or its records.
                var targetPath = path;
                if (target.Equals(Platforms.InstagramStory, StringComparison.OrdinalIgnoreCase))
                {
                    var cut = StoryCut.PathFor(path);
                    if (File.Exists(cut))
                    {
                        targetPath = cut;
                        log.Info($"  [instagram_story] using 60s story cut {Path.GetFileName(cut)}.");
                    }
                }

                if (!publisher.Accepts(sidecar, targetPath, out var reason))
                {
                    log.Info($"  [{publisher.Platform}] skipping {Path.GetFileName(path)}: {reason}");
                    sidecar.PublicationFor(target).Status = PublishStatus.Skipped;
                    sidecar.PublicationFor(target).Error = reason;
                    step = "sidecar save";
                    await SaveSidecarAsync(sidecar, path, log, "acceptance skip", target);
                    continue;
                }

                if (cfg.StaggerSecondsBetweenPosts > 0 && dispatchedThisRun.Values.Sum() > 0)
                    await Task.Delay(TimeSpan.FromSeconds(cfg.StaggerSecondsBetweenPosts));

                dispatchedThisRun[(channel, publisher.Platform)] = Dispatched(channel, publisher.Platform) + 1;

                var publication = sidecar.PublicationFor(target);
                publication.Attempts++;

                // Idempotency wiring (fix 4): hand the publisher the id a
                // previous run may already have recorded, and a callback it
                // invokes the moment the platform returns a live id — the
                // callback persists it immediately, before any post-publish
                // work, so a VM restart in that window cannot double-publish.
                var request = new PublishRequest(targetPath, sidecar)
                {
                    ExistingId = publication.Id,
                    OnPlatformIdAsync = async id =>
                    {
                        publication.Id = id;
                        await SaveSidecarAsync(sidecar, path, log, "record platform id", target);
                    },
                };

                step = "publish";
                var result = await publisher.PublishAsync(request, CancellationToken.None);

                RecordTargetResult(publication, result, cfg.MaxAttemptsPerPlatform, log, channel, publisher.Platform);

                // Saved per-target rather than once at the end of the loop, so a video
                // whose next target throws (moving it to the crash-retry path below)
                // doesn't lose the results already recorded for the targets before it.
                step = "sidecar save";
                await SaveSidecarAsync(sidecar, path, log, "post-publish", target);
            }

            // R3 (fix sequence #2, ITEM 5): reconcile the Short<->long-form
            // cross-links on EVERY run, not just at publish time. A Short that
            // published before its long-form (or a long-form whose Short
            // published first) gets both directions linked once both are live;
            // a second run is a no-op when both links are already present.
            // Best-effort and description-only: it never re-uploads anything,
            // never fails the video's run, and only patches descriptions of
            // already-published videos.
            if (channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase)
                && publishersByPlatform.TryGetValue(Platforms.YouTube, out var ytPublisher)
                && ytPublisher is YouTubePublisher youtubePublisher)
            {
                step = "cross-link reconciliation";
                try
                {
                    await youtubePublisher.ReconcileCrossLinksAsync(path, sidecar, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    log.Warn($"  [youtube] cross-link reconciliation failed for {Path.GetFileName(path)}: {ex.Message}; descriptions unchanged, will retry next run");
                }
            }

            // This video's own processing reached the end without throwing, whatever the
            // per-target outcomes were — clear any crash-retry count from an earlier run.
            ClearCrashRetryState(path);

            if (sidecar.AllTargetsTerminal())
            {
                step = "analytics append";
                if (sidecar.AnyPublished())
                {
                    // The topic only counts as "covered" once something actually went live -
                    // this is the sole writer of topic-history, deliberately later than
                    // render/approval, so VideoGen never marks a topic used until a real
                    // publish confirms it.
                    await TopicHistory.AppendAsync(channel, sidecar.Title);
                    // Analytics loop (priority 1): one line per published video,
                    // in the watch directory's analytics folder, so VideoGen's
                    // next runs can read it without any new service.
                    // The row is keyed by YouTube video ID: take the YouTube
                    // publication specifically. The old code took the first
                    // published target in dictionary order, which could be
                    // Instagram — the COP31 Short's row got Instagram media
                    // ID 18082177424510099 as its VideoId, and that bad ID
                    // 400s the Analytics pull's video== filter. No YouTube
                    // publication yet: skip the row with a warning instead of
                    // writing a wrong ID; the row is added on a later run
                    // once YouTube has published.
                    var ytId = AnalyticsPull.YouTubeVideoId(sidecar.Publications);
                    if (ytId is null)
                    {
                        log.Warn($"  [analytics] skipping log row for {sidecar.Title}: no published YouTube video yet.");
                    }
                    else
                    {
                        await GiggleGarden.Shared.AnalyticsLog.AppendAsync(
                            Path.Combine(cfg.WatchDirectory, "analytics"),
                            new GiggleGarden.Shared.AnalyticsEntry
                            {
                                VideoId = ytId,
                                Title = sidecar.Title,
                                Channel = channel,
                                Aspect = sidecar.Aspect,
                                Tags = sidecar.Tags,
                                ThumbnailPath = sidecar.ThumbnailPath,
                                PublishedAt = sidecar.PublicationFor("youtube").At ?? DateTimeOffset.UtcNow,
                            },
                            // L82: wire the helper's log sink into the run log.
                            msg => log.Warn(msg));
                    }

                    step = "move to done";
                    if (TryMoveWithSiblings(path, cfg.DoneDirFor(channel), log, "done"))
                        log.Info($"Done: {Path.GetFileName(path)} (published)");
                    else
                        log.Error($"Done: {Path.GetFileName(path)} (published) — could not move to done/, left in watch dir; publish already recorded, will re-verify via ExistingId next run");
                }
                else
                {
                    // Nothing went live on any target — this is a failure, not a
                    // completion. It must not sit in done/ alongside published
                    // videos, where it looks finished and is never retried.
                    videosWithNoPublish++;
                    var firstError = sidecar.Publications.Values
                        .Select(p => p.Error)
                        .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))
                        ?? "no target published (all targets skipped or failed without a recorded error)";
                    noPublishDetails.Add((Path.GetFileName(path), firstError));
                    step = "move to failed";
                    if (TryMoveWithSiblings(path, cfg.FailedDirFor(channel), log, "failed"))
                        log.Error($"Done: {Path.GetFileName(path)} (no successful targets) — moved to failed/.");
                    else
                        log.Error($"Done: {Path.GetFileName(path)} (no successful targets) — could not move to failed/, left in watch dir, will retry next run");
                }
            }
            else
            {
                log.Info($"Partial: {Path.GetFileName(path)} — outstanding targets remain, will retry next run.");
            }
        }
        catch (Exception ex)
        {
            // L23: one line carrying the step + platform/target that threw,
            // the exception type + message + first stack frame (via L24's
            // Error overload), and the next action — a SaveAsync failure
            // after a successful publish used to look identical to a
            // publish failure (both logged a bare ex.Message).
            var file = Path.GetFileName(path);
            var targetSuffix = stepTarget is null ? "" : $" [{stepTarget}]";

            // B21: a Trendline Now video without a sidecar is a missing
            // input, not a transient crash — do not burn 3 crash retries
            // (with 30-min backoffs) on it. Move it to failed/ at once
            // with the clear message above.
            if (ex.Message.StartsWith("SIDECAR_REQUIRED:", StringComparison.Ordinal))
            {
                videosWithNoPublish++;
                noPublishDetails.Add((file, "the video needs a sidecar (see above)"));
                log.Error($"Failed {file}: {step}{targetSuffix}; Not retrying — the video needs a sidecar (see above) — moved to failed/.", ex);
                TryMoveWithSiblings(path, cfg.FailedDirFor(channel), log, "failed");
                continue;
            }

            // L11: a corrupt sidecar is permanent, not a transient crash —
            // same fail-fast shape as SIDECAR_REQUIRED above: move it to
            // failed/ at once instead of burning 3 crash retries on it.
            if (ex.Message.StartsWith("SIDECAR_CORRUPT:", StringComparison.Ordinal))
            {
                videosWithNoPublish++;
                noPublishDetails.Add((file, "the sidecar is corrupt (see above)"));
                log.Error($"Failed {file}: {step}{targetSuffix}; Not retrying — the sidecar is corrupt (see above) — moved to failed/.", ex);
                TryMoveWithSiblings(path, cfg.FailedDirFor(channel), log, "failed");
                continue;
            }

            var state = LoadCrashRetryState(path, log) ?? new CrashRetryState();
            state.Attempts++;
            state.LastAttemptUtc = DateTimeOffset.UtcNow;
            SaveCrashRetryState(path, state);

            if (state.Attempts < cfg.MaxCrashRetries)
                log.Error($"Failed {file}: {step}{targetSuffix}; Will retry in {cfg.CrashRetryBackoffMinutes}m " +
                          $"(crash attempt {state.Attempts}/{cfg.MaxCrashRetries}).", ex);
            else
            {
                log.Error($"Failed {file}: {step}{targetSuffix}; Giving up on {file} after {state.Attempts} crash attempt(s) — moved to failed/.", ex);
                TryMoveWithSiblings(path, cfg.FailedDirFor(channel), log, "failed");
            }
        }
    }

    // B31: YouTube's API exposes no remaining-quota endpoint — the only
    // quota signal is a 403 quotaExceeded error, which is already too
    // late. Log a dispatch-based estimate instead so the daily headroom is
    // visible on every run (see EstimateYouTubeQuotaUnits for the unit
    // costs; the project quota is 10,000 units/day).
    var youtubeDispatches = dispatchedThisRun
        .Where(kv => kv.Key.Platform == Platforms.YouTube).Sum(kv => kv.Value);
    log.Info($"YouTube quota estimate this run: ~{EstimateYouTubeQuotaUnits(youtubeDispatches)} units of the 10,000/day project quota " +
             $"({youtubeDispatches} YouTube dispatch(es); the API exposes no remaining-quota endpoint, so this is a dispatch-based estimate).");

    // L28: the summary a monitor reads must name the videos and their
    // reasons, not just the count — the per-video lines are scattered
    // through the run above.
    if (videosWithNoPublish > 0)
        log.Error($"{videosWithNoPublish} video(s) ended with no published target: " +
            $"{string.Join(", ", noPublishDetails.Select(d => $"{d.File} ({d.Reason})"))}; moved to failed/");

    return videosWithNoPublish;
}

// B31: estimated YouTube Data API quota units for N YouTube dispatches in
// one run: ~1,600 (videos.insert) + ~50 (thumbnails.set) + ~50
// (videos.update for cross-links/captions) + ~50 (playlistItems.insert)
// per dispatch. A dispatch that only reconciles a cross-link costs far
// less, so this over-estimates on quiet runs — deliberately: it is a
// headroom guard, not a bill.
static int EstimateYouTubeQuotaUnits(int youtubeDispatches) => youtubeDispatches * 1700;

// Fix #5: whether the day's YouTube headroom allows one more (~1,700-unit)
// dispatch for a bonus second Short. todayRows = analytics-log rows with a
// YouTube publish dated today; runDispatches = YouTube dispatches already
// made this run; +1 is the second Short itself. The 10,000/day project
// quota is the same figure B31 logs. Best-effort guard, not a bill: a row
// lands only once a video's targets are all terminal, so a same-day YouTube
// publish still mid-flight is not counted — the API's own 403
// quotaExceeded remains the backstop.
static bool YouTubeShort2QuotaAllows(int todayRows, int runDispatches) =>
    EstimateYouTubeQuotaUnits(todayRows + runDispatches + 1) <= 10_000;

// The PublishAfterUtc comparison behind the second-Short hold: the
// finish writes PublishAfterUtc = finish time + 12h into the second
// Short's sidecar only (see ShortRules.SecondShortHoldUntil), and it
// governs only as the FALLBACK when the main Short's sidecar cannot be
// found (see SecondShortHoldReason). A hold in the future holds the whole
// file; a hold in the past, a hold exactly now, or no hold (null) never
// holds. Pure so the rule is pinned by tests without restructuring the
// dispatch loop — see SecondShortUploadTests.
static bool IsPublishHeld(DateTimeOffset? publishAfterUtc, DateTimeOffset now) =>
    publishAfterUtc.HasValue && publishAfterUtc.Value > now;

// Re-anchor the second Short's stored PublishAfterUtc to the governing
// hold — the main Short's live publish time + 12h — when the main Short
// IS published (rules-agent handoff, Rishi 2026-11-27). Returns true when
// the stored value was rewritten (the caller saves the sidecar); false
// when there is nothing to re-anchor (no published sibling) or the stored
// value already equals the computed anchor as an instant (no-op, no save).
// Pure so tests can pin it — see SecondShortUploadTests.
static bool ReanchorPublishAfterUtc(Sidecar short2Sidecar, Sidecar? mainShortSidecar)
{
    if (mainShortSidecar is not null
        && mainShortSidecar.Publications.TryGetValue(Platforms.YouTube, out var yt)
        && yt.Status == PublishStatus.Published && yt.At.HasValue)
    {
        var anchored = yt.At.Value.AddHours(12);
        if (short2Sidecar.PublishAfterUtc != anchored)
        {
            short2Sidecar.PublishAfterUtc = anchored;
            return true;
        }
    }
    return false;
}

// The main Short's sidecar for a second Short, mirroring
// YouTubePublisher.FindSiblingSidecarAsync's pattern: the same directory
// first (main Short still in the watch dir), then done/<slug>-short/
// (the main Short usually moved there after publishing — by the time the
// uploader reaches the second Short on a later run). Returns null when no
// sidecar can be found OR loaded: a corrupt/unreadable main-Short sidecar
// must not fail the second Short (no crash retry, no failed/ move), so it
// falls through to the PublishAfterUtc fallback exactly like "not found".
static async Task<Sidecar?> FindMainShortSidecarAsync(string short2Path, Action<string>? log = null)
{
    var longFormSlug = ShortVideo.LongFormSlug(Path.GetFileNameWithoutExtension(short2Path));
    if (longFormSlug is null)
        return null;
    var mainShortSlug = longFormSlug + ShortVideo.Suffix;
    var channelDir = Path.GetDirectoryName(short2Path);
    if (channelDir is null)
        return null;

    foreach (var candidate in new[]
    {
        Path.Combine(channelDir, mainShortSlug + ".mp4"),
        Path.Combine(channelDir, "done", mainShortSlug, mainShortSlug + ".mp4"),
    })
    {
        try
        {
            // Null log sink: LoadAsync already names a corrupt file in the
            // exception it throws; the catch below logs it once.
            var sibling = await Sidecar.LoadAsync(candidate);
            if (sibling is not null)
                return sibling;
        }
        catch (Exception ex)
        {
            log?.Invoke($"main-Short sidecar unreadable for {candidate}: {ex.Message}; treating as not found, falling back to PublishAfterUtc");
        }
    }
    return null;
}

// The second-Short 12h hold decision, pure so tests can pin it without
// the dispatch loop: returns the one-line hold reason (including the next
// action), or null when the second Short may proceed now.
// mainShortSidecar is the main Short's sidecar (null when it could not be
// found/loaded); fallbackPublishAfterUtc is the short2's own
// finish-written PublishAfterUtc. The anchor is the main Short's LIVE
// YouTube publish time (Publication.At): holdUntil = At + 12h. A sibling
// that exists but is not published yet (or has no At) holds indefinitely
// — the second Short must never go out before the main Short. With no
// sibling at all, the finish-written PublishAfterUtc governs instead.
static string? SecondShortHoldReason(Sidecar? mainShortSidecar, DateTimeOffset? fallbackPublishAfterUtc, DateTimeOffset now)
{
    if (mainShortSidecar is not null)
    {
        if (mainShortSidecar.Publications.TryGetValue(Platforms.YouTube, out var yt)
            && yt.Status == PublishStatus.Published && yt.At.HasValue)
        {
            var holdUntil = yt.At.Value.AddHours(12);
            return now < holdUntil
                ? $"main Short published {yt.At.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC; 12h hold until {holdUntil.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC (anchored off the main Short's publish time); waiting for the next uploader run after the hold passes"
                : null;
        }
        return "main Short not published yet; waiting for the next uploader run";
    }

    if (IsPublishHeld(fallbackPublishAfterUtc, now))
        return $"no main-Short sidecar found; fallback PublishAfterUtc hold until {fallbackPublishAfterUtc!.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC governs; waiting for the next uploader run after the hold passes";
    return null;
}

// B27: split a scan into files old enough to process and files skipped for
// being too new, logging each skipped file by name. Extracted from the
// dispatch loop so the accounting is testable on its own — see
// Item6Tests. A file younger than MinFileAgeSeconds is probably still
// being written, so it waits for the next run instead of publishing a
// partial file.
static List<(string Path, string Channel)> FilterByMinAge(
    IEnumerable<(string Path, string Channel)> files, int minAgeSeconds,
    DateTimeOffset utcNow, Action<string> logSkipped)
{
    var ready = new List<(string Path, string Channel)>();
    foreach (var (path, channel) in files)
    {
        var age = (utcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds;
        if (age < minAgeSeconds)
        {
            logSkipped?.Invoke($"Skipping {Path.GetFileName(path)}: only {age:F0}s old (< {minAgeSeconds}s minimum file age; likely still being written).");
            continue;
        }
        ready.Add((path, channel));
    }
    return ready;
}

static string CrashRetryMarkerPath(string videoPath) => Path.ChangeExtension(videoPath, ".retry.json");

// Records one target's publish outcome into its Publication. Extracted
// from the dispatch loop (which increments Attempts before PublishAsync)
// so the accounting is testable on its own — see
// AttemptAndCrossLinkTests. A Defer (B19: the publisher established no
// state and did no work) undoes that increment, so the run consumes none
// of the attempt budget and the target stays Failed (retryable) instead of
// marching toward Abandoned on every run.
static void RecordTargetResult(Publication publication, PublishResult result, int maxAttemptsPerPlatform, Logger log, string channel, string platform)
{
    if (result.IsDeferred)
    {
        publication.Attempts = Math.Max(0, publication.Attempts - 1);
        publication.Status = PublishStatus.Failed;
        publication.Error = result.Error;
        log.Info($"  [{channel}/{platform}] deferred without burning an attempt ({publication.Attempts}/{maxAttemptsPerPlatform} used): {result.Error}");
        return;
    }

    if (result.Success)
    {
        publication.Status = PublishStatus.Published;
        publication.Id = result.Id;
        publication.Url = result.Url;
        publication.At = DateTimeOffset.UtcNow;
        // Clear the stale failure: a published record must not keep
        // carrying an old error (video #2's YouTube record still
        // showed "invalid_grant" even though it published). The
        // message is kept in LastError for history.
        publication.LastError = publication.Error;
        publication.Error = null;
        log.Info($"  [{channel}/{platform}] published: {result.Url ?? result.Id}");
    }
    else if (result.Permanent || publication.Attempts >= maxAttemptsPerPlatform)
    {
        publication.Status = PublishStatus.Abandoned;
        publication.Error = result.Error;
        log.Error($"  [{channel}/{platform}] abandoned after {publication.Attempts} attempt(s): {result.Error}");
    }
    else
    {
        publication.Status = PublishStatus.Failed;
        publication.Error = result.Error;
        log.Error($"  [{channel}/{platform}] failed (attempt {publication.Attempts}/{maxAttemptsPerPlatform}, will retry): {result.Error}");
    }
}

static CrashRetryState? LoadCrashRetryState(string videoPath, Logger? log = null)
{
    var markerPath = CrashRetryMarkerPath(videoPath);
    if (!File.Exists(markerPath)) return null;

    try
    {
        return JsonSerializer.Deserialize<CrashRetryState>(File.ReadAllText(markerPath));
    }
    catch (JsonException ex)
    {
        // L11: a corrupt marker used to silently reset the attempt count to
        // 0, so a repeatedly-crashing video never reached its 3-attempt
        // give-up. Name the file and the reset so it is visible in the run
        // log; the reset itself is unchanged (a corrupt marker must not
        // become a reason the video can never be retried).
        log?.Warn($"corrupt retry marker for {Path.GetFileName(markerPath)}: {ex.Message}; treating as 0 attempts (count reset)");
        return null;
    }
}

static void SaveCrashRetryState(string videoPath, CrashRetryState state)
{
    var markerPath = CrashRetryMarkerPath(videoPath);
    var tmp = markerPath + ".tmp";
    File.WriteAllText(tmp, JsonSerializer.Serialize(state));
    File.Move(tmp, markerPath, overwrite: true);
}

static void ClearCrashRetryState(string videoPath)
{
    var markerPath = CrashRetryMarkerPath(videoPath);
    if (File.Exists(markerPath)) File.Delete(markerPath);
}

static bool IsPastCrashRetryBackoff(string videoPath, AppConfig cfg, Logger? log = null)
{
    var state = LoadCrashRetryState(videoPath, log);
    if (state is null) return true;
    var backoffUntil = state.LastAttemptUtc.AddMinutes(cfg.CrashRetryBackoffMinutes);
    if (DateTimeOffset.UtcNow >= backoffUntil) return true;
    // L29: a video inside its backoff used to vanish from the scan with
    // no line at all. Name it, its backoff expiry, and its attempt count.
    log?.Info($"  [scan] skipping {Path.GetFileName(videoPath)}: in crash-retry backoff until {backoffUntil:yyyy-MM-dd HH:mm:ss} UTC (attempt {state.Attempts}/{cfg.MaxCrashRetries}); will retry next run");
    return false;
}

// Every file belonging to one video - the .mp4, its sidecar .json, the .srt caption
// file, the .thumb.jpg custom thumbnail - moves together into its own subfolder
// (named after the video, e.g. \done\my-video-en\) instead of dumping everything
// flat into \done or \failed, where dozens of videos' files would otherwise mix
// together indistinguishably. Landscape and vertical-short renders of the "same"
// video get separate folders since the pipeline already treats them as independent
// items (separate sidecar, targets, publications) - this just mirrors that.
// L30: a disk-full/permission failure on a sidecar save used to throw
// into the per-video catch and be logged as a video failure — possibly
// after the publish already succeeded, so a live video looked failed.
// Log the failure with the step, the video, the reason, and the next
// action, then rethrow so the existing crash-retry accounting still runs
// (the per-video catch names the step via L23).
static async Task SaveSidecarAsync(Sidecar sidecar, string videoPath, Logger log, string step, string? target)
{
    try
    {
        // L82: wire the helper's log sink into the run log.
        await sidecar.SaveAsync(videoPath, msg => log.Warn(msg));
    }
    catch (Exception ex)
    {
        var targetSuffix = target is null ? "" : $" [{target}]";
        log.Error($"sidecar save failed for {Path.GetFileName(videoPath)} ({step}){targetSuffix}: {ex.GetType().Name}: {ex.Message}; video left in watch dir, will retry next run");
        throw;
    }
}

// L30: same shape for moves — a failed move used to throw out of the run
// loop (in the SIDECAR_REQUIRED branch it could escape to the top-level
// FATAL). Log it with the destination, the video, and the next action and
// report false instead, so the video stays in the watch dir and retries
// next run; the caller decides whether its "moved" line still applies.
static bool TryMoveWithSiblings(string videoPath, string destRoot, Logger log, string destLabel)
{
    try
    {
        MoveWithSiblings(videoPath, destRoot);
        return true;
    }
    catch (Exception ex)
    {
        log.Error($"move to {destLabel} failed for {Path.GetFileName(videoPath)}: {ex.GetType().Name}: {ex.Message}; video left in watch dir, will retry next run");
        return false;
    }
}

static void MoveWithSiblings(string videoPath, string destRoot)
{
    var folder = Directory.CreateDirectory(
        Path.Combine(destRoot, Path.GetFileNameWithoutExtension(videoPath))).FullName;

    void MoveIfExists(string src)
    {
        if (File.Exists(src)) File.Move(src, Path.Combine(folder, Path.GetFileName(src)), overwrite: true);
    }

    MoveIfExists(videoPath);
    // A Story cut belongs to its Short: it travels to done/ (or failed/)
    // with it, so it never lingers in the watch dir after the Short is
    // resolved. Harmless no-op for long-forms (no cut exists for them).
    MoveIfExists(StoryCut.PathFor(videoPath));
    MoveIfExists(Sidecar.PathFor(videoPath));
    foreach (var suffix in new[] { ".srt", ".thumb.jpg", ".tiktok-caption.txt", ".retry.json" })
        MoveIfExists(Path.ChangeExtension(videoPath, suffix));
}

static async Task<Sidecar> ResolveSidecarAsync(
    string videoPath, string channel,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IPublisher>> publishersByChannel,
    AppConfig cfg, Logger log)
{
    // L82: wire the helper's log sink into the run log.
    var existing = await Sidecar.LoadAsync(videoPath, msg => log.Warn(msg));
    if (existing is not null)
    {
        log.Info($"Using sidecar metadata for {Path.GetFileName(videoPath)}");
        AppendMusicCredit(existing, channel, log);
        AppendTrendlineDisclaimer(existing, channel, log);
        return await AppendTrendlineFooterAsync(existing, videoPath, channel, log);
    }

    log.Info($"No sidecar found for {Path.GetFileName(videoPath)} — a sidecar is required.");

    // B21: a video with no sidecar can never publish — it needs the metadata
    // VideoGen writes next to the render, and there is no metadata fallback
    // any more (the old Groq/Gemini fallback wrote kids'
    // metadata and was deleted 2026-11-26). Fail fast with a clear message
    // instead of burning 3 crash retries before landing in failed/.
    if (channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            $"SIDECAR_REQUIRED: no sidecar found for {Path.GetFileName(videoPath)} — Spotlight Now videos must ship with the sidecar VideoGen writes next to the render. " +
            "Drop the video with its sidecar JSON.");

    // Same for every other channel: no sidecar, no publish.
    throw new InvalidOperationException(
        $"SIDECAR_REQUIRED: no sidecar found for {Path.GetFileName(videoPath)} — videos must ship with the sidecar VideoGen writes next to the render.");
}

static Sidecar AppendMusicCredit(Sidecar sidecar, string channel, Logger log)
{
    if (!channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase))
        return sidecar;

    if (string.IsNullOrWhiteSpace(sidecar.MusicTrack))
    {
        // No track recorded (sidecar written before VideoGen added MusicTrack):
        // the credit cannot be determined, and silently skipping it would ship
        // a CC BY track without attribution. Warn loudly instead — until
        // VideoGen writes MusicTrack this warns on every trendlinenow video,
        // which is the intended behaviour, not noise.
        log.Warn("Music credit could not be determined: sidecar has no MusicTrack. " +
                 "If this video uses a CC BY track, add its credit line to the description before approving.");
        return sidecar;
    }

    // CC0 track (or an unknown filename) — no credit line required.
    if (!MusicCredits.ByFile.TryGetValue(sidecar.MusicTrack, out var credit))
        return sidecar;

    if (string.IsNullOrWhiteSpace(sidecar.Description))
        return sidecar;
    if (sidecar.Description.Contains(credit, StringComparison.Ordinal))
        return sidecar;
    sidecar.Description = sidecar.Description.TrimEnd() + "\n\n" + credit;
    log.Info($"Appended music credit for {sidecar.MusicTrack}.");
    return sidecar;
}

// Legal disclaimer (Rishi 2026-11-21) - every Trendline Now video description,
// long-form AND the 60s Short/vertical cut, carries this notice. Applied here,
// in ResolveSidecarAsync, the uploader's single choke point for descriptions,
// so no upload can go out without it. The sidecar on disk is untouched until
// the uploader next saves it; the Contains check keeps a re-run (retry, or a
// sidecar already saved with the disclaimer) from stacking a second copy.
// Other channels are unaffected. This is a notice, not a shield: it does not
// make a false factual claim about a named person safe - rules 1-3 in
// TrendlineNowProfile.SafetyFraming are what keep the content itself legal.
static Sidecar AppendTrendlineDisclaimer(Sidecar sidecar, string channel, Logger log)
{
    // Text lives in Sidecar.TrendlineDisclaimer (Shared) so CaptionFor can strip
    // the same text from the caption fallback — a caption must never carry it.
    var disclaimer = Sidecar.TrendlineDisclaimer;

    if (!channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase))
        return sidecar;
    if (string.IsNullOrWhiteSpace(sidecar.Description))
        return sidecar;
    if (sidecar.Description.Contains(disclaimer, StringComparison.Ordinal))
        return sidecar;
    sidecar.Description = sidecar.Description.TrimEnd() + "\n\n" + disclaimer;
    log.Info("Appended the Spotlight Now legal disclaimer to the description.");
    return sidecar;
}

// Description footer (Rishi 2026-11-22, review SEO #3) - subscribe CTA plus
// the channel's social links, appended after the disclaimer so CaptionFor
// (which truncates at the disclaimer) strips the footer from captions too.
// A Short also gets a "Watch the full video" line pointing at its long-form,
// but only when the long-form's YouTube id is already recorded - the uploader
// processes long-forms before their Shorts, so a same-run long-form is live
// by the time the Short resolves. When the id is not known yet (long-form
// not uploaded, or this Short is the only file), the Short still gets the
// static footer; the cross-link is not faked. A footer saved without the
// link is NOT stuck that way: the per-run reconciliation
// (YouTubePublisher.ReconcileCrossLinksAsync, R3) re-adds it once the
// long-form is Published. Idempotent: the subscribe marker keeps a re-run
// from stacking a second copy.
static async Task<Sidecar> AppendTrendlineFooterAsync(Sidecar sidecar, string videoPath, string channel, Logger log)
{
    if (!channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase))
        return sidecar;
    if (string.IsNullOrWhiteSpace(sidecar.Description))
        return sidecar;
    if (sidecar.Description.Contains(Sidecar.TrendlineFooterMarker, StringComparison.Ordinal))
        return sidecar;

    var footer = Sidecar.TrendlineFooter;
    if (sidecar.IsVertical)
    {
        var longFormId = await FindSiblingLongFormYouTubeIdAsync(videoPath, log);
        if (!string.IsNullOrWhiteSpace(longFormId))
        {
            footer = $"{YouTubePublisher.FullVideoLinkLine(longFormId)}\n\n" + footer;
            log.Info($"Short footer links to the long-form https://youtu.be/{longFormId}.");
        }
        else
        {
            log.Info("Short footer has no long-form link yet (long-form not published); static footer only.");
        }
    }

    sidecar.Description = sidecar.Description.TrimEnd() + "\n\n" + footer;
    log.Info("Appended the Spotlight Now description footer.");
    return sidecar;
}

// The long-form's published YouTube id, for a Short's "Watch the full video"
// line. The Short filename is "<slug>-en-short.mp4" (or "<slug>-en-short2.mp4"
// for the fix-#5 second Short) and the long-form is "<slug>-en.mp4". The
// long-form is usually already published and moved to done/<slug>/<slug>.json;
// the watch-dir copy is the fallback for a long-form that has not been moved
// yet. Returns null when the long-form is not published on YouTube yet - the
// caller then ships the static footer only.
static async Task<string?> FindSiblingLongFormYouTubeIdAsync(string shortVideoPath, Logger log)
{
    var fileName = Path.GetFileNameWithoutExtension(shortVideoPath);
    var longFormSlug = ShortVideo.LongFormSlug(fileName);
    if (longFormSlug is null)
        return null;
    var channelDir = Path.GetDirectoryName(shortVideoPath);
    if (channelDir is null)
        return null;

    var candidates = new[]
    {
        Path.Combine(channelDir, "done", longFormSlug, longFormSlug + ".mp4"),
        Path.Combine(channelDir, longFormSlug + ".mp4"),
    };
    foreach (var candidate in candidates)
    {
        // L82: wire the helper's log sink into the run log.
        var sibling = await Sidecar.LoadAsync(candidate, msg => log.Warn(msg));
        if (sibling is null)
            continue;
        if (sibling.Publications.TryGetValue(Platforms.YouTube, out var pub)
            && pub.Status == PublishStatus.Published
            && !string.IsNullOrWhiteSpace(pub.Id))
            return pub.Id;
    }
    return null;
}

// Marks how many times a video's per-video processing has thrown, and when, so a
// transient failure (a platform API blip, a locked file) gets bounded retries across
// separate scheduled runs instead of either looping forever or giving up on the first
// hiccup. Persisted to disk rather than kept in memory because the Uploader is not a
// long-running process — it exits after each Task Scheduler invocation.
class CrashRetryState
{
    public int Attempts { get; set; }
    public DateTimeOffset LastAttemptUtc { get; set; }
}

// CC BY music credits (from VideoGen/assets/music-trendline/README.md). The two
// John Bartmann tracks are CC0 and need no line; the other three are CC BY 4.0
// and the video description MUST credit them. VideoGen records the resolved
// track filename in Sidecar.MusicTrack at assemble time; the uploader appends
// the matching line next to the disclaimer, so no upload can go out without
// the attribution the licence requires.
static class MusicCredits
{
    public static readonly Dictionary<string, string> ByFile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uplifting-tech-01.mp3"] = "Music: \"Rising Light (Uplifting Orchestral)\" by AurosonMusic — https://freemusicarchive.org/music/aurosonmusic/single/rising-light-uplifting-orchestral/ — CC BY 4.0",
        ["calm-neutral-01.mp3"] = "Music: \"Old chantry\" by Serat — https://freemusicarchive.org/music/serat/piano-textures/old-chantry-1/ — CC BY 4.0",
        ["cinematic-epic-01.mp3"] = "Music: \"A Hero for our Time\" by Geoff Harvey (Purple Planet Music) — https://freemusicarchive.org/music/geoff-harvey-purple-planet-music/epic-missions/a-hero-for-our-timemp3/ — CC BY 4.0",
    };
}
