// Regression tests for ITEM 2 (fix sequence #2): ground scripts in
// retrieved facts (review #2 §E — video #3 invented "HydroFlow",
// "LifePulse", $2.5B revenue, and charts sourced to dates after its own
// Nov 22 prep, because the Wikipedia background was fetched only for
// news[0] — the Trump story, not the GTA 6 story the model picked —
// with no entity guard and no pre-render traceability check).
//
// Two seams under test:
//   (a) background routing: the picked story's background is fetched and
//       guarded (WikipediaClient + TrendResearch), and
//   (b) the pre-render fact-traceability check (FactTraceability.CheckJson
//       over the script JSON): every number, quote, entity and chart
//       source must trace to the supplied trend context, no source may be
//       dated after the prep date, and "internal"/"press release" sources
//       that were never supplied are rejected. The check runs in
//       FinishPrepAsync (see the source-scan guards below) before
//       script.json is saved, so a failing prep can never reach
//       TTS/render unchecked — since the fact-gate rewrite (2026-11-23,
//       FactGateRewriteTests), a finding is REWRITTEN into attributed
//       speculation (or dropped, for named persons) instead of FATAL-ing
//       the prep; only the no-context guard still throws.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BroadcastContentAutomation.Tests;

public class FactTraceabilityTests
{
    private static readonly DateTime PrepDate = new(2026, 11, 22);

    private const string Video3ScriptPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261122-115430/script.json";

    private const string Video3SnapshotPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261122-114431/trend-snapshot.txt";

    // The Grand Theft Auto VI Wikipedia lead as it stood at prep time
    // (pre-release version, verified via Wikipedia 2026-11-22): the real
    // background the fixed pipeline fetches for the picked GTA 6 story.
    // It carries the true facts — Rockstar Games, Leonida/Florida, Vice
    // City/Miami, Jason Duval, Lucia Caminos, 2013, 2022, 2023, 2026 —
    // and none of the invented ones (no HydroFlow, LifePulse, 400
    // engineers, $2.5B, or GameSpot/The Verge/Bloomberg sources).
    internal const string Gta6WikipediaLead =
        "Grand Theft Auto VI is an upcoming action-adventure game developed and published by Rockstar Games. " +
        "It is due to be the eighth main Grand Theft Auto game, following Grand Theft Auto V (2013), and the " +
        "sixteenth entry overall. Set within the fictional US state of Leonida, based on Florida, the story " +
        "follows the romantic criminal duo of Jason Duval and Lucia Caminos. Players will be able to freely " +
        "roam Leonida's open world, which predominantly features the Miami-inspired Vice City. Following years " +
        "of speculation and anticipation, Rockstar confirmed in February 2022 that the game was in development. " +
        "That September, footage from unfinished versions was leaked on the internet in what journalists " +
        "described as one of the biggest leaks in the history of the video game industry. Grand Theft Auto VI " +
        "was formally revealed in December 2023. Following several delays, it is scheduled to be released on " +
        "19 November 2026 for the PlayStation 5 and Xbox Series X/S.";

    // The wrong-story background video #3 was actually given: the
    // Russian-interference timeline fetched for news[0] (the Trump FBI
    // hack story). A guard must reject this for a GTA 6 topic.
    private const string TrumpWikipediaExtract =
        "This is a timeline of events related to Russian interference in the 2016 United States elections, " +
        "sorted by topics. It also includes events described in investigations into the many suspicious links " +
        "between Trump associates and Russian officials. Those investigations continued in 2017, the first and " +
        "second halves of 2018, and 2019, largely as parts of the Crossfire Hurricane FBI investigation, the " +
        "Special Counsel investigation, multiple ongoing criminal investigations by several State Attorneys General, " +
        "and the investigation resulting in the Inspector General report on FBI and DOJ actions in the 2016 election.";

    private const string GtaTopic = "GTA 6 Drops: Rockstar's New Vice City Is Here – What It Means";

    private static List<string> CheckJson(string scriptJson, string? context) =>
        ReflectionHelper.Invoke<List<string>>(
            ReflectionHelper.VideoGen, "CheckJson", scriptJson, context, PrepDate);

