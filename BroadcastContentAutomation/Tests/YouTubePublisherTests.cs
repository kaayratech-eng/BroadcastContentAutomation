// L13 — PublishAsync must not let an unexpected exception escape to the
// per-video catch: it returns a target-level Retry naming the exception
// type, and a live video is never reported as failed.
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;
using GiggleGarden.Uploader.Publishing;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;

namespace BroadcastContentAutomation.Tests;

public class YouTubePublisherTests
{
    // Canned YouTube for the L32 thumbnail test: thumbnails.set answers
    // 403 with a real API error body.
    private sealed class Thumb403Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.Contains("/youtube/v3/thumbnails/set", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        """{"error":{"code":403,"message":"The request cannot be completed because you have exceeded your quota."}}""",
                        Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CannedHttpClientFactory : Google.Apis.Http.IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public CannedHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public Google.Apis.Http.ConfigurableHttpClient CreateHttpClient(Google.Apis.Http.CreateHttpClientArgs args) =>
            new(new Google.Apis.Http.ConfigurableMessageHandler(_handler));
    }

    private static YouTubeService CannedYouTube(HttpMessageHandler handler) =>
        new(new BaseClientService.Initializer
        {
            HttpClientFactory = new CannedHttpClientFactory(handler),
            ApplicationName = "l32-test",
        });

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "production", "finish-chunks.py")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate repo root (production/finish-chunks.py).");
        }
    }

    [Fact]
    public async Task ReconcileFailure_LogNamesVideoAndNextAction()
    {
        // L34: forcing GetServiceAsync to throw (no client-secrets file)
        // must log a Warn naming the video file and the next action, not
        // a bare "reconciliation failed" with no video.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l34-reconcile-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var videoPath = Path.Combine(dir, "my-video-en.mp4");
            File.WriteAllText(videoPath, "");
            var sidecar = new Sidecar
            {
                Title = "Test video",
                Description = "Test description.",
                Channel = "trendlinenow",
                Aspect = "landscape",
                Targets = [Platforms.YouTube],
            };
            sidecar.Publications[Platforms.YouTube] = new Publication
            {
                Status = PublishStatus.Published,
                Id = "vid123",
            };

            var asm = ReflectionHelper.Uploader;
            var cfgType = ReflectionHelper.FindType(asm, "YouTubeConfig");
            var cfg = Activator.CreateInstance(cfgType)!;
            Set(cfgType, cfg, "ClientSecretPath", Path.Combine(dir, "no-such-secrets.json"));
            Set(cfgType, cfg, "TokenStorePath", Path.Combine(dir, "token.json"));
            Set(cfgType, cfg, "Enabled", true);
            var logger = new GiggleGarden.Uploader.Logger(logDir);
            var pubType = ReflectionHelper.FindType(asm, "YouTubePublisher");
            var publisher = Activator.CreateInstance(pubType, cfg, logger)!;
            var method = pubType.GetMethod("ReconcileCrossLinksAsync", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException("YouTubePublisher.ReconcileCrossLinksAsync");
            var task = (Task)method.Invoke(publisher, [videoPath, sidecar, CancellationToken.None])!;
            await task;

            var text = ReadLog(logDir);
            Assert.Contains("cross-link reconciliation skipped for my-video-en.mp4", text);
            Assert.Contains("could not authorize YouTube", text);
            Assert.Contains("descriptions unchanged", text);
            Assert.Contains("will retry next run", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PinnedComment_AlreadyPosted_LogsSkipReason()
    {
        // L33: the PinnedCommentPosted early return used to be silent —
        // the caller could not distinguish "not a Short" from "already
        // posted". The skip line must name the video + Short id + reason.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l33-pin-posted-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var channelDir = Path.Combine(dir, "trendlinenow");
            Directory.CreateDirectory(channelDir);
            var shortPath = Path.Combine(channelDir, "slug-en-short.mp4");
            var sidecar = WriteSidecar(shortPath, "Short title", "Short desc.", "trendlinenow", "vertical",
                [("youtube", PublishStatus.Published, "short123")], pinnedCommentPosted: true);

            var publisher = YouTubePublisherWith(logDir);
            var result = RunEnsureComment(publisher, null!, shortPath, sidecar, "short123");
            Assert.False(result);

            var text = ReadLog(logDir);
            Assert.Contains("skipping Short comment for slug-en-short.mp4", text);
            Assert.Contains("short123", text);
            Assert.Contains("comment already posted", text);
            Assert.Contains("will not re-post", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PinnedComment_Forbidden403_LogNamesShortId()
    {
        // L33: the 403 line used to omit the Short id. A token predating
        // the youtube.force-ssl scope must fail with the Short id in the
        // log, and the flag must stay false so a later run retries.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l33-pin-403-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var channelDir = Path.Combine(dir, "trendlinenow");
            Directory.CreateDirectory(channelDir);
            var shortPath = Path.Combine(channelDir, "slug-en-short.mp4");
            var longPath = Path.Combine(channelDir, "slug-en.mp4");
            WriteSidecar(shortPath, "Short title", "Short desc.", "trendlinenow", "vertical",
                [("youtube", PublishStatus.Published, "short123")]);
            WriteSidecar(longPath, "Long title", "Long desc.", "trendlinenow", "landscape",
                [("youtube", PublishStatus.Published, "long456")]);

            var publisher = YouTubePublisherWith(logDir);
            var youtube = CannedYouTube(new Comment403Handler());
            var sidecar = (await Sidecar.LoadAsync(shortPath))!;
            var result = RunEnsureComment(publisher, youtube, shortPath, sidecar, "short123");
            Assert.False(result);
            Assert.False((await Sidecar.LoadAsync(shortPath))!.PinnedCommentPosted);

            var text = ReadLog(logDir);
            Assert.Contains("Short comment not posted for short123", text);
            Assert.Contains("403", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class Comment403Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.Contains("/youtube/v3/commentThreads", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        """{"error":{"code":403,"message":"The request cannot be completed because you have exceeded your quota."}}""",
                        Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static object YouTubePublisherWith(string logDir)
    {
        var asm = ReflectionHelper.Uploader;
        var cfgType = ReflectionHelper.FindType(asm, "YouTubeConfig");
        var cfg = Activator.CreateInstance(cfgType)!;
        var logger = new GiggleGarden.Uploader.Logger(logDir);
        var pubType = ReflectionHelper.FindType(asm, "YouTubePublisher");
        return Activator.CreateInstance(pubType, cfg, logger)!;
    }

    private static bool RunEnsureComment(object publisher, YouTubeService youtube, string videoPath, Sidecar sidecar,
        string? justPublishedShortId = null)
    {
        var method = publisher.GetType().GetMethod("EnsurePinnedCommentAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException("YouTubePublisher.EnsurePinnedCommentAsync");
        var task = (Task<bool>)method.Invoke(publisher,
            [youtube, videoPath, sidecar, CancellationToken.None, justPublishedShortId])!;
        return task.GetAwaiter().GetResult();
    }

    private static Sidecar WriteSidecar(string videoPath, string title, string description, string channel,
        string aspect, (string Platform, PublishStatus Status, string? Id)[] publications,
        bool pinnedCommentPosted = false)
    {
        File.WriteAllText(videoPath, "");
        var sidecar = new Sidecar
        {
            Title = title,
            Description = description,
            Channel = channel,
            Aspect = aspect,
            Targets = [Platforms.YouTube],
            PinnedCommentPosted = pinnedCommentPosted,
        };
        foreach (var (platform, status, id) in publications)
            sidecar.Publications[platform] = new Publication { Status = status, Id = id };
        File.WriteAllText(Sidecar.PathFor(videoPath), JsonSerializer.Serialize(sidecar, Sidecar.Options));
        return sidecar;
    }

    private static string ReadLog(string logDir)
    {
        var logFile = Directory.GetFiles(logDir, "upload-*.log").Single();
        return File.ReadAllText(logFile);
    }

    [Fact]
    public async Task ThumbnailFailure_LogNamesVideoId()
    {
        // L32: a failed post-publish thumbnail used to log with no video
        // id, so the line could not be tied to a video. Fail thumbnails.set
        // and assert the error line names the video id.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l32-thumb-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var videoPath = Path.Combine(dir, "my-video-en.mp4");
            File.WriteAllText(videoPath, "");
            File.WriteAllBytes(Path.ChangeExtension(videoPath, ".thumb.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);

            var asm = ReflectionHelper.Uploader;
            var cfgType = ReflectionHelper.FindType(asm, "YouTubeConfig");
            var cfg = Activator.CreateInstance(cfgType)!;
            Set(cfgType, cfg, "Enabled", true);
            var logger = new GiggleGarden.Uploader.Logger(logDir);
            var pubType = ReflectionHelper.FindType(asm, "YouTubePublisher");
            var publisher = Activator.CreateInstance(pubType, cfg, logger)!;
            var method = pubType.GetMethod("TrySetThumbnailAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("YouTubePublisher.TrySetThumbnailAsync");
            var youtube = CannedYouTube(new Thumb403Handler());
            var task = (Task)method.Invoke(publisher,
                [youtube, "vid123", videoPath, new Sidecar(), CancellationToken.None])!;
            await task;

            var logFile = Directory.GetFiles(logDir, "upload-*.log").Single();
            var text = File.ReadAllText(logFile);
            Assert.Contains("thumbnail upload failed for vid123", text);
            Assert.Contains("video still published", text);
            Assert.Contains("[ERROR]", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PostPublishLines_NameVideoIdAndStallIsWarn()
    {
        // L32 source guard for the other three lines the behavioral test
        // does not drive: the upload-stall line (promoted Info → Warn,
        // with the filename) and the playlist + caption failure lines
        // (with the video id).
        var publisher = File.ReadAllText(Path.Combine(RepoRoot,
            "BroadcastContentAutomation", "Uploader", "Publishing", "YouTubePublisher.cs"));
        Assert.Contains("log.Warn($\"  [youtube] upload attempt {attempt}/4 for {Path.GetFileName(request.VideoPath)} stalled", publisher);
        Assert.Contains("log.Error($\"  [youtube] playlist add failed for {id} (video still published)", publisher);
        Assert.Contains("log.Error($\"  [youtube] caption upload failed for {videoId} (video still published)", publisher);
    }

    [Fact]
    public async Task UnexpectedException_ReturnsRetryNotThrow()
    {
        // A client-secrets file with no installed/web section makes
        // credential setup throw InvalidOperationException — an unexpected
        // shape the old catches did not cover, so it used to escape to the
        // per-video catch and burn a crash retry.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"youtube-unexpected-{Guid.NewGuid():N}")).FullName;
        try
        {
            var secretsPath = Path.Combine(dir, "client-secrets.json");
            File.WriteAllText(secretsPath, "{}");
            var video = Path.Combine(dir, "my-video-en.mp4");
            await File.WriteAllTextAsync(video, string.Empty);

            // YouTubePublisher / YouTubeConfig are internal to Uploader —
            // construct them via reflection (same pattern as the
            // TikTokPublisher test).
            var asm = ReflectionHelper.Uploader;
            var cfgType = ReflectionHelper.FindType(asm, "YouTubeConfig");
            var cfg = Activator.CreateInstance(cfgType)!;
            Set(cfgType, cfg, "ClientSecretPath", secretsPath);
            Set(cfgType, cfg, "TokenStorePath", Path.Combine(dir, "token.json"));
            Set(cfgType, cfg, "Enabled", true);
            var logger = new GiggleGarden.Uploader.Logger(Path.Combine(dir, "logs"));
            var pubType = ReflectionHelper.FindType(asm, "YouTubePublisher");
            var publisher = Activator.CreateInstance(pubType, cfg, logger)!;
            var publish = pubType.GetMethod("PublishAsync", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException("YouTubePublisher.PublishAsync");
            var request = new PublishRequest(video, new Sidecar());
            var task = (Task<PublishResult>)publish.Invoke(publisher, [request, CancellationToken.None])!;
            var result = await task;

            Assert.False(result.Success);
            Assert.False(result.Permanent);
            Assert.Contains("YouTube unexpected error", result.Error);
            Assert.Contains("InvalidOperationException", result.Error);
            Assert.Contains("re-verified via ExistingId", result.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Set(Type type, object instance, string name, object? value)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException($"YouTubeConfig.{name}");
        prop.SetValue(instance, value);
    }
}
