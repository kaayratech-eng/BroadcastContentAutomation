using System.Text.Json;
using System.Text.Json.Serialization;

namespace GiggleGarden.Shared;

public static class Platforms
{
    public const string YouTube = "youtube";
    public const string Instagram = "instagram";
    public const string Facebook = "facebook";
    public const string TikTok = "tiktok";
    public const string InstagramStory = "instagram_story";
    public const string FacebookStory = "facebook_story";

    public static readonly string[] All = [YouTube, Instagram, Facebook, TikTok, InstagramStory, FacebookStory];
}

public enum PublishStatus
{
    Pending,
    Published,
    Failed,      // retryable — will be attempted again next run
    Abandoned,   // permanently failed, gave up
    Skipped,     // not a target for this asset, or platform disabled
}

public sealed class Publication
{
    public PublishStatus Status { get; set; } = PublishStatus.Pending;
    public string? Id { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset? At { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }

    // Most recent failure message, kept for history once Error is cleared on
    // a later success (Program.cs). A published record must not carry a stale
    // failure as its live Error — video #2's YouTube record still showed the
    // "invalid_grant" error from its first attempt even though it published.
    public string? LastError { get; set; }

    [JsonIgnore]
    public bool IsTerminal => Status is PublishStatus.Published or PublishStatus.Abandoned or PublishStatus.Skipped;
}

// The sidecar is the unit of state for the whole pipeline. VideoGen writes it next to
// each render; the Uploader reads it, publishes to each target that is not yet
// terminal, and writes it back. The video file only moves to \done once every target
// has reached a terminal state — which is what makes publishing to more than one
// platform possible at all.
public sealed class Sidecar
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public bool? Approved { get; set; }

    public string Language { get; set; } = "en";
    public string Aspect { get; set; } = "landscape";      // landscape | vertical
    public List<string> Targets { get; set; } = [];

    // Which ContentProfile/channel this video belongs to (e.g. "trendlinenow",
    // "chronicleandchaos") — drives which channel's credentials the Uploader
    // publishes with. Empty on sidecars written before multi-channel support
    // existed; LoadAsync below defaults those to "trendlinenow" rather than
    // silently routing them through whichever channel happens to be default.
    public string Channel { get; set; } = "";

