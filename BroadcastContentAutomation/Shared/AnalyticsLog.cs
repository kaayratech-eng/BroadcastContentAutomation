using System.Text.Json;

namespace GiggleGarden.Shared;

// Free analytics loop (2026-10-25, priority 1): no paid analytics SaaS.
// The Uploader appends one line per confirmed publish (videoId, title, tags,
// thumbnail, publish time). Views/CTR/retention are imported later, either
// manually as CSV (videoId,views,ctr,avgViewSeconds) or, in future, from the
// YouTube Analytics API once the OAuth token is re-captured with the
// yt-analytics.readonly scope — the log format is identical either way, so
// the summary code does not need to change when the API fetch lands.
// Summarize() turns the log into a short "what worked" brief that VideoGen
// feeds into the next script prompt, so the channel learns from its own
// numbers instead of generic best-practice advice.
public sealed class AnalyticsEntry
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Aspect { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string? ThumbnailPath { get; set; }
    public string? TrendSource { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public long Views { get; set; }
    public double Ctr { get; set; }          // 0-100, when known
    public double AvgViewSeconds { get; set; }
    // Filled by the Analytics API pull (fix #4, 2026-11-26). All default 0 so
    // old log lines deserialize unchanged.
    public double EstimatedMinutesWatched { get; set; }
    public double AverageViewPercentage { get; set; }
    public long SubscribersGained { get; set; }
    public long SubscribersLost { get; set; }
    public long Likes { get; set; }
    public long Comments { get; set; }
    public DateTimeOffset? StatsUpdatedAt { get; set; }
}

/// One video's stats as pulled from the YouTube APIs (fix #4).
/// Ctr is deliberately NOT here: the Analytics API v2 does not expose
/// thumbnail impressions or impression CTR — reports.query returns
/// 400 "Unknown identifier" for `impressions` / `impressionClickThroughRate`
/// (verified against the API metrics reference + a live test, 2026-09-12;
/// Google's issue tracker marks it intended behaviour). Those two numbers
/// are Studio-only; the Reporting API's reach reports carry them as daily
/// CSV jobs, which is a follow-up, not part of this pull.
public sealed record VideoStats(
    long Views,
    double EstimatedMinutesWatched,
    double AverageViewDurationSeconds,
    double AverageViewPercentage,
    long SubscribersGained,
    long SubscribersLost,
    long Likes,
    long Comments);

public static class AnalyticsLog
{
    public static string PathFor(string directory) => Path.Combine(directory, "analytics.jsonl");

    // L82: optional log sink (RetryHandler pattern) — an append failure
    // is also reported through the callback, not just the thrown wrapper.
    public static async Task AppendAsync(string directory, AnalyticsEntry entry, Action<string>? log = null)
    {
        // L8: an append failure used to throw raw, with no video id and no
        // next action — the per-video catch then logged it as a generic
        // video failure. Wrap it so the caller knows which video's row was
        // lost and that the publish itself already succeeded.
        try
        {
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(entry, Sidecar.Options);
            await File.AppendAllTextAsync(PathFor(directory), line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            log?.Invoke($"analytics append failed for {entry.VideoId}: {ex.Message}; publish already recorded, row will be added next run");
            throw new IOException(
                $"analytics append failed for {entry.VideoId}: {ex.Message}; publish already recorded, row will be added next run", ex);
        }
    }

    // `log`, when given, receives one warning per load that skipped corrupt
    // entries (L8): a corrupt entry used to vanish silently. Callers that
    // have a logger pass one; callers without keep the old silent skip.
    public static async Task<List<AnalyticsEntry>> LoadAsync(string directory, Action<string>? log = null)
    {
        var path = PathFor(directory);
        if (!File.Exists(path)) return [];
        // Entries are written with Sidecar.Options (WriteIndented = true), so
        // one entry spans many lines — the old line-by-line read silently
        // skipped every entry in a real file and Summarize() always reported
        // "no analytics yet". Split on top-level object boundaries instead,
        // which reads single-line and pretty-printed entries alike; a
        // corrupt entry is skipped on its own without losing the rest.
        var text = await File.ReadAllTextAsync(path);
        var entries = new List<AnalyticsEntry>();
        var skipped = new List<(int Offset, string Reason)>();
        var depth = 0;
        var inString = false;
        var escaped = false;
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{':
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        var json = text[start..(i + 1)];
                        var offset = start;
                        start = -1;
                        try
                        {
                            var entry = JsonSerializer.Deserialize<AnalyticsEntry>(json, Sidecar.Options);
                            if (entry is not null) entries.Add(entry);
                        }
                        catch (Exception ex)
                        {
                            // L8: record the skip instead of dropping it
                            // silently — the warning below names the count,
                            // the first offset, and the reason.
                            skipped.Add((offset, ex.Message));
                        }
                    }
                    break;
            }
        }
        if (skipped.Count > 0)
            log?.Invoke($"analytics log: skipped {skipped.Count} corrupt entries, first at offset {skipped[0].Offset}: {skipped[0].Reason}");
        return entries;
    }

    // Import a CSV with header: videoId,views,ctr,avgViewSeconds
    // Updates matching entries by videoId; unknown ids are ignored.
    public static async Task<int> ImportCsvAsync(string directory, string csvPath)
    {
        var entries = await LoadAsync(directory);
        var byId = entries.Where(e => !string.IsNullOrWhiteSpace(e.VideoId))
            .ToDictionary(e => e.VideoId, StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        foreach (var line in await File.ReadAllLinesAsync(csvPath))
        {
            if (line.StartsWith("videoId", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(',');
            if (parts.Length < 4) continue;
            if (!byId.TryGetValue(parts[0].Trim(), out var entry)) continue;
            if (long.TryParse(parts[1].Trim(), out var views)) entry.Views = views;
            if (double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ctr)) entry.Ctr = ctr;
            if (double.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var avg)) entry.AvgViewSeconds = avg;
            entry.StatsUpdatedAt = DateTimeOffset.UtcNow;
            updated++;
        }
        if (updated > 0)
        {
            var lines = entries.Select(e => JsonSerializer.Serialize(e, Sidecar.Options));
            await File.WriteAllLinesAsync(PathFor(directory), lines);
        }
        return updated;
    }

    // Fix #4 (2026-11-26): merge API-pulled stats into the log, matched by
    // VideoId, IN PLACE. Never adds a line and never removes one — a log
    // with duplicate rows for one video (the Uploader appends per publish,
    // and 7 duplicates predate this) gets every matching row updated, so
    // the file stays consistent. Unknown ids are ignored. Returns the
    // number of rows updated.
    // L82: optional log sink (RetryHandler pattern) — threaded into the
    // LoadAsync call and the final rewrite, so a corrupt entry or a failed
    // rewrite is reported instead of surfacing as a bare exception.
    public static async Task<int> UpdateStatsAsync(string directory, IReadOnlyDictionary<string, VideoStats> stats, Action<string>? log = null)
    {
        var entries = await LoadAsync(directory, log);
        var updated = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.VideoId)) continue;
            if (!stats.TryGetValue(entry.VideoId, out var s)) continue;
            entry.Views = s.Views;
            entry.EstimatedMinutesWatched = s.EstimatedMinutesWatched;
            entry.AvgViewSeconds = s.AverageViewDurationSeconds;
            entry.AverageViewPercentage = s.AverageViewPercentage;
            entry.SubscribersGained = s.SubscribersGained;
            entry.SubscribersLost = s.SubscribersLost;
            entry.Likes = s.Likes;
            entry.Comments = s.Comments;
            entry.StatsUpdatedAt = DateTimeOffset.UtcNow;
            updated++;
        }
        if (updated > 0)
        {
            try
            {
                var lines = entries.Select(e => JsonSerializer.Serialize(e, Sidecar.Options));
                await File.WriteAllLinesAsync(PathFor(directory), lines);
            }
            catch (Exception ex)
            {
                log?.Invoke($"analytics log rewrite failed for {PathFor(directory)}: {ex.Message}; {updated} row(s) updated in memory only, stats will be re-pulled next run");
                throw;
            }
        }
        return updated;
    }

    /// Context for the weekly brief (fix #4). Channel totals cover the same
    /// date range as the pull; Daily is the last-28-days views series.
    /// FallbackViews/FallbackVideos (brief-label item, 2026-11-27) are the
    /// views/videos whose numbers came from the Data API public counts
    /// because no Analytics row exists yet (2–3 day processing lag) — the
    /// brief labels the mixed source instead of leaving the brief total
    /// (Analytics + fallback) unexplained next to the Analytics-only
    /// channel total. Both default 0 (no fallback) so older call sites
    /// and fake fetchers that do not track provenance compile unchanged
    /// and render no label.
    public sealed record DailyPoint(string Date, long Views, long SubscribersGained);
    public sealed record BriefContext(
        DateTimeOffset AsOf,
        long ChannelViews,
        double ChannelWatchMinutes,
        long ChannelSubsGained,
        long ChannelSubsLost,
        IReadOnlyList<DailyPoint> Daily,
        int SkippedOtherChannel,
        int RowsWithoutStats,
        long FallbackViews = 0,
        int FallbackVideos = 0);

    // Renders the weekly markdown brief from already-pulled entries.
    // Honest by construction at small scale: it reports raw counts and
    // 28-day trends and never declares a winner or a pattern — with a
    // handful of views any "pattern" would be noise.
    // The log keeps one row per publish, so one video can appear several
    // times (e.g. v7I4P3iBw4A ×3 in the live log). Every aggregation over
    // rows must count each video once: group by VideoId and keep the row
    // with the latest stats stamp. UpdateStatsAsync writes identical stats
    // to all duplicates in place, so the pick only matters if a run was
    // interrupted mid-update. RenderBrief and Summarize both use this —
    // the rule must not drift between them.
    private static List<AnalyticsEntry> DedupeByVideoId(IReadOnlyList<AnalyticsEntry> entries)
        => entries
            .Where(e => !string.IsNullOrWhiteSpace(e.VideoId))
            .GroupBy(e => e.VideoId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.StatsUpdatedAt ?? DateTimeOffset.MinValue).First())
            .ToList();

    public static string RenderBrief(IReadOnlyList<AnalyticsEntry> entries, BriefContext ctx)
    {
        var byId = DedupeByVideoId(entries);
        var withStats = byId.Where(e => e.Views > 0).ToList();
        var totalViews = withStats.Sum(e => e.Views);
        var totalWatch = withStats.Sum(e => e.EstimatedMinutesWatched);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Spotlight Now — weekly analytics brief ({ctx.AsOf:yyyy-MM-dd})");
        sb.AppendLine();
        sb.AppendLine($"Generated {ctx.AsOf:yyyy-MM-dd HH:mm} UTC from the YouTube Analytics + Data APIs.");
        sb.AppendLine();
        sb.AppendLine("## Read this first — sample size");
        sb.AppendLine();
        if (withStats.Count == 0)
        {
            sb.AppendLine("No video has any views in the pulled data yet, so there is nothing to rank and no pattern to learn. ");
            sb.AppendLine("Everything below is raw counts only. Do not read a winner into these numbers.");
        }
        else
        {
            sb.AppendLine($"Only {withStats.Count} video(s) with views, {totalViews} views in total. That is far too little data to declare a winner or a pattern — ");
            sb.AppendLine("treat every number below as raw counts, not a verdict. The brief exists so the numbers are real and the learning loop has something true to chew on.");
            // Mixed-source label (2026-11-27): the total above mixes
            // Analytics rows with Data API public-count fallback views for
            // videos not yet processed into Analytics, while the channel
            // total below is Analytics-only — without this line the two
            // numbers look contradictory (21 vs 14 on 2026-11-27). Shown
            // only when fallback views exist; at steady state there is no
            // label at all, so nothing misleading lingers.
            if (ctx.FallbackViews > 0 && ctx.FallbackVideos > 0)
                sb.AppendLine($"Of those {totalViews} views, {ctx.FallbackViews} are public counts (YouTube Data API) for {ctx.FallbackVideos} video(s) published in the last 2–3 days and not yet processed into Analytics; the channel total of {ctx.ChannelViews} views below is Analytics-only and lags by the same window.");
        }
        sb.AppendLine();
        sb.AppendLine("## Channel totals (pulled range)");
        sb.AppendLine();
        sb.AppendLine($"- Views: {ctx.ChannelViews}");
        sb.AppendLine($"- Watch time: {ctx.ChannelWatchMinutes:0} minutes");
        sb.AppendLine($"- Subscribers gained: {ctx.ChannelSubsGained} (lost: {ctx.ChannelSubsLost})");
        if (ctx.RowsWithoutStats > 0)
            sb.AppendLine($"- {ctx.RowsWithoutStats} log row(s) came back with no Analytics row — most likely the 2–3 day Analytics processing lag on a freshly published video, or a video with genuinely zero views.");
        if (ctx.SkippedOtherChannel > 0)
            sb.AppendLine($"- {ctx.SkippedOtherChannel} log row(s) belong to another channel and cannot be pulled with this channel's token — left at zero.");
        sb.AppendLine();
        sb.AppendLine("## Top 5 by views");
        sb.AppendLine();
        if (withStats.Count == 0)
        {
            sb.AppendLine("(none yet)");
        }
        else
        {
            var i = 1;
            foreach (var e in withStats.OrderByDescending(e => e.Views).Take(5))
            {
                sb.AppendLine($"{i}. **{e.Views} views** — \"{e.Title}\" ({e.VideoId}, {e.Aspect}) — avg view {e.AvgViewSeconds:0}s ({e.AverageViewPercentage:0}% watched), {e.Likes} likes, {e.Comments} comments, subs gained {e.SubscribersGained}");
                i++;
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Shorts → long-form funnel proxy");
        sb.AppendLine();
        sb.AppendLine("The API does not expose Related Video clicks, so the proxy is Short views vs long-form views, plus subscribers per 1,000 views on each side.");
        sb.AppendLine();
        var shorts = withStats.Where(e => e.Aspect.Equals("vertical", StringComparison.OrdinalIgnoreCase)).ToList();
        var longs = withStats.Where(e => !e.Aspect.Equals("vertical", StringComparison.OrdinalIgnoreCase)).ToList();
        long ShortViews() => shorts.Sum(e => e.Views);
        long LongViews() => longs.Sum(e => e.Views);
        double SubsPer1k(IEnumerable<AnalyticsEntry> xs)
        {
            var v = xs.Sum(e => e.Views);
            return v == 0 ? 0 : xs.Sum(e => e.SubscribersGained) * 1000.0 / v;
        }
        sb.AppendLine("| Side | Videos | Views | Subs gained | Subs / 1,000 views |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        sb.AppendLine($"| Shorts | {shorts.Count} | {ShortViews()} | {shorts.Sum(e => e.SubscribersGained)} | {SubsPer1k(shorts):0.00} |");
        sb.AppendLine($"| Long-form | {longs.Count} | {LongViews()} | {longs.Sum(e => e.SubscribersGained)} | {SubsPer1k(longs):0.00} |");
        sb.AppendLine();
        sb.AppendLine("## Last 28 days (views per day)");
        sb.AppendLine();
        if (ctx.Daily.Count == 0)
        {
            sb.AppendLine("(no daily series returned)");
        }
        else
        {
            foreach (var d in ctx.Daily)
                sb.AppendLine($"- {d.Date}: {d.Views} views, {d.SubscribersGained} subs gained");
        }
        sb.AppendLine();
        sb.AppendLine("## CTR — why it is blank");
        sb.AppendLine();
        sb.AppendLine("Thumbnail impressions and impression CTR are NOT exposed by the YouTube Analytics API: `reports.query` returns 400 `Unknown identifier` for `impressions` / `impressionClickThroughRate` (verified against the API metrics reference and a live test; Google marks it intended behaviour). They are Studio-only. The YouTube Reporting API's reach reports (`channel_reach_basic_a1`) do carry `video_thumbnail_impressions` and `video_thumbnail_impressions_ctr` as daily CSV jobs, 48–72h behind — a possible follow-up; it is not built into this pull.");
        sb.AppendLine();
        sb.AppendLine("## What this changes");
        sb.AppendLine();
        sb.AppendLine("The log now holds real numbers, so `Summarize()` — the brief VideoGen feeds into the next script prompt — finally learns from this channel's own data instead of generic advice. At this scale that mostly means: keep publishing, keep the packaging fixes landing, and let the numbers accumulate before judging any pattern.");
        return sb.ToString();
    }

    public static string Summarize(IReadOnlyList<AnalyticsEntry> entries, int top = 5)
    {
        // Count each video once (see DedupeByVideoId): without this a video
        // logged 3 times ranked 3 times, had its CTR averaged 3 times, and
        // its question-title counted 3 times — the learning loop VideoGen
        // feeds into the next script prompt would overweight it.
        var byId = DedupeByVideoId(entries);
        if (byId.Count == 0) return "No published-video analytics yet.";
        var withStats = byId.Where(e => e.Views > 0).ToList();
        if (withStats.Count == 0) return $"{byId.Count} videos published, no view stats imported yet. Import a CSV to enable learning.";
        var ranked = withStats.OrderByDescending(e => e.Views).Take(top).ToList();
        var avgCtr = withStats.Where(e => e.Ctr > 0).Select(e => e.Ctr).DefaultIfEmpty(0).Average();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Based on {withStats.Count} videos with stats (avg CTR {avgCtr:0.0}% when known):");
        foreach (var e in ranked)
            sb.AppendLine($"- {e.Views} views, CTR {e.Ctr:0.0}%, avg view {e.AvgViewSeconds:0}s: \"{e.Title}\"");
        var questionTitles = withStats.Count(e => e.Title.Contains('?'));
        if (questionTitles > 0)
            sb.AppendLine($"Note: {questionTitles} of these titles are questions — compare their views to statement titles before reusing a pattern.");
        return sb.ToString();
    }
}
