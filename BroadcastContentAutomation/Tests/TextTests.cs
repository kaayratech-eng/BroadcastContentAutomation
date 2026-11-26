// Regression tests for GiggleGarden.Shared.Text.
// Text.Fit is the title/caption truncator used by every video's sidecar
// (Short title <= 100 chars, captions, chapter labels). A bad truncation
// (split word, split surrogate pair, lost ellipsis) ships a broken title.
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class TextTests
{
    [Fact]
    public void Fit_ShortValue_ReturnsUnchanged()
    {
        Assert.Equal("GTA 6 Drops", Text.Fit("GTA 6 Drops", 100));
    }

    [Fact]
    public void Fit_ExactlyAtLimit_ReturnsUnchanged()
    {
        var value = new string('a', 100);
        Assert.Equal(value, Text.Fit(value, 100));
    }

    [Fact]
    public void Fit_LongValue_TruncatesAtWordBoundaryWithEllipsis()
    {
        // 100-char budget, ellipsis "…" (1 char) => cut at 99, back to the
        // last space before it, then "…".
        var result = Text.Fit("Trump Claims FBI Hack Could Reveal a Russia Hoax What Happens Next", 20);
        Assert.True(result.Length <= 20, $"got '{result}' ({result.Length} chars)");
        Assert.EndsWith("…", result);
        Assert.DoesNotContain("  ", result);
    }

    [Fact]
    public void Fit_LongTitle_KeepsWholeWords()
    {
        // The Short title path: a 100-char title must not end mid-word.
        var title = "GTA 6 Drops: Rockstar's New Vice City Is Here – What It Means For Gamers Everywhere Today";
        var result = Text.Fit(title, 100);
        Assert.True(result.Length <= 100);
        // No trailing partial word: the char before the ellipsis is a letter
        // only if the word itself ended there.
        Assert.DoesNotContain("… ", result);
    }

    [Fact]
    public void Fit_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", Text.Fit(null!, 10));
        Assert.Equal("", Text.Fit("", 10));
    }

    [Fact]
    public void Fit_EmptyEllipsis_TruncatesWithoutEllipsis()
    {
        // WithCanonicalHashtags fits caption text with ellipsis: "" so the
        // hashtags appended after it are not pushed out by a "…".
        var result = Text.Fit("Rockstar's GTA 6 is out and it brings a brand-new Vice City to life", 30, ellipsis: "");
        Assert.True(result.Length <= 30);
        Assert.DoesNotContain("…", result);
    }

    [Fact]
    public void Fit_SurrogatePair_NotSplit()
    {
        // An emoji (surrogate pair) at the cut point must not be split into
        // a lone surrogate, which renders as a replacement character.
        var value = "Hello world 🌴🌴🌴🌴🌴🌴🌴🌴🌴🌴 end";
        var result = Text.Fit(value, 15);
        Assert.DoesNotContain("�", result);
        foreach (var c in result)
            Assert.False(char.IsLowSurrogate(c) && result.IndexOf(c) == 0, "lone low surrogate at start");
    }

    [Fact]
    public void Fit_SingleLongWord_TruncatesHard()
    {
        // No space to break at: cut at the limit, no infinite loop, no crash.
        var result = Text.Fit(new string('x', 50), 10);
        Assert.Equal(10, result.Length);
    }
}
