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
    Directory.CreateDirectory(cfg.DoneDirectory);
    Directory.CreateDirectory(cfg.FailedDirectory);

    if (cfg.Channels.Count == 0)
        throw new InvalidOperationException("appsettings.json has no Channels configured.");

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

    var videos = Directory.GetFiles(cfg.WatchDirectory, "*.mp4")
        .Where(p => (DateTime.UtcNow - File.GetLastWriteTimeUtc(p)).TotalSeconds >= cfg.MinFileAgeSeconds)
        .OrderBy(File.GetCreationTimeUtc)
        .ToList();

    if (videos.Count == 0) { log.Info("No videos to process."); return; }

    log.Info($"Found {videos.Count} video(s).");

    foreach (var path in videos)
    {
        try
        {
            var sidecar = await ResolveSidecarAsync(path, publishersByChannel, cfg, log);
            var channel = string.IsNullOrWhiteSpace(sidecar.Channel) ? cfg.DefaultChannel : sidecar.Channel;

            if (!publishersByChannel.TryGetValue(channel, out var publishersByPlatform))
            {
                log.Error($"Failed {Path.GetFileName(path)}: channel \"{channel}\" has no entry under Channels in appsettings.json.");
                MoveWithSiblings(path, cfg.FailedDirectory);
                continue;
            }

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
            }

            await sidecar.SaveAsync(path);

            if (sidecar.AllTargetsTerminal())
            {
                MoveWithSiblings(path, cfg.DoneDirectory);
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
            MoveWithSiblings(path, cfg.FailedDirectory);
        }
    }
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
    foreach (var suffix in new[] { ".srt", ".thumb.jpg", ".tiktok-caption.txt" })
        MoveIfExists(Path.ChangeExtension(videoPath, suffix));
}

static async Task<Sidecar> ResolveSidecarAsync(
    string videoPath, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IPublisher>> publishersByChannel,
    AppConfig cfg, Logger log)
{
    var existing = await Sidecar.LoadAsync(videoPath);
    if (existing is not null)
    {
        log.Info($"Using sidecar metadata for {Path.GetFileName(videoPath)}");
        return existing;
    }

    log.Info("No sidecar — generating metadata from YouTube trends + Claude.");

    // A manually dropped video carries no channel signal, so it publishes
    // through DefaultChannel's credentials.
    var youtube = (YouTubePublisher)publishersByChannel[cfg.DefaultChannel][Platforms.YouTube];
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
        Channel = cfg.DefaultChannel,
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
