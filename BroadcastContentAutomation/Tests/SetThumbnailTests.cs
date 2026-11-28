// L26 (failure-logging plan): --set-thumbnail failure was logged at Info
// and lost the reason at the call site — SetThumbnailAsync returned only a
// bare bool while the API error stayed inside the publisher. It now returns
// (bool Ok, string? Error) and Program logs the failure at Error level with
// the API reason, exiting 1.
using System.Net;
using System.Reflection;
using System.Text;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;

namespace BroadcastContentAutomation.Tests;

public class SetThumbnailTests
{
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

    // Canned YouTube: the video exists (Videos.List returns it), but
    // thumbnails.set answers 403 with a real API error body.
    private sealed class Thumb403Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/youtube/v3/videos", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"items":[{"id":"vid123"}]}"""));
            if (request.Method == HttpMethod.Post && path.Contains("/youtube/v3/thumbnails/set", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(HttpStatusCode.Forbidden,
                    """{"error":{"code":403,"message":"The request cannot be completed because you have exceeded your quota."}}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
            ApplicationName = "set-thumbnail-test",
        });

    [Fact]
    public async Task Failure_LogsApiReasonAtErrorLevel()
    {
        // A stubbed 403 on thumbnails.set: the returned error must carry
        // the API message, and the log must have it at ERROR level with
        // the video id — the old bare-bool return lost both.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l26-thumb-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var thumbPath = Path.Combine(dir, "thumb.jpg");
            File.WriteAllBytes(thumbPath, [0xFF, 0xD8, 0xFF, 0xE0]);

            var asm = ReflectionHelper.Uploader;
            var cfgType = ReflectionHelper.FindType(asm, "YouTubeConfig");
            var cfg = Activator.CreateInstance(cfgType)!;
            cfgType.GetProperty("Enabled")!.SetValue(cfg, true);
            var logger = new GiggleGarden.Uploader.Logger(logDir);
            var pubType = ReflectionHelper.FindType(asm, "YouTubePublisher");
            var publisher = Activator.CreateInstance(pubType, cfg, logger)!;
            var method = pubType.GetMethod("SetThumbnailWithServiceAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("YouTubePublisher.SetThumbnailWithServiceAsync");
            var youtube = CannedYouTube(new Thumb403Handler());
            var task = (Task<(bool Ok, string? Error)>)method.Invoke(publisher,
                [youtube, "vid123", thumbPath, CancellationToken.None])!;
            var result = await task;

            Assert.False(result.Ok);
            Assert.NotNull(result.Error);
            Assert.Contains("403", result.Error);
            Assert.Contains("exceeded your quota", result.Error);

            var logFile = Directory.GetFiles(logDir, "upload-*.log").Single();
            var text = File.ReadAllText(logFile);
            Assert.Contains("[ERROR]", text);
            Assert.Contains("vid123", text);
            Assert.Contains("exceeded your quota", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProgramLogsFailureAtErrorLevelAndExits1()
    {
        // Source guard: the --set-thumbnail call site in Program.cs must
        // log a failure at Error level with the returned reason (not the
        // old Info line with no reason) and still exit 1.
        var program = File.ReadAllText(Path.Combine(RepoRoot,
            "BroadcastContentAutomation", "Uploader", "Program.cs"));
        Assert.Contains("log.Error($\"--set-thumbnail: FAILED to set thumbnail for https://youtu.be/{videoId}: {error};", program);
        Assert.Contains("thumbnail unchanged, retry with a valid image/video id", program);
        Assert.Contains("return ok ? 0 : 1;", program);
    }
}
