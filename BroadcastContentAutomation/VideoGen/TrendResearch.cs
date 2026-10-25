using System.Text;
using System.Text.Json;
using System.Xml.Linq;

// Trend lookup for topic inspiration - uses the YouTube Data API's public
// search.list endpoint with a simple API key (no OAuth needed, unlike the
// Uploader's authorized upload flow), plus Google Trends RSS (free, no key).
// Priority 3 (2026-10-25): previously one random query, last 30 days, ordered
// by total viewCount — that surfaces old viral videos, not what's rising
// today. Now: every profile query, last 7 days, then a videos.list call for
// real viewCounts, scored by views-per-day, sorted descending. Quota: one
// search (100 units) per query + one videos.list (1 unit) per batch, well
// inside the 10,000/day free quota for 2-3 videos/day/channel. Returns null
// gracefully when the key is missing or every lookup fails, so a trend
// outage never blocks a run.
static class TrendResearch
{
    public sealed record TrendItem(string Title, string Channel, double ViewsPerDay, DateTime PublishedAt);

    public static async Task<string?> FetchAsync(ContentProfile profile, GenConfig cfg)
    {
        var items = new List<TrendItem>();
        if (!string.IsNullOrWhiteSpace(cfg.YouTubeApiKey) && !cfg.YouTubeApiKey.StartsWith("PUT-") && profile.TrendQueries.Length > 0)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var allIds = new List<string>();
                var snippets = new Dictionary<string, (string Title, string Channel, DateTime PublishedAt)>();
                foreach (var query in profile.TrendQueries)
                {
                    var url = "https://www.googleapis.com/youtube/v3/search" +
                               "?part=snippet&type=video&order=date&safeSearch=strict&maxResults=5" +
                               $"&publishedAfter={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-7).ToString("O"))}" +
                               $"&q={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(cfg.YouTubeApiKey)}";
                    var resp = await http.GetAsync(url);
                    if (!resp.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (!doc.RootElement.TryGetProperty("items", out var arr)) continue;
                    foreach (var item in arr.EnumerateArray())
                    {
                        var id = item.GetProperty("id").GetProperty("videoId").GetString();
                        var snip = item.GetProperty("snippet");
                        var title = snip.GetProperty("title").GetString();
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                        DateTime.TryParse(snip.GetProperty("publishedAt").GetString(), out var published);
                        snippets[id] = (title, snip.TryGetProperty("channelTitle", out var ch) ? ch.GetString() ?? "" : "", published);
                        allIds.Add(id);
                    }
                }
                foreach (var batch in allIds.Distinct().Chunk(50))
                {
                    var url = "https://www.googleapis.com/youtube/v3/videos?part=statistics,snippet" +
                              $"&id={Uri.EscapeDataString(string.Join(",", batch))}&key={Uri.EscapeDataString(cfg.YouTubeApiKey)}";
                    var resp = await http.GetAsync(url);
                    if (!resp.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (!doc.RootElement.TryGetProperty("items", out var arr)) continue;
                    foreach (var item in arr.EnumerateArray())
                    {
                        var id = item.GetProperty("id").GetString()!;
                        if (!snippets.TryGetValue(id, out var snip)) continue;
                        var views = item.GetProperty("statistics").TryGetProperty("viewCount", out var vc) && long.TryParse(vc.GetString(), out var v) ? v : 0;
                        var days = Math.Max(1.0, (DateTime.UtcNow - snip.PublishedAt).TotalDays);
                        items.Add(new TrendItem(snip.Title, snip.Channel, views / days, snip.PublishedAt));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [trends] YouTube lookup failed, continuing without it: {ex.Message}");
            }
        }

        var googleTrends = await FetchGoogleTrendsAsync();

        var sb = new StringBuilder();
        if (items.Count > 0)
        {
            sb.AppendLine("Trending YouTube titles from the last 7 days, ranked by views per day (not total views):");
            foreach (var item in items.OrderByDescending(i => i.ViewsPerDay).Take(10))
                sb.AppendLine($"- \"{item.Title}\" | {item.Channel} | {item.ViewsPerDay:0} views/day");
            var questionCount = items.Count(i => i.Title.Contains('?'));
            var whatIfCount = items.Count(i => i.Title.Contains("What if", StringComparison.OrdinalIgnoreCase));
            if (questionCount > 0 || whatIfCount > 0)
                sb.AppendLine($"Pattern note: {questionCount} question titles, {whatIfCount} 'What if' titles in this set — use the pattern, do not copy a title.");
        }
        if (googleTrends.Count > 0)
        {
            sb.AppendLine("Google Trends daily searches (broad signal only):");
            foreach (var t in googleTrends.Take(5)) sb.AppendLine($"- {t}");
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static async Task<List<string>> FetchGoogleTrendsAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var xml = await http.GetStringAsync("https://trends.google.com/trends/trendingsearches/daily/rss?geo=US");
            var doc = XDocument.Parse(xml);
            return doc.Descendants("item").Select(i => i.Element("title")?.Value).Where(t => !string.IsNullOrWhiteSpace(t)).Cast<string>().Take(10).ToList();
        }
        catch { return []; }
    }
}
