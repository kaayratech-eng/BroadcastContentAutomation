using GiggleGarden.Shared;

namespace GiggleGarden.Uploader.Publishing;

public sealed record PublishRequest(string VideoPath, Sidecar Sidecar)
{
    public long SizeBytes => new FileInfo(VideoPath).Length;
    public string FileName => Path.GetFileName(VideoPath);

    // Platform ID recorded by a previous run whose publish reached the platform
    // but died before the sidecar was saved (the VM restarts every ~2-4h, so
    // this is a realistic event). A publisher that finds this set must verify
    // the post is actually live before creating a new one, so a restart
    // mid-upload cannot double-publish. Null on a first attempt.
    public string? ExistingId { get; init; }

    // Called by the publisher the moment the platform returns a live ID, so
    // Program.cs can persist it (sidecar.SaveAsync) before any post-publish
    // work (thumbnail, captions, permalink). Without this, a kill between
    // "live on the platform" and "recorded" re-publishes on the next run.
    // Records the ID only — the publish outcome (Published/Failed/Abandoned)
    // is still decided by the returned PublishResult.
    public Func<string, Task>? OnPlatformIdAsync { get; init; }
}

public sealed record PublishResult(bool Success, string? Id = null, string? Url = null, string? Error = null, bool Permanent = false, bool IsDeferred = false)
{
    public static PublishResult Ok(string id, string? url = null) => new(true, id, url);

    // Retryable: transient network / rate limit / processing timeout.
    public static PublishResult Retry(string error) => new(false, Error: error);

    // Permanent: rejected content, bad credentials, unsupported format. Retrying
    // will not help, and silently retrying forever is how a queue fills with corpses.
    public static PublishResult Fatal(string error) => new(false, Error: error, Permanent: true);

    // Deferred (B19): the publisher could not establish any state and did
    // no work — e.g. a prior-video status lookup that returned no status
    // at all. A Retry here would burn one of the 3 attempts on every run
    // and land a healthy video Abandoned. A Defer waits for the next run
    // WITHOUT consuming an attempt: Program.cs undoes the increment for
    // it, so the target stays Failed (retryable) with its budget intact.
    public static PublishResult Defer(string error) => new(false, Error: error, IsDeferred: true);
}

public interface IPublisher
{
    string Platform { get; }
    bool Enabled { get; }
    int MaxPerRun { get; }

    // Content-shape gate. Instagram and TikTok only take the vertical cut; returning
    // false here marks the target Skipped rather than burning retries on a rejection.
    // Takes the video path so a gate can probe the file (e.g. the Instagram Story
    // 60s duration cap) instead of relying only on sidecar fields.
    bool Accepts(Sidecar sidecar, string videoPath, out string? reason);

    Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct);
}
