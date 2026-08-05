// GiggleGarden Daily Uploader (.NET 8)
// Watches D:\Business\Videos for .mp4 files, generates or reads metadata,
// uploads to YouTube with selfDeclaredMadeForKids=true, moves files to \done.
//
// Metadata resolution order per video:
//   1. Sidecar JSON  (myvideo.mp4 -> myvideo.json)  — used as-is if present
//   2. Auto-generate — pulls trending kids' video data from the YouTube API,
//      sends it + the video filename to the Claude API, receives
//      title/description/tags in English + Hindi + Punjabi.
//
// Scheduled via Windows Task Scheduler (see README).

using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
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

    var videos = Directory.GetFiles(cfg.WatchDirectory, "*.mp4")
        .OrderBy(File.GetCreationTimeUtc)
        .Take(cfg.MaxUploadsPerRun)   // quota guard: ~1600 units/upload, 10k/day
        .ToList();

    if (videos.Count == 0) { log.Info("No videos to upload."); return; }

    var youtube = await CreateYouTubeServiceAsync(cfg);
    log.Info($"Found {videos.Count} video(s). Beginning uploads.");

    foreach (var path in videos)
    {
        try
        {
            var meta = await ResolveMetadataAsync(path, youtube, cfg, log);

            if (cfg.RequireApproval && meta.Approved != true)
            {
                // Human-in-the-loop gate: write the generated metadata next to the
                // video and skip. You review, set "approved": true, next run uploads.
                var sidecar = Path.ChangeExtension(path, ".json");
                if (!File.Exists(sidecar))
                {
                    await File.WriteAllTextAsync(sidecar,
                        JsonSerializer.Serialize(meta, JsonOpts.Pretty));
                    log.Info($"Awaiting approval: {Path.GetFileName(sidecar)} written. " +
                             "Review it, set \"approved\": true, and it uploads next run.");
                }
                else
                {
                    log.Info($"Still awaiting approval: {Path.GetFileName(sidecar)}");
                }
                continue;
            }

            await UploadAsync(youtube, path, meta, cfg, log);

            var dest = Path.Combine(cfg.DoneDirectory, Path.GetFileName(path));
            File.Move(path, dest, overwrite: true);
            var sc = Path.ChangeExtension(path, ".json");
            if (File.Exists(sc)) File.Move(sc, Path.ChangeExtension(dest, ".json"), overwrite: true);
            log.Info($"Done: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            log.Error($"Failed {Path.GetFileName(path)}: {ex.Message}");
            File.Move(path, Path.Combine(cfg.FailedDirectory, Path.GetFileName(path)), overwrite: true);
        }
    }
}

static async Task<YouTubeService> CreateYouTubeServiceAsync(AppConfig cfg)
{
    using var stream = new FileStream(cfg.ClientSecretPath, FileMode.Open, FileAccess.Read);
    var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
        GoogleClientSecrets.FromStream(stream).Secrets,
        new[] { YouTubeService.Scope.YoutubeUpload, YouTubeService.Scope.YoutubeReadonly },
        "gigglegarden-channel-owner",                        // must match TokenCapture
        CancellationToken.None,
        new FileDataStore(cfg.TokenStorePath, fullPath: true));
    // Token already captured -> this never opens a browser; it refreshes silently.

    return new YouTubeService(new Google.Apis.Services.BaseClientService.Initializer
    {
        HttpClientInitializer = credential,
        ApplicationName = "GiggleGarden Uploader"
    });
}

static async Task<VideoMetadata> ResolveMetadataAsync(
    string videoPath, YouTubeService youtube, AppConfig cfg, Logger log)
{
    var sidecar = Path.ChangeExtension(videoPath, ".json");
    if (File.Exists(sidecar))
    {
        log.Info($"Using sidecar metadata for {Path.GetFileName(videoPath)}");
        return JsonSerializer.Deserialize<VideoMetadata>(
            await File.ReadAllTextAsync(sidecar), JsonOpts.CaseInsensitive)!;
    }

    log.Info("No sidecar — generating metadata from YouTube trends + Claude.");
    var trends = await FetchTrendingKidsDataAsync(youtube, cfg, log);
    return await GenerateMetadataAsync(videoPath, trends, cfg, log);
}

// ---- YouTube trend research (legit, via Data API; no scraping) --------------