    private static string ScriptJson(params (string Narration, string? VisualJson, bool Speculative)[] scenes)
    {
        var sceneJson = scenes.Select(s =>
            "{\"Narration\": " + JsonSerializer.Serialize(s.Narration) +
            ", \"SceneKind\": " + (s.VisualJson is null ? "\"art\"" : "\"visual\"") +
            ", \"IsSpeculative\": " + (s.Speculative ? "true" : "false") +
            ", \"VisualSpec\": " + (s.VisualJson ?? "null") + "}");
        return "{\"Title\": \"Test\", \"Scenes\": [" + string.Join(",", sceneJson) + "], \"BriefScenes\": [], \"Brief\": \"\"}";
    }

    private static string CalloutSpec(string text, string source) =>
        "{\"Kind\": \"callout\", \"Title\": \"\", \"Labels\": [], \"Values\": [], \"Unit\": \"\", " +
        "\"Headers\": [], \"Rows\": [], \"Text\": " + JsonSerializer.Serialize(text) +
        ", \"Subtext\": \"\", \"Source\": " + JsonSerializer.Serialize(source) + ", \"Accent\": null}";

    // --- (a) background routing: picked topic, not news[0] -----------------

    [Fact]
    public void KeyTerms_GtaTitle_ExtractsDistinctiveTerms()
    {
        var terms = ReflectionHelper.Invoke<IReadOnlyList<string>>(
            ReflectionHelper.VideoGen, "ExtractKeyTerms", GtaTopic);
        Assert.Contains("gta", terms);
        Assert.Contains("rockstar", terms);
        Assert.Contains("vice", terms);
        Assert.Contains("city", terms);
        Assert.DoesNotContain("the", terms);
        Assert.DoesNotContain("drops", terms);
        Assert.DoesNotContain("here", terms);
    }

    [Fact]
    public void BackgroundMatches_GtaArticle_MatchesGtaTopic()
    {
        Assert.True(ReflectionHelper.Invoke<bool>(
            ReflectionHelper.VideoGen, "BackgroundMatchesTopic",
            "Grand Theft Auto VI", Gta6WikipediaLead, GtaTopic));
    }

    [Fact]
    public void BackgroundMatches_TrumpArticle_DoesNotMatchGtaTopic()
    {
        // The exact failure that shipped video #3's fabrication: the
        // Trump-story background must be rejected for a GTA 6 topic.
        Assert.False(ReflectionHelper.Invoke<bool>(
            ReflectionHelper.VideoGen, "BackgroundMatchesTopic",
            "Russian interference in the 2016 United States elections", TrumpWikipediaExtract, GtaTopic));
    }

    [Fact]
    public void BackgroundMatches_TrumpArticle_MatchesTrumpHeadline()
    {
        // Positive control: the same extract DOES match the story it is
        // actually about, so the guard is not vacuous.
        Assert.True(ReflectionHelper.Invoke<bool>(
            ReflectionHelper.VideoGen, "BackgroundMatchesTopic",
            "Russian interference in the 2016 United States elections", TrumpWikipediaExtract,
            "Trump says FBI hack might expose Russia \"hoax\""));
    }

    [Fact]
    public void BackgroundMatches_GtaAcronym_MatchesByInitials()
    {
        // "GTA 6" on its own has only one key term ("gta"), and the
        // article lead spells it out as "Grand Theft Auto" — the guard
        // matches the acronym against the title's initials.
        Assert.True(ReflectionHelper.Invoke<bool>(
            ReflectionHelper.VideoGen, "BackgroundMatchesTopic",
            "Grand Theft Auto VI",
            "Grand Theft Auto VI is an upcoming action-adventure game developed and published by Rockstar Games.",
            "GTA 6"));
    }

    [Fact]
    public void BackgroundMatches_NoKeyTerms_DoesNotMatch()
    {
        Assert.False(ReflectionHelper.Invoke<bool>(
            ReflectionHelper.VideoGen, "BackgroundMatchesTopic",
            "Grand Theft Auto VI", Gta6WikipediaLead, "!!!"));
    }

    [Fact]
    public void BackgroundMatches_SequelNumber_MustMatchInstallment()
    {
        // The key-term check alone cannot tell "Grand Theft Auto VI" from
        // the 2002 "Grand Theft Auto: Vice City" — same words, wrong game
        // (verified live 2026-11-22: the term query ranked Vice City
        // first and the old guard passed it). The topic's "6" must match
        // the article's "VI".
        const string viceCityLead =
            "Grand Theft Auto: Vice City is a 2002 action-adventure game developed by Rockstar North and published by Rockstar Games. " +
            "It is the fourth main game in the Grand Theft Auto series, following 2001's Grand Theft Auto III, set in the fictional Vice City.";
        Assert.False(ReflectionHelper.Invoke<bool>(
            ReflectionHelper.VideoGen, "BackgroundMatchesTopic",
            "Grand Theft Auto: Vice City", viceCityLead, "GTA 6"));
        Assert.True(ReflectionHelper.Invoke<bool>(
            ReflectionHelper.VideoGen, "BackgroundMatchesTopic",
            "Grand Theft Auto VI", Gta6WikipediaLead, "GTA 6"));
    }

