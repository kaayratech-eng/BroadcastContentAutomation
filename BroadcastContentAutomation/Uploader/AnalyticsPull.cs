using System.Globalization;
using Google.Apis.Auth.OAuth2;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTubeAnalytics.v2;
using GiggleGarden.Shared;
using GiggleGarden.Uploader.Publishing;

namespace GiggleGarden.Uploader;

// Weekly analytics pull (fix #4, 2026-11-26): fills videos/analytics/analytics.jsonl
// with real numbers from the YouTube Analytics + Data APIs and emits the weekly
// markdown brief. The Uploader appends one log line per publish with Views 0;
// this is the step that finally puts true numbers behind those lines, so the
// Summarize() learning loop VideoGen feeds into the next script prompt learns
// from this channel's own data.
//
// The class is public so tests (a separate assembly) can reach the static
// RunCoreAsync seam; the constructor and RunAsync stay internal because they
// take the Uploader's internal config records.
public sealed class AnalyticsPull
{
    private readonly YouTubeConfig _ytCfg;
    private readonly Logger _log;

    internal AnalyticsPull(YouTubeConfig ytCfg, Logger log)
    {
        _ytCfg = ytCfg;
        _log = log;
    }
    // Per-video metrics pulled from Analytics reports.query. Verified against
    // the Analytics API v2 metrics reference: these six exist. `impressions`
    // and `impressionClickThroughRate` do NOT — reports.query returns 400
    // "Unknown identifier" for them, so thumbnail CTR stays Studio-only
    // (see VideoStats in Shared/AnalyticsLog.cs).
    public const string PerVideoMetrics = "views,estimatedMinutesWatched,averageViewDuration,averageViewPercentage,subscribersGained,subscribersLost";
    public const string ChannelMetrics = "views,estimatedMinutesWatched,subscribersGained,subscribersLost";
    public const string DailyMetrics = "views,subscribersGained";

    public sealed record FetchResult(
        Dictionary<string, VideoStats> ByVideo,
        long ChannelViews,
        double ChannelWatchMinutes,
        long ChannelSubsGained,
        long ChannelSubsLost,
        List<AnalyticsLog.DailyPoint> Daily,
        int DataApiUnitsUsed,
        int RowsWithoutStats,
        // Provenance for the brief label (2026-11-27): views/videos whose
        // numbers came from the Data API public-count fallback (no Analytics
        // row yet). Default 0 so fake fetchers that do not track it compile
        // unchanged and render no label.
        long FallbackViews = 0,
        int FallbackVideos = 0);

