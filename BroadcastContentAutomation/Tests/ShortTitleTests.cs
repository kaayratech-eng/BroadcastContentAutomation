// Regression tests for the Short title (fix #9, review 2026-11-22 SEO #5).
// The Short title is the plain script title, Text.Fit to 100 chars, with
// NO " #Shorts" suffix. YouTube auto-detects Shorts from the vertical
// format, so the suffix added nothing — and it pushed video #2's Short
// title to 85 chars. The Description keeps its #Shorts hashtag (the
// YouTube marker); only the title drops it.
using System.Text.Json;
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class ShortTitleTests
{
    private const string JobsDir =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow";

    private static string? LoadTitle(string jobDir)
    {
        var path = Path.Combine(JobsDir, jobDir, "script.json");
        if (!File.Exists(path))
        {
            Console.WriteLine($"SKIP: script.json not found at {path}");
            return null;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("Title").GetString();
    }

    private static void AssertShortTitle(string? title, int expectedLength)
    {
        Assert.NotNull(title);
        var shortTitle = Text.Fit(title!, 100);
        // The Short title IS the script title: no suffix, no truncation.
        Assert.Equal(title, shortTitle);
        Assert.Equal(expectedLength, shortTitle.Length);
        Assert.DoesNotContain("#Shorts", shortTitle);
        Assert.True(shortTitle.Length <= 100, $"Short title is {shortTitle.Length} chars");
    }

    [Fact]
    public void ShortTitle_Video2_Is77CharsWithNoSuffix()
    {
        // Was 85 chars with the " #Shorts" suffix.
        AssertShortTitle(LoadTitle("job-20261122-005435"), 77);
    }

    [Fact]
    public void ShortTitle_Video3_Is61CharsWithNoSuffix()
    {
        AssertShortTitle(LoadTitle("job-20261122-115430"), 61);
    }

    [Fact]
    public void ShortTitle_Video1_EqualsScriptTitleWithNoSuffix()
    {
        // NOTE: the task brief said 53 chars, but that is the short form
        // from memory ("Trump's D.C. Memorial Arch Faces a Second Court
        // Block"). Video #1's actual script.json title carries the tail
        // " – Why It Matters" and is 70 chars. The regression that matters
        // — no " #Shorts" suffix, <= 100 chars, equals the script title —
        // is asserted against the real title.
        AssertShortTitle(LoadTitle("job-20261121-085638"), 70);
    }

    [Fact]
    public void ShortTitle_Source_NoLongerAppendsShortsSuffix()
    {
        // Source-level guard: the Short sidecar's Title assignment must not
        // append " #Shorts" (the old code was
        // `Title = Text.Fit($"{script.Title} #Shorts", 100)`).
        const string programPath =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/Program.cs";
        Assert.True(File.Exists(programPath), $"Program.cs not found at {programPath}");
        var lines = File.ReadAllLines(programPath);
        var offending = lines.Where(l => l.Contains("Title", StringComparison.Ordinal)
            && l.Contains("#Shorts", StringComparison.Ordinal)).ToList();
        Assert.Empty(offending);
    }

    [Fact]
    public void ShortTitle_Source_DescriptionKeepsShortsMarker()
    {
        // Positive control for the guard above: the Description is SUPPOSED
        // to keep its #Shorts hashtag (the YouTube marker), so the file must
        // still contain #Shorts somewhere — the guard is not vacuous.
        const string programPath =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/Program.cs";
        var lines = File.ReadAllLines(programPath);
        Assert.Contains(lines, l => l.Contains("Description", StringComparison.Ordinal)
            && l.Contains("#Shorts", StringComparison.Ordinal));
    }

    [Fact]
    public void ShortTitle_FinishChunks_NoLongerAppendsShortsSuffix()
    {
        // Fix #9 port (review #2 R2): the chunk path is the production
        // path for every shipped video, and its vertical sidecar still
        // wrote `"Title": fit(script["Title"] + " #Shorts", 100)` — so
        // shipped #2/#3 Shorts carry the suffix even though Program.cs
        // dropped it. No Title line in finish-chunks.py may mention
        // #Shorts at all.
        const string finishChunksPath =
            "/home/hatch/workspace/BroadcastContentAutomation/production/finish-chunks.py";
        Assert.True(File.Exists(finishChunksPath), $"finish-chunks.py not found at {finishChunksPath}");
        var lines = File.ReadAllLines(finishChunksPath);
        var offending = lines.Where(l => l.Contains("Title", StringComparison.Ordinal)
            && l.Contains("#Shorts", StringComparison.Ordinal)).ToList();
        Assert.Empty(offending);
    }

    [Fact]
    public void ShortTitle_FinishChunks_DescriptionKeepsShortsMarker()
    {
        // Positive control for the guard above: the vertical Description
        // in finish-chunks.py is SUPPOSED to keep its #Shorts hashtag
        // (the YouTube marker) — only the title drops it.
        const string finishChunksPath =
            "/home/hatch/workspace/BroadcastContentAutomation/production/finish-chunks.py";
        var lines = File.ReadAllLines(finishChunksPath);
        Assert.Contains(lines, l => l.Contains("Description", StringComparison.Ordinal)
            && l.Contains("#Shorts", StringComparison.Ordinal));
    }
}
