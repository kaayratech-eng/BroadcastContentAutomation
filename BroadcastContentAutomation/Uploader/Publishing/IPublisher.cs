using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

public sealed record PublishRequest(string VideoPath, Sidecar Sidecar)
{
    public long SizeBytes => new FileInfo(VideoPath).Length;
    public string FileName => Path.GetFileName(VideoPath);
}

public sealed record PublishResult(bool Success, string? Id = null, string? Url = null, string? Error = null, bool Permanent = false)
{
    public static PublishResult Ok(string id, string? url = null) => new(true, id, url);

    // Retryable: transient network / rate limit / processing timeout.
    public static PublishResult Retry(string error) => new(false, Error: error);

    // Permanent: rejected content, bad credentials, unsupported format. Retrying
    // will not help, and silently retrying forever is how a queue fills with corpses.
    public static PublishResult Fatal(string error) => new(false, Error: error, Permanent: true);
}

public interface IPublisher
{
    string Platform { get; }
    bool Enabled { get; }
    int MaxPerRun { get; }

    // Content-shape gate. Instagram and TikTok only take the vertical cut; returning
    // false here marks the target Skipped rather than burning retries on a rejection.
    bool Accepts(Sidecar sidecar, out string? reason);

    Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct);
}
