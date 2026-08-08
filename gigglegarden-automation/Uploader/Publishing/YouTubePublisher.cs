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

    public bool Accepts(Sidecar sidecar, out string? reason)
    {
        reason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        try
        {
            var youtube = await GetServiceAsync();

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
                    SelfDeclaredMadeForKids = true,
                    MadeForKids = true,
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

            var result = await insert.UploadAsync(ct);
            if (result.Status != UploadStatus.Completed)
                return PublishResult.Retry($"Upload did not complete: {uploadError ?? result.Exception?.Message}");

            var id = insert.ResponseBody.Id;
            log.Info($"  [youtube] uploaded https://youtu.be/{id} ({cfg.PrivacyStatus})");

            await TrySetThumbnailAsync(youtube, id, request.VideoPath, ct);
            await TryUploadCaptionsAsync(youtube, id, request.VideoPath, request.Sidecar.Language, ct);

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
    }

    // Custom thumbnail is the single biggest click-through lever on YouTube, so VideoGen
    // generates one for every landscape render; a failure here must not turn an
    // otherwise successful video publish into a retry/abandon. Note: custom thumbnails
    // require a phone-verified channel - Google silently rejects them otherwise, which
    // is a one-time manual step in YouTube Studio, not a bug in this code.
    private async Task TrySetThumbnailAsync(YouTubeService youtube, string videoId, string videoPath, CancellationToken ct)
    {
        var thumbPath = Path.ChangeExtension(videoPath, ".thumb.jpg");
        if (!File.Exists(thumbPath)) return;

        try
        {
            using var stream = new FileStream(thumbPath, FileMode.Open, FileAccess.Read);
            var set = youtube.Thumbnails.Set(videoId, stream, "image/jpeg");
            await set.UploadAsync(ct);
            log.Info("  [youtube] custom thumbnail set");
        }
        catch (Exception ex)
        {
            log.Error($"  [youtube] thumbnail upload failed (video still published): {ex.Message}");
        }
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
            log.Error($"  [youtube] caption upload failed (video still published): {ex.Message}");
        }
    }

    // Trend research (legit, via the Data API; no scraping) used by Program.cs
    // to seed Claude's metadata prompt when a video has no sidecar. Kept here
    // rather than in Program.cs so it reuses the same authorized service
    // instead of creating a second one.
    public async Task<string> FetchTrendingDataAsync(IReadOnlyList<string> queries, Logger log, CancellationToken ct)
    {
        var youtube = await GetServiceAsync();
        var sb = new StringBuilder();
        try
        {
            foreach (var q in queries)
            {
                var search = youtube.Search.List("snippet");
                search.Q = q;
                search.Type = "video";
                search.Order = SearchResource.ListRequest.OrderEnum.ViewCount;
                search.SafeSearch = SearchResource.ListRequest.SafeSearchEnum.Strict;
                search.PublishedAfterDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-30);
                search.MaxResults = 5;
                var res = await search.ExecuteAsync(ct);   // 100 quota units per call

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

    private async Task<YouTubeService> GetServiceAsync()
    {
        if (_service is not null) return _service;

        using var stream = new FileStream(cfg.ClientSecretPath, FileMode.Open, FileAccess.Read);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            new[] { YouTubeService.Scope.YoutubeUpload, YouTubeService.Scope.YoutubeReadonly },
            "gigglegarden-channel-owner",                        // must match TokenCapture
            CancellationToken.None,
            new FileDataStore(cfg.TokenStorePath, fullPath: true));

        _service = new YouTubeService(new Google.Apis.Services.BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "GiggleGarden Uploader",
        });
        return _service;
    }
}
