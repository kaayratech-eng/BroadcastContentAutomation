// L22 — RetryHandler logged each attempt but never logged exhaustion:
// the give-up was invisible, the reason was only a status int or an
// exception type name, the `retry {attempt}/{maxAttempts - 1}` label
// printed `1/3` for attempt 1 of 4, and the large-body single-attempt
// path logged nothing about skipping retries.
using System.Net;
using System.Text;
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class RetryHandlerTests
{
    private sealed class CannedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return respond(request);
        }
    }

    [Fact]
    public async Task RetryHandler_ExhaustionLogsFinalOutcome()
    {
        // Stub handler returns 503 four times, each with a distinct body
        // and Retry-After: 0 (so the test does not sleep on backoff).
        var lines = new List<string>();
        var attempts = 0;
        var inner = new CannedHandler(_ =>
        {
            attempts++;
            var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent($"overloaded attempt {attempts}", Encoding.UTF8, "text/plain"),
            };
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(resp);
        });
        var handler = new RetryHandler(maxAttempts: 4, log: lines.Add) { InnerHandler = inner };
        using var client = new HttpClient(handler);
        var response = await client.GetAsync("https://example.test/x");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // The returned response is the 4th attempt's, body still readable.
        Assert.Equal("overloaded attempt 4", await response.Content.ReadAsStringAsync());
        Assert.Equal(4, attempts);
        var text = string.Join('\n', lines);
        Assert.Contains("gave up after 4 attempts", text);
        Assert.Contains("503", text);
        Assert.Contains("overloaded attempt 4", text);
        // The fixed label counts attempts against the max, not max - 1.
        Assert.Contains("attempt 1/4", text);
        Assert.DoesNotContain("1/3", text);
    }

    [Fact]
    public async Task RetryHandler_TransportExhaustionLogsMessageAndThrows()
    {
        // A transport failure carries its message (not just the type name)
        // on both the retry line and the exhaustion line, then throws.
        var lines = new List<string>();
        var inner = new CannedHandler(_ =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset by peer")));
        var handler = new RetryHandler(maxAttempts: 2, log: lines.Add) { InnerHandler = inner };
        using var client = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.test/x"));
        Assert.Contains("connection reset by peer", ex.Message);
        var text = string.Join('\n', lines);
        Assert.Contains("gave up after 2 attempts", text);
        Assert.Contains("connection reset by peer", text);
        Assert.Contains("HttpRequestException", text);
    }

    [Fact]
    public async Task RetryHandler_LargeBodyLogsSkipAndAttemptsOnce()
    {
        // A body over the retryable limit is a single attempt by design —
        // the skip must be logged, and the call must still go through once.
        var lines = new List<string>();
        var inner = new CannedHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var handler = new RetryHandler(maxAttempts: 4, log: lines.Add) { InnerHandler = inner };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/upload")
        {
            Content = new ByteArrayContent(new byte[16]),
        };
        request.Content.Headers.ContentLength = 9L * 1024 * 1024;
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.Calls);
        var text = string.Join('\n', lines);
        Assert.Contains("large body", text);
        Assert.Contains("no retries", text);
    }
}
