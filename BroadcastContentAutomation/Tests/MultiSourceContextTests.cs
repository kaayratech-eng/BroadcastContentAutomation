// Regression tests for the multi-source context (2026-11-23).
//
// Video #4's script failed the fact-traceability gate with 76 invented
// claims because the model had only headlines + one Wikipedia article to
// write from — and for COP31 even the Wikipedia fetch was rejected, so it
// had headlines alone. The pipeline now also feeds the model the full
// text of the top stories' articles (publisher RSS + trafilatura, via the
// article_fetch.py helper) and up to 3 guarded Wikipedia backgrounds for
// the picked story, and the gate checks the finished script against the
// same material.
//
// Seams under test:
//   (a) ArticleClient.ParseArticles — the helper's JSON output parses
//       into articles, tolerantly (a malformed document yields []).
//   (b) ArticleClient.BuildArticleSection — the trend-context section is
//       bounded (per-article and total caps) and deduplicated by URL.
//   (c) The gate wiring — BuildCheckContext keeps the article section
//       (it is not a Wikipedia block) while stripping the top-story
//       Wikipedia block, so a number that appears in the article text
//       traces and a number that appears nowhere fails.
//   (d) The prompt wiring — the facts block carries the article text
//       (ScriptGenerator.ArticleTextBlock) and FetchReportAsync fetches
//       it for the top 3 stories.
using System.Reflection;
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class MultiSourceContextTests
{
    private static readonly DateTime PrepDate = new(2026, 11, 22);

    // Real COP31 facts as they appear in the Guardian's takeaways piece
    // (verified live 2026-11-23): the article text the pipeline now feeds
    // the model. The script that cites these numbers must trace; a script
    // citing a number from nowhere must not.
    internal const string Cop31ArticleText =
        "Finance, fossil fuels and the ‘overshoot’: six key takeaways from Cop31. " +
        "Close to 200 countries have been meeting in Antalya, Turkey, for the annual UN climate summit. " +
        "For the first time, this Cop recognised and began to discuss the “overshoot” of the 1.5C target. " +
        "75 countries backed Türkiye’s goal to raise electricity’s share of global energy to 35% by 2035. " +
        "A proposed $120 billion annual adaptation target was dropped, replaced by a call to triple funding by 2035. " +
        "Full implementation of current national climate plans would still leave the world on a trajectory toward 2.6 degrees Celsius of warming, " +
        "well above the Paris Agreement’s 1.5-degree Celsius limit.";

    private const string ArticleJson =
        "{\"articles\": [" +
        "{\"outlet\": \"The Guardian\", \"url\": \"https://www.theguardian.com/environment/2026/nov/22/cop31-takeaways\", \"title\": \"Finance, fossil fuels and the ‘overshoot’: six key takeaways from Cop31\", \"text\": \"TEXT1\", \"chars\": 5}," +
        "{\"outlet\": \"BBC\", \"url\": \"https://www.bbc.com/news/articles/cop31\", \"title\": \"COP31 talks fail\", \"text\": \"TEXT2\", \"chars\": 5}" +
        "], \"feeds_checked\": [\"guardian-environment\", \"bbc-world\"], \"errors\": []}";

    private static string TrendContextWithArticles(string? topStoryWikipedia = null) =>
        "Top global news stories right now, ranked by global views potential:\n" +
        "- \"Finance, fossil fuels and the ‘overshoot’: six key takeaways from Cop31\" (The Guardian, 2026-11-22 21:30 UTC) - https://news.google.com/rss/articles/CBMiX\n" +
        (topStoryWikipedia is null ? "" : $"Background facts (Wikipedia) for the top story \"COP31\":\n{topStoryWikipedia}\n") +
        "Full article text for the top stories (use ONLY these facts, plus the headlines above, for the story you pick — numbers, quotes and attributions must come from here or the headlines, never from memory):\n" +
        $"[The Guardian] \"Finance, fossil fuels and the ‘overshoot’: six key takeaways from Cop31\" — https://www.theguardian.com/environment/2026/nov/22/cop31-takeaways\n" +
        Cop31ArticleText;

    private static List<string> CheckJson(string scriptJson, string? context) =>
        ReflectionHelper.Invoke<List<string>>(
            ReflectionHelper.VideoGen, "CheckJson", scriptJson, context, PrepDate);

    private static string ScriptJson(params string[] narrations)
    {
        var sceneJson = narrations.Select(n =>
            "{\"Narration\": " + JsonSerializer.Serialize(n) +
            ", \"SceneKind\": \"art\", \"IsSpeculative\": false, \"VisualSpec\": null}");
        return "{\"Title\": \"Cop31 Climate Talks Fail\", \"Scenes\": [" + string.Join(",", sceneJson) + "], \"BriefScenes\": [], \"Brief\": \"\"}";
    }

    // --- (a) helper JSON parsing --------------------------------------------

    [Fact]
    public void ParseArticles_ParsesHelperJson()
    {
        var json = ArticleJson.Replace("TEXT1", "Guardian text here").Replace("TEXT2", "BBC text here");
        var articles = ReflectionHelper.Invoke<System.Collections.IList>(
            ReflectionHelper.VideoGen, "ParseArticles", json, null);
        Assert.Equal(2, articles.Count);
        var first = articles[0]!;
        var outlet = first.GetType().GetProperty("Outlet")!.GetValue(first) as string;
        var url = first.GetType().GetProperty("Url")!.GetValue(first) as string;
        var text = first.GetType().GetProperty("Text")!.GetValue(first) as string;
        Assert.Equal("The Guardian", outlet);
        Assert.Equal("https://www.theguardian.com/environment/2026/nov/22/cop31-takeaways", url);
        Assert.Equal("Guardian text here", text);
    }

    [Fact]
    public void ParseArticles_MalformedJson_ReturnsEmptyInsteadOfThrowing()
    {
        var articles = ReflectionHelper.Invoke<System.Collections.IList>(
            ReflectionHelper.VideoGen, "ParseArticles", "{not json", null);
        Assert.Empty(articles);
        var noKey = ReflectionHelper.Invoke<System.Collections.IList>(
            ReflectionHelper.VideoGen, "ParseArticles", "{\"feeds_checked\": []}", null);
        Assert.Empty(noKey);
    }

    [Fact]
    public void ArticleClient_MalformedJsonLogsAndReturnsPartial()
    {
        // L64: a truncated document (one valid article, then garbage) must
        // return the valid article and log the parse error + the count —
        // an exit-0 helper emitting garbage JSON used to be
        // indistinguishable from "no articles".
        const string json = "{\"articles\": [" +
            "{\"outlet\": \"X\", \"url\": \"https://example.com/a\", \"title\": \"A\", \"text\": \"kept text\"}, broken";
        var sb = new StringWriter();
        var oldOut = Console.Out;
        Console.SetOut(sb);
        System.Collections.IList articles;
        try
        {
            articles = ReflectionHelper.Invoke<System.Collections.IList>(
                ReflectionHelper.VideoGen, "ParseArticles", json, new PrepIdentity("job-20261126-test"));
        }
        finally
        {
            Console.SetOut(oldOut);
        }
        Assert.Single(articles);
        var first = articles[0]!;
        Assert.Equal("X", first.GetType().GetProperty("Outlet")!.GetValue(first) as string);
        var log = sb.ToString();
        Assert.Contains("malformed JSON", log);
        Assert.Contains("job-20261126-test", log);
        Assert.Contains("1 parsed article", log);
    }

    [Fact]
    public void ParseArticles_SkipsArticlesWithEmptyTextOrUrl()
    {
        const string json = "{\"articles\": [" +
            "{\"outlet\": \"X\", \"url\": \"https://example.com/a\", \"title\": \"A\", \"text\": \"\"}," +
            "{\"outlet\": \"Y\", \"url\": \"\", \"title\": \"B\", \"text\": \"real text\"}," +
            "{\"outlet\": \"Z\", \"url\": \"https://example.com/c\", \"title\": \"C\", \"text\": \"kept\"}" +
            "]}";
        var articles = ReflectionHelper.Invoke<System.Collections.IList>(
            ReflectionHelper.VideoGen, "ParseArticles", json, null);
        Assert.Single(articles);
    }

    // --- (b) section building + caps ----------------------------------------

    private static object MakeArticle(string outlet, string url, string text)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "Article");
        return Activator.CreateInstance(type, outlet, url, "Title " + outlet, text)!;
    }

    // A real List<Article> (typed): MethodInfo.Invoke does not convert a
    // List<object> to IReadOnlyList<Article>, so the list must be built
    // against the actual nested record type.
    private static object MakeArticleList(params object[] articles)
    {
        var articleType = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "Article");
        var listType = typeof(List<>).MakeGenericType(articleType);
        var list = Activator.CreateInstance(listType)!;
        var add = listType.GetMethod("Add")!;
        foreach (var a in articles) add.Invoke(list, new[] { a });
        return list;
    }

    [Fact]
    public void BuildArticleSection_Empty_ReturnsEmpty()
    {
        var section = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildArticleSection",
            MakeArticleList(), 3500, 10500);
        Assert.Equal("", section);
    }

    [Fact]
    public void BuildArticleSection_CapsPerArticle_AndDedupsByUrl()
    {
        var longText = new string('x', 5000);
        var articles = MakeArticleList(
            MakeArticle("The Guardian", "https://example.com/a", longText),
            MakeArticle("The Guardian", "https://example.com/a", longText),
            MakeArticle("BBC", "https://example.com/b", "short"));
        var section = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildArticleSection", articles, 3500, 10500);
        Assert.Contains("Full article text", section);
        Assert.Contains("The Guardian", section);
        Assert.Contains("BBC", section);
        // The 5,000-char article is cut to 3,500 + "…"; the duplicate URL
        // appears once (one header line for it).
        Assert.Equal(1, section.Split("https://example.com/a").Length - 1);
        Assert.True(section.Length < 3500 + 500, $"section should be bounded, was {section.Length} chars");
    }

    [Fact]
    public void BuildArticleSection_TotalCap_StopsAtThreeArticles()
    {
        var made = new List<object>();
        for (var i = 0; i < 5; i++)
            made.Add(MakeArticle($"Outlet{i}", $"https://example.com/{i}", new string('y', 3500)));
        var section = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildArticleSection", MakeArticleList(made.ToArray()), 3500, 10500);
        Assert.Contains("Outlet0", section);
        Assert.Contains("Outlet2", section);
        Assert.DoesNotContain("Outlet3", section);
    }

    // --- (c) the gate wiring -------------------------------------------------

    [Fact]
    public void BuildCheckContext_KeepsArticleText_StripsTopStoryWikipedia()
    {
        // The top-story Wikipedia block belongs to news[0] and is stripped
        // (it could let a script "trace" a number from a different story);
        // the article section is the picked story's own reporting and must
        // survive, so the gate checks against it.
        const string topWiki = "The 2026 in climate change article mentions the year 1999 and the number 77.";
        var trendContext = TrendContextWithArticles(topWiki);
        var checkContext = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", trendContext, null);
        Assert.Contains("Full article text", checkContext);
        Assert.Contains("35%", checkContext);
        Assert.DoesNotContain("1999", checkContext);
        Assert.DoesNotContain("77", checkContext);
    }

    [Fact]
    public void Check_NumberFromArticleText_Passes_NumberFromNowhere_Fails()
    {
        var checkContext = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", TrendContextWithArticles(), null);
        // 35% and $120 billion are in the article text — traceable.
        var passing = CheckJson(ScriptJson(
            "75 countries backed a goal to raise electricity's share to 35% by 2035.",
            "The proposed $120 billion adaptation target was dropped."), checkContext);
        Assert.DoesNotContain(passing, f => f.Contains("35%"));
        Assert.DoesNotContain(passing, f => f.Contains("120"));
        // 99% appears nowhere in any source — untraceable.
        var failing = CheckJson(ScriptJson(
            "A shocking 99% of delegates walked out of the talks."), checkContext);
        Assert.Contains(failing, f => f.Contains("99%") && f.Contains("UntraceableNumber"));
    }

    [Fact]
    public void Check_NumberOnlyInStrippedTopStoryWikipedia_Fails()
    {
        // The number 77 lives only in the top-story Wikipedia block, which
        // BuildCheckContext strips. A script citing it must fail — the
        // strip is what stops a wrong-story background from laundering
        // invented numbers.
        const string topWiki = "The 2026 in climate change article mentions the year 1999 and the number 77.";
        var checkContext = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", TrendContextWithArticles(topWiki), null);
        var findings = CheckJson(ScriptJson("Delegates cited 77 separate reports."), checkContext);
        Assert.Contains(findings, f => f.Contains("77") && f.Contains("UntraceableNumber"));
    }

    // --- (d) the prompt wiring ----------------------------------------------

    [Fact]
    public void ArticleTextBlock_ExtractsAndCapsTheSection()
    {
        var block = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "ArticleTextBlock", TrendContextWithArticles());
        Assert.Contains("Full article text", block);
        Assert.Contains("35%", block);
        Assert.True(block.Length <= 6000 + 100, $"facts-block cut should be ~6,000 chars, was {block.Length}");
        var none = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "ArticleTextBlock", "Top global news stories: nothing here.");
        Assert.Equal("", none);
    }

    [Fact]
    public void Wiring_TrendFetchFetchesTopStoryArticles_AndFactsBlockCarriesThem()
    {
        // Source-scan guards for the two wiring points: the trend fetch
        // must pull article text for the top stories (so the prompt and
        // the gate both see it), and the facts block must carry it into
        // the script prompt.
        const string trendPath =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/TrendResearch.cs";
        var trendText = File.ReadAllText(trendPath);
        Assert.Contains("FetchTopStoryArticlesAsync", trendText);
        Assert.Contains("news.Take(3)", trendText);
        const string scriptPath =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/ScriptGenerator.cs";
        var scriptText = File.ReadAllText(scriptPath);
        Assert.Contains("ArticleTextBlock(trendContext)", scriptText);
    }

    [Fact]
    public void Check_AttributionWithApostrophe_TracesWhenNameIsInContext()
    {
        // Gate fix found by the multi-source COP31 dry run: the Guardian
        // article text has "Darragh O'Brien" (curly apostrophe), the model
        // quoted him, and the gate flagged it — because CleanAttribution
        // strips apostrophes ("obrien") while the normalised context keeps
        // them ("o'brien"). A name with an apostrophe must trace.
        var context = "The proposed deal failed to meet the moment, said Ireland\u2019s environment minister, Darragh O\u2019Brien, speaking for the EU.";
        var findings = CheckJson(ScriptJson(
            "Ireland\u2019s environment minister Darragh O\u2019Brien said the deal failed to meet the moment and ignored the science."), context);
        Assert.DoesNotContain(findings, f => f.Contains("O\u2019Brien") && f.Contains("UntraceableAttribution"));
        // And a name that genuinely is not in the context still fails.
        var missing = CheckJson(ScriptJson(
            "Ireland\u2019s environment minister Darragh O\u2019Brien said the deal failed."), "The deal failed.");
        Assert.Contains(missing, f => f.Contains("UntraceableAttribution"));
    }

    [Fact]
    public void Wiring_PickedTopicFetchesUpToThreeWikipediaBackgrounds()
    {
        const string trendPath =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/TrendResearch.cs";
        var text = File.ReadAllText(trendPath);
        Assert.Contains("FetchBackgroundsForTopicAsync", text);
        Assert.Contains("maxArticles: 3", text);
    }
}