    // A YouTube video ID is exactly 11 chars from [A-Za-z0-9_-]. Anything
    // else is not a YouTube video and reports.query's video== filter rejects
    // the whole call with a 400 — one bad ID used to kill the entire pull.
    // (Known bad value: 18082177424510099, an Instagram media ID that the
    // old log-append code wrote as a VideoId for the COP31 Short.)
    public static bool IsValidVideoId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length != 11) return false;
        foreach (var c in id)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        return true;
    }

    // The analytics log is keyed by YouTube video ID. The old append code
    // took the first published target in dictionary order, which could be
    // Instagram: the COP31 Short's log row got the Instagram media ID
    // 18082177424510099 as its VideoId. Take the YouTube publication only;
    // null when there is no published YouTube video (caller skips the row).
    public static string? YouTubeVideoId(IReadOnlyDictionary<string, Publication> publications)
    {
        if (publications.TryGetValue("youtube", out var yt)
            && yt.Status == PublishStatus.Published
            && !string.IsNullOrWhiteSpace(yt.Id))
            return yt.Id;
        return null;
    }

    public interface IAnalyticsFetcher
    {
        Task<FetchResult> FetchAsync(IReadOnlyList<string> videoIds, string startDate, string endDate, CancellationToken ct);
    }

    // Live fetcher: Analytics API v2 (no Data API quota consumed — it is a
    // separate API) + Data API videos.list statistics (1 unit per call, up
    // to 50 ids per call). Views come from Analytics when a row exists;
    // otherwise the Data API's public viewCount is used, so a video inside
    // the Analytics 2–3 day processing lag still gets a real number.
    public sealed class YouTubeAnalyticsFetcher(UserCredential credential) : IAnalyticsFetcher
    {
        private readonly YouTubeAnalyticsService _analytics = new(new Google.Apis.Services.BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "GiggleGarden Uploader",
        });
        private readonly YouTubeService _youtube = new(new Google.Apis.Services.BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "GiggleGarden Uploader",
        });

        public async Task<FetchResult> FetchAsync(IReadOnlyList<string> videoIds, string startDate, string endDate, CancellationToken ct)
        {
            var byVideo = new Dictionary<string, VideoStats>(StringComparer.OrdinalIgnoreCase);
            var withAnalyticsRow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var units = 0;

            // Per-video Analytics rows, 10 ids per call (filters=video== takes a list).
            // Defensive: drop any non-YouTube ID from the batch — one bad ID
            // 400s the whole call. RunCoreAsync already filters + warns; this
            // is the last line of defence for direct FetchAsync callers.
            foreach (var batch in videoIds.Chunk(10))
            {
                var valid = batch.Where(IsValidVideoId).ToArray();
                if (valid.Length == 0) continue;
                var req = _analytics.Reports.Query();
                req.Ids = "channel==MINE";
                req.StartDate = startDate;
                req.EndDate = endDate;
                req.Metrics = PerVideoMetrics;
                req.Dimensions = "video";
                req.Filters = "video==" + string.Join(",", valid);
                var resp = await req.ExecuteAsync(ct);
                var idx = ColumnIndex(resp.ColumnHeaders);
                foreach (var row in resp.Rows ?? [])
                {
                    var id = Cell(row, idx, "video");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    withAnalyticsRow.Add(id);
                    byVideo[id] = new VideoStats(
                        Views: (long)Num(row, idx, "views"),
                        EstimatedMinutesWatched: Num(row, idx, "estimatedMinutesWatched"),
                        AverageViewDurationSeconds: Num(row, idx, "averageViewDuration"),
                        AverageViewPercentage: Num(row, idx, "averageViewPercentage"),
                        SubscribersGained: (long)Num(row, idx, "subscribersGained"),
                        SubscribersLost: (long)Num(row, idx, "subscribersLost"),
                        Likes: 0,
                        Comments: 0);
                }
            }

            // Channel totals over the same range (no dimensions).
            var totalsReq = _analytics.Reports.Query();
            totalsReq.Ids = "channel==MINE";
            totalsReq.StartDate = startDate;
            totalsReq.EndDate = endDate;
            totalsReq.Metrics = ChannelMetrics;
            var totals = await totalsReq.ExecuteAsync(ct);
            var tIdx = ColumnIndex(totals.ColumnHeaders);
            var tRow = totals.Rows?.FirstOrDefault();
            var channelViews = tRow is null ? 0 : (long)Num(tRow, tIdx, "views");
            var channelWatch = tRow is null ? 0 : Num(tRow, tIdx, "estimatedMinutesWatched");
            var channelSubsGained = tRow is null ? 0 : (long)Num(tRow, tIdx, "subscribersGained");
            var channelSubsLost = tRow is null ? 0 : (long)Num(tRow, tIdx, "subscribersLost");

            // Daily series for the last 28 days (or the pull range if shorter).
            var daily = new List<AnalyticsLog.DailyPoint>();
            var dailyStart = DateOnly.Parse(endDate).AddDays(-27).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.CompareOrdinal(dailyStart, startDate) < 0) dailyStart = startDate;
            var dailyReq = _analytics.Reports.Query();
            dailyReq.Ids = "channel==MINE";
            dailyReq.StartDate = dailyStart;
            dailyReq.EndDate = endDate;
            dailyReq.Metrics = DailyMetrics;
            dailyReq.Dimensions = "day";
            dailyReq.Sort = "day";
            var dailyResp = await dailyReq.ExecuteAsync(ct);
            var dIdx = ColumnIndex(dailyResp.ColumnHeaders);
            foreach (var row in dailyResp.Rows ?? [])
                daily.Add(new AnalyticsLog.DailyPoint(Cell(row, dIdx, "day"), (long)Num(row, dIdx, "views"), (long)Num(row, dIdx, "subscribersGained")));

            // Data API statistics (1 unit per call, up to 50 ids).
            var fallbackViews = 0L;
            var fallbackVideos = 0;
            foreach (var batch in videoIds.Chunk(50))
            {
                var valid = batch.Where(IsValidVideoId).ToArray();
                if (valid.Length == 0) continue;
                var list = _youtube.Videos.List("statistics");
                list.Id = string.Join(",", valid);
                var videos = await list.ExecuteAsync(ct);
                units++;
                foreach (var v in videos.Items ?? [])
                {
                    if (string.IsNullOrWhiteSpace(v.Id)) continue;
                    var s = v.Statistics;
                    var views = (long)(s?.ViewCount ?? 0);
                    var likes = (long)(s?.LikeCount ?? 0);
                    var comments = (long)(s?.CommentCount ?? 0);
                    if (byVideo.TryGetValue(v.Id, out var cur))
                    {
                        byVideo[v.Id] = cur with { Likes = likes, Comments = comments };
                    }
                    else if (views > 0 || likes > 0 || comments > 0)
                    {
                        // No Analytics row yet (processing lag) but public stats exist.
                        byVideo[v.Id] = new VideoStats(views, 0, 0, 0, 0, 0, likes, comments);
                        // Provenance for the brief label: these views are
                        // Data API public counts, not Analytics rows. Only
                        // videos that actually contribute fallback views
                        // are counted, so the label's video count matches
                        // its view count.
                        if (views > 0) { fallbackViews += views; fallbackVideos++; }
                    }
                }
            }

            var noStats = videoIds.Count(id =>
                !withAnalyticsRow.Contains(id) &&
                (!byVideo.TryGetValue(id, out var s) || (s.Views == 0 && s.Likes == 0 && s.Comments == 0)));
            return new FetchResult(byVideo, channelViews, channelWatch, channelSubsGained, channelSubsLost, daily, units, noStats, fallbackViews, fallbackVideos);
        }

        private static Dictionary<string, int> ColumnIndex(IList<Google.Apis.YouTubeAnalytics.v2.Data.ResultTableColumnHeader>? headers)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (headers is null) return map;
            for (var i = 0; i < headers.Count; i++)
                if (!string.IsNullOrWhiteSpace(headers[i].Name))
                    map[headers[i].Name] = i;
            return map;
        }

        private static string Cell(IList<object> row, Dictionary<string, int> idx, string name)
            => idx.TryGetValue(name, out var i) && i < row.Count ? row[i]?.ToString() ?? "" : "";

        private static double Num(IList<object> row, Dictionary<string, int> idx, string name)
            => double.TryParse(Cell(row, idx, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    internal async Task<bool> RunAsync(AppConfig cfg, CancellationToken ct)
    {
        // A fetch/auth failure must log and return false (exit 1 at the call
        // site), never an unhandled stack trace / abort (exit 134).
        try
        {
            var publisher = new YouTubePublisher(_ytCfg, _log);
            var credential = await publisher.GetCredentialAsync();
            var fetcher = new YouTubeAnalyticsFetcher(credential);
            return await RunCoreAsync(Path.Combine(cfg.WatchDirectory, "analytics"), fetcher, _log, ct);
        }
        catch (Exception ex)
        {
            _log.Error($"--analytics-pull: failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Test seam: orchestration over an injected fetcher. The live path goes
    // through RunAsync; tests call this directly with a temp dir + fake fetcher.
    public static async Task<bool> RunCoreAsync(string analyticsDir, IAnalyticsFetcher fetcher, Logger log, CancellationToken ct)
    {
        // L73: the post-fetch steps ran unguarded — a failure in the load,
        // the stats update, or the brief write crashed unhandled (the same
        // exit-134 shape the fetch guard was meant to kill). Each step logs
        // its name + the exception type + what was/wasn't written, and
        // returns false (exit 1 at the call site); the next run retries.
        List<AnalyticsEntry> entries;
        try
        {
            entries = await AnalyticsLog.LoadAsync(analyticsDir, msg => log.Warn(msg));
        }
        catch (Exception ex)
        {
            log.Error($"--analytics-pull: analytics log load failed: {ex.GetType().Name}: {ex.Message}; nothing was read or written, will retry next run");
            return false;
        }
        // This channel's token can only read this channel's analytics.
        // Rows of other channels (e.g. chronicleandchaos) are skipped and
        // reported, never deleted.
        var mine = entries
            .Where(e => e.Channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(e.VideoId))
            .ToList();
        var skippedOther = entries.Count(e =>
            !string.IsNullOrWhiteSpace(e.Channel)
            && !e.Channel.Equals("trendlinenow", StringComparison.OrdinalIgnoreCase));
        var ids = mine.Select(e => e.VideoId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // A log row whose VideoId is not a YouTube video ID (e.g. the COP31
        // Short row carrying Instagram media ID 18082177424510099) 400s the
        // Analytics video== filter and used to kill the whole pull. Skip
        // such rows with a named warning; they get no stats this run.
        foreach (var bad in ids.Where(id => !IsValidVideoId(id)).ToList())
        {
            var title = mine.First(e => e.VideoId.Equals(bad, StringComparison.OrdinalIgnoreCase)).Title;
            log.Warn($"--analytics-pull: skipping '{bad}' ('{title}') — not a valid YouTube video ID (11 chars, letters/digits/-/_); it gets no stats this run.");
        }
        ids = ids.Where(IsValidVideoId).ToList();
        if (ids.Count == 0)
        {
            log.Warn("--analytics-pull: no trendlinenow videos with a valid YouTube ID in the log; nothing to pull.");
            return false;
        }
        var start = mine.Min(e => e.PublishedAt).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        log.Info($"--analytics-pull: pulling {ids.Count} video(s), {start} to {end}.");

        FetchResult result;
        try
        {
            result = await fetcher.FetchAsync(ids, start, end, ct);
        }
        catch (Exception ex)
        {
            // L73: name the batch (first/last video id + size) — the
            // fetcher sees the whole id list as one batch at this level.
            log.Error($"--analytics-pull: fetch failed (batch of {ids.Count} video(s), {ids[0]}…{ids[^1]}): {ex.GetType().Name}: {ex.Message}; nothing was written, will retry next run");
            return false;
        }
        int updated;
        try
        {
            // L82: wire the helper's log sink into the run log.
            updated = await AnalyticsLog.UpdateStatsAsync(analyticsDir, result.ByVideo, msg => log.Warn(msg));
        }
        catch (Exception ex)
        {
            log.Error($"--analytics-pull: stats update failed: {ex.GetType().Name}: {ex.Message}; log rows may be partially updated, brief not written, will retry next run");
            return false;
        }
        string briefPath;
        try
        {
            var fresh = await AnalyticsLog.LoadAsync(analyticsDir, msg => log.Warn(msg));
            var ctx = new AnalyticsLog.BriefContext(
                DateTimeOffset.UtcNow,
                result.ChannelViews, result.ChannelWatchMinutes, result.ChannelSubsGained, result.ChannelSubsLost,
                result.Daily, skippedOther, result.RowsWithoutStats,
                result.FallbackViews, result.FallbackVideos);
            var brief = AnalyticsLog.RenderBrief(fresh, ctx);
            briefPath = Path.Combine(analyticsDir, $"brief-{DateTimeOffset.UtcNow:yyyy-MM-dd}.md");
            await File.WriteAllTextAsync(briefPath, brief, ct);
        }
        catch (Exception ex)
        {
            log.Error($"--analytics-pull: brief write failed: {ex.GetType().Name}: {ex.Message}; stats were updated but the brief was not written, will retry next run");
            return false;
        }

        log.Info($"--analytics-pull: updated {updated} log row(s); brief written to {briefPath}.");
        log.Info($"--analytics-pull: quota — Data API {result.DataApiUnitsUsed} unit(s); Analytics API calls consume no Data API quota (separate API).");
        log.Info($"--analytics-pull: channel totals — {result.ChannelViews} views, {result.ChannelWatchMinutes:0} watch minutes, {result.ChannelSubsGained} subs gained.");
        return true;
    }
}
