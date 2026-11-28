// L20 — an anonymous Pollinations 429 must log the scene, the attempt
// and the wait instead of stalling 30–60s in complete silence. (Note:
// AutomatedArtClient is currently uncalled — the pipeline uses the keyed
// PollinationsKeyedClient since 2026-11-11 — so this guards a dormant
// path; the log line is implemented exactly as the plan specifies it.)
using System.Net;

namespace BroadcastContentAutomation.Tests;

public class AutomatedArtTests
{
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public CannedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            _respond(request);
    }

    [Fact]
    public async Task AutomatedArt_429LogsWaitAndAttempt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qg-autoart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var output = Path.Combine(dir, "scene01-art.png");
            var calls = 0;
            using var client = new HttpClient(new CannedHandler(_ =>
            {
                calls++;
                // First call: 429. Second call: 200 with a real-sized body.
                if (calls == 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[2048])
                });
            }));

            var sb = new System.Text.StringBuilder();
            var oldOut = Console.Out;
            string result;
            try
            {
                Console.SetOut(new StringWriter(sb));
                // delaySeconds 0 skips the 15s throttle; retryWaitBaseSeconds
                // 1 keeps the 429 backoff to a 1s real wait (production: 30).
                result = await AutomatedArtClient.GenerateAsync("a test prompt", output, "flux",
                    delaySeconds: 0, maxAttempts: 3, http: client, retryWaitBaseSeconds: 1);
            }
            finally
            {
                Console.SetOut(oldOut);
            }

            Assert.Equal(output, result);
            Assert.Equal(2, calls);
            Assert.True(File.Exists(output));
            var log = sb.ToString();
            Assert.Contains("scene01-art.png", log);
            Assert.Contains("HTTP 429", log);
            Assert.Contains("attempt 1/3", log);
            Assert.Contains("waiting 1s before retry", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
