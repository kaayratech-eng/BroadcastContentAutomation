using System.Text.Json;
using System.Text.RegularExpressions;

// Free fact-check (2026-10-25, priority 4): Wikipedia REST API, no key.
// Used two ways: (a) before script generation when --topic is given, the
// topic's summary is appended to the trend context so the model starts from
// real facts; (b) after generation for Chronicle, the figure's summary is
// saved to the job folder and any year mentioned in narration but absent
// from the summary is logged as a warning — a heuristic, not a proof, but
// it catches the most common mythology hallucination (a wrong date) for
// free. Never blocks a run: every failure returns null.
//
// Entity guard (2026-11-22, fix sequence #2 ITEM 2): the old code fetched a
// summary for a headline string and took the first search result blindly,
// so video #3 was written against the WRONG story's background — the
// summary fetched for news[0] (the Trump FBI-hack story) was a timeline of
// Russian interference in the 2016 election, while the model had picked GTA
// 6. FetchBackgroundForTopicAsync now verifies the fetched article actually
// relates to the topic (one of the topic's distinctive key terms in the
// article title or lead, or the term matching the title's initials —
// "GTA" against "Grand Theft Auto VI") and returns null with a clear
// warning when it does not, so the caller falls back to the trend item's
// own facts instead of grounding the script in an unrelated article.
static class WikipediaClient
{
    public static async Task<string?> FetchSummaryAsync(string query)
    {
        var result = await FetchSummaryWithTitleAsync(query);
        return result?.Extract;
    }