    // Drives YouTube's required self-declared audience flag (VideoStatus.MadeForKids /
    // SelfDeclaredMadeForKids in YouTubePublisher). Defaults false: this codebase
    // publishes general-audience entertainment only (Spotlight Now), and no
    // kids channel remains — so an old/hand-edited sidecar with no MadeForKids
    // key must never silently upload as made-for-kids content.
    public bool MadeForKids { get; set; } = false;
    public Dictionary<string, Publication> Publications { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Fix #2 (Rishi 2026-11-26, Shorts→long-form bridge): the "Full story
    // here →" comment has been posted on this Short's YouTube video. Set
    // only after a successful commentThreads.insert, so a later run never
    // posts a duplicate comment for the same Short. False on every old
    // sidecar (the field did not exist), which is exactly right — a Short
    // published before this flag gets its comment on the next reconciling
    // run while it is still in the watch dir.
    public bool PinnedCommentPosted { get; set; } = false;

    // 2nd-Short rule (Rishi 2026-11-27, replacing the fix-#5 tail cut):
    // the second Short is a different moment with its own title/thumbnail
    // and is posted ~12h after the main Short goes live, never the same
    // day. Earliest instant the uploader may publish this asset. Null =
    // no hold — every old sidecar and every long-form / main-Short sidecar
    // has null, so only a 2nd-Short sidecar ever carries a value. The
    // finish step writes a provisional hold, finish time + 12h
    // (ShortRules.SecondShortHoldUntilFromFinish); when the main Short goes
    // live the hold is re-anchored to its publication time + 12h
    // (ShortRules.SecondShortHoldUntil) — the 12h is measured from the main
    // Short going live, not from the finish (cadence, Rishi 2026-11-27
    // 18:19 EST: upload something every 12h).
    public DateTimeOffset? PublishAfterUtc { get; set; }

    // Optional per-platform caption override. Absent means "derive from Description".
    public Dictionary<string, string> CaptionOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Free metadata/analytics fields (2026-10-25, priority 5). All optional
    // and null-ignored, so old sidecars still load. They exist so the
    // analytics loop can correlate title/hook/thumbnail with views without
    // re-deriving them from the video file.
    public List<string>? TitleVariants { get; set; }
    public string? HookText { get; set; }
    public string? CommentPrompt { get; set; }
    public List<string>? Chapters { get; set; }
    public string? PlaylistId { get; set; }
    public string? ThumbnailPath { get; set; }
    public double? LoudnessLUFS { get; set; }
    public int? HookScore { get; set; }
    public int? SeoScore { get; set; }

    // Resolved background-music track filename (e.g. "uplifting-tech-01.mp3"),
    // written by VideoGen at assemble time. The uploader uses it to append the
    // CC BY attribution line for the track (see Program.cs MusicCredits). Null
    // on sidecars written before this field existed — in that case the uploader
    // logs a warning instead of silently publishing without a credit.
    public string? MusicTrack { get; set; }

    // Rendered video duration in seconds, written by VideoGen. Used by the
    // uploader for duration gates (e.g. the 60s Instagram Story cap). Null on
    // older sidecars; the uploader falls back to probing the file with ffprobe.
    public double? DurationSeconds { get; set; }

    // Spotlight Now legal disclaimer (Rishi 2026-11-21). Kept here, not in
    // Program.cs, so both the description append and CaptionFor's fallback can
    // refer to the same text: a caption must never carry the disclaimer.
    public const string TrendlineDisclaimer =
        "Disclaimer: This video is for informational and entertainment purposes. " +
        "We work from publicly reported facts, but some segments include commentary, " +
        "opinion, and speculation — these are clearly labelled and are not presented " +
        "as established fact. News develops quickly and details may change after " +
        "publication. If you spot an error, tell us in the comments and we will correct it.";

    // Spotlight Now description footer (Rishi 2026-11-22, review SEO #3;
    // rebranded 2026-11-24; emoji overhaul 2026-11-25, Rishi: descriptions
    // must ALWAYS be catchy and carry emojis wherever possible). Appended to
    // every YouTube description after the disclaimer, so a caption (which
    // CaptionFor truncates at the disclaimer) never carries it either. The
    // subscribe line is the idempotency marker: the uploader skips the footer
    // when it is already present, so a re-run never stacks a second copy —
    // the marker text itself is VERBATIM and must never change, only the
    // emoji/wording after it. The cross-link lines (Short -> long-form,
    // long-form -> Short) are NOT part of this constant — they carry a video
    // id that is only known after upload, so the uploader builds them at
    // upload time (YouTubePublisher.FullVideoLinkLine / ShortVersionLinkLine).
    // The footer ends with the video outro line (same wording as the outro
    // card in TrendlineNowProfile) plus the channel hashtags.
    public const string TrendlineFooterMarker = "Subscribe for a new star story every day:";
    public const string TrendlineFooter =
        "Subscribe for a new star story every day: 🔔 https://www.youtube.com/@spotlightnowofficial\n" +
        "\n" +
        "🌟 Follow Spotlight Now:\n" +
        "📸 Instagram: https://www.instagram.com/spotlightnowofficial\n" +
        "🎵 TikTok: https://www.tiktok.com/@spotlightnowofficial\n" +
        "👍 Facebook: https://www.facebook.com/spotlightnowofficial\n" +
        "🧵 Threads: https://www.threads.com/@spotlightnowofficial\n" +
        "\n" +
        "✨ The next big story is already brewing. Follow @spotlightnowofficial so you don't miss it.\n" +
        "\n" +
        "#SpotlightNow #Bollywood #Hollywood #CelebrityNews";

    [JsonIgnore] public bool IsVertical => Aspect.Equals("vertical", StringComparison.OrdinalIgnoreCase);

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string PathFor(string videoPath) => Path.ChangeExtension(videoPath, ".json");

    // L82 (RetryHandler pattern): an optional log sink so the helper can
    // report a corrupt file instead of leaving the caller to guess.
    public static async Task<Sidecar?> LoadAsync(string videoPath, Action<string>? log = null)
    {
        var path = PathFor(videoPath);
        if (!File.Exists(path)) return null;

        Sidecar? sidecar;
        try
        {
            sidecar = JsonSerializer.Deserialize<Sidecar>(await File.ReadAllTextAsync(path), Options);
        }
        catch (JsonException ex)
        {
            // L11: a corrupt sidecar is permanent, not a transient crash.
            // Name the file and the reason (SIDECAR_CORRUPT marker lets the
            // per-video catch fail fast, mirroring SIDECAR_REQUIRED) instead
            // of burning 3 crash retries before landing in failed/.
            log?.Invoke($"sidecar load failed for {path}: corrupt sidecar JSON: {ex.Message}; move to failed/, do not retry");
            throw new InvalidDataException(
                $"SIDECAR_CORRUPT: {Path.GetFileName(path)}: corrupt sidecar JSON: {ex.Message}; move to failed/, do not retry", ex);
        }
        if (sidecar is null)
        {
            log?.Invoke($"sidecar load failed for {path}: corrupt sidecar JSON: deserialized to null; move to failed/, do not retry");
            throw new InvalidDataException(
                $"SIDECAR_CORRUPT: {Path.GetFileName(path)}: corrupt sidecar JSON: deserialized to null; move to failed/, do not retry");
        }

        // Backward compatibility with v1 sidecars (title/description/tags/approved only):
        // an absent Targets list means the file predates multi-platform support, so it
        // keeps the original YouTube-only behaviour rather than silently fanning out.
        if (sidecar.Targets.Count == 0) sidecar.Targets.Add(Platforms.YouTube);

        // Same backward-compat reasoning as Targets above: a sidecar written
        // before multi-channel support existed belongs to the channel that
        // exists today.
        if (string.IsNullOrWhiteSpace(sidecar.Channel)) sidecar.Channel = "trendlinenow";

        return sidecar;
    }

    // L82: optional log sink (RetryHandler pattern) — a save failure
    // used to surface only as a bare exception with no file context.
    public async Task SaveAsync(string videoPath, Action<string>? log = null)
    {
        var path = PathFor(videoPath);
        var temp = path + ".tmp";

        try
        {
            // Write-then-replace: a crash mid-write must not destroy the record of what
            // has already been published, which would cause duplicate posts on the retry.
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            log?.Invoke($"sidecar save failed for {path}: {ex.GetType().Name}: {ex.Message}; sidecar unchanged, publish state kept in memory");
            throw;
        }
    }

    public Publication PublicationFor(string platform)
    {
        if (!Publications.TryGetValue(platform, out var publication))
            Publications[platform] = publication = new Publication();
        return publication;
    }

    // Distinct here, not just at the source that populates Targets - this is the
    // actual publish dispatch point, and a duplicated platform slipping through
    // means an actual duplicate live post, not just a cosmetic list glitch.
    public IEnumerable<string> OutstandingTargets() =>
        Targets.Distinct(StringComparer.OrdinalIgnoreCase).Where(t => !PublicationFor(t).IsTerminal);

    public bool AllTargetsTerminal() =>
        Targets.Count > 0 && Targets.All(t => PublicationFor(t).IsTerminal);

    public bool AnyPublished() =>
        Publications.Values.Any(p => p.Status == PublishStatus.Published);

    public string CaptionFor(string platform, int maxLength)
    {
        if (CaptionOverrides.TryGetValue(platform, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return Text.Fit(custom, maxLength);

        // Fall back to the description BEFORE the disclaimer: ResolveSidecarAsync
        // appends the Trendline disclaimer to Description in memory, and a saved
        // sidecar already carries it — but a caption (notably the TikTok
        // .tiktok-caption.txt, which is the only carrier in draft flow) must never
        // paste the legal paragraph. Strip it here so a blank per-platform
        // override can never leak the disclaimer into a caption.
        var text = Description;
        var idx = text.IndexOf(TrendlineDisclaimer, StringComparison.Ordinal);
        if (idx >= 0)
            text = text[..idx].TrimEnd();
        // B18: the CC BY music credit is appended to the description BEFORE
        // the disclaimer, so the truncation above leaves it in the caption.
        // A credit line is its own paragraph starting with "Music: " and
        // carrying a "CC BY" licence marker (see Uploader MusicCredits) —
        // cut the caption there too, so a fallback caption never carries
        // the attribution line. Only the first such line is cut; a
        // description that merely mentions music in prose is untouched.
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("Music: ", StringComparison.Ordinal)
                && line.Contains("CC BY", StringComparison.Ordinal))
            {
                var creditIdx = text.IndexOf(line, StringComparison.Ordinal);
                if (creditIdx >= 0)
                    text = text[..creditIdx].TrimEnd();
                break;
            }
        }
        return Text.Fit(text, maxLength);
    }
}
