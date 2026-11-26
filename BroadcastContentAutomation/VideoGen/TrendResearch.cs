using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// Trend lookup for topic inspiration - uses the YouTube Data API's public
// search.list endpoint with a simple API key (no OAuth needed, unlike the
// Uploader's authorized upload flow), plus Google News RSS (free, no key) as
// the trending-entertainment signal (SPOTLIGHT NOW pivot 2026-11-24:
// Bollywood, Hollywood, singers, gaming, fashion — see NewsFeeds).
//
// 2026-11-21: Google Trends is DEAD as a free source - every Trends endpoint
// (trendingsearches/daily/rss, trendingsearches/realtime/rss, api/dailytrends,
// api/realtimetrends) now returns 404 (verified live from this VM). The old
// FetchGoogleTrendsAsync silently returned [] for the same reason. The
// replacement signal is Google News RSS: entertainment search feeds
// (news.google.com/rss/search) per beat and edition, merged and deduped -
// Google's own ranking is effectively "what's trending in entertainment",
// and it carries what a script needs: headline, source, link, publish time.
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
    // Global topic strategy (2026-11-22): Editions is the set of Google News
    // editions the story appeared in (US/CA/GB/IN/AU/WORLD) — cross-border
    // presence is the free signal of global appeal. ViewsPerDay is the best
    // matching YouTube trending title's views/day (0 when nothing matches).
    // Both default so direct construction sites keep working, and both are
    // set by the ranker, never by the RSS parser alone. The explicit 5-arg
    // overload exists because Activator.CreateInstance does NOT fill
    // optional parameters — the old 5-arg construction used by
    // FactTraceabilityTests would otherwise stop finding a constructor.
    public sealed record NewsItem(string Title, string Source, string Link, string? Description, DateTime PublishedAt, IReadOnlyList<string>? Editions = null, double ViewsPerDay = 0)
    {
        public NewsItem(string Title, string Source, string Link, string? Description, DateTime PublishedAt)
            : this(Title, Source, Link, Description, PublishedAt, null, 0)
        {
        }
    }

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

        // Google News is the trending-entertainment signal for every profile, but only
        // the news profile turns its headlines into script facts - for the other
        // profiles the block is inspiration only, same as the YouTube titles.
        var news = await FetchGoogleNewsAsync();
        // Global topic strategy (2026-11-22): rank the merged multi-edition
        // list by global views potential before anything else reads it, so
        // the category, the report text, and the facts block all follow the
        // top global candidate, not the top US headline.
        news = RankByGlobalViews(news, items);
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
            sb.AppendLine("Top entertainment stories right now, ranked by global views potential (most editions + most YouTube views/day first) - this IS today's trending entertainment, worldwide:");
            foreach (var n in news.Take(8))
                sb.AppendLine($"- \"{n.Title}\" ({n.Source}, {n.PublishedAt:yyyy-MM-dd HH:mm} UTC) - {n.Link}");
            sb.AppendLine($"Topic category of the top story: {category} (drives this video's style - see the category style table).");
        }

        // Best-effort background facts for the top story (news profile only):
        // a Wikipedia summary gives the script real, checkable background to
        // draw the backstory from instead of the model's memory alone. Null
        // when the story has no article - the headlines above still stand.
        // GUARDED (2026-11-22, fix sequence #2 ITEM 2): the old code took
        // the first search result for news[0] with no check, so video #3
        // was grounded in the Trump story's background while the model had
        // picked GTA 6. FetchBackgroundForTopicAsync rejects an article
        // that does not match the top story and logs it; the picked story's
        // own background is fetched after generation instead (see
        // FetchBackgroundForPickedTopicAsync), because the model picks the
        // story — the trend fetch cannot know it in advance.
        if (profile.CategoryStyles is not null && news.Count > 0)
        {
            var background = await WikipediaClient.FetchBackgroundForTopicAsync(news[0].Title);
            if (background is not null)
                sb.AppendLine($"Background facts (Wikipedia) for the top story \"{news[0].Title}\":\n{background.Value.Extract}");
            else
                Console.WriteLine($"  [trends] No verified Wikipedia background for the top story \"{news[0].Title}\" — headlines only.");
        }

        // Multi-source context (2026-11-23): full article text for the top
        // 3 ranked stories, via the publisher-RSS helper. Headlines alone
        // left the model inventing numbers (video #4: 76 invented claims);
        // the article text gives it real, traceable facts to write from,
        // and the gate checks the finished script against this same text
        // (it survives BuildCheckContext — it is not a Wikipedia block).
        // News profile only; best-effort (a missing helper or no matches
        // just means headlines + Wikipedia, never a failed lookup).
        if (profile.CategoryStyles is not null && news.Count > 0)
        {
            var articleSection = await FetchTopStoryArticlesAsync(news.Take(3).ToList(), cfg);
            if (articleSection.Length > 0)
                sb.AppendLine(articleSection);
        }

        return new TrendReport(sb.ToString(), category, news);
    }

    // The background for the story the script ACTUALLY picked (fix sequence
    // #2 ITEM 2). The trend fetch runs before generation, when the picked
    // story is unknowable — the model chooses it — so this runs after
    // GenerateAsync returns, on script.Title, and its result is saved to
    // the job folder and fed to the fact-traceability gate. Three queries
    // are tried in order: the picked title's key terms ("gta rockstar vice
    // city"), the full picked title, and the matched trend item's title.
    // Every attempt is guarded against the PICKED topic, never the query
    // wording, so a wrong-story article is rejected even when the search
    // that found it looked right.
    // Multi-source context (2026-11-23): collects up to 3 related
    // Wikipedia backgrounds instead of stopping at the first — a broad
    // topic grounds itself in several related articles, and the gate
    // checks the script against all of them.
    public static async Task<PickedTopicBackground> FetchBackgroundForPickedTopicAsync(string pickedTopic, IReadOnlyList<NewsItem>? news)
    {
        var matched = news is { Count: > 0 } ? FindMatchingNewsItem(pickedTopic, news) : null;
        var queries = new List<string>();
        var termQuery = BuildPickedTopicQuery(pickedTopic);
        if (!string.IsNullOrWhiteSpace(termQuery)) queries.Add(termQuery);
        if (!string.IsNullOrWhiteSpace(pickedTopic) && !queries.Contains(pickedTopic)) queries.Add(pickedTopic);
        if (matched is not null && !queries.Contains(matched.Title)) queries.Add(matched.Title);
        var backgrounds = new List<(string Title, string Extract)>();
        foreach (var q in queries)
        {
            if (backgrounds.Count >= 3) break;
            foreach (var bg in await WikipediaClient.FetchBackgroundsForTopicAsync(q, pickedTopic, maxArticles: 3))
            {
                if (backgrounds.Count >= 3) break;
                if (backgrounds.Any(b => b.Title.Equals(bg.Title, StringComparison.OrdinalIgnoreCase))) continue;
                backgrounds.Add(bg);
            }
        }
        if (backgrounds.Count == 0)
            Console.WriteLine($"  [trends] No verified Wikipedia background for the picked story \"{pickedTopic}\" — the script will be checked against the trend headlines only.");
        return new PickedTopicBackground(pickedTopic, matched, backgrounds);
    }

    // Full article text for the top ranked stories (multi-source context
    // 2026-11-23): one helper call per story (the helper fetches the
    // publisher feeds and matches by title/key-term overlap), run in
    // parallel, each failing independently. Deduplicated by URL (the same
    // article can match two stories) and capped at 3 articles so the
    // combined context stays near ~12,000 chars. Returns "" when nothing
    // was fetched — the caller appends nothing and the pipeline continues
    // on headlines + Wikipedia.
    internal static async Task<string> FetchTopStoryArticlesAsync(IReadOnlyList<NewsItem> topStories, GenConfig cfg)
    {
        if (topStories is null || topStories.Count == 0) return "";
        var tasks = topStories.Select(async item =>
        {
            try { return await ArticleClient.FetchForTopicAsync(item.Title, cfg); }
            catch (Exception ex)
            {
                Console.WriteLine($"  [articles] fetch failed for \"{item.Title}\": {ex.Message}");
                return (IReadOnlyList<ArticleClient.Article>)[];
            }
        });
        var results = await Task.WhenAll(tasks);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var articles = results
            .SelectMany(r => r)
            .Where(a => !string.IsNullOrWhiteSpace(a.Text) && seen.Add(a.Url))
            .Take(ArticleClient.DefaultMaxArticles)
            .ToList();
        if (articles.Count == 0) return "";
        return ArticleClient.BuildArticleSection(articles);
    }

    // The picked title reduced to its distinctive key terms, in order,
    // for a Wikipedia search: "GTA 6 Drops: Rockstar's New Vice City Is
    // Here – What It Means" becomes "gta 6 rockstar vice city". A full
    // headline string searches badly; the terms search well — and the
    // numeral matters: without the "6", the search ranks the 2002
    // "Grand Theft Auto: Vice City" first (verified live 2026-11-22).
    internal static string BuildPickedTopicQuery(string pickedTopic)
    {
        var keyTerms = WikipediaClient.ExtractKeyTerms(pickedTopic).ToHashSet(StringComparer.Ordinal);
        var numerals = WikipediaClient.ExtractNumerals(pickedTopic).SelectMany(n => n.Forms).ToHashSet(StringComparer.Ordinal);
        var words = Regex.Matches(WikipediaClient.NormalizeForMatch(pickedTopic), @"[a-z0-9]+").Select(m => m.Value);
        return string.Join(" ", words.Where(w => keyTerms.Contains(w) || numerals.Contains(w)).Distinct().Take(6));
    }

    // The trend item the picked story came from: the news headline sharing
    // the most key terms with the picked title (ties go to the higher-ranked
    // headline). Null when no headline shares even one term — the picked
    // story then has no trend item of its own, only the Wikipedia
    // background. The item's own facts (headline, outlet, publish time)
    // are the traceable core the script is checked against.
    internal static NewsItem? FindMatchingNewsItem(string pickedTopic, IReadOnlyList<NewsItem> news)
    {
        var terms = WikipediaClient.ExtractKeyTerms(pickedTopic);
        if (terms.Count == 0) return null;
        NewsItem? best = null;
        var bestScore = 0;
        foreach (var item in news)
        {
            var title = WikipediaClient.NormalizeForMatch(item.Title);
            var score = terms.Count(t => Regex.IsMatch(title, $@"\b{Regex.Escape(t)}\b"));
            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }
        return bestScore > 0 ? best : null;
    }

    // The picked-topic background as a context block, in the same shape as
    // the top-story block above: the matched trend item's facts first, then
    // each guarded Wikipedia extract (up to 3, multi-source context
    // 2026-11-23). Empty when neither exists.
    internal static string BuildPickedTopicContext(PickedTopicBackground picked)
    {
        var sb = new StringBuilder();
        if (picked.MatchedNewsItem is not null)
            sb.AppendLine($"Picked story (trend item): \"{picked.MatchedNewsItem.Title}\" ({picked.MatchedNewsItem.Source}, {picked.MatchedNewsItem.PublishedAt:yyyy-MM-dd HH:mm} UTC) - {picked.MatchedNewsItem.Link}");
        foreach (var bg in picked.WikipediaBackgrounds)
            sb.AppendLine($"Background facts (Wikipedia) for the picked story \"{picked.Topic}\":\n{bg.Extract}");
        return sb.ToString().Trim();
    }

    // Google News RSS, free, no key: entertainment search feeds, fetched
    // in parallel and merged with dedupe by normalised title.
    // SPOTLIGHT NOW pivot (2026-11-24, Rishi): the channel no longer covers
    // global news — it covers fashion, games and entertainment (Bollywood,
    // Hollywood, top singers/actors). The old picker fetched six English
    // Top Stories editions, so US politics dominated. The new picker fetches
    // six Google News SEARCH feeds, one per beat, across five English
    // editions (IN/US/GB/CA/AU): Bollywood, Hollywood, general
    // entertainment, music/singers, gaming, fashion. A search feed is the
    // same free RSS endpoint (news.google.com/rss/search?q=...) — verified
    // live 2026-11-24: each returns on-topic headlines. Each feed is allowed
    // to fail independently - a single dead feed must never take the whole
    // lookup down. HttpClient follows Google's 302 automatically.
    internal static readonly (string Url, string Edition)[] NewsFeeds =
    {
        ("https://news.google.com/rss/search?q=Bollywood&hl=en-IN&gl=IN&ceid=IN:en", "IN"),
        ("https://news.google.com/rss/search?q=Hollywood&hl=en-US&gl=US&ceid=US:en", "US"),
        ("https://news.google.com/rss/search?q=entertainment&hl=en-GB&gl=GB&ceid=GB:en", "GB"),
        ("https://news.google.com/rss/search?q=music&hl=en-CA&gl=CA&ceid=CA:en", "CA"),
        ("https://news.google.com/rss/search?q=gaming&hl=en-US&gl=US&ceid=US:en", "US"),
        ("https://news.google.com/rss/search?q=fashion&hl=en-AU&gl=AU&ceid=AU:en", "AU"),
    };

    // Per-edition cap in the final ranked list: without it the US feed
    // (mostly US politics) could still fill the list even after demotion.
    // A cross-border story counts once, against its primary (first-seen)
    // edition, but carries all its editions for the score.
    internal const int MaxPerEdition = 3;
    internal const int MaxCandidates = 14;

    // A single-country politics story is demoted unless its YouTube views
    // signal is overwhelming (the audience has spoken). 500k views/day is
    // far above what a routine politics headline earns, and far below what a
    // genuine global viral story earns — it is a deliberate, visible bar.
    internal const double PoliticsOverwhelmingViewsPerDay = 500_000;

    internal static string NormaliseTitle(string title) =>
        Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static async Task<List<NewsItem>> FetchGoogleNewsAsync()
    {
        var tasks = NewsFeeds.Select(async feed =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
                var xml = await http.GetStringAsync(feed.Url);
                var doc = XDocument.Parse(xml);
                return doc.Descendants("item").Select(ParseNewsItem).Where(n => n is not null).Cast<NewsItem>().ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [trends] Google News feed failed ({feed.Url}): {ex.Message}");
                return new List<NewsItem>();
            }
        });
        var results = await Task.WhenAll(tasks);

        // Merge in fixed feed order (Bollywood IN, Hollywood US, entertainment GB,
        // music CA, gaming US, fashion AU) so the primary edition is deterministic,
        // not completion-order dependent.
        // Dedupe on the normalised title: the same story in several editions
        // becomes ONE candidate, and its edition set is unioned — that set
        // IS the cross-border signal. Cap ~10 items per feed to bound work.
        var byTitle = new Dictionary<string, NewsItem>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < NewsFeeds.Length; i++)
        {
            var edition = NewsFeeds[i].Edition;
            foreach (var item in results[i].Take(10))
            {
                var key = NormaliseTitle(item.Title);
                if (key.Length == 0) continue;
                if (byTitle.TryGetValue(key, out var existing))
                {
                    var editions = existing.Editions is { Count: > 0 }
                        ? existing.Editions.ToList()
                        : new List<string>();
                    if (!editions.Contains(edition, StringComparer.Ordinal))
                        editions.Add(edition);
                    byTitle[key] = existing with { Editions = editions };
                }
                else
                {
                    byTitle[key] = item with { Editions = new[] { edition } };
                }
            }
        }
        return byTitle.Values.ToList();
    }

    // Best-matching YouTube trending title's views/day for a news headline:
    // the titles share >= 2 key terms (numerals count — "GTA 6" needs both
    // "gta" and "6" to match, so a bare "6" never matches alone). 0 when
    // nothing matches. Matching against the already-fetched trending titles
    // costs no extra quota; a per-candidate YouTube search would cost
    // 100 units each and is deliberately not done.
    internal static double MatchYoutubeViews(string title, IReadOnlyList<TrendItem> youtube)
    {
        if (youtube is null || youtube.Count == 0 || string.IsNullOrWhiteSpace(title)) return 0;
        var (terms, numerals) = MatchParts(title);
        if (terms.Count + numerals.Count == 0) return 0;
        var best = 0.0;
        foreach (var t in youtube)
        {
            if (string.IsNullOrWhiteSpace(t.Title)) continue;
            var (tTerms, tNumerals) = MatchParts(t.Title);
            var shared = terms.Count(term => tTerms.Contains(term, StringComparer.Ordinal))
                       + numerals.Count(n => tNumerals.Contains(n));
            if (shared >= 2 && t.ViewsPerDay > best) best = t.ViewsPerDay;
        }
        return best;
    }

    // Key terms plus installment numeral VALUES, for matching:
    // ExtractKeyTerms drops 1-char tokens, so "6" in "GTA 6" would be lost
    // without the numerals — but a numeral value counts ONCE, not once per
    // form ("6" and "vi" are the same numeral), so a bare "6" shared
    // between two titles is one shared signal, not two.
    internal static (HashSet<string> Terms, HashSet<int> Numerals) MatchParts(string title)
    {
        var terms = WikipediaClient.ExtractKeyTerms(title).ToHashSet(StringComparer.Ordinal);
        var numerals = WikipediaClient.ExtractNumerals(title).Select(n => n.Value).ToHashSet();
        return (terms, numerals);
    }

    // Country editions only — a "WORLD" edition, if one ever appears, is not
    // a country and must not confer cross-border status (the old World topic
    // feed was US-parameterised, so a US story landed in both the US feed and
    // the World feed without being global at all). The pivot feeds carry only
    // country editions, so this is defensive; it stays because a future feed
    // must not silently reintroduce the free +2.
    internal static int CountryEditionCount(NewsItem item) =>
        item.Editions?.Count(e => !e.Equals("WORLD", StringComparison.Ordinal)) ?? 0;

    // Global views score for one candidate. Higher = more global views.
    // Edition bonus (+2 per edition beyond the first) is the strongest free
    // signal of global appeal; the YouTube bonus is log-scaled so one viral
    // outlier cannot swamp it; gaming/tech/entertainment/sports get +1
    // (the categories Rishi named — science maps to tech/general in the
    // existing table, virality is the YouTube signal itself). A
    // single-country politics story is halved unless its YouTube views/day
    // is overwhelming.
    // STAR BOOST (Rishi 2026-11-24 ~23:10 EST — biggest stars first):
    // a candidate naming a ranked star (StarRanking, ordered by Instagram
    // followers, biggest first) gains StarBoost — log-scaled, ~+2.83 for
    // Ronaldo down to ~+1.59 for the smallest ranked star. When two star
    // stories are both in the news the bigger star wins; a star story
    // beats a no-star story. The boost is additive, never a veto: a
    // non-star story with overwhelming editions/views can still win.
    internal static double GlobalViewsScore(NewsItem item, IReadOnlyList<TrendItem> youtube)
    {
        var editions = CountryEditionCount(item);
        var views = item.ViewsPerDay > 0 ? item.ViewsPerDay : MatchYoutubeViews(item.Title, youtube);
        var score = 2.0 * Math.Max(0, editions - 1);
        score += Math.Min(6.0, Math.Log10(views + 1));
        var category = TopicCategoryStyles.Classify(item.Title);
        if (category is "gaming" or "tech" or "entertainment" or "sports")
            score += 1.0;
        if (category == "politics" && editions <= 1 && views < PoliticsOverwhelmingViewsPerDay)
            score *= 0.5;
        var star = StarRanking.TryMatchStar(item.Title + " " + (item.Description ?? ""));
        if (star is not null) score += StarRanking.StarBoost(star.Value.Star);
        return score;
    }

    // Rank + cap: score every candidate, sort by score desc (ties: fresher
    // first), then take at most MaxPerEdition per primary edition so one
    // country's politics cannot fill the list. Returns the top
    // MaxCandidates. Every returned item carries its Editions and its
    // matched ViewsPerDay, so the ranking is auditable from the list alone.
    internal static List<NewsItem> RankByGlobalViews(List<NewsItem> candidates, IReadOnlyList<TrendItem> youtube)
    {
        var scored = candidates.Select(item =>
        {
            var views = MatchYoutubeViews(item.Title, youtube);
            var scoredItem = item with { ViewsPerDay = views };
            return (Item: scoredItem, Score: GlobalViewsScore(scoredItem, youtube));
        }).OrderByDescending(x => x.Score)
          .ThenByDescending(x => x.Item.PublishedAt)
          .ToList();

        var perEdition = new Dictionary<string, int>(StringComparer.Ordinal);
        var ranked = new List<NewsItem>();
        foreach (var (item, _) in scored)
        {
            var primary = item.Editions?.FirstOrDefault() ?? "US";
            if (perEdition.TryGetValue(primary, out var count) && count >= MaxPerEdition) continue;
            perEdition[primary] = count + 1;
            ranked.Add(item);
            if (ranked.Count >= MaxCandidates) break;
        }
        return ranked;
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

// What FetchBackgroundForPickedTopicAsync found for the story the script
// actually picked (fix sequence #2 ITEM 2): the picked title itself, the
// trend item it best matches (null when no headline shares a key term),
// and the entity-guarded Wikipedia articles (up to 3, multi-source
// context 2026-11-23; empty when none verified).
// Either half may be missing — the fact-traceability gate checks the
// script against whatever verified facts exist, never against a summary
// for a different story.
sealed record PickedTopicBackground(
    string Topic,
    TrendResearch.NewsItem? MatchedNewsItem,
    IReadOnlyList<(string Title, string Extract)> WikipediaBackgrounds);
