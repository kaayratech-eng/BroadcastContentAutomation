// GiggleGarden Daily Uploader (.NET 8)
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

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables("GIGGLE_")
    .Build();

var cfg = config.Get<AppConfig>() ?? throw new InvalidOperationException("appsettings.json missing or invalid");
var log = new Logger(cfg.LogDirectory);

// B28: single-instance lock — two concurrent uploader runs can both read
// Pending and both publish (the ExistingId check cannot help: neither has
// saved an id yet). Held for the whole run; a second process exits
// without touching anything. The lock is kernel-held, so a killed run
// releases it automatically.
using var instanceLock = SingleInstanceLock.TryAcquire(Path.Combine(cfg.LogDirectory, "uploader.lock"));
if (instanceLock is null)
{
    log.Warn("Another uploader run is already active (uploader.lock is held); exiting without publishing anything.");
    return 0;
}

try
{
    var videosWithNoPublish = await RunAsync(cfg, log);
    if (videosWithNoPublish > 0)
    {
        // A run that publishes nothing is a failure, not a success — the exit
        // code must say so, so the scheduler/monitoring can tell it apart from
        // a clean run (previously every run exited 0, even one where every
        // target was Abandoned).
        log.Error($"{videosWithNoPublish} video(s) ended with no published target.");
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
    var videos = FilterByMinAge(allFiles, cfg.MinFileAgeSeconds, DateTime.UtcNow,
            msg => log.Info($"  [scan] {msg}"))
        .Where(v => IsPastCrashRetryBackoff(v.Path, cfg))
        // Long-forms before their Shorts (Rishi 2026-11-22, review SEO #3):
        // a Short's "Watch the full video" footer needs the long-form's
        // YouTube id, which only exists once the long-form is published.
        // The vertical cut is rendered (and therefore created) first, so
        // plain creation-time order would upload the Short before its
        // long-form and the cross-link could never be filled in.
        .OrderBy(v => Path.GetFileNameWithoutExtension(v.Path).EndsWith("-short", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
        .ThenBy(v => File.GetCreationTimeUtc(v.Path))
        .ToList();

    if (videos.Count == 0) { log.Info("No videos to process."); return videosWithNoPublish; }

    log.Info($"Found {videos.Count} video(s).");

    foreach (var (path, channel) in videos)
    {
        try
        {
            if (!publishersByChannel.TryGetValue(channel, out var publishersByPlatform))
            {
                log.Error($"Failed {Path.GetFileName(path)}: channel \"{channel}\" has no entry under Channels in appsettings.json.");
                MoveWithSiblings(path, cfg.FailedDirFor(channel));
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
                    await sidecar.SaveAsync(path);
                    log.Info($"Awaiting approval: {Path.GetFileName(sidecarPath)} written. " +
                             "Review it, set \"approved\": true, and it publishes next run.");
                }
                else
                {
                    log.Info($"Still awaiting approval: {Path.GetFileName(sidecarPath)}");
                }
                continue;
            }

            foreach (var target in sidecar.OutstandingTargets().ToList())
            {
                if (!publishersByPlatform.TryGetValue(target, out var publisher) || !publisher.Enabled)
                {
                    sidecar.PublicationFor(target).Status = PublishStatus.Skipped;
                    await sidecar.SaveAsync(path);
                    continue;
                }

                if (Dispatched(channel, publisher.Platform) >= publisher.MaxPerRun)
                {
                    log.Info($"  [{channel}/{publisher.Platform}] run quota reached ({publisher.MaxPerRun}); deferring {Path.GetFileName(path)} to next run.");
                    continue;
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
                    await sidecar.SaveAsync(path);
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
                        await sidecar.SaveAsync(path);
                    },
                };

                var result = await publisher.PublishAsync(request, CancellationToken.None);

                RecordTargetResult(publication, result, cfg.MaxAttemptsPerPlatform, log, channel, publisher.Platform);

                // Saved per-target rather than once at the end of the loop, so a video
                // whose next target throws (moving it to the crash-retry path below)
                // doesn't lose the results already recorded for the targets before it.
                await sidecar.SaveAsync(path);
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
                try
                {
                    await youtubePublisher.ReconcileCrossLinksAsync(path, sidecar, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    log.Warn($"  [youtube] cross-link reconciliation failed: {ex.Message}");
                }
            }

            // This video's own processing reached the end without throwing, whatever the
            // per-target outcomes were — clear any crash-retry count from an earlier run.
            ClearCrashRetryState(path);

            if (sidecar.AllTargetsTerminal())
            {
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
                    var published = sidecar.Publications.Values.FirstOrDefault(p => p.Status == GiggleGarden.Shared.PublishStatus.Published);
                    await GiggleGarden.Shared.AnalyticsLog.AppendAsync(
                        Path.Combine(cfg.WatchDirectory, "analytics"),
                        new GiggleGarden.Shared.AnalyticsEntry
                        {
                            VideoId = published?.Id ?? "",
                            Title = sidecar.Title,
                            Channel = channel,
                            Aspect = sidecar.Aspect,
                            Tags = sidecar.Tags,
                            ThumbnailPath = sidecar.ThumbnailPath,
                            PublishedAt = published?.At ?? DateTimeOffset.UtcNow,
                        });

                    MoveWithSiblings(path, cfg.DoneDirFor(channel));
                    log.Info($"Done: {Path.GetFileName(path)} (published)");
                }
                else
                {
                    // Nothing went live on any target — this is a failure, not a
                    // completion. It must not sit in done/ alongside published
                    // videos, where it looks finished and is never retried.
                    videosWithNoPublish++;
                    MoveWithSiblings(path, cfg.FailedDirFor(channel));
                    log.Error($"Done: {Path.GetFileName(path)} (no successful targets) — moved to failed/.");
                }
            }
            else
            {
                log.Info($"Partial: {Path.GetFileName(path)} — outstanding targets remain, will retry next run.");
            }
        }
        catch (Exception ex)
        {
            log.Error($"Failed {Path.GetFileName(path)}: {ex.Message}");

            // B21: a Trendline Now video without a sidecar is a missing
            // input, not a transient crash — do not burn 3 crash retries
            // (with 30-min backoffs) on it. Move it to failed/ at once
            // with the clear message above.
            if (ex.Message.StartsWith("SIDECAR_REQUIRED:", StringComparison.Ordinal))
            {
                videosWithNoPublish++;
                log.Error($"  Not retrying {Path.GetFileName(path)}: the video needs a sidecar (see above) — moved to failed/.");
                MoveWithSiblings(path, cfg.FailedDirFor(channel));
                continue;
            }

            var state = LoadCrashRetryState(path) ?? new CrashRetryState();
            state.Attempts++;
            state.LastAttemptUtc = DateTimeOffset.UtcNow;
            SaveCrashRetryState(path, state);

            if (state.Attempts < cfg.MaxCrashRetries)
                log.Warn($"  Will retry {Path.GetFileName(path)} in {cfg.CrashRetryBackoffMinutes}m " +
                         $"(crash attempt {state.Attempts}/{cfg.MaxCrashRetries}).");
            else
            {
                log.Error($"  Giving up on {Path.GetFileName(path)} after {state.Attempts} crash attempt(s).");
                MoveWithSiblings(path, cfg.FailedDirFor(channel));
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

    return videosWithNoPublish;
}

// B31: estimated YouTube Data API quota units for N YouTube dispatches in
// one run: ~1,600 (videos.insert) + ~50 (thumbnails.set) + ~50
// (videos.update for cross-links/captions) + ~50 (playlistItems.insert)
// per dispatch. A dispatch that only reconciles a cross-link costs far
// less, so this over-estimates on quiet runs — deliberately: it is a
// headroom guard, not a bill.
static int EstimateYouTubeQuotaUnits(int youtubeDispatches) => youtubeDispatches * 1700;

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

static CrashRetryState? LoadCrashRetryState(string videoPath)
{
    var markerPath = CrashRetryMarkerPath(videoPath);
    if (!File.Exists(markerPath)) return null;

    try
    {
        return JsonSerializer.Deserialize<CrashRetryState>(File.ReadAllText(markerPath));
    }
    catch (JsonException)
    {
        // A corrupt marker shouldn't itself become a reason the video can never be
        // retried — treat it as "no prior attempts recorded".
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

static bool IsPastCrashRetryBackoff(string videoPath, AppConfig cfg)
{
    var state = LoadCrashRetryState(videoPath);
    if (state is null) return true;
    return DateTimeOffset.UtcNow >= state.LastAttemptUtc.AddMinutes(cfg.CrashRetryBackoffMinutes);
}

// Every file belonging to one video - the .mp4, its sidecar .json, the .srt caption
// file, the .thumb.jpg custom thumbnail - moves together into its own subfolder
// (named after the video, e.g. \done\my-video-en\) instead of dumping everything
// flat into \done or \failed, where dozens of videos' files would otherwise mix
// together indistinguishably. Landscape and vertical-short renders of the "same"
// video get separate folders since the pipeline already treats them as independent
// items (separate sidecar, targets, publications) - this just mirrors that.
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
    var existing = await Sidecar.LoadAsync(videoPath);
    if (existing is not null)
    {
        log.Info($"Using sidecar metadata for {Path.GetFileName(videoPath)}");
        AppendMusicCredit(existing, channel, log);
        AppendTrendlineDisclaimer(existing, channel, log);
        return await AppendTrendlineFooterAsync(existing, videoPath, channel, log);
    }

    log.Info("No sidecar — generating metadata from YouTube trends + free Groq/Gemini.");

    // B21: the no-sidecar path always fails for Trendline Now — it needs
    // GroqApiKey/GeminiApiKey, neither of which is configured for this
    // channel — and the cryptic "No free metadata provider configured"
    // error then burns 3 crash retries before landing in failed/. Fail
    // fast with a clear message instead: a Trendline Now video must ship
    // with the sidecar VideoGen writes next to the render. (Other
    // channels keep the fallback — they may have a key configured.)
    if (channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            $"SIDECAR_REQUIRED: no sidecar found for {Path.GetFileName(videoPath)} — Spotlight Now videos must ship with the sidecar VideoGen writes next to the render. " +
            "The auto-metadata fallback cannot help here: it needs GroqApiKey or GeminiApiKey, neither is configured for this channel, and its prompt writes kids-nursery-rhyme metadata. " +
            "Drop the video with its sidecar JSON, or configure a metadata API key.");

    // A manually dropped video carries no channel signal of its own, but it was
    // found under `channel`'s watch folder, so it publishes through that
    // channel's credentials.
    var youtube = (YouTubePublisher)publishersByChannel[channel][Platforms.YouTube];
    var trends = await youtube.FetchTrendingDataAsync(cfg.TrendQueries, log, CancellationToken.None);
    var meta = await GenerateMetadataAsync(videoPath, trends, cfg, log);

    var fresh = AppendTrendlineDisclaimer(new Sidecar
    {
        Title = meta.Title,
        Description = meta.Description,
        Tags = meta.Tags,
        Approved = false,
        Language = "en",
        Aspect = "landscape",
        Channel = channel,
        // Aspect ratio of a manually dropped video is unknown, so default to
        // the one target every render supports regardless of orientation.
        Targets = [Platforms.YouTube],
    }, channel, log);
    return await AppendTrendlineFooterAsync(fresh, videoPath, channel, log);
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
// line. The Short filename is "<slug>-en-short.mp4" and the long-form is
// "<slug>-en.mp4". The long-form is usually already published and moved to
// done/<slug>/<slug>.json; the watch-dir copy is the fallback for a long-form
// that has not been moved yet. Returns null when the long-form is not
// published on YouTube yet - the caller then ships the static footer only.
static async Task<string?> FindSiblingLongFormYouTubeIdAsync(string shortVideoPath, Logger log)
{
    var fileName = Path.GetFileNameWithoutExtension(shortVideoPath);
    if (!fileName.EndsWith("-short", StringComparison.OrdinalIgnoreCase))
        return null;
    var longFormSlug = fileName[..^"-short".Length];
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
        var sibling = await Sidecar.LoadAsync(candidate);
        if (sibling is null)
            continue;
        if (sibling.Publications.TryGetValue(Platforms.YouTube, out var pub)
            && pub.Status == PublishStatus.Published
            && !string.IsNullOrWhiteSpace(pub.Id))
            return pub.Id;
    }
    return null;
}

// ---- Free metadata generation (fallback for videos with no sidecar) -------
// Previously Claude (paid). Now Groq first, Gemini second — both free tiers.
// Same prompt shape, same JSON contract, so callers do not change.

static async Task<GeneratedMetadata> GenerateMetadataAsync(
    string videoPath, string trendData, AppConfig cfg, Logger log)
{
    var fileName = Path.GetFileNameWithoutExtension(videoPath);

    var prompt = $$"""
You are an SEO assistant for a YouTube channel that makes original animated
nursery-rhyme and learning videos for young children.

Video filename (describes the content): "{{fileName}}"

Recent high-performing kids' video titles on YouTube for context:
{{trendData}}

Generate ORIGINAL metadata (do not copy any existing title). Respond ONLY with
JSON, no markdown fences, matching exactly:
{
  "title": "<engaging English title, <=90 chars, includes 1-2 relevant keywords, keyword in first 30 chars, hook first>",
  "description": "<3 short paragraphs: English first, then the same summary in Hindi (Devanagari), then Punjabi (Gurmukhi). End with 3-5 hashtags.>",
  "tags": ["<10-15 tags mixing English, Hindi, Punjabi keywords>"]
}
""";

    string? text = null;
    if (!string.IsNullOrWhiteSpace(cfg.GroqApiKey))
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = cfg.GroqModel,
                messages = new[] { new { role = "user", content = prompt } }
            });
            using var http = new HttpClient(new RetryHandler(log: m => log.Info($"  [groq] {m}")));
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {cfg.GroqApiKey}");
            var resp = await http.PostAsync("https://api.groq.com/openai/v1/chat/completions",
                new StringContent(body, Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception ex) { log.Info($"Groq metadata failed, trying Gemini: {ex.Message}"); }
    }
    if (text is null && !string.IsNullOrWhiteSpace(cfg.GeminiApiKey))
    {
        var body = JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } }
        });
        using var http = new HttpClient(new RetryHandler(log: m => log.Info($"  [gemini] {m}")));
        var resp = await http.PostAsync($"https://generativelanguage.googleapis.com/v1beta/models/{cfg.GeminiModel}:generateContent?key={cfg.GeminiApiKey}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
    }
    if (text is null) throw new Exception("No free metadata provider configured (set GroqApiKey or GeminiApiKey).");
    text = text.Replace("```json", "").Replace("```", "").Trim();

    var meta = JsonSerializer.Deserialize<GeneratedMetadata>(text,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new Exception("Free metadata provider returned no parseable metadata JSON.");

    log.Info($"Generated metadata: {meta.Title}");
    return meta;
}

class GeneratedMetadata
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
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
