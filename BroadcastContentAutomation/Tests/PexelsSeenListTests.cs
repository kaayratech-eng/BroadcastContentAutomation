// PexelsSeenListTests.cs — fail-first xUnit tests for the per-job Pexels
// seen-list (VideoGen/PexelsSeenList.cs), added 2026-11-26 after the
// Ronaldo video shipped the same night-stadium clip in scenes 26+31:
// the stock-video path had no dedupe at all.
using Xunit;

namespace BroadcastContentAutomation.Tests;

public class PexelsSeenListTests
{
    static List<PexelsUsedEntry> Entries() =>
    [
        new("video", 111, 2, "football stadium lights", "https://www.pexels.com/video/a-111/"),
        new("video", 222, 3, "football stadium lights", "https://www.pexels.com/video/b-222/"),
        new("video", 333, 5, "stadium lights at night", "https://www.pexels.com/video/c-333/"),
        new("photo", 444, 5, "stadium lights at night", "https://www.pexels.com/photo/d-444/"),
        new("video", 555, 1, "football crowd", "https://www.pexels.com/video/e-555/"),
    ];

    [Fact]
    public void ExcludedIdsCoversPreviousThreeScenesOnly()
    {
        // Scene 6: the window is scenes 3, 4, 5 (distance < 4). The
        // photo entry never counts (videos only), scene 2 is distance
        // 4 — allowed — and scene 1 is older still.
        var excluded = PexelsSeenList.ExcludedIds(Entries(), 6);
        Assert.Equal(new HashSet<long> { 222, 333 }, excluded);
    }

    [Fact]
    public void ExcludedIdsEmptyForFirstScenes()
    {
        Assert.Empty(PexelsSeenList.ExcludedIds(Entries(), 0));
        Assert.Empty(PexelsSeenList.ExcludedIds(Entries(), 1));
        Assert.Empty(PexelsSeenList.ExcludedIds([], 10));
    }

    [Fact]
    public void ExcludedIdsAllowsReuseAtDistanceFour()
    {
        // Rishi's rule: reuse is allowed 4+ scenes later. A clip used
        // in scene 2 may come back in scene 6.
        var used = new List<PexelsUsedEntry> { new("video", 111, 2, "football stadium lights", null) };
        Assert.DoesNotContain(111L, PexelsSeenList.ExcludedIds(used, 6));
        Assert.Contains(111L, PexelsSeenList.ExcludedIds(used, 5));
    }

    [Fact]
    public void AppendLoadRoundTrips()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-seenlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var manifest = Path.Combine(tmp, "pexels-video-used.json");
            Assert.Empty(PexelsSeenList.Load(manifest));
            PexelsSeenList.Append(manifest, new PexelsUsedEntry("video", 111, 2, "football stadium lights", "https://www.pexels.com/video/a-111/"));
            PexelsSeenList.Append(manifest, new PexelsUsedEntry("video", 222, 3, "football stadium lights", "https://www.pexels.com/video/b-222/"));
            var loaded = PexelsSeenList.Load(manifest);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(111, loaded[0].Id);
            Assert.Equal(2, loaded[0].Scene);
            Assert.Equal("football stadium lights", loaded[0].Query);
            Assert.Equal(222, loaded[1].Id);

            // A corrupt manifest must not kill a prep — Load starts fresh.
            File.WriteAllText(manifest, "{ not json");
            Assert.Empty(PexelsSeenList.Load(manifest));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