    [Fact]
    public void ExtractNumerals_SequelAndPronoun()
    {
        var numerals = ReflectionHelper.Invoke<System.Collections.IList>(
            ReflectionHelper.VideoGen, "ExtractNumerals", "GTA 6 Drops");
        Assert.Single(numerals);
        var first = numerals[0]!;
        Assert.Equal(6, (int)first.GetType().GetProperty("Value")!.GetValue(first)!);
        var forms = (IReadOnlyList<string>)first.GetType().GetProperty("Forms")!.GetValue(first)!;
        Assert.Contains("6", forms);
        Assert.Contains("vi", forms);

        // "World War II" is a 2; the pronoun "I" is not a numeral at all.
        var war = ReflectionHelper.Invoke<System.Collections.IList>(
            ReflectionHelper.VideoGen, "ExtractNumerals", "World War II");
        Assert.Single(war);
        Assert.Equal(2, (int)war[0]!.GetType().GetProperty("Value")!.GetValue(war[0])!);
        var none = ReflectionHelper.Invoke<System.Collections.IList>(
            ReflectionHelper.VideoGen, "ExtractNumerals", "What I learned today");
        Assert.Empty(none);
    }

    [Fact]
    public void PickedTopicQuery_UsesPickedTerms_NotTopStory()
    {
        var query = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildPickedTopicQuery", GtaTopic);
        Assert.Contains("gta", query);
        Assert.Contains("6", query);
        Assert.Contains("vice", query);
        Assert.DoesNotContain("trump", query);
    }

