// L17 — Wikipedia fetch failures must be logged with the reason (HTTP
// status / exception type + query) and return null, never throw.
using System.Net;
using System.Text;

namespace BroadcastContentAutomation.Tests;

public class WikipediaTests
{
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public CannedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            _respond(request);
    }

    [Fact]
    public async Task Wikipedia_FetchFailureLogsReasonAndReturnsNull()
    {
        var sb = new StringBuilder();
        var oldOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter(sb));

            // (a) Wikipedia returns 500: the status is logged, caller gets null.
            using var client500 = new HttpClient(new CannedHandler(_ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
                { Content = new StringContent("oops", Encoding.UTF8, "text/plain") })));
            var r500 = await WikipediaClient.FetchSummaryForTitleAsync("GTA_6", client500);
            Assert.Null(r500);
            Assert.Contains("GTA_6", sb.ToString());
            Assert.Contains("HTTP 500", sb.ToString());
            Assert.Contains("returning null", sb.ToString());

            // (b) Timeout: the exception type is logged, caller gets null.
            sb.Clear();
            using var clientTimeout = new HttpClient(new CannedHandler(
                _ => throw new TaskCanceledException("simulated timeout")));
            var rTimeout = await WikipediaClient.FetchSummaryForTitleAsync("GTA_6", clientTimeout);
            Assert.Null(rTimeout);
            Assert.Contains("GTA_6", sb.ToString());
            Assert.Contains("TaskCanceledException", sb.ToString());
            Assert.Contains("returning null", sb.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
        }
    }
}
