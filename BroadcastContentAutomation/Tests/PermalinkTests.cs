// L74 + L75 (failure-logging plan, Medium): permalink fetch failures were
// swallowed silently (bare `catch (Exception) { return null; }`), and a
// TikTok platform-side processing rejection was logged at Info. The
// permalink line now names the media id + reason; the TikTok processing
// failure is logged at Error (the target still retries next run per B20 —
// only the level changed, not the outcome).
using System.Reflection;
using System.Text;
using GiggleGarden.Uploader;

namespace BroadcastContentAutomation.Tests;

public class PermalinkTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tn-permalink-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string LogText(string logDir)
        => File.ReadAllText(Directory.GetFiles(logDir, "upload-*.log").Single());

    // A handler that throws on every request.
    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException(message);
    }

    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static object MakePublisher(string typeName, object cfg, Logger logger)
    {
        var asm = ReflectionHelper.Uploader;
        var type = ReflectionHelper.FindType(asm, typeName);
        return Activator.CreateInstance(type, cfg, logger)!;
    }

    private static async Task<string?> InvokeTryPermalinkAsync(object publisher, HttpClient http, string mediaId)
    {
        var method = publisher.GetType().GetMethod("TryPermalinkAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException("TryPermalinkAsync");
        var task = (Task<string?>)method.Invoke(publisher, [http, mediaId, CancellationToken.None])!;
        return await task;
    }

    [Fact]
    public async Task PermalinkFailure_LogsAndReturnsNull()
    {
        var dir = TempDir("ig");
        try
        {
            var logDir = Path.Combine(dir, "logs");
            var logger = new Logger(logDir);
            var asm = ReflectionHelper.Uploader;
            var cfg = Activator.CreateInstance(ReflectionHelper.FindType(asm, "InstagramConfig"))!;
            var publisher = MakePublisher("InstagramPublisher", cfg, logger);
            using var http = new HttpClient(new ThrowingHandler("boom-permalink"));

            var result = await InvokeTryPermalinkAsync(publisher, http, "17841457625867299");

            Assert.Null(result);
            var log = LogText(logDir);
            Assert.Contains("permalink fetch failed", log);
            Assert.Contains("17841457625867299", log);
            Assert.Contains("boom-permalink", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StoryPermalinkFailure_LogsAndReturnsNull()
    {
        var dir = TempDir("ig-story");
        try
        {
            var logDir = Path.Combine(dir, "logs");
            var logger = new Logger(logDir);
            var asm = ReflectionHelper.Uploader;
            var cfg = Activator.CreateInstance(ReflectionHelper.FindType(asm, "InstagramConfig"))!;
            var publisher = MakePublisher("InstagramStoryPublisher", cfg, logger);
            using var http = new HttpClient(new ThrowingHandler("boom-story-permalink"));

            var result = await InvokeTryPermalinkAsync(publisher, http, "17900000000000000");

            Assert.Null(result);
            var log = LogText(logDir);
            Assert.Contains("permalink fetch failed", log);
            Assert.Contains("17900000000000000", log);
            Assert.Contains("boom-story-permalink", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TikTokProcessingFailed_LoggedAtErrorLevel()
    {
        var dir = TempDir("tiktok");
        try
        {
            var logDir = Path.Combine(dir, "logs");
            var logger = new Logger(logDir);
            var asm = ReflectionHelper.Uploader;
            var cfgType = ReflectionHelper.FindType(asm, "TikTokConfig");
            var cfg = Activator.CreateInstance(cfgType)!;
            cfgType.GetProperty("Enabled")!.SetValue(cfg, true);
            var publisher = MakePublisher("TikTokPublisher", cfg, logger);
            using var http = new HttpClient(new CannedHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data": {"status": "FAILED", "fail_reason": "audio rejected"}}""",
                    Encoding.UTF8, "application/json")
            }));

            var method = publisher.GetType().GetMethod("WaitForPublishAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("WaitForPublishAsync");
            // WaitForPublishAsync starts with a 5s delay before the first poll.
            var task = (Task)method.Invoke(publisher, [http, "v2.12345", CancellationToken.None])!;
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;

            // The target retries next run (B20) — only the log level changed.
            var resultType = result.GetType();
            Assert.False((bool)resultType.GetProperty("Success")!.GetValue(result)!);
            Assert.Contains("processing failed", (string)resultType.GetProperty("Error")!.GetValue(result)!);
            var log = LogText(logDir);
            Assert.Contains("[ERROR]", log);
            Assert.Contains("processing failed", log);
            Assert.Contains("audio rejected", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