    // Same fetch as FetchSummaryAsync, but also returns the article title
    // actually fetched — the entity guard needs it, and the title is what
    // the search fallback chose (first result), not the query string.
    public static async Task<(string Title, string Extract)?> FetchSummaryWithTitleAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var direct = await FetchSummaryForTitleAsync(query.Trim().Replace(' ', '_'));
        if (direct is not null) return direct;
        try
        {
            var titles = await SearchTitlesAsync(query, maxResults: 1);
            if (titles.Count == 0) return null;
            return await FetchSummaryForTitleAsync(titles[0].Replace(' ', '_'));
        }
        catch { return null; }
    }

    // Direct REST summary fetch for a known article title (no search).
    // Null on any failure — the caller tries the next title, never throws.
    internal static async Task<(string Title, string Extract)?> FetchSummaryForTitleAsync(string titleSlug)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("User-Agent", "BroadcastContentAutomation/1.0 (free fact-check)");
            var title = Uri.EscapeDataString(titleSlug);
            var resp = await http.GetAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{title}");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var articleTitle = doc.RootElement.TryGetProperty("title", out var rt) ? rt.GetString() : null;
            var extract = doc.RootElement.TryGetProperty("extract", out var extractEl) ? extractEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(extract) || string.IsNullOrWhiteSpace(articleTitle)) return null;
            return (articleTitle, extract);
        }
        catch { return null; }
    }

    // Wikipedia search result titles for a query, best first. The plural
    // background fetch walks these (rather than taking only the first) so
    // a topic can ground itself in 2-3 related articles instead of one.
    internal static async Task<IReadOnlyList<string>> SearchTitlesAsync(string query, int maxResults = 8)
    {
        var titles = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return titles;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("User-Agent", "BroadcastContentAutomation/1.0 (free fact-check)");
            var search = await http.GetAsync($"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&format=json");
            if (!search.IsSuccessStatusCode) return titles;
            using var sdoc = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
            if (!sdoc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("search", out var arr)) return titles;
            foreach (var item in arr.EnumerateArray())
            {
                if (titles.Count >= maxResults) break;
                if (item.TryGetProperty("title", out var t) && !string.IsNullOrWhiteSpace(t.GetString()))
                    titles.Add(t.GetString()!);
            }
        }
        catch { /* search failure yields no titles, never a throw */ }
        return titles;
    }

    // Guarded fetch, plural (2026-11-23, multi-source context): up to
    // maxArticles related backgrounds for a topic, each entity-guarded
    // against the topic exactly like the single fetch. Walks the search
    // results in order and keeps the first distinct titles that pass the
    // guard — a broad topic ("COP31") grounds itself in the conference
    // article, the framework convention, and the agreement, not just one
    // of them. A topic with no matching article returns [] (the caller
    // falls back to the trend item's own facts).
    public static async Task<IReadOnlyList<(string Title, string Extract)>> FetchBackgroundsForTopicAsync(string query, string? guardTopic = null, int maxArticles = 3)
    {
        var results = new List<(string Title, string Extract)>();
        if (string.IsNullOrWhiteSpace(query) || maxArticles <= 0) return results;
        var topic = guardTopic ?? query;
        foreach (var title in await SearchTitlesAsync(query))
        {
            if (results.Count >= maxArticles) break;
            var summary = await FetchSummaryForTitleAsync(title.Replace(' ', '_'));
            if (summary is null) continue;
            if (results.Any(r => r.Title.Equals(summary.Value.Title, StringComparison.OrdinalIgnoreCase))) continue;
            if (!BackgroundMatchesTopic(summary.Value.Title, summary.Value.Extract, topic))
            {
                Console.WriteLine(
                    $"  [wikipedia] Background for \"{topic}\" rejected: fetched article \"{summary.Value.Title}\" " +
                    "does not match the topic — skipping, never grounding in an unrelated summary.");
                continue;
            }
            results.Add(summary.Value);
        }
        return results;
    }

    // Guarded fetch: the summary for a topic, or null when no summary was
    // found OR the article fetched does not relate to the topic. The guard
    // topic defaults to the query; pass the picked story explicitly when
    // the query is a derived search string (key terms, a headline) so the
    // guard checks the story, not the query wording.
    public static async Task<(string Title, string Extract)?> FetchBackgroundForTopicAsync(string query, string? guardTopic = null)
    {
        var result = await FetchSummaryWithTitleAsync(query);
        if (result is null) return null;
        var topic = guardTopic ?? query;
        if (!BackgroundMatchesTopic(result.Value.Title, result.Value.Extract, topic))
        {
            Console.WriteLine(
                $"  [wikipedia] Background for \"{topic}\" rejected: fetched article \"{result.Value.Title}\" " +
                "does not match the topic — falling back to the trend item's own facts, never an unrelated summary.");
            return null;
        }
        return result;
    }

    // Distinctive key terms of a topic: lowercase word/digit tokens of at
    // least 3 characters (or at least 2 when they contain a digit, e.g.
    // "ps5"), with stopwords and generic news words removed. "GTA 6 Drops:
    // Rockstar's New Vice City Is Here" gives gta, rockstar, vice, city.
    internal static IReadOnlyList<string> ExtractKeyTerms(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return [];
        return Regex.Matches(NormalizeForMatch(topic), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(w => (w.Length >= 3 || (w.Length >= 2 && w.Any(char.IsDigit))) && !Stopwords.Contains(w))
            .Distinct()
            .ToList();
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "in", "on", "for", "and", "or", "to", "is", "are", "was", "were",
        "says", "say", "said", "new", "today", "breaking", "live", "update", "updates", "video",
        "story", "watch", "after", "with", "from", "by", "at", "as", "it", "its", "this", "that",
        "how", "what", "why", "when", "who", "top", "big", "biggest", "global", "world", "news",
        "report", "reports", "revealed", "announced", "drops", "drop", "landed", "here", "means",
        "mean", "really", "just", "now", "over", "into", "more", "than", "his", "her", "their",
        "has", "have", "had", "will", "would", "could", "should", "been", "being", "about",
    };

    // True when at least one of the topic's key terms relates to the fetched
    // article: the term appears as a whole word in the article title or in
    // the first 1,200 characters of the extract, or the term is the initials
    // of consecutive words in the title ("gta" against "Grand Theft Auto
    // VI"). False when the topic has no key terms at all — an uncheckable
    // background is rejected, not trusted.
    // Sequel guard: when the topic names a numbered installment ("GTA 6",
    // "World War II"), the article must be for THAT installment — the
    // key-term check alone cannot tell "Grand Theft Auto VI" from the 2002
    // "Grand Theft Auto: Vice City" (same words, wrong game), so at least
    // one of the topic's numerals must appear in the title or lead, in
    // digit or Roman form ("6" or "vi").
    internal static bool BackgroundMatchesTopic(string? title, string? extract, string topic)
    {
        var terms = ExtractKeyTerms(topic);
        if (terms.Count == 0) return false;
        var haystack = NormalizeForMatch((title ?? "") + "\n" + (extract ?? ""));
        var lead = haystack.Length > 1200 ? haystack[..1200] : haystack;
        var titleNorm = NormalizeForMatch(title ?? "");
        if (!terms.Any(t =>
            Regex.IsMatch(lead, $@"\b{Regex.Escape(t)}\b") || MatchesInitials(t, titleNorm)))
            return false;
        var numerals = ExtractNumerals(topic);
        if (numerals.Count > 0)
            return numerals.Any(n => n.Forms.Any(f =>
                Regex.IsMatch(lead, $@"\b{Regex.Escape(f)}\b")));
        return true;
    }

    // Numbered installments in a topic: standalone digits ("GTA 6") and
    // standalone Roman numerals ("World War II"), values 1-30 only — years
    // and counts ("2026", "52 hours") are not installments. Each numeral
    // carries both its digit and Roman forms, so "6" matches an article
    // titled "Grand Theft Auto VI" and "II" matches "World War 2".
    internal sealed record TopicNumeral(int Value, IReadOnlyList<string> Forms);

    internal static IReadOnlyList<TopicNumeral> ExtractNumerals(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return [];
        var words = Regex.Matches(NormalizeForMatch(topic), @"[a-z0-9]+").Select(m => m.Value).ToList();
        var keyTerms = ExtractKeyTerms(topic).ToHashSet(StringComparer.Ordinal);
        var found = new List<TopicNumeral>();
        for (var i = 0; i < words.Count; i++)
        {
            var token = words[i];
            int value;
            if (char.IsDigit(token[0]))
            {
                if (token.Length > 2 || !int.TryParse(token, out value) || value < 1 || value > 30) continue;
            }
            else if (Regex.IsMatch(token, @"^[ivx]+$"))
            {
                // A one-letter Roman numeral ("GTA V") only counts right
                // after a key term — otherwise the pronoun "I" would read
                // as the numeral 1 and wrongly constrain the guard.
                if (token.Length < 2 && (i == 0 || !keyTerms.Contains(words[i - 1]))) continue;
                value = FromRoman(token);
                if (value < 1 || value > 30) continue;
            }
            else continue;
            if (found.Any(n => n.Value == value)) continue;
            found.Add(new TopicNumeral(value, [value.ToString(), ToRoman(value)]));
        }
        return found;
    }

    private static int FromRoman(string roman)
    {
        var values = new Dictionary<char, int> { ['i'] = 1, ['v'] = 5, ['x'] = 10, ['l'] = 50, ['c'] = 100 };
        var total = 0;
        var prev = 0;
        foreach (var c in roman.Reverse())
        {
            if (!values.TryGetValue(c, out var v)) return 0;
            if (v < prev) total -= v;
            else { total += v; prev = v; }
        }
        return total;
    }

    private static string ToRoman(int value)
    {
        var map = new[] { (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i") };
        var s = "";
        foreach (var (v, r) in map)
            while (value >= v) { s += r; value -= v; }
        return s;
    }

    // "gta" matches the title "grand theft auto vi": the first letters of
    // the words grand/theft/auto spell it out. Only runs of words are
    // considered, so a single word can never "match" by initials.
    private static bool MatchesInitials(string term, string normalizedTitle)
    {
        if (term.Length < 2 || term.Any(char.IsDigit)) return false;
        var words = Regex.Matches(normalizedTitle, @"[a-z]+").Select(m => m.Value).ToArray();
        for (var i = 0; i + term.Length <= words.Length; i++)
        {
            var initials = string.Concat(words.Skip(i).Take(term.Length).Select(w => w[0]));
            if (initials == term) return true;
        }
        return false;
    }

    // Shared normalisation for matching: lowercase, every Unicode space or
    // dash becomes a plain space/hyphen, curly quotes become straight ones,
    // whitespace collapses. Both sides of every comparison go through this,
    // so "GTA\u202f6" (narrow no-break space, as the model writes it) and
    // "GTA 6" compare equal.
    internal static string NormalizeForMatch(string text)
    {
        var t = text
            .Replace('\u00A0', ' ').Replace('\u202F', ' ').Replace('\u2009', ' ')
            .Replace('\u2007', ' ').Replace('\u200B', ' ').Replace('\u2028', ' ')
            .Replace('\u2018', '\'').Replace('\u2019', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"')
            .Replace('\u2010', '-').Replace('\u2011', '-')
            .Replace('\u2013', '-').Replace('\u2014', '-')
            .ToLowerInvariant();
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    // Years in narration (1000-2099) that do not appear in the Wikipedia
    // summary — worth a human check, not an automatic rejection.
    public static List<string> YearsToCheck(string narration, string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return [];
        var years = Regex.Matches(narration, @"\b(1[0-9]{3}|20[0-9]{2})\b").Select(m => m.Value).Distinct().ToList();
        return years.Where(y => !summary.Contains(y)).ToList();
    }
}
