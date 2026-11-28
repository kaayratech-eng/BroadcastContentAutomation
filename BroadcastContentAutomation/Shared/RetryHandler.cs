using System.Net;
using System.Text;

namespace GiggleGarden.Shared;

// Retries transient failures (408 / 429 / 5xx / transport errors) with exponential
// backoff plus jitter, honouring Retry-After when the server sends one.
//
// Large bodies are NOT retried: re-sending requires buffering the request into
// memory, which is fine for a JSON prompt and wasteful for a 200 MB video. Upload
// calls handle their own resume semantics instead.
public sealed class RetryHandler : DelegatingHandler
{
    private const long MaxRetryableBodyBytes = 8L * 1024 * 1024;

    private readonly int _maxAttempts;
    private readonly Action<string>? _log;

    public RetryHandler(int maxAttempts = 4, Action<string>? log = null)
        : base(new HttpClientHandler())
    {
        _maxAttempts = Math.Max(1, maxAttempts);
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // A body we can't cheaply re-send means a single attempt, no retries.
        // L22: that used to log nothing — a large-body call that failed
        // looked like any other single failure, with no record that retries
        // were skipped by design.
        var length = request.Content?.Headers.ContentLength;
        if (length is > MaxRetryableBodyBytes)
        {
            _log?.Invoke($"{request.Method} {request.RequestUri?.Host} -> large body ({length} bytes) exceeds the {MaxRetryableBodyBytes}-byte retryable limit; single attempt, no retries");
            return await base.SendAsync(request, ct);
        }

        HttpResponseMessage? response = null;
        Exception? transportError = null;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            response?.Dispose();
            response = null;
            transportError = null;

            var attemptRequest = attempt == 1 ? request : await CloneAsync(request, ct);

            try
            {
                response = await base.SendAsync(attemptRequest, ct);
                if (!IsTransient(response.StatusCode)) return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                transportError = ex;
            }

            if (attempt == _maxAttempts) break;

            var delay = response is not null
                ? RetryAfter(response) ?? Backoff(attempt)
                : Backoff(attempt);

            // L22: the reason used to be only the status-code int or the
            // exception TYPE name — no message, no body. Include the
            // message (truncated) so the retry line is diagnosable.
            var reason = response is not null
                ? ((int)response.StatusCode).ToString()
                : transportError is null
                    ? "no response"
                    : $"{transportError.GetType().Name}: {Text.Tail(transportError.Message.Replace('\n', ' ').Replace('\r', ' '), 200)}";
            // L22: the label was `retry {attempt}/{_maxAttempts - 1}`, so
            // attempt 1 of 4 printed `1/3`. The total is the max attempts.
            _log?.Invoke($"{request.Method} {request.RequestUri?.Host} -> {reason}; retry attempt {attempt}/{_maxAttempts} in {delay.TotalSeconds:0.#}s");
            await Task.Delay(delay, ct);
        }

        // L22: exhaustion used to be invisible — the loop just returned the
        // last response or threw, with no final line. Log the give-up with
        // the last status/body (or the transport error's message) and what
        // happens next, before returning/throwing. The log callback is
        // preserved as-is: every caller passes one (see the call sites),
        // and a missing callback still means no retry logging anywhere.
        if (response is not null)
        {
            var body = await ReadBodySnippetAsync(response, ct);
            _log?.Invoke($"{request.Method} {request.RequestUri?.Host} -> gave up after {_maxAttempts} attempts (last status {(int)response.StatusCode}{body}) — returning last response, caller falls through to next leg");
            return response;
        }
        var lastError = transportError is null
            ? "no response"
            : $"{transportError.GetType().Name}: {Text.Tail(transportError.Message.Replace('\n', ' ').Replace('\r', ' '), 200)}";
        _log?.Invoke($"{request.Method} {request.RequestUri?.Host} -> gave up after {_maxAttempts} attempts ({lastError}) — throwing, caller falls through to next leg");
        throw transportError ?? new HttpRequestException("Request failed with no response.");
    }

    // Truncated body of the last response, for the exhaustion line. The
    // content is buffered and restored, so the caller can still read the
    // full body from the returned response afterwards.
    private static async Task<string> ReadBodySnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null) return "";
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var restored = new ByteArrayContent(bytes);
            foreach (var header in response.Content.Headers)
                restored.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = restored;
            var text = Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(text)) return "";
            return $"; body: \"{Text.Tail(text.Replace('\n', ' ').Replace('\r', ' '), 200)}\"";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout
             or HttpStatusCode.TooManyRequests
             or HttpStatusCode.InternalServerError
             or HttpStatusCode.BadGateway
             or HttpStatusCode.ServiceUnavailable
             or HttpStatusCode.GatewayTimeout
        || (int)code == 529; // Anthropic "overloaded"

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return Clamp(delta);
        if (ra?.Date is { } date) return Clamp(date - DateTimeOffset.UtcNow);
        return null;

        static TimeSpan Clamp(TimeSpan t) =>
            t < TimeSpan.Zero ? TimeSpan.Zero : t > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : t;
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 750));

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri) { Version = req.Version };

        foreach (var header in req.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (req.Content is not null)
        {
            var bytes = await req.Content.ReadAsByteArrayAsync(ct);
            var content = new ByteArrayContent(bytes);
            foreach (var header in req.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = content;
        }

        return clone;
    }
}