    private static object MakeNewsItem(string title, string source)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "NewsItem");
        return Activator.CreateInstance(type, title, source, "https://example.com/x", null, new DateTime(2026, 11, 22))!;
    }

    [Fact]
    public void FindMatchingNewsItem_PicksGtaItem_NotTopStory()
    {
        var newsType = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "NewsItem");
        var news = Array.CreateInstance(newsType, 2);
        news.SetValue(MakeNewsItem("Trump says FBI hack might expose Russia \"hoax\"", "Axios"), 0);
        news.SetValue(MakeNewsItem("IShowSpeed Completes GTA 6 Story in Just 52 Hours Without Sleep", "PK CineTalks"), 1);
        var matched = ReflectionHelper.Invoke<object?>(
            ReflectionHelper.VideoGen, "FindMatchingNewsItem", GtaTopic, news);
        Assert.NotNull(matched);
        var title = (string)matched!.GetType().GetProperty("Title")!.GetValue(matched)!;
        Assert.Contains("GTA 6", title);
    }

    [Fact]
    public void FindMatchingNewsItem_NoOverlap_ReturnsNull()
    {
        var newsType = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "NewsItem");
        var news = Array.CreateInstance(newsType, 1);
        news.SetValue(MakeNewsItem("Trump says FBI hack might expose Russia \"hoax\"", "Axios"), 0);
        var matched = ReflectionHelper.Invoke<object?>(
            ReflectionHelper.VideoGen, "FindMatchingNewsItem", "GTA 6 Drops: Rockstar's New Vice City Is Here", news);
        Assert.Null(matched);
    }

    [Fact]
    public void BuildPickedTopicContext_IncludesMatchedItemAndBackground()
    {
        var pickedType = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "PickedTopicBackground");
        var matched = MakeNewsItem("IShowSpeed Completes GTA 6 Story in Just 52 Hours Without Sleep", "PK CineTalks");
        var backgrounds = new List<(string, string)> { ("Grand Theft Auto VI", Gta6WikipediaLead) };
        var picked = Activator.CreateInstance(pickedType, GtaTopic, matched, backgrounds)!;
        var text = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildPickedTopicContext", picked);
        Assert.Contains("IShowSpeed Completes GTA 6", text);
        Assert.Contains("Rockstar Games", text);
        Assert.Contains("picked story", text);
    }

    [Fact]
    public void BuildPickedTopicContext_IncludesAllWikipediaBackgrounds()
    {
        // Multi-source context (2026-11-23): the picked story can carry up
        // to 3 guarded Wikipedia backgrounds, and the context block must
        // carry every one of them — the gate checks against all of them.
        var pickedType = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "PickedTopicBackground");
        var matched = MakeNewsItem("COP31 climate talks fail", "The Guardian");
        var backgrounds = new List<(string, string)>
        {
            ("2026 United Nations Climate Change Conference", "COP31 was held in Antalya, Turkey."),
            ("United Nations Framework Convention on Climate Change", "The UNFCCC is the parent treaty of the COP meetings."),
        };
        var picked = Activator.CreateInstance(pickedType, "Cop31 Climate Talks Fail", matched, backgrounds)!;
        var text = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildPickedTopicContext", picked);
        Assert.Contains("Antalya", text);
        Assert.Contains("parent treaty", text);
        Assert.Equal(2, text.Split("Background facts (Wikipedia)").Length - 1);
    }

    [Fact]
    public void SourceGuard_TopStoryBackgroundIsGuarded()
    {
        // The old code fetched the background for news[0] with no guard:
        // `WikipediaClient.FetchSummaryAsync(news[0].Title)`. The new code
        // must fetch it through the guarded helper instead.
        const string path =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/TrendResearch.cs";
        var text = File.ReadAllText(path);
        Assert.Contains("FetchBackgroundForTopicAsync(news[0].Title)", text);
        Assert.DoesNotContain("FetchSummaryAsync(news[0]", text);
    }

    [Fact]
    public void SourceGuard_PrepFetchesPickedTopicBackground_AndGatesOnCheck()
    {
        // The prep path must fetch the background for the story the script
        // actually picked (script.Title) and run the traceability gate in
        // FinishPrepAsync — before script.json is saved, so a failing prep
        // can never reach TTS/render unchecked (findings are rewritten by
        // FactGateRewrite since 2026-11-23, not thrown).
        const string path =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/Program.cs";
        var text = File.ReadAllText(path);
        Assert.Contains("FetchBackgroundForPickedTopicAsync(script.Title", text);
        var gate = text.IndexOf("FactTraceability.Check(script", StringComparison.Ordinal);
        Assert.True(gate >= 0, "FinishPrepAsync must call FactTraceability.Check(script, ...)");
        var finishPrep = text.IndexOf("FinishPrepAsync(string workDir", StringComparison.Ordinal);
        var firstSave = text.IndexOf("await SaveScriptAsync(workDir, script);", finishPrep, StringComparison.Ordinal);
        Assert.True(gate > finishPrep && gate < firstSave,
            "The fact-traceability gate must run inside FinishPrepAsync before the first SaveScriptAsync.");
        Assert.Contains("fact-check-failures.txt", text);
    }

    // --- (b) the pre-render fact-traceability check -------------------------

    private const string TraceableContext =
        "Trending YouTube titles: \"IShowSpeed Completes GTA 6 Story in Just 52 Hours Without Sleep\" | PK CineTalks | 1814 views/day\n" +
        "Top global news stories: \"Trump says FBI hack might expose Russia hoax\" (Axios, 2026-11-21 23:19 UTC)\n" +
        "Background facts (Wikipedia) for the picked story \"GTA 6\":\n" + Gta6WikipediaLead;

    [Fact]
    public void Check_TraceableScript_Passes()
    {
        var json = ScriptJson(
            ("Rockstar Games released GTA 6 in 2026, and players roam Leonida freely.", null, false),
            ("IShowSpeed completed the story in just 52 hours without sleep.",
                "{\"Kind\": \"callout\", \"Title\": \"Speedrun\", \"Labels\": [], \"Values\": [], \"Unit\": \"\", " +
                "\"Headers\": [], \"Rows\": [], \"Text\": \"52 hours to finish\", \"Subtext\": \"\", " +
                "\"Source\": \"Axios, 2026-11-22\", \"Accent\": null}", false));
        Assert.Empty(CheckJson(json, TraceableContext));
    }

    [Fact]
    public void Check_FutureDatedSource_Fails()
    {
        var json = ScriptJson(
            ("Critics praised the dynamic weather in the new Vice City.",
                CalloutSpec("\"The city feels alive,\" – GameSpot review", "GameSpot, 2026-11-23"), false));
        var findings = CheckJson(json, TraceableContext);
        Assert.Contains(findings, f => f.Contains("FutureDated") && f.Contains("2026-11-23"));
    }

    [Fact]
    public void Check_InternalReportSource_Fails()
    {
        var json = ScriptJson(
            ("Early playtests showed a 40% increase in player-reported immersion versus GTA V.",
                "{\"Kind\": \"bar\", \"Title\": \"Immersion\", \"Labels\": [\"GTA V\", \"GTA VI\"], \"Values\": [60, 84], " +
                "\"Unit\": \"percent\", \"Headers\": [], \"Rows\": [], \"Text\": \"\", " +
                "\"Subtext\": \"Internal focus-group data, Q4 2025\", " +
                "\"Source\": \"Rockstar internal focus-group report, Q4 2025\", \"Accent\": null}", false));
        var findings = CheckJson(json, TraceableContext);
        Assert.Contains(findings, f => f.Contains("InternalSource"));
    }

    [Fact]
    public void Check_PressReleaseSource_Fails()
    {
        var json = ScriptJson(
            ("Rockstar showed peak concurrency of over a million players at launch.",
                CalloutSpec("Peak players at launch", "Rockstar press release, 2026-11-22"), false));
        var findings = CheckJson(json, TraceableContext);
        Assert.Contains(findings, f => f.Contains("press release", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Check_QuoteAttributedToUnknownSource_Fails()
    {
        // Narration attribution to an outlet that is nowhere in the context.
        var json = ScriptJson(
            ("According to GameSpot's review, the city feels alive in a way no open world has before.", null, false));
        var findings = CheckJson(json, TraceableContext);
        Assert.Contains(findings, f => f.Contains("UntraceableAttribution") && f.Contains("GameSpot"));

        // And the same failure as a chart callout quoting an unknown outlet.
        var quoted = ScriptJson(
            ("Critics praised the dynamic weather in the new Vice City.",
                CalloutSpec("\"The city feels alive,\" – GameSpot review", "Axios, 2026-11-22"), false));
        var quotedFindings = CheckJson(quoted, TraceableContext);
        Assert.Contains(quotedFindings, f => f.Contains("UntraceableQuote") && f.Contains("GameSpot"));
    }

    [Fact]
    public void Check_QuoteAttributedToKnownSource_Passes()
    {
        // Positive control: the same "According to" shape passes when the
        // outlet IS in the supplied context.
        var json = ScriptJson(
            ("According to Rockstar Games, the story follows Jason Duval and Lucia Caminos.", null, false));
        Assert.Empty(CheckJson(json, TraceableContext));
    }

    [Fact]
    public void Check_QuotedAttributionWithApostrophe_TracesWhenNameIsInContext()
    {
        // Gate fix found by the backstory COP31 dry run (2026-11-23): the
        // model quoted Darragh O'Brien in a visual callout and the gate
        // flagged it as an untraceable quote — because CleanAttribution
        // strips apostrophes ("obrien") while the normalised context keeps
        // them ("o'brien"). The narration-attribution check already had
        // the apostrophe-stripped-context fix; the quoted-attribution check
        // did not. A quoted line attributed to a name that IS in the
        // context must trace.
        const string context =
            "The proposed deal failed to meet the moment, said Ireland\u2019s environment minister, Darragh O\u2019Brien, speaking for the EU.";
        var json = ScriptJson(
            ("Delegates left Antalya without a deal on the final day.",
                CalloutSpec("\u201cThe deal failed to meet the moment,\u201d – Darragh O\u2019Brien", "The Guardian, 2026-11-22"), false));
        var findings = CheckJson(json, context);
        Assert.DoesNotContain(findings, f => f.Contains("O\u2019Brien") && f.Contains("UntraceableQuote"));
        // And a quoted line attributed to someone genuinely not in the
        // context still fails.
        var missing = ScriptJson(
            ("Delegates left Antalya without a deal on the final day.",
                CalloutSpec("\u201cThe deal failed to meet the moment,\u201d – Darragh O\u2019Brien", "The Guardian, 2026-11-22"), false));
        var missingFindings = CheckJson(missing, "The deal failed and delegates left.");
        Assert.Contains(missingFindings, f => f.Contains("UntraceableQuote"));
    }

    [Fact]
    public void Check_NumberAtSentenceEnd_Traces()
    {
        // Gate fix found by the Russian-ship auto-pick dry run (2026-11-23):
        // the picked Wikipedia background says "...since its invasion of
        // Ukraine on 24 February 2022." — the only "2022" in the context,
        // and it ends a sentence. The old (?![\d.]) boundary rejected it
        // because of the trailing period, so the gate flagged "2022" twice
        // even though it IS in the context. A number at a sentence end
        // must trace; a number that is only a prefix of a longer number
        // ("2024" in "20240") must still fail.
        const string context =
            "There have been attacks in mainland Russia as a result of the Russo-Ukrainian war since its invasion of Ukraine on 24 February 2022. The main targets have been the military, the arms industry and the oil industry.";
        var json = ScriptJson(
            ("The war began in 2022, and the attacks have continued ever since.", null, false));
        var findings = CheckJson(json, context);
        Assert.DoesNotContain(findings, f => f.Contains("2022") && f.Contains("UntraceableNumber"));
        // Prefix-of-longer-number still fails.
        var prefix = ScriptJson(
            ("The war began in 2024, and the attacks have continued ever since.", null, false));
        var prefixFindings = CheckJson(prefix, "The count reached 20240 in the latest report.");
        Assert.Contains(prefixFindings, f => f.Contains("2024") && f.Contains("UntraceableNumber"));
    }

    [Fact]
    public void Check_AttributionFillerWords_StrippedBeforeTrace()
    {
        // Gate fix found by the COP31 v3 dry run (2026-11-23): the model
        // wrote "As Guterres said, 'people everywhere pay the price'..."
        // and the gate flagged "As Guterres" — but Guterres IS in the
        // Guardian article text. The SaidRegex sweeps the leading "As"
        // (and trailing auxiliaries/adverbs like "has repeatedly", "have
        // already", "first") into the captured subject, so the gate
        // checked "as guterres" instead of "guterres". The filler words
        // must be stripped so the gate checks the actual entity name.
        const string context =
            "UN Secretary-General Antonio Guterres said people everywhere pay the price for the failure to act on climate change.";
        var json = ScriptJson(
            ("The stakes are high. As Guterres said, 'people everywhere pay the price' for the failure to act.", null, false));
        var findings = CheckJson(json, context);
        Assert.DoesNotContain(findings, f => f.Contains("Guterres") && f.Contains("UntraceableAttribution"));
        // Trailing filler: "Ukraine first claimed" traces when Ukraine
        // is in the context.
        var ukraine = ScriptJson(
            ("In 2022, Ukraine first claimed a successful drone strike on a Russian-linked cargo vessel.", null, false));
        var ukraineFindings = CheckJson(ukraine,
            "There have been attacks as a result of the Russo-Ukrainian war since its invasion of Ukraine on 24 February 2022.");
        Assert.DoesNotContain(ukraineFindings, f => f.Contains("Ukraine") && f.Contains("UntraceableAttribution"));
        // But an invented entity still fails after stripping — the
        // stripped name is still not in the context.
        var invented = ScriptJson(
            ("Maritime insurers have already warned of rising premiums for vessels near Libyan waters.", null, false));
        var inventedFindings = CheckJson(invented,
            "An explosion on a Russian ship at a Libya port was caused by a suspected drone attack, officials say.");
        Assert.Contains(inventedFindings, f => f.Contains("Maritime insurers") && f.Contains("UntraceableAttribution"));
    }

    [Fact]
    public void Check_ChartSourceAfterPrep_Fails()
    {
        var json = ScriptJson(
            ("Industry analysts say the tech stack could become the new baseline for open-world titles.",
                "{\"Kind\": \"table\", \"Title\": \"Key Tech Features\", \"Labels\": [], \"Values\": [], \"Unit\": \"\", " +
                "\"Headers\": [\"Studio\", \"Feature\"], \"Rows\": [[\"Ubisoft\", \"Procedural weather\"]], \"Text\": \"\", " +
                "\"Subtext\": \"\", \"Source\": \"Industry analyst report, Bloomberg, 2026-11-24\", \"Accent\": null}", false));
        var findings = CheckJson(json, TraceableContext);
        Assert.Contains(findings, f => f.Contains("FutureDated") && f.Contains("2026-11-24"));
    }

    [Fact]
    public void Check_InventedEntity_Fails()
    {
        var json = ScriptJson(
            ("That system uses a new physics engine called 'HydroFlow', unveiled at GDC 2024.", null, false),
            ("The AI framework, dubbed 'LifePulse', was first tested on a small downtown block.", null, false));
        var findings = CheckJson(json, TraceableContext);
        Assert.Contains(findings, f => f.Contains("UntraceableEntity") && f.Contains("HydroFlow"));
        Assert.Contains(findings, f => f.Contains("UntraceableEntity") && f.Contains("LifePulse"));
    }

    [Fact]
    public void Check_UntraceableNumber_Fails()
    {
        var json = ScriptJson(
            ("All these tech leaps were funded by Rockstar's record-breaking $2.5 billion revenue in 2023.", null, false));
        var findings = CheckJson(json, TraceableContext);
        Assert.Contains(findings, f => f.Contains("UntraceableNumber") && f.Contains("2.5"));
    }

    [Fact]
    public void Check_EmptyContext_Skips()
    {
        // With no supplied context there is nothing to trace against: the
        // check reports nothing and the caller logs a loud skip warning
        // instead of failing every hand-written/manual script. The gate
        // only bites when a real trend context exists.
        var json = ScriptJson(("HydroFlow changes everything.", null, false));
        Assert.Empty(CheckJson(json, ""));
        Assert.Empty(CheckJson(json, null));
    }

    [Fact]
    public void Check_SpeculativeScene_NumbersExempt_EntityStillFlagged()
    {
        // A labelled rumour may cite speculative numbers, but it may not
        // invent a proper noun: the 1990s date is exempt, HydroFlow is not.
        var json = ScriptJson(
            ("Rumors swirl that Rockstar is already prototyping a Vice City expansion set in the 1990s, re-using the HydroFlow engine.", null, true));
        var findings = CheckJson(json, TraceableContext);
        Assert.DoesNotContain(findings, f => f.Contains("1990"));
        Assert.Contains(findings, f => f.Contains("UntraceableEntity") && f.Contains("HydroFlow"));
    }

    [Fact]
    public void Check_BuildCheckContext_StripsTopStoryBlock()
    {
        var snapshot = File.ReadAllText(Video3SnapshotPath);
        var picked = "Background facts (Wikipedia) for the picked story \"GTA 6\":\n" + Gta6WikipediaLead;
        var context = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", snapshot, picked);
        Assert.Contains("IShowSpeed Completes GTA 6", context);
        Assert.Contains("Rockstar Games", context);
        Assert.DoesNotContain("Russian interference", context);
    }

    // --- real video #3 data --------------------------------------------------

    private static string RealVideo3Context()
    {
        var snapshot = File.ReadAllText(Video3SnapshotPath);
        var picked = "Background facts (Wikipedia) for the picked story \"GTA 6\":\n" + Gta6WikipediaLead;
        return ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", snapshot, picked);
    }

    [Fact]
    public void RealData_Video3_FlagsInventedItems()
    {
        // Scene numbers are 1-based over the Scenes array, matching
        // ScriptGenerator.ValidateNewsScenes ("scene {i+1}"): the review's
        // 0-based "scene 16" (HydroFlow, Scenes[16]) is "scene 17" here.
        var scriptJson = File.ReadAllText(Video3ScriptPath);
        var findings = CheckJson(scriptJson, RealVideo3Context());
        // Every invented item from review #2 §E must be flagged:
        Assert.Contains(findings, f => f.Contains("scene 17") && f.Contains("HydroFlow"));
        Assert.Contains(findings, f => f.Contains("scene 18") && f.Contains("HydroFlow"));
        Assert.Contains(findings, f => f.Contains("scene 20") && f.Contains("LifePulse"));
        Assert.Contains(findings, f => f.Contains("scene 24") && f.Contains("2.5"));
        Assert.Contains(findings, f => f.Contains("scene 26") && f.Contains("InternalSource"));
        Assert.Contains(findings, f => f.Contains("scene 29") && f.Contains("press release", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, f => f.Contains("scene 31") && f.Contains("FutureDated") && f.Contains("2026-11-23"));
        Assert.Contains(findings, f => f.Contains("scene 33") && f.Contains("FutureDated") && f.Contains("2026-11-24"));
        Assert.Contains(findings, f => f.Contains("scene 35") && f.Contains("FutureDated") && f.Contains("2026-11-24"));
        Assert.Contains(findings, f => f.Contains("scene 37") && f.Contains("FutureDated") && f.Contains("2026-11-25"));
        // True facts that ARE in the picked-topic background must not flag:
        Assert.DoesNotContain(findings, f => f.StartsWith("scene 4 ", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.StartsWith("scene 5 ", StringComparison.Ordinal));
        Assert.True(findings.Count >= 15, $"Expected at least 15 findings on video #3, got {findings.Count}:\n{string.Join("\n", findings)}");
    }

    [Fact]
    public void RealData_SanitizedVideo3_Passes()
    {
        // The same real script with every untraceable claim replaced by a
        // claim that IS in the context (52 from the IShowSpeed headline,
        // Axios from the news headlines, Rockstar Games from the Wikipedia
        // lead, 2026) must pass clean — proving the check flags the
        // fabrication, not the script's shape.
        var node = JsonNode.Parse(File.ReadAllText(Video3ScriptPath))!;
        Sanitize(node);
        var findings = CheckJson(node.ToJsonString(), RealVideo3Context());
        Assert.Empty(findings);
    }

    private static void Sanitize(JsonNode node)
    {
        // String replacements first (full source strings before their parts).
        var stringReplacements = new (string Old, string New)[]
        {
            ("Rockstar internal data, 2025", "Axios, 2026-11-22"),
            ("Rockstar Financial Report 2023", "Axios, 2026-11-22"),
            ("Rockstar internal focus-group report, Q4 2025", "Axios, 2026-11-22"),
            ("Rockstar press release, 2026-11-22", "Axios, 2026-11-22"),
            ("GameSpot, 2026-11-23", "Axios, 2026-11-22"),
            ("Industry analyst report, Bloomberg, 2026-11-24", "Axios, 2026-11-22"),
            ("Rockstar dev blog, 2026-11-24", "Axios, 2026-11-22"),
            ("The Verge, 2026-11-25", "Axios, 2026-11-22"),
            ("Internal focus-group data, Q4 2025", "Axios focus-group data"),
            ("HydroFlow", "Rockstar Games"),
            ("LifePulse", "Rockstar Games"),
            ("250000", "52"), ("600000", "52"), ("900000", "52"), ("1100000", "52"),
            ("400", "52"), ("300", "52"), ("30", "52"),
            ("2.5", "52"), ("40", "52"), ("60", "52"), ("84", "52"),
            ("2024", "2026"), ("2019", "2026"), ("2027", "2026"), ("2023", "2026"),
            ("GameSpot", "Axios"), ("The Verge", "Axios"), ("Rockstar dev blog", "Axios"),
            ("According to Rockstar's lead composer", "According to Rockstar Games"),
        };
        // Chart values are JSON numbers, not strings — replace those too.
        var numberReplacements = new HashSet<double> { 60, 84, 250000, 600000, 900000, 1100000 };
        SanitizeNode(node, stringReplacements, numberReplacements);
    }

    // The model writes non-breaking hyphens (U+2011) and narrow no-break
    // spaces (U+202F) inside dates and source strings ("2026‑11‑23",
    // "focus‑group", "Q4 2025"), so the replacements above — written with
    // plain ASCII — only match after the same normalisation the check
    // itself applies. Normalising the sanitised copy is fine: it is a
    // scratch copy built only to prove the check passes on traceable text.
    private static string NormalizeScratch(string s) => s
        .Replace('\u2011', '-').Replace('\u2010', '-').Replace('\u2013', '-').Replace('\u2014', '-')
        .Replace('\u202F', ' ').Replace('\u00A0', ' ').Replace('\u2009', ' ')
        .Replace('\u2019', '\'').Replace('\u2018', '\'')
        .Replace('\u201C', '"').Replace('\u201D', '"');

    private static void SanitizeNode(JsonNode node, (string Old, string New)[] replacements, HashSet<double> numbers)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                var child = obj[key];
                if (child is JsonValue v && v.TryGetValue<string>(out var s))
                    obj[key] = replacements.Aggregate(NormalizeScratch(s), (t, r) => t.Replace(r.Old, r.New));
                else if (child is JsonValue n && n.TryGetValue<double>(out var d) && numbers.Contains(d))
                    obj[key] = 52;
                else if (child is not null)
                    SanitizeNode(child, replacements, numbers);
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var child = arr[i];
                if (child is JsonValue v && v.TryGetValue<string>(out var s))
                    arr[i] = replacements.Aggregate(NormalizeScratch(s), (t, r) => t.Replace(r.Old, r.New));
                else if (child is JsonValue n && n.TryGetValue<double>(out var d) && numbers.Contains(d))
                    arr[i] = 52;
                else if (child is not null)
                    SanitizeNode(child, replacements, numbers);
            }
        }
    }
}
