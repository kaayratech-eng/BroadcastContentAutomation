using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// Trend lookup for topic inspiration - uses the YouTube Data API's public
// search.list endpoint with a simple API key (no OAuth needed, unlike the
// Uploader's authorized upload flow), plus Google News RSS (free, no key) as
// the global trending-news signal.
//
// 2026-11-21: Google Trends is DEAD as a free source - every Trends endpoint
// (trendingsearches/daily/rss, trendingsearches/realtime/rss, api/dailytrends,
// api/realtimetrends) now returns 404 (verified live from this VM). The old
// FetchGoogleTrendsAsync silently returned [] for the same reason. The
// replacement signal is Google News RSS: the Top Stories feed
// (news.google.com/rss) and the World topic feed, merged and deduped -
// Google's own ranking is effectively "what's trending in world news", and
// it carries what a script needs: headline, source, link, publish time.
// HttpClient follows the 302 redirect Google News issues; a browser
// User-Agent is set because bare/default agents get redirected or blocked.
//
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

    // One Google News headline: the fact unit the news script is built from.
    // Title has the trailing " - SourceName" suffix stripped (the <source>
    // element carries the outlet separately). Description is usually just a
    // link list, so it is stripped of HTML and kept only when it has real text.
    public sealed record NewsItem(string Title, string Source, string Link, string? Description, DateTime PublishedAt);

    // The structured result of a trend lookup. Text is the same human-readable
    // block FetchAsync has always returned (kept so existing callers and the
    // per-job trend-snapshot.txt need no changes). Category is the topic
    // category of the top story (TopicCategoryStyles.Classify) - the news
    // prompt and TTS pacing take their style from it. News is the ranked
    // headline list the script's facts come from.
    public sealed record TrendReport(string Text, string Category, IReadOnlyList<NewsItem> News);

    public static async Task<string?> FetchAsync(ContentProfile profile, GenConfig cfg)
    {
        var report = await FetchReportAsync(profile, cfg);
        return report.Text.Length > 0 ? report.Text : null;
    }

    public static async Task<TrendReport> FetchReportAsync(ContentProfile profile, GenConfig cfg)
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

        // Google News is the global trending signal for every profile, but only
        // the news profile turns its headlines into script facts - for the other
        // profiles the block is inspiration only, same as the YouTube titles.
        var news = await FetchGoogleNewsAsync();
        var category = news.Count > 0
            ? TopicCategoryStyles.Classify(news[0].Title + " " + (news.Count > 1 ? news[1].Title : ""))
            : "general";

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
        if (news.Count > 0)
        {
            sb.AppendLine("Top global news stories right now (Google News, ranked by Google - this IS today's trending news):");
            foreach (var n in news.Take(8))
                sb.AppendLine($"- \"{n.Title}\" ({n.Source}, {n.PublishedAt:yyyy-MM-dd HH:mm} UTC) - {n.Link}");
            sb.AppendLine($"Topic category of the top story: {category} (drives this video's style - see the category style table).");
        }

        // Best-effort background facts for the top story (news profile only):
        // a Wikipedia summary gives the script real, checkable background to
        // draw the backstory from instead of the model's memory alone. Null
        // when the story has no article - the headlines above still stand.
        if (profile.CategoryStyles is not null && news.Count > 0)
        {
            var background = await WikipediaClient.FetchSummaryAsync(news[0].Title);
            if (!string.IsNullOrWhiteSpace(background))
                sb.AppendLine($"Background facts (Wikipedia) for the top story \"{news[0].Title}\":\n{background}");
        }

        return new TrendReport(sb.ToString(), category, news);
    }

    // Google News RSS, free, no key: the Top Stories feed and the World topic
    // feed, fetched in parallel and merged with dedupe by normalised title.
    // Top Stories leads (it is Google's own ranking); World fills in global
    // stories the US-weighted Top Stories under-ranks. Each feed is allowed to
    // fail independently - a single dead feed must never take the whole lookup
    // down. HttpClient follows Google's 302 automatically.
    private static async Task<List<NewsItem>> FetchGoogleNewsAsync()
    {
        var feeds = new[]
        {
            "https://news.google.com/rss?hl=en&gl=US&ceid=US:en",
            "https://news.google.com/rss/topics/CAAqJggKIiBDQkFTRWdvSUwyMHZNRFZxYUdjU0FtVnVHZ0pKVGlnQVAB?hl=en&gl=US&ceid=US:en",
        };

        var all = new List<NewsItem>();
        var tasks = feeds.Select(async url =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
                var xml = await http.GetStringAsync(url);
                var doc = XDocument.Parse(xml);
                return doc.Descendants("item").Select(ParseNewsItem).Where(n => n is not null).Cast<NewsItem>().ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [trends] Google News feed failed ({url}): {ex.Message}");
                return new List<NewsItem>();
            }
        });
        var results = await Task.WhenAll(tasks);

        // Top Stories first, then World, deduped on the normalised title so the
        // same story reported by two feeds appears once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in results)
            foreach (var item in list)
            {
                var key = Regex.Replace(item.Title.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
                if (key.Length == 0 || !seen.Add(key)) continue;
                all.Add(item);
                if (all.Count >= 12) break;
            }
        return all.Take(12).ToList();
    }

    private static NewsItem? ParseNewsItem(XElement item)
    {
        var rawTitle = item.Element("title")?.Value;
        if (string.IsNullOrWhiteSpace(rawTitle)) return null;

        // Google News appends " - SourceName" to the title; the <source>
        // element carries the outlet, so strip the suffix to avoid doubling it.
        var source = item.Element("source")?.Value?.Trim() ?? "";
        var title = rawTitle.Trim();
        if (source.Length > 0 && title.EndsWith(" - " + source, StringComparison.Ordinal))
            title = title[..^(source.Length + 3)].Trim();

        var description = StripHtml(item.Element("description")?.Value ?? "");
        if (description is not null && description.Length < 20) description = null;

        DateTime.TryParse(item.Element("pubDate")?.Value, out var published);
        return new NewsItem(
            title,
            source,
            item.Element("link")?.Value?.Trim() ?? "",
            description,
            published);
    }

    private static string? StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 0 ? text : null;
    }
}