static async Task<string> FetchTrendingKidsDataAsync(YouTubeService yt, AppConfig cfg, Logger log)
{
    var sb = new StringBuilder();
    try
    {
        // Top kid-focused results by view count for each research query.
        foreach (var q in cfg.TrendQueries)
        {
            var search = yt.Search.List("snippet");
            search.Q = q;
            search.Type = "video";
            search.Order = Google.Apis.YouTube.v3.SearchResource.ListRequest.OrderEnum.ViewCount;
            search.SafeSearch = Google.Apis.YouTube.v3.SearchResource.ListRequest.SafeSearchEnum.Strict;
            search.PublishedAfterDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-30);
            search.MaxResults = 5;
            var res = await search.ExecuteAsync();   // 100 quota units per call

            foreach (var item in res.Items)
                sb.AppendLine($"- \"{item.Snippet.Title}\" | channel: {item.Snippet.ChannelTitle}");
        }
    }
    catch (Exception ex)
    {
        log.Error($"Trend fetch failed (continuing with generic prompt): {ex.Message}");
    }
    return sb.ToString();
}

// ---- Claude metadata generation ---------------------------------------------

static async Task<VideoMetadata> GenerateMetadataAsync(
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

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("x-api-key", cfg.AnthropicApiKey);
    http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

    var resp = await http.PostAsync("https://api.anthropic.com/v1/messages",
        new StringContent(body, Encoding.UTF8, "application/json"));
    resp.EnsureSuccessStatusCode();

    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!;
    text = text.Replace("```json", "").Replace("```", "").Trim();

    var meta = JsonSerializer.Deserialize<VideoMetadata>(text, JsonOpts.CaseInsensitive)!;
    meta.Approved = false; // always requires the human glance when RequireApproval=true
    log.Info($"Generated metadata: {meta.Title}");
    return meta;
}

// ---- Upload ------------------------------------------------------------------

static async Task UploadAsync(
    YouTubeService yt, string path, VideoMetadata meta, AppConfig cfg, Logger log)
{
    var video = new Video
    {
        Snippet = new VideoSnippet
        {
            Title = meta.Title,
            Description = meta.Description,
            Tags = meta.Tags,
            CategoryId = "24", // Entertainment
            DefaultLanguage = "en",
            DefaultAudioLanguage = "en"
        },
        Status = new VideoStatus
        {
            PrivacyStatus = cfg.PrivacyStatus,        // start with "private" for testing
            SelfDeclaredMadeForKids = true,
            MadeForKids = true
        }
    };

    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
    var insert = yt.Videos.Insert(video, "snippet,status", fs, "video/mp4");
    insert.ChunkSize = ResumableUpload.MinimumChunkSize * 4;

    insert.ProgressChanged += p =>
    {
        if (p.Status == UploadStatus.Failed)
            log.Error($"Upload error: {p.Exception?.Message}");
    };

    var result = await insert.UploadAsync();
    if (result.Status != UploadStatus.Completed)
        throw new Exception($"Upload did not complete: {result.Exception?.Message}");

    log.Info($"Uploaded: https://youtu.be/{insert.ResponseBody.Id} ({cfg.PrivacyStatus})");
}

// ---- Support types -------------------------------------------------------------

record AppConfig
{
    public string WatchDirectory { get; init; } = @"D:\Business\Videos";
    public string DoneDirectory { get; init; } = @"D:\Business\Videos\done";
    public string FailedDirectory { get; init; } = @"D:\Business\Videos\failed";
    public string LogDirectory { get; init; } = @"D:\Business\Videos\logs";
    public string ClientSecretPath { get; init; } = "";
    public string TokenStorePath { get; init; } = "";
    public string AnthropicApiKey { get; init; } = "";
    public string ClaudeModel { get; init; } = "claude-sonnet-4-6";
    public string PrivacyStatus { get; init; } = "private";
    public int MaxUploadsPerRun { get; init; } = 3;
    public bool RequireApproval { get; init; } = true;
    public string[] TrendQueries { get; init; } =
        ["nursery rhymes for kids", "kids learning songs", "hindi rhymes for children"];
}

class VideoMetadata
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public bool? Approved { get; set; }
}

static class JsonOpts
{
    public static readonly JsonSerializerOptions CaseInsensitive =
        new() { PropertyNameCaseInsensitive = true };
    public static readonly JsonSerializerOptions Pretty =
        new() { WriteIndented = true };
}

class Logger(string dir)
{
    private readonly string _file = Path.Combine(
        Directory.CreateDirectory(dir).FullName,
        $"upload-{DateTime.Now:yyyy-MM-dd}.log");

    public void Info(string msg) => Write("INFO", msg);
    public void Error(string msg) => Write("ERROR", msg);

    private void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {msg}";
        Console.WriteLine(line);
        File.AppendAllText(_file, line + Environment.NewLine);
    }
}
