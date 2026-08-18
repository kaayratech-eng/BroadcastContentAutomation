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
//      sends it + the video filename to the Claude API, receives
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

try
{
    await RunAsync(cfg, log);
    return 0;
}
catch (Exception ex)
{
    log.Error($"FATAL: {ex}");
    return 1;
}

static async Task RunAsync(AppConfig cfg, Logger log)
{
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
    var videos = channels
        .Select(channel => (Channel: channel, Dir: Path.Combine(cfg.WatchDirectory, channel)))
        .Where(c => Directory.Exists(c.Dir))
        .SelectMany(c => Directory.GetFiles(c.Dir, "*.mp4").Select(p => (Path: p, c.Channel)))
        .Where(v => (DateTime.UtcNow - File.GetLastWriteTimeUtc(v.Path)).TotalSeconds >= cfg.MinFileAgeSeconds)
        .Where(v => IsPastCrashRetryBackoff(v.Path, cfg))
        .OrderBy(v => File.GetCreationTimeUtc(v.Path))
        .ToList();

    if (videos.Count == 0) { log.Info("No videos to process."); return; }

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

                if (!publisher.Accepts(sidecar, out var reason))
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

                var result = await publisher.PublishAsync(new PublishRequest(path, sidecar), CancellationToken.None);

                if (result.Success)
                {
                    publication.Status = PublishStatus.Published;
                    publication.Id = result.Id;
                    publication.Url = result.Url;
                    publication.At = DateTimeOffset.UtcNow;
                    log.Info($"  [{channel}/{publisher.Platform}] published: {result.Url ?? result.Id}");
                }
                else if (result.Permanent || publication.Attempts >= cfg.MaxAttemptsPerPlatform)
                {
                    publication.Status = PublishStatus.Abandoned;
                    publication.Error = result.Error;
                    log.Error($"  [{channel}/{publisher.Platform}] abandoned after {publication.Attempts} attempt(s): {result.Error}");
                }
                else
                {
                    publication.Status = PublishStatus.Failed;
                    publication.Error = result.Error;
                    log.Error($"  [{channel}/{publisher.Platform}] failed (attempt {publication.Attempts}/{cfg.MaxAttemptsPerPlatform}, will retry): {result.Error}");
                }

                // Saved per-target rather than once at the end of the loop, so a video
                // whose next target throws (moving it to the crash-retry path below)
                // doesn't lose the results already recorded for the targets before it.
                await sidecar.SaveAsync(path);
            }

            // This video's own processing reached the end without throwing, whatever the
            // per-target outcomes were — clear any crash-retry count from an earlier run.
            ClearCrashRetryState(path);

            if (sidecar.AllTargetsTerminal())
            {
                // The topic only counts as "covered" once something actually went live -
                // this is the sole writer of topic-history, deliberately later than
                // render/approval, so VideoGen never marks a topic used until a real
                // publish confirms it.
                if (sidecar.AnyPublished())
                    await TopicHistory.AppendAsync(channel, sidecar.Title);

                MoveWithSiblings(path, cfg.DoneDirFor(channel));
                log.Info($"Done: {Path.GetFileName(path)} ({(sidecar.AnyPublished() ? "published" : "no successful targets")})");
            }
            else
            {
                log.Info($"Partial: {Path.GetFileName(path)} — outstanding targets remain, will retry next run.");
            }
        }
        catch (Exception ex)
        {
            log.Error($"Failed {Path.GetFileName(path)}: {ex.Message}");

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
}

static string CrashRetryMarkerPath(string videoPath) => Path.ChangeExtension(videoPath, ".retry.json");

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
        return existing;
    }

    log.Info("No sidecar — generating metadata from YouTube trends + Claude.");

    // A manually dropped video carries no channel signal of its own, but it was
    // found under `channel`'s watch folder, so it publishes through that
    // channel's credentials.
    var youtube = (YouTubePublisher)publishersByChannel[channel][Platforms.YouTube];
    var trends = await youtube.FetchTrendingDataAsync(cfg.TrendQueries, log, CancellationToken.None);
    var meta = await GenerateMetadataAsync(videoPath, trends, cfg, log);

    return new Sidecar
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
    };
}

// ---- Claude metadata generation (fallback for videos with no sidecar) -------

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
  "title": "<engaging English title, <=90 chars, includes 1-2 relevant keywords>",
  "description": "<3 short paragraphs: English first, then the same summary in Hindi (Devanagari), then Punjabi (Gurmukhi). End with 3-5 hashtags.>",
  "tags": ["<10-15 tags mixing English, Hindi, Punjabi keywords>"]
}
""";

    var body = JsonSerializer.Serialize(new
    {
        model = cfg.ClaudeModel,
        max_tokens = 1500,
        messages = new[] { new { role = "user", content = prompt } }
    });

    using var http = new HttpClient(new RetryHandler(log: m => log.Info($"  [claude] {m}")));
    http.DefaultRequestHeaders.Add("x-api-key", cfg.AnthropicApiKey);
    http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

    var resp = await http.PostAsync("https://api.anthropic.com/v1/messages",
        new StringContent(body, Encoding.UTF8, "application/json"));
    resp.EnsureSuccessStatusCode();

    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!;
    text = text.Replace("```json", "").Replace("```", "").Trim();

    var meta = JsonSerializer.Deserialize<GeneratedMetadata>(text,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new Exception("Claude returned no parseable metadata JSON.");

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
