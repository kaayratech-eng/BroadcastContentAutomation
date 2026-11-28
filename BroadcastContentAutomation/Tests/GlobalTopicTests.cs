// Global topic strategy (2026-11-22, Rishi): GTA 6 (gaming, global) got the
// maximum views of the first 3 videos; the two US-politics videos got fewer.
// The picker now fetches six English Google News editions (US, Canada, UK,
// India, Australia, World topic) and ranks candidates by a global-views
// score (edition count + YouTube views/day + category), demoting
// single-country politics unless its views signal is overwhelming. Every
// test here pins that behaviour with canned data — no test touches the
// network, a real API, or a render.
using System.Collections;
using System.Reflection;
using Xunit;

namespace BroadcastContentAutomation.Tests;

public class GlobalTopicTests
{
    private static readonly DateTime BaseTime = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------
    // Construction helpers (NewsItem / TrendItem are internal nested
    // records in TrendResearch — built by reflection, as in
    // FactTraceabilityTests).
    // ------------------------------------------------------------------

    private static object MakeNewsItem(string title, string source, string[] editions,
        double viewsPerDay = 0, DateTime? published = null)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "NewsItem");
        return Activator.CreateInstance(type, title, source, "https://example.com/x",
            null, published ?? BaseTime, editions, viewsPerDay)!;
    }

    private static object MakeNewsItemFiveArgs(string title, string source)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "NewsItem");
        return Activator.CreateInstance(type, title, source, "https://example.com/x",
            null, BaseTime)!;
    }

    private static object MakeTrendItem(string title, double viewsPerDay)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "TrendItem");
        return Activator.CreateInstance(type, title, "SomeChannel", viewsPerDay, BaseTime)!;
    }

    private static IList MakeNewsList(params object[] items)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "NewsItem");
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        foreach (var item in items) list.Add(item);
        return list;
    }

    private static IList MakeTrendList(params object[] items)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "TrendItem");
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        foreach (var item in items) list.Add(item);
        return list;
    }

    private static string TitleOf(object item) =>
        (string)item.GetType().GetProperty("Title")!.GetValue(item)!;

    private static string SourceOf(object item) =>
        (string)item.GetType().GetProperty("Source")!.GetValue(item)!;

    private static string LinkOf(object item) =>
        (string)item.GetType().GetProperty("Link")!.GetValue(item)!;

    private static DateTime PublishedOf(object item) =>
        (DateTime)item.GetType().GetProperty("PublishedAt")!.GetValue(item)!;

    private static IReadOnlyList<string>? EditionsOf(object item) =>
        (IReadOnlyList<string>?)item.GetType().GetProperty("Editions")!.GetValue(item);

    private static double ViewsOf(object item) =>
        (double)item.GetType().GetProperty("ViewsPerDay")!.GetValue(item)!;

    private static double Score(object newsItem, IList youtube) =>
        ReflectionHelper.Invoke<double>(ReflectionHelper.VideoGen, "GlobalViewsScore", newsItem, youtube);

    private static IList Rank(IList news, IList youtube) =>
        (IList)ReflectionHelper.Invoke<object>(ReflectionHelper.VideoGen, "RankByGlobalViews", news, youtube)!;

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BroadcastContentAutomation", "VideoGen", "TrendResearch.cs")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate repo root (BroadcastContentAutomation/VideoGen/TrendResearch.cs).");
        }
    }

    // ------------------------------------------------------------------
    // Fetch set: six entertainment search feeds across five English
    // editions (SPOTLIGHT NOW pivot 2026-11-24 - was six Top Stories
    // editions for global news; now one search feed per beat)
    // ------------------------------------------------------------------

    [Fact]
    public void NewsFeeds_HasSixEnglishEditions()
    {
        var research = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "TrendResearch");
        var field = research.GetField("NewsFeeds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("TrendResearch.NewsFeeds");
        var feeds = (IList)field.GetValue(null)!;
        Assert.Equal(6, feeds.Count);

        var editions = new List<string>();
        var glParams = new List<string>();
        foreach (var feed in feeds)
        {
            var url = (string)feed.GetType().GetField("Item1")!.GetValue(feed)!;
            var edition = (string)feed.GetType().GetField("Item2")!.GetValue(feed)!;
            editions.Add(edition);
            Assert.Contains("news.google.com/rss", url);
            Assert.Contains("hl=en", url);
            var gl = url.Split("gl=", StringSplitOptions.None)[1].Split('&')[0];
            glParams.Add(gl);
        }
        Assert.Equal(new[] { "IN", "US", "GB", "CA", "US", "AU" }, editions);
        // The six search feeds cover five distinct countries (US twice:
        // Hollywood + gaming); every feed is a Google News search for one
        // entertainment beat, not a Top Stories feed.
        Assert.Equal(new[] { "AU", "CA", "GB", "IN", "US" }, glParams.Distinct().OrderBy(g => g).ToArray());
        foreach (var feed in feeds)
        {
            var url = (string)feed.GetType().GetField("Item1")!.GetValue(feed)!;
            Assert.Contains("/rss/search?q=", url);
        }
    }

    // ------------------------------------------------------------------
    // YouTube match: shared key terms, numerals count, one term is not enough
    // ------------------------------------------------------------------

    [Fact]
    public void MatchYoutubeViews_MatchesGta6_OnSharedTermsAndNumeral()
    {
        var youtube = MakeTrendList(MakeTrendItem("GTA 6 gameplay walkthrough part 1", 800_000));
        var views = ReflectionHelper.Invoke<double>(
            ReflectionHelper.VideoGen, "MatchYoutubeViews",
            "GTA 6 gameplay breaks launch records", youtube);
        Assert.Equal(800_000, views);
    }

    [Fact]
    public void MatchYoutubeViews_NoMatch_ReturnsZero()
    {
        var youtube = MakeTrendList(MakeTrendItem("GTA 6 gameplay walkthrough part 1", 800_000));
        var views = ReflectionHelper.Invoke<double>(
            ReflectionHelper.VideoGen, "MatchYoutubeViews",
            "Democrats win House majority in historic vote", youtube);
        Assert.Equal(0, views);
    }

    [Fact]
    public void MatchYoutubeViews_SingleSharedNumeral_NoMatch()
    {
        // "6" alone must not match: a bare numeral is not evidence the
        // stories are the same. Needs >= 2 shared terms.
        var youtube = MakeTrendList(MakeTrendItem("Top 6 moments of the year", 500_000));
        var views = ReflectionHelper.Invoke<double>(
            ReflectionHelper.VideoGen, "MatchYoutubeViews",
            "GTA 6 breaks launch records", youtube);
        Assert.Equal(0, views);
    }

    // ------------------------------------------------------------------
    // Score: edition bonus, category boost, politics demotion
    // ------------------------------------------------------------------

    [Fact]
    public void GlobalViewsScore_MultiEditionBeatsSingleEdition_ByTwoPerEdition()
    {
        var youtube = MakeTrendList();
        var single = MakeNewsItem("New public library opens downtown", "Local News", new[] { "US" });
        var multi = MakeNewsItem("New public library opens downtown", "Local News", new[] { "US", "GB", "IN" });
        Assert.Equal(0.0, Score(single, youtube));
        Assert.Equal(4.0, Score(multi, youtube));
    }

    [Fact]
    public void GlobalViewsScore_GamingBoost_IsPlusOne()
    {
        var youtube = MakeTrendList();
        var general = MakeNewsItem("New public library opens downtown", "Local News", new[] { "US" });
        var gaming = MakeNewsItem("GTA 6 gameplay breaks launch records", "IGN", new[] { "US" });
        Assert.Equal(0.0, Score(general, youtube));
        Assert.Equal(1.0, Score(gaming, youtube));
    }

    [Fact]
    public void GlobalViewsScore_SingleCountryPolitics_IsHalved()
    {
        // log10(50,001) ≈ 4.699; politics, single edition, 50k/day is not
        // overwhelming, so the score is halved to ≈ 2.349.
        var youtube = MakeTrendList(MakeTrendItem("Democrats win House majority", 50_000));
        var politics = MakeNewsItem("Democrats win House majority in historic vote", "AP", new[] { "US" });
        var score = Score(politics, youtube);
        Assert.InRange(score, 2.34, 2.36);
    }

    [Fact]
    public void GlobalViewsScore_WorldFeedAlone_GivesNoEditionBonus()
    {
        // The WORLD topic feed is US-parameterised, so a US story lands in
        // both the US feed and the World feed without being global. Only
        // COUNTRY editions confer cross-border status: [US, WORLD] scores
        // the same as [US] alone, while [US, GB] earns the +2 bonus.
        var youtube = MakeTrendList();
        var usOnly = MakeNewsItem("New public library opens downtown", "Local News", new[] { "US" });
        var usWorld = MakeNewsItem("New public library opens downtown", "Local News", new[] { "US", "WORLD" });
        var usGb = MakeNewsItem("New public library opens downtown", "Local News", new[] { "US", "GB" });
        Assert.Equal(Score(usOnly, youtube), Score(usWorld, youtube));
        Assert.Equal(Score(usOnly, youtube) + 2.0, Score(usGb, youtube));
    }

    [Fact]
    public void GlobalViewsScore_PoliticsInUsAndWorld_IsDemoted()
    {
        // A politics story in [US, WORLD] is effectively single-country —
        // the World feed does not rescue it from the demotion.
        var youtube = MakeTrendList(MakeTrendItem("Democrats win House majority", 50_000));
        var politics = MakeNewsItem("Democrats win House majority in historic vote", "AP", new[] { "US", "WORLD" });
        var score = Score(politics, youtube);
        Assert.InRange(score, 2.34, 2.36);
    }

    [Fact]
    public void DroppedStoryTitle_ClassifiesAsPolitics_AndIsDemoted()
    {
        // The exact story Rishi dropped (video #4): "Democrats win largest
        // House majority" — "Democrats" (plural) must classify as politics,
        // or the demotion never fires on the story type it exists for.
        // (Before the keyword fix it classified as "general".)
        var category = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "Classify",
            "Democrats win largest House majority since 1992 after flipping North Carolina seat");
        Assert.Equal("politics", category);
        var youtube = MakeTrendList(MakeTrendItem("Democrats win House majority", 123));
        var story = MakeNewsItem(
            "Democrats win largest House majority since 1992 after flipping North Carolina seat",
            "BBC", new[] { "US", "WORLD" });
        // Single country edition (WORLD does not count) + politics + 123/day
        // is not overwhelming: log10(124) ≈ 2.093, halved to ≈ 1.047.
        Assert.InRange(Score(story, youtube), 1.04, 1.06);
    }

    [Fact]
    public void GlobalViewsScore_OverwhelmingPolitics_IsNotDemoted()
    {
        // 600k/day >= the 500k overwhelming bar: the score is the full
        // log10(600,001) ≈ 5.778, not halved.
        var youtube = MakeTrendList(MakeTrendItem("Democrats win House majority", 600_000));
        var politics = MakeNewsItem("Democrats win House majority in historic vote", "AP", new[] { "US" });
        var score = Score(politics, youtube);
        Assert.InRange(score, 5.77, 5.79);
    }

    // ------------------------------------------------------------------
    // Rank: global gaming first, single-country politics last; cap enforced
    // ------------------------------------------------------------------

    [Fact]
    public void RankByGlobalViews_GlobalGamingFirst_SingleCountryPoliticsLast()
    {
        var youtube = MakeTrendList(
            MakeTrendItem("GTA 6 gameplay walkthrough", 800_000),
            MakeTrendItem("SpaceX launches new rocket", 200_000),
            MakeTrendItem("Cricket World Cup final highlights", 100_000),
            MakeTrendItem("Democrats win House majority", 50_000));
        var news = MakeNewsList(
            MakeNewsItem("Democrats win House majority in historic vote", "AP", new[] { "US" }, published: BaseTime.AddHours(4)),
            MakeNewsItem("GTA 6 gameplay breaks launch records", "IGN", new[] { "US", "GB", "IN" }, published: BaseTime.AddHours(3)),
            MakeNewsItem("SpaceX launches new rocket to orbit", "Reuters", new[] { "IN", "US" }, published: BaseTime.AddHours(2)),
            MakeNewsItem("Cricket World Cup final breaks viewing records", "BBC", new[] { "GB" }, published: BaseTime.AddHours(1)));
        var ranked = Rank(news, youtube);
        Assert.Equal(4, ranked.Count);
        Assert.Equal("GTA 6 gameplay breaks launch records", TitleOf(ranked[0]!));
        Assert.Equal("SpaceX launches new rocket to orbit", TitleOf(ranked[1]!));
        Assert.Equal("Cricket World Cup final breaks viewing records", TitleOf(ranked[2]!));
        Assert.Equal("Democrats win House majority in historic vote", TitleOf(ranked[3]!));
    }

    [Fact]
    public void RankByGlobalViews_PerEditionCap_AtMostThreePerCountry()
    {
        // Five single-edition US items and two CA items, all score 0 — the
        // cap, not the score, is what limits the US to three.
        var youtube = MakeTrendList();
        var news = MakeNewsList(
            MakeNewsItem("New public library opens downtown", "A", new[] { "US" }, published: BaseTime.AddHours(5)),
            MakeNewsItem("City council approves road repairs", "B", new[] { "US" }, published: BaseTime.AddHours(4)),
            MakeNewsItem("Local market extends weekend hours", "C", new[] { "US" }, published: BaseTime.AddHours(3)),
            MakeNewsItem("School board elects new chair", "D", new[] { "US" }, published: BaseTime.AddHours(2)),
            MakeNewsItem("Town hall hosts budget hearing", "E", new[] { "US" }, published: BaseTime.AddHours(1)),
            MakeNewsItem("Toronto transit adds night service", "F", new[] { "CA" }, published: BaseTime.AddHours(6)),
            MakeNewsItem("Vancouver seawall reopens to cyclists", "G", new[] { "CA" }, published: BaseTime.AddHours(7)));
        var ranked = Rank(news, youtube);
        Assert.Equal(5, ranked.Count);
        var usCount = ranked.Cast<object>().Count(i => EditionsOf(i)!.First() == "US");
        Assert.Equal(3, usCount);
    }

    [Fact]
    public void RankByGlobalViews_TraceabilityFieldsPreserved_OnEveryCandidate()
    {
        // The fact-traceability gate checks the script against the picked
        // story's headline + source + link + publish time — ranking must
        // never strip those fields, and every ranked item must carry its
        // editions and matched views/day so the ranking is auditable.
        var youtube = MakeTrendList(MakeTrendItem("GTA 6 gameplay walkthrough", 800_000));
        var news = MakeNewsList(
            MakeNewsItem("GTA 6 gameplay breaks launch records", "IGN", new[] { "US", "GB", "IN" }),
            MakeNewsItem("Democrats win House majority in historic vote", "AP", new[] { "US" }));
        var ranked = Rank(news, youtube);
        foreach (var item in ranked.Cast<object>())
        {
            Assert.False(string.IsNullOrWhiteSpace(TitleOf(item)));
            Assert.False(string.IsNullOrWhiteSpace(SourceOf(item)));
            Assert.False(string.IsNullOrWhiteSpace(LinkOf(item)));
            Assert.NotEqual(default, PublishedOf(item));
            Assert.NotNull(EditionsOf(item));
            Assert.NotEmpty(EditionsOf(item)!);
        }
        Assert.Equal(800_000, ViewsOf(ranked[0]!));
        Assert.Equal(0, ViewsOf(ranked[1]!));
    }

    // ------------------------------------------------------------------
    // NewsItem shape: the two new fields are optional (old 5-arg construction
    // still works, as FactTraceabilityTests relies on it)
    // ------------------------------------------------------------------

    [Fact]
    public void NewsItem_OptionalFieldsDefault_WhenBuiltWithFiveArgs()
    {
        var item = MakeNewsItemFiveArgs("GTA 6 gameplay breaks launch records", "IGN");
        Assert.Null(EditionsOf(item));
        Assert.Equal(0, ViewsOf(item));
        Assert.Equal("GTA 6 gameplay breaks launch records", TitleOf(item));
    }

    [Fact]
    public void NormaliseTitle_LowercasesAndCollapsesPunctuation()
    {
        var key = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "NormaliseTitle",
            "GTA 6 Drops: Rockstar's New Vice City!");
        Assert.Equal("gta 6 drops rockstar s new vice city", key);
    }

    // ------------------------------------------------------------------
    // TrendQueries: entertainment beats (SPOTLIGHT NOW pivot 2026-11-24 -
    // was global-appeal news categories; now Bollywood, Hollywood,
    // celebrity, music, movies, gaming, fashion)
    // ------------------------------------------------------------------

    [Fact]
    public void TrendQueries_CoversGlobalAppealCategories()
    {
        var profile = ReflectionHelper.TrendlineNowProfile();
        var queries = (string[])profile.GetType().GetProperty("TrendQueries")!.GetValue(profile)!;
        foreach (var q in new[] { "bollywood news", "hollywood news", "celebrity news", "entertainment news", "gaming news", "singer new album", "new movie trailer", "box office", "fashion week", "music news", "celebrity interview" })
            Assert.Contains(q, queries);
        Assert.Equal(11, queries.Length);
    }

    // ------------------------------------------------------------------
    // Source guards: pick prompt prefers global; per-feed failure tolerance kept
    // ------------------------------------------------------------------

    [Fact]
    public void PickPrompt_PrefersGlobalOverSingleCountryPolitics()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "VideoGen", "ScriptGenerator.cs"));
        Assert.Contains("Prefer a cross-border topic", source);
        Assert.Contains("single-country politics story is the last resort", source);
        Assert.Contains("already ranked by global views potential", source);
        Assert.DoesNotContain("the single biggest trending story in the world today", source);
    }

    [Fact]
    public void FactsBlock_CarriesRankingEvidencePerHeadline()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "VideoGen", "ScriptGenerator.cs"));
        Assert.Contains("[editions:", source);
        Assert.Contains("views/day]", source);
    }

    [Fact]
    public void FetchGoogleNews_PerFeedFailureTolerance_Kept()
    {
        // Each of the six feeds is fetched inside its own try/catch and a
        // dead feed logs and contributes nothing — a dead feed must never
        // kill the lookup. The old two-feed code had this shape; the
        // six-feed rewrite must keep it.
        var source = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "VideoGen", "TrendResearch.cs"));
        Assert.Contains("Google News feed failed", source);
        Assert.Contains("Task.WhenAll", source);
        Assert.Contains("NewsFeeds.Select", source);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task TrendResearch_AllSourcesFailLogsAggregate()
    {
        // L65: a YouTube 403 per query + all RSS feeds 500 must log each
        // skipped call with its status and one aggregate line — and return
        // an empty news list, not an exception.
        var handler = new StubHandler(req =>
            req.RequestUri!.Host.Contains("googleapis.com", StringComparison.Ordinal)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                    { Content = new StringContent("{\"error\": \"quota exceeded\"}") }
                : new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                    { Content = new StringContent("feed down") });
        var previous = TrendResearch.HttpClientFactory;
        TrendResearch.HttpClientFactory = () => new HttpClient(handler);
        var sb = new StringWriter();
        var oldOut = Console.Out;
        Console.SetOut(sb);
        TrendResearch.TrendReport report;
        try
        {
            var profile = ContentProfileRegistry.Get("trendlinenow");
            report = await TrendResearch.FetchReportAsync(profile,
                new GenConfig { YouTubeApiKey = "test-key" },
                new PrepIdentity("job-20261126-test", "Test topic"));
        }
        finally
        {
            Console.SetOut(oldOut);
            TrendResearch.HttpClientFactory = previous;
        }
        Assert.Empty(report.News);
        Assert.Equal("", report.Text);
        var log = sb.ToString();
        Assert.Contains("403", log);
        Assert.Contains("YouTube search failed", log);
        Assert.Contains("all trend sources failed", log);
        Assert.Contains("job-20261126-test", log);
    }
}
