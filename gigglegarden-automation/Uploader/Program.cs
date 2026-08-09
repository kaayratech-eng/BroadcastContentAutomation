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

    var publishers = new IPublisher[]
    {
        new YouTubePublisher(cfg.Platforms.YouTube, log),
        new InstagramPublisher(cfg.Platforms.Instagram, log),
        new FacebookPublisher(cfg.Platforms.Facebook, log),
        new TikTokPublisher(cfg.Platforms.TikTok, log),
    };
    var publishersByPlatform = publishers.ToDictionary(p => p.Platform, StringComparer.OrdinalIgnoreCase);

    // Quota guard, tracked per platform across the whole run rather than
    // per video — a platform that hits its MaxPerRun stops accepting new
    // dispatches, but earlier videos it already handled are unaffected.
    var dispatchedThisRun = publishers.ToDictionary(p => p.Platform, _ => 0, StringComparer.OrdinalIgnoreCase);

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
            var sidecar = await ResolveSidecarAsync(path, publishersByPlatform, cfg, log);

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

                if (dispatchedThisRun[publisher.Platform] >= publisher.MaxPerRun)
                {
                    log.Info($"  [{publisher.Platform}] run quota reached ({publisher.MaxPerRun}); deferring {Path.GetFileName(path)} to next run.");
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

                dispatchedThisRun[publisher.Platform]++;

                var publication = sidecar.PublicationFor(target);
                publication.Attempts++;

                var result = await publisher.PublishAsync(new PublishRequest(path, sidecar), CancellationToken.None);

                if (result.Success)
                {
                    publication.Status = PublishStatus.Published;
                    publication.Id = result.Id;
                    publication.Url = result.Url;
                    publication.At = DateTimeOffset.UtcNow;
                    log.Info($"  [{publisher.Platform}] published: {result.Url ?? result.Id}");
                }
                else if (result.Permanent || publication.Attempts >= cfg.MaxAttemptsPerPlatform)
                {
                    publication.Status = PublishStatus.Abandoned;
                    publication.Error = result.Error;
                    log.Error($"  [{publisher.Platform}] abandoned after {publication.Attempts} attempt(s): {result.Error}");
                }
                else
                {
                    publication.Status = PublishStatus.Failed;
                    publication.Error = result.Error;
                    log.Error($"  [{publisher.Platform}] failed (attempt {publication.Attempts}/{cfg.MaxAttemptsPerPlatform}, will retry): {result.Error}");
                }
            }

            await sidecar.SaveAsync(path);

            if (sidecar.AllTargetsTerminal())
            {
                var dest = Path.Combine(cfg.DoneDirectory, Path.GetFileName(path));
                File.Move(path, dest, overwrite: true);
                var sc = Sidecar.PathFor(path);
                if (File.Exists(sc)) File.Move(sc, Sidecar.PathFor(dest), overwrite: true);

                // Sibling artifacts VideoGen wrote alongside the video (captions, custom
                // thumbnail) - without this they're orphaned in the watch directory forever,
                // since only the .mp4 and its sidecar were ever moved.
                foreach (var suffix in new[] { ".srt", ".thumb.jpg" })
                {
                    var src = Path.ChangeExtension(path, suffix);
                    if (File.Exists(src)) File.Move(src, Path.ChangeExtension(dest, suffix), overwrite: true);
                }

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
            File.Move(path, Path.Combine(cfg.FailedDirectory, Path.GetFileName(path)), overwrite: true);
        }
    }
}

static async Task<Sidecar> ResolveSidecarAsync(
    string videoPath, IReadOnlyDictionary<string, IPublisher> publishersByPlatform, AppConfig cfg, Logger log)
{
    var existing = await Sidecar.LoadAsync(videoPath);
    if (existing is not null)
    {
        log.Info($"Using sidecar metadata for {Path.GetFileName(videoPath)}");
        return existing;
    }

    log.Info("No sidecar — generating metadata from YouTube trends + Claude.");

    var youtube = (YouTubePublisher)publishersByPlatform[Platforms.YouTube];
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
