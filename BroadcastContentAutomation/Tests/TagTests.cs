// Regression tests for the tag pipeline (review 2026-11-22, SEO #6).
// ScriptGenerator.NormalizeTag turns the model's CamelCase single words
// ("GTA6", "RockstarGames") into the lowercase search phrases viewers
// type ("gta 6", "rockstar games"), and TrendlineNowProfile.BaseTags was
// trimmed from 7 generics to 4 so a Short's 12 tag slots are not burned
// on near-worthless phrases ("what happened", "explained", "world news").
// A regression here ships tags nobody searches for and wastes the slots
// the topic tags need.
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class TagTests
{
    private static string NormalizeTag(string tag) =>
        ReflectionHelper.Invoke<string>(ReflectionHelper.VideoGen, "NormalizeTag", tag);

    [Fact]
    public void NormalizeTag_CamelCaseAcronym_SplitsBeforeDigit()
    {
        // "GTA6": the acronym stays together, the digit splits off.
        Assert.Equal("gta 6", NormalizeTag("GTA6"));
    }

    [Fact]
    public void NormalizeTag_CamelCaseWords_SplitsAtCaseBoundary()
    {
        Assert.Equal("rockstar games", NormalizeTag("RockstarGames"));
        Assert.Equal("vice city", NormalizeTag("ViceCity"));
        Assert.Equal("jason duval", NormalizeTag("JasonDuval"));
    }

    [Fact]
    public void NormalizeTag_LetterDigitBoundary_Splits()
    {
        Assert.Equal("2026 release", NormalizeTag("2026release"));
    }

    [Fact]
    public void NormalizeTag_AlreadyCorrectPhrase_Unchanged()
    {
        Assert.Equal("fbi hack", NormalizeTag("FBI hack"));
        Assert.Equal("grand theft auto 6", NormalizeTag("grand theft auto 6"));
        Assert.Equal("trendline now", NormalizeTag("trendline now"));
    }

    [Fact]
    public void NormalizeTag_Whitespace_Trimmed()
    {
        Assert.Equal("gta 6", NormalizeTag("  GTA6  "));
    }

    [Fact]
    public void NormalizeTag_BrandNames_NotSplit()
    {
        // Review #2 R1: NormalizeTag split every CamelCase boundary with
        // no allowlist, so brand names became non-phrases — "YouTube"
        // became "you tube", "TikTok" became "tik tok". A brand is
        // lowercased but left whole.
        Assert.Equal("youtube", NormalizeTag("YouTube"));
        Assert.Equal("tiktok", NormalizeTag("TikTok"));
        Assert.Equal("iphone", NormalizeTag("iPhone"));
        Assert.Equal("ps5", NormalizeTag("PS5"));
        Assert.Equal("mcdonald", NormalizeTag("McDonald"));
        Assert.Equal("playstation", NormalizeTag("PlayStation"));
        Assert.Equal("openai", NormalizeTag("OpenAI"));
        Assert.Equal("ebay", NormalizeTag("eBay"));
        Assert.Equal("ipad", NormalizeTag("iPad"));
        Assert.Equal("macbook", NormalizeTag("MacBook"));
    }

    [Fact]
    public void NormalizeTag_BrandCompounds_SplitAfterBrand()
    {
        // A brand followed by another word/digit at a CamelCase or
        // digit boundary keeps the brand whole and splits after it.
        Assert.Equal("youtube shorts", NormalizeTag("YouTubeShorts"));
        Assert.Equal("iphone 15", NormalizeTag("iPhone15"));
        Assert.Equal("playstation 5", NormalizeTag("PlayStation5"));
    }

    [Fact]
    public void NormalizeTag_Separators_NormalizedToSpaces()
    {
        // Review #2 R1: hyphens/underscores passed through unchanged, so
        // a model-written "vice-city" never became the search phrase
        // "vice city". Separators become spaces.
        Assert.Equal("vice city", NormalizeTag("vice-city"));
        Assert.Equal("grand theft auto", NormalizeTag("grand_theft_auto"));
        Assert.Equal("gta 6", NormalizeTag("GTA-6"));
        Assert.Equal("rockstar games", NormalizeTag("rockstar_games"));
    }

    [Fact]
    public void NormalizeTag_GenuineCamelCase_StillSplits()
    {
        // The brand allowlist must not stop genuine CamelCase splits:
        // these are not brands, so they still become search phrases.
        Assert.Equal("rockstar games", NormalizeTag("RockstarGames"));
        Assert.Equal("gta 6", NormalizeTag("GTA6"));
        Assert.Equal("jason duval", NormalizeTag("JasonDuval"));
    }

    [Fact]
    public void BaseTags_IsExactlyFour()
    {
        // The trimmed list (review 2026-11-22, SEO #6): brand plus the
        // three strongest generic search phrases. The old 7 burned 7 of
        // the ~13 tag slots on generics, leaving room for ~5 topic tags.
        // SPOTLIGHT NOW pivot (2026-11-24): brand + Bollywood + Hollywood
        // + celebrity news.
        var baseTags = ReflectionHelper.TrendlineNowBaseTags();
        Assert.Equal(
            new[] { "spotlight now", "bollywood", "hollywood", "celebrity news" },
            baseTags);
    }

    [Fact]
    public void BaseTags_DoesNotContainDroppedGenerics()
    {
        var baseTags = ReflectionHelper.TrendlineNowBaseTags();
        Assert.DoesNotContain("what happened", baseTags);
        Assert.DoesNotContain("explained", baseTags);
        Assert.DoesNotContain("world news", baseTags);
    }

    [Fact]
    public void TagPipeline_Video3Tags_NormalizedTopicTagsPresent()
    {
        // Video #3's stored Tags still bake in the old 7 BaseTags, so strip
        // those first to simulate a fresh video #4 generation, then prepend
        // the live 4, normalize each tag, dedupe, and cap at 30.
        var stored = LoadVideo3Tags();
        if (stored is null) return;
        var tags = FullTagPipeline(stored);
        Assert.Contains("gta 6", tags);
        Assert.Contains("vice city", tags);
        Assert.Contains("rockstar games", tags);
    }

    [Fact]
    public void TagPipeline_Video3Tags_DroppedGenericsGone()
    {
        var stored = LoadVideo3Tags();
        if (stored is null) return;
        var tags = FullTagPipeline(stored);
        Assert.DoesNotContain("what happened", tags);
        Assert.DoesNotContain("explained", tags);
        Assert.DoesNotContain("world news", tags);
    }

    [Fact]
    public void TagPipeline_ShortFirstTwelve_ContainsTopicTag()
    {
        // The Short carries only the first 12 tags (plus "shorts"); the
        // topic tag must survive that cut, not sit behind the generics.
        var stored = LoadVideo3Tags();
        if (stored is null) return;
        var tags = FullTagPipeline(stored);
        Assert.Contains("gta 6", tags.Take(12));
    }

    private static List<string> FullTagPipeline(List<string> storedTags)
    {
        // The old 7 BaseTags baked into video #3's script.json.
        var oldBaseTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "trendline now", "trending news", "news explained", "world news",
            "breaking news", "what happened", "explained",
        };
        var topicTags = storedTags.Where(t => !oldBaseTags.Contains(t)).ToList();
        var combined = ReflectionHelper.TrendlineNowBaseTags().Concat(topicTags);
        // Normalize each tag, dedupe case-insensitively keeping first
        // occurrence (BaseTags first, so the brand leads), cap at 30.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return combined
            .Select(NormalizeTag)
            .Where(t => t.Length > 0 && seen.Add(t))
            .Take(30)
            .ToList();
    }

    private static List<string>? LoadVideo3Tags()
    {
        const string path = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261122-115430/script.json";
        if (!File.Exists(path))
        {
            Console.WriteLine($"SKIP: video #3 script.json not found at {path}");
            return null;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("Tags").EnumerateArray()
            .Select(t => t.GetString() ?? "")
            .Where(t => t.Length > 0)
            .ToList();
    }
}
