// Fix #4 (monetization audit, 2026-11-26): weekly analytics pull — real
// numbers into analytics.jsonl from the YouTube Analytics + Data APIs,
// plus the weekly brief. Tests use a fake fetcher; no live API calls.
//
// The sandbox runner invokes [Fact] methods without awaiting a returned
// Task, so these tests are synchronous and block on the async calls in the
// helpers below (the xUnit1031 analyzer stays quiet — same pattern as
// AttemptAndCrossLinkTests / UploaderTests).
using GiggleGarden.Shared;
using GiggleGarden.Uploader;
using Xunit;

namespace BroadcastContentAutomation.Tests;

public class AnalyticsPullTests
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

    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tn-analytics-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Append(string dir, AnalyticsEntry entry)
        => AnalyticsLog.AppendAsync(dir, entry).GetAwaiter().GetResult();

    private static List<AnalyticsEntry> Load(string dir)
        => AnalyticsLog.LoadAsync(dir).GetAwaiter().GetResult();

    private static int UpdateStats(string dir, Dictionary<string, VideoStats> stats)
        => AnalyticsLog.UpdateStatsAsync(dir, stats).GetAwaiter().GetResult();

    private static bool RunCore(string dir, AnalyticsPull.IAnalyticsFetcher fetcher, string logDir)
        => AnalyticsPull.RunCoreAsync(dir, fetcher, new Logger(logDir), CancellationToken.None).GetAwaiter().GetResult();

    private static AnalyticsEntry Entry(string id, string title, string channel, string aspect, long views = 0)
        => new()
        {
            VideoId = id,
            Title = title,
            Channel = channel,
            Aspect = aspect,
            PublishedAt = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero),
            Views = views,
        };

    private sealed class FakeFetcher(
        Dictionary<string, VideoStats> byVideo,
        long channelViews = 0,
        List<AnalyticsLog.DailyPoint>? daily = null,
        long fallbackViews = 0,
        int fallbackVideos = 0) : AnalyticsPull.IAnalyticsFetcher
    {
        public IReadOnlyList<string>? ReceivedIds { get; private set; }

        public Task<AnalyticsPull.FetchResult> FetchAsync(IReadOnlyList<string> videoIds, string startDate, string endDate, CancellationToken ct)
        {
            ReceivedIds = videoIds;
            return Task.FromResult(new AnalyticsPull.FetchResult(
                byVideo, channelViews, 0, 0, 0, daily ?? [], 1, 0, fallbackViews, fallbackVideos));
        }
    }

    private sealed class ThrowingFetcher : AnalyticsPull.IAnalyticsFetcher
    {
        public Task<AnalyticsPull.FetchResult> FetchAsync(IReadOnlyList<string> videoIds, string startDate, string endDate, CancellationToken ct)
            => throw new InvalidOperationException("simulated fetch failure");
    }

    private static string LogText(string logDir)
        => File.ReadAllText(Path.Combine(logDir, $"upload-{DateTime.Now:yyyy-MM-dd}.log"));

    [Fact]
    public void LoadAsync_ReadsPrettyPrintedLog()
    {
        // The real log is written with Sidecar.Options (WriteIndented), so one
        // entry spans many lines. The old line-by-line reader silently
        // returned zero entries for a real file.
        var dir = TempDir("pretty");
        Append(dir, Entry("aaa", "First", "trendlinenow", "landscape"));
        Append(dir, Entry("bbb", "Second", "trendlinenow", "vertical"));
        var loaded = Load(dir);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("aaa", loaded[0].VideoId);
        Assert.Equal("bbb", loaded[1].VideoId);
    }

    [Fact]
    public void LoadAsync_SkipsCorruptEntryWithoutLosingTheRest()
    {
        var dir = TempDir("corrupt");
        var good1 = """{"VideoId":"aaa","Title":"Good one","Channel":"trendlinenow","Aspect":"landscape","PublishedAt":"2026-11-21T00:00:00+00:00","Views":0}""";
        var good2 = """{"VideoId":"bbb","Title":"Good two","Channel":"trendlinenow","Aspect":"vertical","PublishedAt":"2026-11-21T00:00:00+00:00","Views":0}""";
        File.WriteAllText(AnalyticsLog.PathFor(dir), good1 + "\n{\"VideoId\": broken}\n" + good2 + "\n");
        var loaded = Load(dir);
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, e => e.VideoId == "aaa");
        Assert.Contains(loaded, e => e.VideoId == "bbb");
    }

    [Fact]
    public void UpdateStats_MatchesByVideoIdWithoutDuplicatingLines()
    {
        var dir = TempDir("update");
        Append(dir, Entry("aaa", "First", "trendlinenow", "landscape"));
        Append(dir, Entry("aaa", "First (dup)", "trendlinenow", "landscape"));
        Append(dir, Entry("bbb", "Second", "trendlinenow", "vertical"));
        Append(dir, Entry("ccc", "Other channel", "chronicleandchaos", "landscape"));

        var stats = new Dictionary<string, VideoStats>(StringComparer.OrdinalIgnoreCase)
        {
            ["aaa"] = new VideoStats(12, 30, 150, 40, 2, 0, 3, 1),
            ["bbb"] = new VideoStats(5, 10, 120, 30, 0, 0, 1, 0),
            ["zzz"] = new VideoStats(99, 99, 99, 99, 9, 0, 9, 9), // unknown id: ignored
        };
        var updated = UpdateStats(dir, stats);
        Assert.Equal(3, updated); // both "aaa" rows + "bbb"; "ccc" untouched, "zzz" ignored

        var loaded = Load(dir);
        Assert.Equal(4, loaded.Count); // no line added, none removed
        Assert.All(loaded.Where(e => e.VideoId == "aaa"), e =>
        {
            Assert.Equal(12, e.Views);
            Assert.Equal(30, e.EstimatedMinutesWatched);
            Assert.Equal(150, e.AvgViewSeconds);
            Assert.Equal(40, e.AverageViewPercentage);
            Assert.Equal(2, e.SubscribersGained);
            Assert.Equal(3, e.Likes);
            Assert.Equal(1, e.Comments);
            Assert.NotNull(e.StatsUpdatedAt);
        });
        var other = loaded.Single(e => e.VideoId == "ccc");
        Assert.Equal(0, other.Views);
        Assert.Null(other.StatsUpdatedAt);
    }

    [Fact]
    public void RenderBrief_RendersRealNumbersAndStaysHonest()
    {
        var long1 = Entry("long1", "Star story one", "trendlinenow", "landscape", 10);
        long1.AvgViewSeconds = 200; long1.AverageViewPercentage = 50; long1.Likes = 2; long1.Comments = 1; long1.SubscribersGained = 1;
        var long2 = Entry("long2", "Star story two", "trendlinenow", "landscape", 4);
        long2.AvgViewSeconds = 100; long2.AverageViewPercentage = 25;
        var short1 = Entry("short1", "Star story one #Shorts", "trendlinenow", "vertical", 6);
        short1.AvgViewSeconds = 40; short1.AverageViewPercentage = 60; short1.Likes = 1; short1.SubscribersGained = 1;
        var entries = new List<AnalyticsEntry> { long1, long2, short1, Entry("zero1", "Unwatched", "trendlinenow", "landscape") };
        var ctx = new AnalyticsLog.BriefContext(
            new DateTimeOffset(2026, 11, 26, 19, 0, 0, TimeSpan.Zero),
            20, 15, 2, 0,
            [new AnalyticsLog.DailyPoint("2026-11-26", 20, 2)],
            SkippedOtherChannel: 1, RowsWithoutStats: 0);
        var brief = AnalyticsLog.RenderBrief(entries, ctx);

        // Top-5 ordering + real numbers.
        Assert.Contains("10 views", brief);
        Assert.True(brief.IndexOf("long1", StringComparison.Ordinal) < brief.IndexOf("long2", StringComparison.Ordinal),
            "Top-5 must rank the 10-view video above the 4-view one.");
        // Honesty about the sample size, and the CTR limitation stated plainly.
        Assert.Contains("too little data to declare a winner", brief);
        Assert.Contains("raw counts", brief);
        Assert.Contains("NOT exposed by the YouTube Analytics API", brief);
        Assert.Contains("Studio-only", brief);
        // Funnel proxy: Shorts 6 views / 1 sub = 166.67 per 1k; long-form 14 views / 1 sub = 71.43 per 1k.
        Assert.Contains("| Shorts | 1 | 6 | 1 | 166.67 |", brief);
        Assert.Contains("| Long-form | 2 | 14 | 1 | 71.43 |", brief);
        Assert.Contains("2026-11-26: 20 views, 2 subs gained", brief);
        Assert.Contains("1 log row(s) belong to another channel", brief);
    }

    [Fact]
    public void RenderBrief_DuplicateRowsCountEachVideoOnce()
    {
        // Live log 2026-11-27: BjbtRFV-cSY ×2, v7I4P3iBw4A ×3. RenderBrief
        // dedupes by VideoId (fix #4); this test pins it so a future change
        // cannot silently re-inflate the brief total or the funnel.
        var dupShort1 = Entry("aaaaaaaaaaa", "Dup short", "trendlinenow", "vertical", 3);
        var dupShort2 = Entry("aaaaaaaaaaa", "Dup short", "trendlinenow", "vertical", 3);
        var dupLong1 = Entry("bbbbbbbbbbb", "Dup long", "trendlinenow", "landscape", 5);
        var dupLong2 = Entry("bbbbbbbbbbb", "Dup long", "trendlinenow", "landscape", 5);
        var dupLong3 = Entry("bbbbbbbbbbb", "Dup long", "trendlinenow", "landscape", 5);
        var entries = new List<AnalyticsEntry> { dupShort1, dupShort2, dupLong1, dupLong2, dupLong3 };
        var ctx = new AnalyticsLog.BriefContext(
            new DateTimeOffset(2026, 11, 26, 19, 0, 0, TimeSpan.Zero),
            8, 2, 0, 0,
            [new AnalyticsLog.DailyPoint("2026-11-26", 8, 0)],
            SkippedOtherChannel: 0, RowsWithoutStats: 0);
        var brief = AnalyticsLog.RenderBrief(entries, ctx);

        // 3 + 5 = 8, not the raw row sum 3+3+5+5+5 = 21.
        Assert.Contains("Only 2 video(s) with views, 8 views in total.", brief);
        Assert.Contains("| Shorts | 1 | 3 | 0 | 0.00 |", brief);
        Assert.Contains("| Long-form | 1 | 5 | 0 | 0.00 |", brief);
        // Each video appears once in the top-5.
        Assert.Equal(1, CountOccurrences(brief, "aaaaaaaaaaa"));
        Assert.Equal(1, CountOccurrences(brief, "bbbbbbbbbbb"));
        // The daily series comes from the Analytics API (channel-level), not
        // from rows, so it is unaffected by duplicate rows.
        Assert.Contains("2026-11-26: 8 views, 0 subs gained", brief);
    }

    [Fact]
    public void RenderBrief_DuplicateRowsPreferLatestStatsStamp()
    {
        // UpdateStatsAsync writes identical stats to all duplicates in
        // place, so rows for one video only disagree if a run was interrupted
        // mid-update. The tiebreak is documented: latest StatsUpdatedAt wins.
        var older = Entry("aaaaaaaaaaa", "Dup video", "trendlinenow", "landscape", 3);
        older.StatsUpdatedAt = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
        var newer = Entry("aaaaaaaaaaa", "Dup video", "trendlinenow", "landscape", 7);
        newer.StatsUpdatedAt = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var ctx = new AnalyticsLog.BriefContext(
            new DateTimeOffset(2026, 11, 26, 19, 0, 0, TimeSpan.Zero),
            7, 1, 0, 0, [], SkippedOtherChannel: 0, RowsWithoutStats: 0);
        var brief = AnalyticsLog.RenderBrief([older, newer], ctx);
        Assert.Contains("Only 1 video(s) with views, 7 views in total.", brief);
    }

    [Fact]
    public void RenderBrief_WithFallbackViews_LabelsMixedSource()
    {
        // Brief-label item (2026-11-27): the brief total mixes Analytics
        // rows with Data API public-count fallback views, while the channel
        // total is Analytics-only — the brief must say so, with the right
        // numbers, instead of leaving 21 next to 14 unexplained.
        var entries = new List<AnalyticsEntry>
        {
            Entry("aaaaaaaaaaa", "Older video", "trendlinenow", "landscape", 14),
            Entry("bbbbbbbbbbb", "New video one", "trendlinenow", "vertical", 2),
            Entry("ccccccccccc", "New video two", "trendlinenow", "vertical", 2),
            Entry("ddddddddddd", "New video three", "trendlinenow", "vertical", 2),
            Entry("eeeeeeeeeee", "New video four", "trendlinenow", "vertical", 1),
        };
        var ctx = new AnalyticsLog.BriefContext(
            new DateTimeOffset(2026, 11, 27, 2, 0, 0, TimeSpan.Zero),
            14, 4, 1, 0,
            [new AnalyticsLog.DailyPoint("2026-11-26", 14, 1)],
            SkippedOtherChannel: 0, RowsWithoutStats: 0,
            FallbackViews: 7, FallbackVideos: 4);
        var brief = AnalyticsLog.RenderBrief(entries, ctx);

        Assert.Contains("Only 5 video(s) with views, 21 views in total.", brief);
        Assert.Contains("Of those 21 views, 7 are public counts (YouTube Data API) for 4 video(s) published in the last 2–3 days and not yet processed into Analytics; the channel total of 14 views below is Analytics-only and lags by the same window.", brief);
    }

    [Fact]
    public void RenderBrief_WithoutFallbackViews_ShowsNoLabel()
    {
        // Steady state: no fallback views, so no mixed-source label at all
        // — nothing misleading lingers once every video has an Analytics row.
        var entries = new List<AnalyticsEntry>
        {
            Entry("aaaaaaaaaaa", "Older video", "trendlinenow", "landscape", 14),
            Entry("bbbbbbbbbbb", "New video one", "trendlinenow", "vertical", 2),
            Entry("ccccccccccc", "New video two", "trendlinenow", "vertical", 2),
            Entry("ddddddddddd", "New video three", "trendlinenow", "vertical", 2),
            Entry("eeeeeeeeeee", "New video four", "trendlinenow", "vertical", 1),
        };
        var ctx = new AnalyticsLog.BriefContext(
            new DateTimeOffset(2026, 11, 27, 2, 0, 0, TimeSpan.Zero),
            21, 4, 1, 0,
            [new AnalyticsLog.DailyPoint("2026-11-26", 21, 1)],
            SkippedOtherChannel: 0, RowsWithoutStats: 0);
        var brief = AnalyticsLog.RenderBrief(entries, ctx);

        Assert.Contains("Only 5 video(s) with views, 21 views in total.", brief);
        Assert.DoesNotContain("are public counts", brief);
        Assert.DoesNotContain("Analytics-only", brief);
        Assert.DoesNotContain("not yet processed into Analytics", brief);
    }

    [Fact]
    public void RunCore_PassesFallbackCountsIntoBrief()
    {
        // Pins the wiring: a fetcher that reports fallback views must land
        // them in the written brief — RenderBrief alone proves the label,
        // this proves the pull actually feeds it the counts.
        var dir = TempDir("fallback");
        var logDir = TempDir("fallbacklog");
        Append(dir, Entry("aaaaaaaaaaa", "Older video", "trendlinenow", "landscape"));
        Append(dir, Entry("bbbbbbbbbbb", "New video", "trendlinenow", "vertical"));

        var fetcher = new FakeFetcher(
            new Dictionary<string, VideoStats>(StringComparer.OrdinalIgnoreCase)
            {
                ["aaaaaaaaaaa"] = new VideoStats(14, 20, 150, 40, 1, 0, 2, 1),
                ["bbbbbbbbbbb"] = new VideoStats(7, 0, 0, 0, 0, 0, 1, 0),
            },
            channelViews: 14,
            daily: [new AnalyticsLog.DailyPoint("2026-11-26", 14, 1)],
            fallbackViews: 7,
            fallbackVideos: 1);
        var ok = RunCore(dir, fetcher, logDir);
        Assert.True(ok);

        var briefPath = Path.Combine(dir, $"brief-{DateTimeOffset.UtcNow:yyyy-MM-dd}.md");
        var brief = File.ReadAllText(briefPath);
        Assert.Contains("Only 2 video(s) with views, 21 views in total.", brief);
        Assert.Contains("Of those 21 views, 7 are public counts (YouTube Data API) for 1 video(s) published in the last 2–3 days and not yet processed into Analytics; the channel total of 14 views below is Analytics-only and lags by the same window.", brief);
    }

    [Fact]
    public void Summarize_DuplicateRowsCountEachVideoOnce()
    {
        // Same video logged 3 times (live log: v7I4P3iBw4A ×3). Summarize
        // feeds the next script prompt, so a triple-counted video would
        // dominate the learning loop: ranked 3 times, CTR averaged 3 times,
        // question-titles counted 3 times.
        var entries = new List<AnalyticsEntry>
        {
            Entry("aaaaaaaaaaa", "Dup video?", "trendlinenow", "landscape", 3),
            Entry("aaaaaaaaaaa", "Dup video?", "trendlinenow", "landscape", 3),
            Entry("aaaaaaaaaaa", "Dup video?", "trendlinenow", "landscape", 3),
        };
        entries.ForEach(e => e.Ctr = 10);
        var summary = AnalyticsLog.Summarize(entries);
        Assert.Contains("Based on 1 videos with stats (avg CTR 10.0% when known):", summary);
        Assert.Equal(1, CountOccurrences(summary, "Dup video?"));
        Assert.Contains("Note: 1 of these titles are questions", summary);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    [Fact]
    public void PerVideoMetrics_ExcludeImpressionsAndCtr()
    {
        // The Analytics API has no thumbnail-impression metrics: reports.query
        // returns 400 "Unknown identifier" for them. If a future API revision
        // adds them, this test is the place that must change deliberately.
        Assert.Equal("views,estimatedMinutesWatched,averageViewDuration,averageViewPercentage,subscribersGained,subscribersLost",
            AnalyticsPull.PerVideoMetrics);
        Assert.DoesNotContain("impressions", AnalyticsPull.PerVideoMetrics);
        Assert.DoesNotContain("impressionClickThroughRate", AnalyticsPull.PerVideoMetrics);
    }

    [Fact]
    public void YouTubePublisherScopeListHasAllFiveScopes()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "Uploader", "Publishing", "YouTubePublisher.cs"));
        Assert.Contains("YouTubeService.Scope.YoutubeUpload", source);
        Assert.Contains("YouTubeService.Scope.YoutubeReadonly", source);
        Assert.Contains("https://www.googleapis.com/auth/yt-analytics.readonly", source);
        Assert.Contains("https://www.googleapis.com/auth/youtube.force-ssl", source);
        Assert.Contains("https://www.googleapis.com/auth/youtube\"", source);
    }

    [Fact]
    public void RunCore_WritesBriefAndUpdatesLog()
    {
        var dir = TempDir("runcore");
        var logDir = TempDir("runlog");
        // IDs must be real-shaped 11-char YouTube IDs: RunCore now filters
        // out anything that is not one (see RunCore_SkipsInvalidVideoId...).
        Append(dir, Entry("aaaaaaaaaaa", "First", "trendlinenow", "landscape"));
        Append(dir, Entry("bbbbbbbbbbb", "Second", "trendlinenow", "vertical"));
        Append(dir, Entry("ccc", "Other channel", "chronicleandchaos", "landscape"));

        var fetcher = new FakeFetcher(
            new Dictionary<string, VideoStats>(StringComparer.OrdinalIgnoreCase)
            {
                ["aaaaaaaaaaa"] = new VideoStats(8, 20, 150, 40, 1, 0, 2, 1),
                ["bbbbbbbbbbb"] = new VideoStats(3, 2, 40, 60, 0, 0, 0, 0),
            },
            channelViews: 11,
            daily: [new AnalyticsLog.DailyPoint("2026-11-26", 11, 1)]);
        var ok = RunCore(dir, fetcher, logDir);
        Assert.True(ok);

        var loaded = Load(dir);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(8, loaded.Single(e => e.VideoId == "aaaaaaaaaaa").Views);
        Assert.Equal(0, loaded.Single(e => e.VideoId == "ccc").Views);

        var briefPath = Path.Combine(dir, $"brief-{DateTimeOffset.UtcNow:yyyy-MM-dd}.md");
        Assert.True(File.Exists(briefPath), $"Brief not written at {briefPath}.");
        var brief = File.ReadAllText(briefPath);
        Assert.Contains("8 views", brief);
        Assert.Contains("1 log row(s) belong to another channel", brief);
    }

    [Fact]
    public void AnalyticsPull_PostFetchFailure_Exits1Cleanly()
    {
        // L73: the fetch succeeds but the brief write fails (the brief
        // path is blocked) — RunCore must return false (exit 1 at the
        // call site) with an error line naming the step, not crash
        // unhandled the way the old unguarded post-fetch steps did.
        var dir = TempDir("brief-fail");
        var logDir = TempDir("brief-fail-log");
        try
        {
            Append(dir, Entry("aaaaaaaaaaa", "First", "trendlinenow", "landscape"));
            Append(dir, Entry("bbbbbbbbbbb", "Second", "trendlinenow", "vertical"));
            // Block the brief write: a directory sits where the brief file
            // would go, so only the write fails (the stats update before
            // it still succeeds).
            Directory.CreateDirectory(Path.Combine(dir, $"brief-{DateTimeOffset.UtcNow:yyyy-MM-dd}.md"));
            var fetcher = new FakeFetcher(
                new Dictionary<string, VideoStats>(StringComparer.OrdinalIgnoreCase)
                {
                    ["aaaaaaaaaaa"] = new VideoStats(8, 20, 150, 40, 1, 0, 2, 1),
                    ["bbbbbbbbbbb"] = new VideoStats(3, 2, 40, 60, 0, 0, 0, 0),
                },
                channelViews: 11,
                daily: [new AnalyticsLog.DailyPoint("2026-11-26", 11, 1)]);
            var ok = RunCore(dir, fetcher, logDir);
            Assert.False(ok);
            var log = LogText(logDir);
            Assert.Contains("brief write failed", log);
            Assert.Contains("will retry next run", log);
            Assert.DoesNotContain("Unhandled", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public void AnalyticsPull_FetchFailure_NamesBatch()
    {
        // L73: a fetch failure must name the batch (first/last video id
        // + size) and the exception type — "fetch failed" alone cannot
        // say which videos were being pulled.
        var dir = TempDir("fetch-fail");
        var logDir = TempDir("fetch-fail-log");
        try
        {
            Append(dir, Entry("aaaaaaaaaaa", "First", "trendlinenow", "landscape"));
            Append(dir, Entry("bbbbbbbbbbb", "Second", "trendlinenow", "vertical"));
            var ok = RunCore(dir, new ThrowingFetcher(), logDir);
            Assert.False(ok);
            var log = LogText(logDir);
            Assert.Contains("fetch failed", log);
            Assert.Contains("batch of 2 video(s)", log);
            Assert.Contains("aaaaaaaaaaa", log);
            Assert.Contains("bbbbbbbbbbb", log);
            Assert.Contains("InvalidOperationException", log);
            Assert.Contains("simulated fetch failure", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public void IsValidVideoId_AcceptsElevenCharIdsAndRejectsOthers()
    {
        Assert.True(AnalyticsPull.IsValidVideoId("piRxpJGSOSk"));
        Assert.True(AnalyticsPull.IsValidVideoId("-9-gBZyxKgY"));
        Assert.True(AnalyticsPull.IsValidVideoId("N51TjjIndJg"));
        Assert.True(AnalyticsPull.IsValidVideoId("aaaaaaaaaaa"));
        // The real offender: a 17-digit Instagram media ID written as a
        // VideoId for the COP31 Short (2026-11-26 20:15 EST pull, 400).
        Assert.False(AnalyticsPull.IsValidVideoId("18082177424510099"));
        Assert.False(AnalyticsPull.IsValidVideoId(""));
        Assert.False(AnalyticsPull.IsValidVideoId("   "));
        Assert.False(AnalyticsPull.IsValidVideoId(null));
        Assert.False(AnalyticsPull.IsValidVideoId("short"));
        Assert.False(AnalyticsPull.IsValidVideoId("aaaaaaaaaaaa")); // 12 chars
        Assert.False(AnalyticsPull.IsValidVideoId("aaa bbb_ccc")); // space
    }

    [Fact]
    public void RunCore_SkipsInvalidVideoIdWithoutAbortingPull()
    {
        // The real COP31 Short row: VideoId is the Instagram media ID
        // 18082177424510099, not the YouTube Short pYTkZLdw9ig. Before this
        // fix it 400d the Analytics video== filter and killed the pull.
        var dir = TempDir("badid");
        var logDir = TempDir("badidlog");
        Append(dir, Entry("aaaaaaaaaaa", "Good video", "trendlinenow", "landscape"));
        Append(dir, Entry("18082177424510099", "COP31 Collapses: Why the World Walked Away From a Climate Deal", "trendlinenow", "vertical"));

        var fetcher = new FakeFetcher(
            new Dictionary<string, VideoStats>(StringComparer.OrdinalIgnoreCase)
            {
                ["aaaaaaaaaaa"] = new VideoStats(8, 20, 150, 40, 1, 0, 2, 1),
            },
            channelViews: 8,
            daily: [new AnalyticsLog.DailyPoint("2026-11-26", 8, 1)]);
        var ok = RunCore(dir, fetcher, logDir);
        Assert.True(ok); // one bad ID must not abort the pull

        // The fetcher never saw the bad ID.
        Assert.NotNull(fetcher.ReceivedIds);
        Assert.Single(fetcher.ReceivedIds);
        Assert.Equal("aaaaaaaaaaa", fetcher.ReceivedIds[0]);

        // Good row updated; bad row untouched (still 0 views, no stats stamp).
        var loaded = Load(dir);
        Assert.Equal(8, loaded.Single(e => e.VideoId == "aaaaaaaaaaa").Views);
        var bad = loaded.Single(e => e.VideoId == "18082177424510099");
        Assert.Equal(0, bad.Views);
        Assert.Null(bad.StatsUpdatedAt);

        // The warning names the offending ID.
        var log = LogText(logDir);
        Assert.Contains("18082177424510099", log);
        Assert.Contains("skipping", log);
    }

    [Fact]
    public void RunCore_FetchFailureReturnsFalseCleanly()
    {
        // Follow-up #1 from the first fix #4 report: a fetch failure must
        // log and return false (exit 1 at the call site), never an unhandled
        // stack trace / abort (exit 134, as seen 2026-11-26 20:05 + 20:15).
        var dir = TempDir("fetchfail");
        var logDir = TempDir("fetchfaillog");
        Append(dir, Entry("aaaaaaaaaaa", "Good video", "trendlinenow", "landscape"));

        var ok = RunCore(dir, new ThrowingFetcher(), logDir);
        Assert.False(ok);

        Assert.Contains("fetch failed", LogText(logDir));
        var briefPath = Path.Combine(dir, $"brief-{DateTimeOffset.UtcNow:yyyy-MM-dd}.md");
        Assert.False(File.Exists(briefPath), "No brief may be written when the fetch failed.");
    }

    [Fact]
    public void YouTubeVideoId_PrefersYouTubePublication()
    {
        // Dictionary order puts Instagram first (as it was for the COP31
        // Short): the old "first published target" code grabbed the
        // Instagram media ID. The helper must return the YouTube ID.
        var pubs = new Dictionary<string, Publication>(StringComparer.OrdinalIgnoreCase)
        {
            ["instagram"] = new Publication { Status = PublishStatus.Published, Id = "18082177424510099", At = DateTimeOffset.UtcNow },
            ["youtube"] = new Publication { Status = PublishStatus.Published, Id = "pYTkZLdw9ig", At = DateTimeOffset.UtcNow },
        };
        Assert.Equal("pYTkZLdw9ig", AnalyticsPull.YouTubeVideoId(pubs));

        var noYoutube = new Dictionary<string, Publication>(StringComparer.OrdinalIgnoreCase)
        {
            ["instagram"] = new Publication { Status = PublishStatus.Published, Id = "18082177424510099", At = DateTimeOffset.UtcNow },
        };
        Assert.Null(AnalyticsPull.YouTubeVideoId(noYoutube));

        var youtubeNotPublished = new Dictionary<string, Publication>(StringComparer.OrdinalIgnoreCase)
        {
            ["youtube"] = new Publication { Status = PublishStatus.Pending, Id = "pYTkZLdw9ig" },
        };
        Assert.Null(AnalyticsPull.YouTubeVideoId(youtubeNotPublished));
    }
}
