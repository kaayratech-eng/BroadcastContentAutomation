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
    public DateTimeOffset? StatsUpdatedAt { get; set; }
}

public static class AnalyticsLog
{
    public static string PathFor(string directory) => Path.Combine(directory, "analytics.jsonl");

    public static async Task AppendAsync(string directory, AnalyticsEntry entry)
    {
        Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(entry, Sidecar.Options);
        await File.AppendAllTextAsync(PathFor(directory), line + Environment.NewLine);
    }

    public static async Task<List<AnalyticsEntry>> LoadAsync(string directory)
    {
        var path = PathFor(directory);
        if (!File.Exists(path)) return [];
        var entries = new List<AnalyticsEntry>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { entries.Add(JsonSerializer.Deserialize<AnalyticsEntry>(line, Sidecar.Options)!); } catch { /* skip corrupt line */ }
        }
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

    public static string Summarize(IReadOnlyList<AnalyticsEntry> entries, int top = 5)
    {
        if (entries.Count == 0) return "No published-video analytics yet.";
        var withStats = entries.Where(e => e.Views > 0).ToList();
        if (withStats.Count == 0) return $"{entries.Count} videos published, no view stats imported yet. Import a CSV to enable learning.";
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
