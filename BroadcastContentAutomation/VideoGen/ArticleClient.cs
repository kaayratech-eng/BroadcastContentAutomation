// Full-text news articles for the picked story (multi-source context,
// 2026-11-23).
//
// Video #4's script failed the fact-traceability gate with 76 invented
// claims because the model had only headlines + one Wikipedia article to
// write from — and for COP31 even the Wikipedia fetch was rejected, so it
// had headlines alone. The real numbers live in the articles themselves
// (the Guardian's COP31 piece carries the $120 billion adaptation target,
// the 35% electrification goal, the 2.6C warming figure), so the pipeline
// now feeds the model the article text and checks the finished script
// against the same material.
//
// Why a helper: Google News RSS links (news.google.com/rss/articles/…)
// cannot be resolved by plain HTTP clients in this sandbox — curl lands on
// a Google interstitial page, trafilatura.fetch_url returns 0 bytes, and
// Jina Reader blocks news.google.com anonymously. The publishers' own RSS
// feeds DO work and give real article URLs, and curl + trafilatura.extract
// on the downloaded HTML extracts the text. The helper
// (custom.articles/bin/article_fetch.py) does that work: it fetches the
// publisher feeds, matches articles to the topic by title/key-term
// overlap, fetches the top 2-3, and returns JSON. Free, no key.
//
// Best-effort, like every other helper-backed client: a missing helper,
// a dead feed, or an empty result returns [] and the pipeline continues
// on headlines + Wikipedia alone. A missing article must never block a
// run — the failure mode is a thinner context, not a failed prep.
using System.Text;
using System.Text.Json;

static class ArticleClient
{
    public sealed record Article(string Outlet, string Url, string Title, string Text);

    // Default caps: 3 articles x 3,500 chars = 10,500 chars of article
    // text in the trend context (the gate's material). The script prompt
    // carries a shorter cut of the same section (ScriptGenerator
    // ArticleTextBlock, 6,000 chars) so Groq's 8,000 TPM budget holds —
    // the gate is a superset of the prompt, never the reverse.
    public const int DefaultMaxArticles = 3;
    public const int DefaultMaxCharsPerArticle = 3500;
    public const int DefaultMaxTotalChars = 10500;

