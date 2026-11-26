// Regression tests for canonical hashtags (review 2026-11-22, SEO #6).
// Program.CanonicalHashtags takes the long-form Description's hashtag line
// as the single source of truth, and WithCanonicalHashtags rebuilds each
// Short caption from it. The model writes a different hashtag set into
// every caption (#trendline vs #TrendlineNow, #gaming vs #gamingnews), so
// without this a video and its own Short drift apart in search.
// Hashtag extraction uses the same regex as production:
// #[letter/digit/underscore]+.
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BroadcastContentAutomation.Tests;

public class HashtagTests
{
    private const string Video3ScriptPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261122-115430/script.json";

    private static readonly string[] ExpectedCanonical =
    {
        "#TrendlineNow", "#GTA6", "#RockstarGames", "#gaming",
        "#news", "#explained", "#trending", "#tech",
    };

    private static List<string> CanonicalHashtags(string description) =>
        ReflectionHelper.Invoke<List<string>>(ReflectionHelper.VideoGen, "CanonicalHashtags", description);

    private static string WithCanonicalHashtags(string caption, IReadOnlyList<string> canonical, int maxHashtags, int maxLength) =>
        ReflectionHelper.Invoke<string>(ReflectionHelper.VideoGen, "WithCanonicalHashtags", caption, canonical, maxHashtags, maxLength);

    private static List<string> ExtractHashtags(string text) =>
        Regex.Matches(text ?? "", @"#[\p{L}\p{N}_]+").Select(m => m.Value).ToList();

    private static (string Description, string ReelsCaption, string TikTokCaption)? LoadVideo3Captions()
    {
        if (!File.Exists(Video3ScriptPath))
        {
            Console.WriteLine($"SKIP: video #3 script.json not found at {Video3ScriptPath}");
            return null;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Video3ScriptPath));
        var root = doc.RootElement;
        return (
            root.GetProperty("Description").GetString() ?? "",
            root.GetProperty("ReelsCaption").GetString() ?? "",
            root.GetProperty("TikTokCaption").GetString() ?? "");
    }

    [Fact]
    public void CanonicalHashtags_Video3Description_ReturnsEightHashtags()
    {
        var data = LoadVideo3Captions();
        if (data is null) return;
        var canonical = CanonicalHashtags(data.Value.Description);
        Assert.Equal(ExpectedCanonical, canonical);
    }

    [Fact]
    public void WithCanonicalHashtags_ReelsCaption_HashtagsExactlyCanonical()
    {
        // No drift: #TrendlineNow, not the model's #trendline; #gaming,
        // not the model's #gamingnews.
        var data = LoadVideo3Captions();
        if (data is null) return;
        var canonical = CanonicalHashtags(data.Value.Description);
        var rebuilt = WithCanonicalHashtags(data.Value.ReelsCaption, canonical, maxHashtags: 8, maxLength: 300);
        Assert.Equal(ExpectedCanonical, ExtractHashtags(rebuilt));
    }

    [Fact]
    public void WithCanonicalHashtags_ReelsCaption_Fits300CharsAndKeepsText()
    {
        var data = LoadVideo3Captions();
        if (data is null) return;
        var canonical = CanonicalHashtags(data.Value.Description);
        var rebuilt = WithCanonicalHashtags(data.Value.ReelsCaption, canonical, maxHashtags: 8, maxLength: 300);
        Assert.True(rebuilt.Length <= 300, $"rebuilt Reels caption is {rebuilt.Length} chars");
        Assert.StartsWith("Rockstar", rebuilt);
    }

    [Fact]
    public void WithCanonicalHashtags_TikTokCaption_HashtagsAreFirstFourCanonical()
    {
        var data = LoadVideo3Captions();
        if (data is null) return;
        var canonical = CanonicalHashtags(data.Value.Description);
        var rebuilt = WithCanonicalHashtags(data.Value.TikTokCaption, canonical, maxHashtags: 4, maxLength: 150);
        Assert.Equal(ExpectedCanonical.Take(4), ExtractHashtags(rebuilt));
    }

    [Fact]
    public void WithCanonicalHashtags_TikTokCaption_Fits150Chars()
    {
        var data = LoadVideo3Captions();
        if (data is null) return;
        var canonical = CanonicalHashtags(data.Value.Description);
        var rebuilt = WithCanonicalHashtags(data.Value.TikTokCaption, canonical, maxHashtags: 4, maxLength: 150);
        Assert.True(rebuilt.Length <= 150, $"rebuilt TikTok caption is {rebuilt.Length} chars");
    }

    [Fact]
    public void WithCanonicalHashtags_EmptyCanonical_LeavesCaptionAsWritten()
    {
        // Fallback: with no canonical set there is nothing to rebuild from,
        // so the caption stays exactly as the model wrote it.
        const string caption = "Big news today, find out what happened next.";
        var result = WithCanonicalHashtags(caption, Array.Empty<string>(), maxHashtags: 8, maxLength: 300);
        Assert.Equal(caption, result);
    }

    [Fact]
    public void WithCanonicalHashtags_StripsModelHashtagsBeforeAppending()
    {
        // R6: only TRAILING model hashtags are stripped and replaced by
        // the canonical set — a model hashtag at the end of the caption
        // must not survive alongside the canonical ones.
        var canonical = new List<string> { "#TrendlineNow", "#GTA6", "#gaming" };
        var result = WithCanonicalHashtags(
            "Breaking news today, more to come. #WrongTag #OldTag", canonical, maxHashtags: 3, maxLength: 300);
        Assert.DoesNotContain("#WrongTag", result);
        Assert.DoesNotContain("#OldTag", result);
        Assert.Equal(canonical, ExtractHashtags(result));
    }

    [Fact]
    public void WithCanonicalHashtags_KeepsInlineHashtags()
    {
        // R6: an INLINE hashtag is part of the caption text ("Watch
        // #GTA6 gameplay now" keeps #GTA6) — only trailing ones are
        // replaced.
        var canonical = new List<string> { "#TrendlineNow", "#GTA6" };
        var result = WithCanonicalHashtags(
            "Watch #GTA6 gameplay now #news #tech", canonical, maxHashtags: 2, maxLength: 150);
        Assert.Contains("#GTA6", result);
        Assert.StartsWith("Watch #GTA6 gameplay now", result);
        Assert.DoesNotContain("#news", result);
        Assert.DoesNotContain("#tech", result);
    }
}
