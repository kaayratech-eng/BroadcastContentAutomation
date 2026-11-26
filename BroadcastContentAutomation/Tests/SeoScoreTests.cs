// Regression tests for GiggleGarden.Shared.SeoScore.
// TrimTagsForYouTube enforces YouTube's 500-char tag budget; TitleVariants
// supplies the alternate titles carried on every sidecar.
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class SeoScoreTests
{
    [Fact]
    public void TrimTagsForYouTube_UnderLimit_KeepsAll()
    {
        var tags = new[] { "gta 6", "rockstar games", "vice city", "gaming" };
        Assert.Equal(tags, SeoScore.TrimTagsForYouTube(tags));
    }

    [Fact]
    public void TrimTagsForYouTube_OverLimit_CutsAt500Chars()
    {
        // 500-char total (each tag costs length + 1). The first tag that
        // would push the total past 500 is dropped, and so is everything
        // after it — order is trending/evergreen first.
        var tags = Enumerable.Range(0, 30).Select(i => $"tag-number-{i:D2}-padding").ToList();
        var kept = SeoScore.TrimTagsForYouTube(tags);
        var total = kept.Sum(t => t.Length + 1) - 1; // trailing separator not counted the same way; bound loosely
        Assert.True(kept.Count < tags.Count, "expected some tags to be cut");
        Assert.True(kept.Count > 0, "expected at least one tag kept");
        // The kept set must fit YouTube's 500-char budget (sum of lengths +
        // one comma each, minus the last).
        var budget = string.Join(",", kept).Length;
        Assert.True(budget <= 500, $"kept tags total {budget} chars, over the 500 budget");
        // Order preserved: the kept tags are a prefix of the input.
        Assert.Equal(tags.Take(kept.Count), kept);
    }

    [Fact]
    public void TrimTagsForYouTube_DeduplicatesCaseInsensitively()
    {
        var tags = new[] { "GTA 6", "gta 6", "Vice City", "vice city", "gaming" };
        var kept = SeoScore.TrimTagsForYouTube(tags);
        Assert.Equal(new[] { "GTA 6", "Vice City", "gaming" }, kept);
    }

    [Fact]
    public void TrimTagsForYouTube_Empty_ReturnsEmpty()
    {
        Assert.Empty(SeoScore.TrimTagsForYouTube(Array.Empty<string>()));
    }

    [Fact]
    public void TitleVariants_AlwaysIncludesOriginalFirst()
    {
        var variants = SeoScore.TitleVariants("GTA 6 Drops: Rockstar's New Vice City Is Here");
        Assert.Equal("GTA 6 Drops: Rockstar's New Vice City Is Here", variants[0]);
    }

    [Fact]
    public void TitleVariants_AddsQuestionAndRealStoryForms()
    {
        var variants = SeoScore.TitleVariants("GTA 6 Drops");
        Assert.Contains("What really happened in GTA 6 Drops?", variants);
        Assert.Contains("The real story behind GTA 6 Drops", variants);
        Assert.True(variants.Count <= 3, $"expected at most 3 variants, got {variants.Count}");
    }

    [Fact]
    public void TitleVariants_QuestionTitle_DoesNotAddAnotherQuestion()
    {
        var variants = SeoScore.TitleVariants("What's Really Happening?");
        Assert.DoesNotContain(variants, v => v.StartsWith("What really happened in"));
    }

    [Fact]
    public void TitleVariants_Deduplicates()
    {
        var variants = SeoScore.TitleVariants("The real story behind GTA 6");
        Assert.Equal(variants.Distinct(StringComparer.OrdinalIgnoreCase).Count(), variants.Count);
    }
}
