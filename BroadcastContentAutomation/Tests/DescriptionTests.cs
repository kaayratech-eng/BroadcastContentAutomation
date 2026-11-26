// Catchy, emoji-rich descriptions (Rishi 2026-11-25 13:42 EST: video
// descriptions must ALWAYS be catchy and carry emojis wherever possible).
//
// What is pinned here, and why:
//   * The body template lives in TrendlineNowProfile.BuildDescriptionHookGuidance
//     — ScriptGenerator slots it into the description instruction as
//     "line 1: ...", so the model writes every future description body
//     from it. The guidance must demand the emoji hook up top, the
//     hashtag line on line 2 (Program.CanonicalHashtags reads line 2 as
//     the single source of truth for the Short's captions — moving it
//     would break the Short/caption hashtag sync), and the 3-4 emoji
//     bullets for the story beats.
//   * The footer lives in Sidecar.TrendlineFooter. Its marker is the
//     uploader's idempotency key and must stay verbatim; the URLs must
//     stay verbatim; only emoji/wording around them changed.
//   * The legal disclaimer is pinned WORD-FOR-WORD — it must never be
//     reworded, shortened, or moved relative to the description.
//   * CaptionFor must still strip everything from the disclaimer onward,
//     so no emoji footer ever leaks into a TikTok/Reels caption.
//   * The YouTube cross-link lines keep their plain "Watch the …:
//     https://youtu.be/…" substring (existing tests + the live-URL
//     check rely on it); the emoji is a prefix only.
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class DescriptionTests
{
    // The live Trendline Now profile, reached the same way the other
    // profile tests reach it (ReflectionHelper.TrendlineNowProfile).
    private static Func<bool, string> DescriptionHookGuidance()
    {
        var profile = ReflectionHelper.TrendlineNowProfile();
        var prop = profile.GetType().GetProperty("BuildDescriptionHookGuidance")
            ?? throw new MissingMemberException("ContentProfile.BuildDescriptionHookGuidance");
        return (Func<bool, string>)prop.GetValue(profile)!;
    }

    private static string HashtagExamples()
    {
        var profile = ReflectionHelper.TrendlineNowProfile();
        var formats = (System.Collections.IList)profile.GetType().GetProperty("Formats")!.GetValue(profile)!;
        var format = formats[0]!;
        var instructions = format.GetType().GetProperty("Instructions")!.GetValue(format)!;
        return (string)instructions.GetType().GetProperty("HashtagExamples")!.GetValue(instructions)!;
    }

    // ------------------------------------------------------------------
    // Body template — TrendlineNowProfile.BuildDescriptionHookGuidance
    // ------------------------------------------------------------------

    [Fact]
    public void DescriptionHookGuidance_DemandsEmojiHookOnLineOne()
    {
        var guidance = DescriptionHookGuidance()(false);
        Assert.Contains("emoji", guidance);
        Assert.Contains("line 1", guidance);
        Assert.Contains("exclamation", guidance);
        // The hook must pop before YouTube's "…more" — emojis up front.
        Assert.Contains("Start line 1 with 1-2 emojis", guidance);
    }

    [Fact]
    public void DescriptionHookGuidance_DemandsEmojiBulletsForStoryBeats()
    {
        var guidance = DescriptionHookGuidance()(false);
        Assert.Contains("🔥 In this video:", guidance);
        Assert.Contains("3-4", guidance);
        Assert.Contains("what happened", guidance);
        Assert.Contains("why it matters", guidance);
        Assert.Contains("what happens next", guidance);
        Assert.Contains("DIFFERENT", guidance); // one different emoji per bullet
        Assert.Contains("👇 Watch the full story", guidance);
    }

    [Fact]
    public void DescriptionHookGuidance_EmojisNeverInventFacts()
    {
        // Emojis decorate real facts; they never justify inventing one.
        var guidance = DescriptionHookGuidance()(false);
        Assert.Contains("never invent a fact to justify an emoji", guidance);
        Assert.Contains("real fact", guidance);
    }

    [Fact]
    public void HashtagExamples_ChannelFirst()
    {
        // The hashtag line is line 2, channel first — CanonicalHashtags
        // takes the first tags as the most specific, and the Short's
        // captions reuse them.
        Assert.StartsWith("#SpotlightNow", HashtagExamples());
        Assert.Contains("#bollywood", HashtagExamples());
        Assert.Contains("#hollywood", HashtagExamples());
    }

    // ------------------------------------------------------------------
    // Footer — Sidecar.TrendlineFooter (+ marker, unchanged)
    // ------------------------------------------------------------------

    [Fact]
    public void TrendlineFooterMarker_Unchanged()
    {
        // Idempotency key: the uploader skips the footer when this is
        // already present. If it drifts, re-runs stack a second footer.
        Assert.Equal("Subscribe for a new star story every day:", Sidecar.TrendlineFooterMarker);
        Assert.StartsWith(Sidecar.TrendlineFooterMarker, Sidecar.TrendlineFooter);
    }

    [Fact]
    public void TrendlineFooter_IsEmojiRichAndKeepsEveryLink()
    {
        var footer = Sidecar.TrendlineFooter;
        Assert.Contains("🔔", footer);
        Assert.Contains("🌟 Follow Spotlight Now:", footer);
        Assert.Contains("📸 Instagram: https://www.instagram.com/spotlightnowofficial", footer);
        Assert.Contains("🎵 TikTok: https://www.tiktok.com/@spotlightnowofficial", footer);
        Assert.Contains("👍 Facebook: https://www.facebook.com/spotlightnowofficial", footer);
        Assert.Contains("🧵 Threads: https://www.threads.com/@spotlightnowofficial", footer);
        Assert.Contains("https://www.youtube.com/@spotlightnowofficial", footer);
        Assert.DoesNotContain("trendlinenow", footer);
    }

    [Fact]
    public void TrendlineFooter_EndsWithOutroLineAndChannelHashtags()
    {
        var footer = Sidecar.TrendlineFooter;
        // Same wording as the outro card (TrendlineNowProfile.OutroLines).
        Assert.Contains("The next big story is already brewing. Follow @spotlightnowofficial so you don't miss it.", footer);
        Assert.Contains("#SpotlightNow", footer);
        Assert.Contains("#Bollywood", footer);
        Assert.Contains("#Hollywood", footer);
    }

    // ------------------------------------------------------------------
    // Disclaimer — word-for-word, untouched by the emoji overhaul
    // ------------------------------------------------------------------

    [Fact]
    public void TrendlineDisclaimer_WordForWordUnchanged()
    {
        Assert.Equal(
            "Disclaimer: This video is for informational and entertainment purposes. " +
            "We work from publicly reported facts, but some segments include commentary, " +
            "opinion, and speculation — these are clearly labelled and are not presented " +
            "as established fact. News develops quickly and details may change after " +
            "publication. If you spot an error, tell us in the comments and we will correct it.",
            Sidecar.TrendlineDisclaimer);
    }

    // ------------------------------------------------------------------
    // CaptionFor — emoji body stays, disclaimer + footer never leak
    // ------------------------------------------------------------------

    [Fact]
    public void CaptionFor_KeepsEmojiBody_StripsDisclaimerAndFooter()
    {
        var sidecar = new Sidecar
        {
            Description = "🔥 Nobody believed she would make it. Then she broke every record!\n" +
                "#SpotlightNow #bollywood #hollywood\n\n" +
                "🔥 In this video:\n🎬 She walked in unknown — and walked out a star.\n\n" +
                Sidecar.TrendlineDisclaimer + "\n\n" + Sidecar.TrendlineFooter,
        };
        var caption = sidecar.CaptionFor("youtube", 1000);
        Assert.Contains("🔥 Nobody believed she would make it.", caption);
        Assert.Contains("🔥 In this video:", caption);
        Assert.DoesNotContain("Disclaimer:", caption);
        Assert.DoesNotContain(Sidecar.TrendlineFooterMarker, caption);
        Assert.DoesNotContain("instagram.com/spotlightnowofficial", caption);
        Assert.DoesNotContain("#CelebrityNews", caption);
    }

    // ------------------------------------------------------------------
    // Cross-link lines — emoji prefix only, plain wording + URL intact
    // ------------------------------------------------------------------

    [Fact]
    public void CrossLinkLines_EmojiPrefix_PlainWordingAndUrlIntact()
    {
        var full = ReflectionHelper.Invoke<string>(ReflectionHelper.Uploader, "FullVideoLinkLine", "long456");
        var shortLine = ReflectionHelper.Invoke<string>(ReflectionHelper.Uploader, "ShortVersionLinkLine", "short123");
        Assert.StartsWith("🎬 ", full);
        Assert.StartsWith("⚡ ", shortLine);
        Assert.Contains("Watch the full video: https://youtu.be/long456", full);
        Assert.Contains("Watch the 60-second version: https://youtu.be/short123", shortLine);
    }
}
