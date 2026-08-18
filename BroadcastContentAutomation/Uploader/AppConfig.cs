namespace GiggleGarden.Uploader;

record AppConfig
{
    public string WatchDirectory { get; init; } = @"D:\Business\Videos";
    public string DoneDirectory { get; init; } = @"D:\Business\Videos\done";
    public string FailedDirectory { get; init; } = @"D:\Business\Videos\failed";
    public string LogDirectory { get; init; } = @"D:\Business\Videos\logs";

    public bool RequireApproval { get; init; } = true;

    // How many times a single platform may fail on one video before it is abandoned.
    public int MaxAttemptsPerPlatform { get; init; } = 3;

    // A file younger than this is assumed to still be written by a concurrent FFmpeg
    // run. Without the guard the publisher happily uploads a half-muxed mp4.
    public int MinFileAgeSeconds { get; init; } = 90;

    // Optional pause between posts so a batch does not look like a burst to any
    // platform's spam heuristics. 0 disables.
    public int StaggerSecondsBetweenPosts { get; init; }

    // Metadata fallback (only used for videos dropped in without a sidecar).
    public string AnthropicApiKey { get; init; } = "";
    public string ClaudeModel { get; init; } = "claude-opus-5";
    public string[] TrendQueries { get; init; } =
        ["nursery rhymes for kids", "kids learning songs", "hindi rhymes for children"];

    // One credential set per ContentProfile/channel (keyed by profile Id, e.g.
    // "gigglegarden", "chronicleandchaos"), so two channels can publish in the
    // same run without sharing tokens. Sidecar.Channel selects which entry a
    // given video routes through; DefaultChannel covers sidecar-less videos
    // dropped in manually with no channel signal at all.
    public Dictionary<string, PlatformsConfig> Channels { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string DefaultChannel { get; init; } = "gigglegarden";
}

record PlatformsConfig
{
    public YouTubeConfig YouTube { get; init; } = new();
    public InstagramConfig Instagram { get; init; } = new();
    public FacebookConfig Facebook { get; init; } = new();
    public TikTokConfig TikTok { get; init; } = new();
}

record YouTubeConfig
{
    public bool Enabled { get; init; } = true;
    public string ClientSecretPath { get; init; } = "";
    public string TokenStorePath { get; init; } = "";
    public string PrivacyStatus { get; init; } = "private";
    public string CategoryId { get; init; } = "24";        // Entertainment
    public int MaxUploadsPerRun { get; init; } = 3;        // ~1600 quota units each
}

// Meta Graph API. Instagram Reels and Facebook Reels share the app and, usually,
// the Page access token — the IG account must be a Business/Creator account linked
// to the Page. Credentials are uploaded as binary via rupload.facebook.com, so no
// publicly reachable file URL is required.
record InstagramConfig
{
    public bool Enabled { get; init; }
    public string IgUserId { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string GraphVersion { get; init; } = "v21.0";
    public bool ShareToFeed { get; init; } = true;
    public int MaxUploadsPerRun { get; init; } = 5;
    public int ProcessingTimeoutSeconds { get; init; } = 600;
}

record FacebookConfig
{
    public bool Enabled { get; init; }
    public string PageId { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string GraphVersion { get; init; } = "v21.0";
    public int MaxUploadsPerRun { get; init; } = 5;
    public int ProcessingTimeoutSeconds { get; init; } = 600;
}

record TikTokConfig
{
    public bool Enabled { get; init; }
    public string ClientKey { get; init; } = "";
    public string ClientSecret { get; init; } = "";

    // Where the rotating refresh token is persisted. TikTok issues a NEW refresh
    // token on every refresh and invalidates the old one, so this file must be
    // writable and backed up — losing it means re-running the OAuth consent flow.
    public string TokenStorePath { get; init; } = @"C:\Secure\GiggleGarden\tiktok-token.json";

    // Direct Post requires an audited app with the video.publish scope. Until that
    // audit passes, leave this false: the video lands in the creator's TikTok inbox
    // (drafts) for manual publish, which only needs video.upload.
    public bool DirectPost { get; init; }

    // TikTok rejects a direct post whose privacy level is not in the creator's
    // allowed set; unaudited apps are restricted to SELF_ONLY.
    public string PrivacyLevel { get; init; } = "SELF_ONLY";
    public bool DisableComment { get; init; } = true;
    public bool DisableDuet { get; init; } = true;
    public bool DisableStitch { get; init; } = true;

    public int MaxUploadsPerRun { get; init; } = 5;
    public int ProcessingTimeoutSeconds { get; init; } = 600;
}
