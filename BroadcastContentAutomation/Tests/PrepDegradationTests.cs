// L18 — a prep that completes on degraded sources must say so: one
// DEGRADED summary line + prep-warnings.txt in the job folder, never a
// clean-looking success. A full end-to-end --prep cannot be stubbed in a
// unit test (no fake script provider, no feed seams), so the test drives
// PrepDegradation the way RunPrepAsync/FinishPrepAsync do, and a source
// guard pins the wiring into FinishPrepAsync's tail.
namespace BroadcastContentAutomation.Tests;

public class PrepDegradationTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "production", "finish-chunks.py")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate repo root (production/finish-chunks.py).");
        }
    }

    private static string ReadRepoFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. parts]));

    [Fact]
    public async Task Prep_AllTrendSourcesDown_ExitsDegradedNotClean()
    {
        // (a) All trend sources down (picked topic): the report came back
        // with no text and no news, and the picked-topic Wikipedia
        // background fetch failed too.
        var emptyReport = new TrendResearch.TrendReport("", "general", []);
        var degraded = PrepDegradation.Collect(emptyReport,
            forcedArticleEmpty: false, forcedWikipediaEmpty: false, pickedBackgroundFailed: true);
        Assert.Contains("trend feeds", string.Join("; ", degraded));
        Assert.Contains(PrepDegradation.PickedBackground, degraded);
        var line = PrepDegradation.SummaryLine(degraded);
        Assert.StartsWith("prep completed DEGRADED:", line);
        Assert.Contains("trend feeds", line);
        Assert.DoesNotContain("prep completed OK", line);

        // (b) Forced topic with both its fetches down: article + Wikipedia.
        var forced = PrepDegradation.Collect(null,
            forcedArticleEmpty: true, forcedWikipediaEmpty: true, pickedBackgroundFailed: false);
        Assert.Contains("article fetch", string.Join("; ", forced));
        Assert.Contains("Wikipedia background", string.Join("; ", forced));

        // (c) The degradation is recorded in the job folder.
        var dir = Path.Combine(Path.GetTempPath(), "qg-prepdeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await PrepDegradation.FinishAsync(dir, degraded);
            var file = Path.Combine(dir, PrepDegradation.WarningsFile);
            Assert.True(File.Exists(file));
            var text = await File.ReadAllTextAsync(file);
            Assert.Contains("DEGRADED", text);
            Assert.Contains("trend feeds", text);
            Assert.Contains(PrepDegradation.PickedBackground, text);

            // (d) A clean prep says OK and leaves no warnings file behind
            // (a stale one from an earlier degraded run is deleted).
            var goodReport = new TrendResearch.TrendReport("headlines...",
                "general",
                [new TrendResearch.NewsItem("T", "S", "https://example.com", null, DateTime.UtcNow)]);
            var clean = PrepDegradation.Collect(goodReport,
                forcedArticleEmpty: false, forcedWikipediaEmpty: false, pickedBackgroundFailed: false);
            Assert.Empty(clean);
            Assert.Equal("prep completed OK", PrepDegradation.SummaryLine(clean));
            await PrepDegradation.FinishAsync(dir, clean);
            Assert.False(File.Exists(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PrepDegradation_WiredIntoFinishPrepAsync()
    {
        // Source guard: the helper alone proves nothing — FinishPrepAsync
        // must actually summarize before its return 0, and RunPrepAsync
        // must collect the upstream sources and pass them in.
        var program = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "Program.cs");
        var finish = program[(program.IndexOf("async Task<int> FinishPrepAsync", StringComparison.Ordinal))..];
        var tail = finish[..finish.IndexOf("return 0;", StringComparison.Ordinal)];
        Assert.Contains("PrepDegradation.FinishAsync(workDir, degraded)", tail);
        Assert.Contains("pickedBackgroundFailed", tail);
        var prep = program[..program.IndexOf("async Task<int> FinishPrepAsync", StringComparison.Ordinal)];
        Assert.Contains("PrepDegradation.Collect(trendReport, forcedArticleEmpty, forcedWikipediaEmpty", prep);
        Assert.Contains("FinishPrepAsync(workDir, script, pooled, addToPool, trendContext, trendReport?.News, DateTime.Today, degradedSources)", prep);
    }
}
