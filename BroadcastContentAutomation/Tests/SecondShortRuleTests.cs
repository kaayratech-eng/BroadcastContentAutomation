// The 2nd Short, the NEW way (Rishi 2026-11-27 17:59 EST — replaces the
// fix-#5 tail cut; publish hold corrected to 12h 18:07 EST): a different,
// self-contained moment (the payoff stretch near the end) with ZERO
// overlap with the main Short, its own title, its own thumbnail, and
// posted ~12h after the main Short, never the same day. ShortRules is
// the mechanical backstop for the first two parts (overlap + title) and
// the hold; Sidecar.PublishAfterUtc is the hold the uploader enforces.
//
// The title picker contract is shared with the Python finish step, which
// implements the same picker: variant 3 first, else the first sentence of
// the opening narration (truncated to 100 chars), else the main title
// with UsedFallback = true (caller logs a loud warning and flags the
// sidecar). Note the list is often EMPTY in practice — Drake's
// script.json has TitleVariants: [] — so the narration-derivation path
// is the common one, not an edge case.
//
// The 12h hold is ANCHORED ON THE MAIN SHORT GOING LIVE (cadence, Rishi
// 2026-11-27 18:19 EST: upload something every 12h — long-form + main
// Short together, 2nd Short 12h later, next story 12h after that), not
// on the finish: SecondShortHoldUntil(publishedAt) is the rule, and
// SecondShortHoldUntilFromFinish(finishTime) is only the provisional
// hold the finish writes before the main Short has gone live.
using System.Text.Json;
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class SecondShortRuleTests
{
    // --- Overlap: main Short covers scenes 0..mainLastScene, 2nd Short
    // starts at secondFirstScene. Disjoint iff it starts strictly later. ---

    [Fact]
    public void Overlaps_DisjointStrictlyLaterStart_IsFalse()
    {
        Assert.False(ShortRules.SecondShortOverlapsMainShort(mainLastScene: 10, secondFirstScene: 11));
        Assert.False(ShortRules.SecondShortOverlapsMainShort(mainLastScene: 0, secondFirstScene: 1));
    }

    [Fact]
    public void Overlaps_AdjacentBoundary_StartEqualsLastScene_IsTrue()
    {
        // The boundary case: starting ON the main Short's last scene
        // reuses that scene — it looks adjacent, but it overlaps.
        Assert.True(ShortRules.SecondShortOverlapsMainShort(mainLastScene: 10, secondFirstScene: 10));
    }

    [Fact]
    public void Overlaps_OverlappingStart_IsTrue()
    {
        Assert.True(ShortRules.SecondShortOverlapsMainShort(mainLastScene: 10, secondFirstScene: 5));
        Assert.True(ShortRules.SecondShortOverlapsMainShort(mainLastScene: 10, secondFirstScene: 9));
    }

    [Fact]
    public void Overlaps_SameStart_IsTrue()
    {
        Assert.True(ShortRules.SecondShortOverlapsMainShort(mainLastScene: 10, secondFirstScene: 0));
        Assert.True(ShortRules.SecondShortOverlapsMainShort(mainLastScene: 0, secondFirstScene: 0));
    }

    // --- Title picker: variant 3 wins when usable. ---

    [Fact]
    public void PickTitle_Variant3Used()
    {
        var (title, usedFallback) = ShortRules.PickSecondShortTitle(
            "Drake's Unauthorized Rice University Shoot – The Inside Story",
            ["Drake's Unauthorized Rice University Shoot – The Inside Story",
             "What really happened in Drake's Unauthorized Rice University Shoot?",
             "The payoff nobody saw coming"],
            "Some other opening line. It should never win over variant 3.");
        Assert.Equal("The payoff nobody saw coming", title);
        Assert.False(usedFallback);
    }

    [Fact]
    public void PickTitle_Variant3Trimmed()
    {
        var (title, usedFallback) = ShortRules.PickSecondShortTitle(
            "Main Title", ["Main Title", "Second", "  Padded variant 3  "], "Narration here.");
        Assert.Equal("Padded variant 3", title);
        Assert.False(usedFallback);
    }

    [Fact]
    public void PickTitle_EmptyVariants_DerivesFromNarration()
    {
        // The common case in practice: TitleVariants is [] (Drake).
        var (title, usedFallback) = ShortRules.PickSecondShortTitle(
            "Drake's Unauthorized Rice University Shoot – The Inside Story",
            [],
            "He walked into the arena a punchline. The crowd went silent when the numbers came up.");
        Assert.Equal("He walked into the arena a punchline", title);
        Assert.False(usedFallback);
    }

    [Fact]
    public void PickTitle_NullVariants_DerivesFromNarration()
    {
        var (title, usedFallback) = ShortRules.PickSecondShortTitle(
            "Main Title", null, "Can he pull it off? The crowd holds its breath.");
        Assert.Equal("Can he pull it off?", title);
        Assert.False(usedFallback);
    }

    [Fact]
    public void PickTitle_FewerThanThreeVariants_DerivesFromNarration()
    {
        // Only variants[0] and [1] exist — there is no variant 3 to use.
        var (title, usedFallback) = ShortRules.PickSecondShortTitle(
            "Main Title", ["Main Title", "What really happened?"], "The payoff lands here. Then the credits roll.");
        Assert.Equal("The payoff lands here", title);
        Assert.False(usedFallback);
    }

    [Fact]
    public void PickTitle_Variant3EqualsMainTitle_DerivesFromNarration()
    {
        // Variant 3 that just repeats the main title (case-insensitive,
        // padded) is not an own title — fall through to derivation.
        var (title, usedFallback) = ShortRules.PickSecondShortTitle(
            "Main Title", ["Main Title", "Other", "  main title  "],
            "The record falls in the final minute. Nobody saw it coming.");
        Assert.Equal("The record falls in the final minute", title);
        Assert.False(usedFallback);
    }

    [Fact]
    public void PickTitle_Variant3Blank_DerivesFromNarration()
    {
        var (title, usedFallback) = ShortRules.PickSecondShortTitle(
            "Main Title", ["Main Title", "Other", "   "], "A real opening line. More follows.");
        Assert.Equal("A real opening line", title);
        Assert.False(usedFallback);
    }

    [Fact]
    public void PickTitle_NoVariantAndEmptyNarration_FallsBackToMainTitle()
    {
        var (title, usedFallback) = ShortRules.PickSecondShortTitle("Main Title", [], null);
        Assert.Equal("Main Title", title);
        Assert.True(usedFallback);

        (title, usedFallback) = ShortRules.PickSecondShortTitle("Main Title", null, "   ");
        Assert.Equal("Main Title", title);
        Assert.True(usedFallback);
    }

    [Fact]
    public void PickTitle_SingleSentenceNarration_WholeTextDerived()
    {
        var (title, usedFallback) = ShortRules.PickSecondShortTitle(
            "Main Title", [], "One sentence is the whole opening.");
        Assert.Equal("One sentence is the whole opening", title);
        Assert.False(usedFallback);
    }

    [Fact]
    public void PickTitle_DerivedTitle_TruncatedTo100Chars()
    {
        var longSentence = new string('a', 150) + ". Short tail follows.";
        var (title, usedFallback) = ShortRules.PickSecondShortTitle("Main Title", [], longSentence);
        Assert.Equal(100, title.Length);
        Assert.Equal(new string('a', 100), title);
        Assert.False(usedFallback);
    }

    // --- Hold: the 12h is anchored on the main Short GOING LIVE, not on
    // the finish. The finish-anchored helper is only the provisional hold
    // written before the main Short has published. ---

    [Fact]
    public void HoldUntil_FromMainShortPublishedAt_IsPublishedPlusTwelveHours()
    {
        // Primary path: main Short went live 2026-11-27 18:00 UTC, so the
        // 2nd Short may go out 2026-11-28 06:00 UTC.
        var publishedAt = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero),
            ShortRules.SecondShortHoldUntil(publishedAt));
    }

    [Fact]
    public void HoldUntil_FromMainShortPublishedAt_PreservesOffset()
    {
        var publishedAt = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.FromHours(-5));
        var hold = ShortRules.SecondShortHoldUntil(publishedAt);
        Assert.Equal(TimeSpan.FromHours(-5), hold.Offset);
        Assert.Equal(publishedAt.AddHours(12), hold);
    }

    [Fact]
    public void HoldUntil_PrimaryIsLaterWhenMainShortGoesLiveLate()
    {
        // The anchoring matters: the finish ran at 10:00, but the main
        // Short only went live at 20:00. A finish-anchored hold would
        // release the 2nd Short at 22:00 — 2h after the main Short, not
        // 12h — and break the every-12h alternation. The live-anchored
        // hold correctly releases it at 08:00 the next day.
        var finish = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var publishedAt = new DateTimeOffset(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 27, 22, 0, 0, TimeSpan.Zero),
            ShortRules.SecondShortHoldUntilFromFinish(finish));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 28, 8, 0, 0, TimeSpan.Zero),
            ShortRules.SecondShortHoldUntil(publishedAt));
        Assert.True(
            ShortRules.SecondShortHoldUntil(publishedAt) > ShortRules.SecondShortHoldUntilFromFinish(finish));
    }

    [Fact]
    public void HoldUntilFromFinish_IsFinishPlusTwelveHours()
    {
        // Fallback / provisional path: what the finish writes into
        // PublishAfterUtc before the main Short has gone live.
        var finish = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero),
            ShortRules.SecondShortHoldUntilFromFinish(finish));
    }

    [Fact]
    public void HoldUntilFromFinish_PreservesOffset()
    {
        var finish = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.FromHours(-5));
        var hold = ShortRules.SecondShortHoldUntilFromFinish(finish);
        Assert.Equal(TimeSpan.FromHours(-5), hold.Offset);
        Assert.Equal(finish.AddHours(12), hold);
    }

    // --- PublishAfterUtc: JSON round-trip through Sidecar.Options. ---

    [Fact]
    public void PublishAfterUtc_RoundTripsThroughSidecarJson()
    {
        var hold = new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);
        var sidecar = new Sidecar { Title = "Test", PublishAfterUtc = hold };
        var json = JsonSerializer.Serialize(sidecar, Sidecar.Options);
        var loaded = JsonSerializer.Deserialize<Sidecar>(json, Sidecar.Options);
        Assert.NotNull(loaded);
        Assert.Equal(hold, loaded!.PublishAfterUtc);
    }

    [Fact]
    public async Task PublishAfterUtc_RoundTripsThroughSaveAndLoad()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"sidecar-hold-{Guid.NewGuid():N}")).FullName;
        try
        {
            var video = Path.Combine(dir, "test-video-en-short2.mp4");
            var hold = new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);
            await new Sidecar { Title = "Test", PublishAfterUtc = hold }.SaveAsync(video);
            var loaded = await Sidecar.LoadAsync(video);
            Assert.NotNull(loaded);
            Assert.Equal(hold, loaded!.PublishAfterUtc);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PublishAfterUtc_PythonIsoString_DeserializesToSameInstant()
    {
        // The Python finish step writes the hold as an ISO-8601 string;
        // it must land on the same instant in C#.
        const string json = """{"title":"Test","publishAfterUtc":"2026-11-28T22:45:00+00:00"}""";
        var loaded = JsonSerializer.Deserialize<Sidecar>(json, Sidecar.Options);
        Assert.NotNull(loaded);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 28, 22, 45, 0, TimeSpan.Zero),
            loaded!.PublishAfterUtc);
    }

    [Fact]
    public void PublishAfterUtc_OldSidecarWithoutField_LoadsAsNull()
    {
        const string json = """{"title":"Old video","description":"d","tags":[]}""";
        var loaded = JsonSerializer.Deserialize<Sidecar>(json, Sidecar.Options);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.PublishAfterUtc);

        // And a fresh sidecar defaults to no hold.
        Assert.Null(new Sidecar().PublishAfterUtc);
    }

    // --- The rule text itself: the hold is 12h, not a full day. ---

    [Fact]
    public void SecondShortRuleText_SaysTwelveHourHoldAnchoredOnMainShortLive()
    {
        var root = RepoRoot();
        var rules = File.ReadAllText(Path.Combine(root, "BroadcastContentAutomation", "VideoGen", "ShortRules.cs"));
        Assert.Contains("12h", rules);
        Assert.DoesNotContain("24h", rules);
        Assert.Contains("ZERO OVERLAP", rules);
        Assert.Contains("SecondShortOverlapsMainShort", rules);
        Assert.Contains("PickSecondShortTitle", rules);
        Assert.Contains("SecondShortHoldUntil", rules);
        Assert.Contains("SecondShortHoldUntilFromFinish", rules);
        Assert.Contains("GOING LIVE", rules);
        Assert.Contains("every 12h", rules);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "production", "finish-chunks.py")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repo root (production/finish-chunks.py).");
    }
}
