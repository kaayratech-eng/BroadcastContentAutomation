using System.Net;

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
        var length = request.Content?.Headers.ContentLength;
        if (length is > MaxRetryableBodyBytes)
            return await base.SendAsync(request, ct);

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

            var reason = response is not null ? ((int)response.StatusCode).ToString() : transportError?.GetType().Name;
            _log?.Invoke($"{request.Method} {request.RequestUri?.Host} -> {reason}; retry {attempt}/{_maxAttempts - 1} in {delay.TotalSeconds:0.#}s");
            await Task.Delay(delay, ct);
        }

        if (response is not null) return response;
        throw transportError ?? new HttpRequestException("Request failed with no response.");
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