    public static async Task<IReadOnlyList<Article>> FetchForTopicAsync(
        string topic, GenConfig cfg, int maxArticles = DefaultMaxArticles, int maxCharsPerArticle = DefaultMaxCharsPerArticle,
        PrepIdentity? context = null)
    {
        if (string.IsNullOrWhiteSpace(topic)) return [];
        if (!File.Exists(cfg.ArticleHelperPath) || !File.Exists(cfg.ArticleHelperPythonPath))
        {
            Console.WriteLine($"  {PrepContext.PrefixFor(context)}[articles] helper not found (article_fetch.py or its venv python) — continuing without article text.");
            return [];
        }
        try
        {
            var result = await ProcessHelper.RunAsync(cfg.ArticleHelperPythonPath,
            [
                cfg.ArticleHelperPath,
                "--topic", topic,
                "--max-articles", maxArticles.ToString(),
                "--max-chars", maxCharsPerArticle.ToString(),
            ], timeoutMinutes: 3, what: $"article helper ({cfg.ArticleHelperPath})", context: context);
            if (result.ExitCode != 0)
            {
                Console.WriteLine($"  {PrepContext.PrefixFor(context)}[articles] helper failed for \"{topic}\" (exit {result.ExitCode}): {TextTail(result.Stderr + result.Stdout, 300)}");
                return [];
            }
            var articles = ParseArticles(result.Stdout);
            if (articles.Count > 0)
                Console.WriteLine($"  {PrepContext.PrefixFor(context)}[articles] {articles.Count} article(s) for \"{Truncate(topic, 60)}\": {string.Join(", ", articles.Select(a => $"{a.Outlet} ({a.Text.Length} chars)"))}");
            else
                Console.WriteLine($"  {PrepContext.PrefixFor(context)}[articles] no matching articles for \"{Truncate(topic, 60)}\" — headlines + Wikipedia only.");
            return articles;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {PrepContext.PrefixFor(context)}[articles] fetch failed for \"{topic}\": {ex.Message} — continuing without article text.");
            return [];
        }
    }

    // Parse the helper's JSON output. Tolerant by design: a malformed or
    // partial document yields the articles that did parse, never a throw —
    // the caller treats [] as "no articles", not as an error.
    internal static IReadOnlyList<Article> ParseArticles(string json, PrepIdentity? context = null)
    {
        var articles = new List<Article>();
        if (string.IsNullOrWhiteSpace(json)) return articles;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("articles", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return articles;
            var dropped = 0;
            foreach (var el in arr.EnumerateArray())
            {
                var outlet = GetString(el, "outlet") ?? "";
                var url = GetString(el, "url") ?? "";
                var title = GetString(el, "title") ?? "";
                var text = GetString(el, "text") ?? "";
                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(url)) { dropped++; continue; }
                articles.Add(new Article(outlet, url, title, text));
            }
            // L64: dropped articles were silent — name the count.
            if (dropped > 0)
                Console.WriteLine($"  {PrepContext.PrefixFor(context)}[articles] helper output skipped {dropped} article(s) with empty text/url — continuing with the {articles.Count} parsed article(s).");
        }
        catch (JsonException ex)
        {
            // L64: a malformed document used to yield whatever parsed
            // (usually nothing) with no log — salvage each complete
            // article object, and log the parse error + how many survived.
            articles.AddRange(SalvageArticles(json));
            Console.WriteLine($"  {PrepContext.PrefixFor(context)}[articles] helper output was malformed JSON ({ex.Message}); returning {articles.Count} parsed article(s) — continuing without the rest");
        }
        return articles;
    }

    // L64 salvage: the helper can emit a truncated document (a valid
    // article followed by `, broken`). JsonDocument.Parse fails the whole
    // document, so scan for complete top-level `{...}` objects and keep
    // the ones that parse as articles — a truncated tail must not discard
    // the good articles ahead of it.
    private static List<Article> SalvageArticles(string json)
    {
        var found = new List<Article>();
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] != '{') continue;
            var depth = 0;
            var inString = false;
            var escaped = false;
            var j = i;
            for (; j < json.Length; j++)
            {
                var c = json[j];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) break;
                }
            }
            if (depth != 0) continue; // unterminated object — not salvageable
            var span = json[i..(j + 1)];
            try
            {
                using var doc = JsonDocument.Parse(span);
                var el = doc.RootElement;
                var outlet = GetString(el, "outlet") ?? "";
                var url = GetString(el, "url") ?? "";
                var title = GetString(el, "title") ?? "";
                var text = GetString(el, "text") ?? "";
                if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(url))
                    found.Add(new Article(outlet, url, title, text));
            }
            catch (JsonException) { /* not an article object — keep scanning */ }
            i = j;
        }
        return found;
    }

    // The trend-context section carrying the article text, in the same
    // labelled shape as the Wikipedia blocks so the prompt and the gate
    // can find it by marker. Bounded: each article is capped at
    // maxCharsPerArticle and the section as a whole at maxTotalChars, so
    // the combined context stays near ~12,000 chars and Groq's token
    // budget holds. Duplicates (same URL matched for two stories) are
    // dropped; empty texts are dropped. An empty result is "" (the caller
    // appends nothing).
    internal static string BuildArticleSection(IReadOnlyList<Article> articles, int maxCharsPerArticle = DefaultMaxCharsPerArticle, int maxTotalChars = DefaultMaxTotalChars)
    {
        if (articles is null || articles.Count == 0) return "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine("Full article text for the top stories (use ONLY these facts, plus the headlines above, for the story you pick — numbers, quotes and attributions must come from here or the headlines, never from memory):");
        var total = 0;
        foreach (var a in articles)
        {
            if (string.IsNullOrWhiteSpace(a.Text) || !seen.Add(a.Url)) continue;
            var text = a.Text.Length > maxCharsPerArticle ? a.Text[..maxCharsPerArticle] + "…" : a.Text;
            if (total + text.Length > maxTotalChars && total > 0) break;
            sb.AppendLine($"[{a.Outlet}] \"{a.Title}\" — {a.Url}");
            sb.AppendLine(text);
            total += text.Length;
        }
        return total == 0 ? "" : sb.ToString().Trim();
    }

    private static string? GetString(JsonElement el, string name)
    {
        foreach (var p in el.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }

    private static string TextTail(string text, int maxChars) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= maxChars ? text : text[^maxChars..];

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";
}
