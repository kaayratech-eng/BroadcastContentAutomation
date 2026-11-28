// L31 (failure-logging plan): MetaGraph's bare catches collapsed every
// cause into "Unknown" (media-exists) / null (offset probe) with no log —
// a transient 500, a bad token, and a transport failure were
// indistinguishable. Both catches now log the exception type + message
// (the Unknown / null result is unchanged: retry, never re-publish blind).
//
// L35 (same plan): WaitForStatusAsync logged nothing for a failed status
// poll, so a poll throwing on attempt 1 vs attempt 20 was
// indistinguishable. Each failed poll now logs its attempt number + last
// status and the retry deadline, and polling continues until the deadline.
using System.Net;
using System.Text;
using System.Text.Json;
using GiggleGarden.Uploader.Publishing;

namespace BroadcastContentAutomation.Tests;

public class MetaGraphTests
{
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tn-metagraph-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ReadLog(string logDir)
    {
        var logFile = Directory.GetFiles(logDir, "upload-*.log").Single();
        return File.ReadAllText(logFile);
    }

    [Fact]
    public async Task MediaExists_TransportFailure_LogsReasonAndReturnsUnknown()
    {
        // The request never reaches Meta at all: the result must still be
        // Unknown (never publish blind), but the log must now name the
        // transport failure instead of collapsing it silently.
        var dir = TempDir("lookup");
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var logger = new GiggleGarden.Uploader.Logger(logDir);
            var handler = new CannedHandler(_ => throw new HttpRequestException("Connection reset by peer"));
            using var http = new HttpClient(handler);
            var task = ReflectionHelper.Invoke<Task<MediaLookup>>(ReflectionHelper.Uploader, "MediaExistsAsync",
                http, "https://graph.facebook.com/v21.0", "123456789", "test-token", CancellationToken.None, logger);
            var result = await task;

            Assert.Equal(MediaLookup.Unknown, result);
            var text = ReadLog(logDir);
            Assert.Contains("media-exists check failed for 123456789", text);
            Assert.Contains("HttpRequestException", text);
            Assert.Contains("Connection reset by peer", text);
            Assert.Contains("treating as Unknown", text);
            Assert.Contains("will retry not re-publish", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task WaitForStatus_PollFailure_LogsAttemptAndContinues()
    {
        // L35: polls 1–2 fail (transport), poll 3 succeeds. The log must
        // carry two Warn lines with attempt numbers, and the call must
        // return the final status instead of throwing on the first failure.
        // (The poll loop waits 5s, then 7.5s, then 11.25s between polls,
        // so this test takes ~24s.)
        var dir = TempDir("wait-status");
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var logger = new GiggleGarden.Uploader.Logger(logDir);
            var calls = 0;
            var handler = new CannedHandler(_ =>
            {
                calls++;
                if (calls <= 2) throw new HttpRequestException("Connection reset by peer");
                return JsonResponse(HttpStatusCode.OK, """{"status_code":"FINISHED","status":"done"}""");
            });
            using var http = new HttpClient(handler);
            Func<JsonElement, (bool Done, bool Failed, string Detail)> read = root =>
            {
                var code = root.TryGetProperty("status_code", out var sc) ? sc.GetString() ?? "" : "";
                var detail = root.TryGetProperty("status", out var st) ? st.GetString() ?? code : code;
                return (code == "FINISHED", code is "ERROR" or "EXPIRED", detail);
            };
            var task = ReflectionHelper.Invoke<Task<string>>(ReflectionHelper.Uploader, "WaitForStatusAsync",
                http, "https://graph.facebook.com/v21.0/123?fields=status_code,status", read, 120, logger,
                "instagram", CancellationToken.None);
            var detail = await task;

            Assert.Equal("done", detail);
            var text = ReadLog(logDir);
            Assert.Contains("status poll failed (attempt 1, last status unknown)", text);
            Assert.Contains("status poll failed (attempt 2, last status unknown)", text);
            Assert.Contains("HttpRequestException", text);
            Assert.Contains("retrying until", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
