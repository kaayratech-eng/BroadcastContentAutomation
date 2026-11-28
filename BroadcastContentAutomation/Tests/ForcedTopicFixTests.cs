// Regression tests for the forced-topic wiring fix (2026-11-23).
//
// A forced --topic prep SKIPPED TrendResearch.FetchReportAsync entirely
// (Program.cs: `trendReport = topic is null ? FetchReportAsync(...) : null`),
// so it got NO headlines and NO article text — only the single Wikipedia
// summary (null for COP31). The model then invented 42 numbers from a
// headlines-only context (worse than the auto-pick dry run's 25, which had
// articles). The fix fetches the article text for the forced topic in
// RunPrepAsync (and in the dry-script path) and writes trend-snapshot.txt
// with the full built context, so resumes and the gate have something to
// read.
//
// A second hole closed in the same change: when a forced-topic prep left
// no trend context at all, FinishPrepAsync logged a warning and SKIPPED
// the fact gate — a path meant for hand-written/manual scripts. A
// gate-failed resume exploited it and reached TTS unchecked. For NEWS
// profiles the prep must now fail loudly when the check context is empty.
//
// Seams under test (no live helper calls, no network):
//   (a) The forced-topic context shape — articles + forced Wikipedia
//       summary, no headlines — feeds the prompt (ArticleTextBlock) and
//       survives the gate (BuildCheckContext), so article numbers trace.
//   (b) The prompt instruction — both news prompts carry the FACTS RULE
//       (numbers/dates/attributions only from the FACTS; describe in
//       words when a number is not there; never invent).
//   (c) The gate guard — the empty-check-context skip is gone for news
//       profiles (source guard + the BuildCheckContext precondition).
//   (d) The wiring — Program.cs fetches articles for the forced topic and
//       writes the snapshot after all context assembly, in both the prep
//       and the dry-script paths.
using System.Reflection;
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class ForcedTopicFixTests
{
    private static readonly DateTime PrepDate = new(2026, 11, 23);

    private const string ProgramPath =
        "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/Program.cs";
    private const string ScriptPath =
        "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/ScriptGenerator.cs";

    // The forced-topic trend context as RunPrepAsync now builds it: the
    // article section first, then the analytics brief, then the forced
    // Wikipedia summary. No headlines (FetchReportAsync never ran).
    private static string ForcedTopicContext() =>
        "Full article text for the top stories (use ONLY these facts, plus the headlines above, for the story you pick — numbers, quotes and attributions must come from here or the headlines, never from memory):\n" +
        "[The Guardian] \"Finance, fossil fuels and the ‘overshoot’: six key takeaways from Cop31\" — https://www.theguardian.com/environment/2026/nov/22/cop31-takeaways\n" +
        "Close to 200 countries have been meeting in Antalya, Turkey, for the annual UN climate summit. " +
        "75 countries backed Türkiye’s goal to raise electricity’s share of global energy to 35% by 2035. " +
        "A proposed $120 billion annual adaptation target was dropped, replaced by a call to triple funding by 2035.\n" +
        "What worked on this channel before:\nGTA 6 video: 10,000 views in 24 hours.\n" +
        "Wikipedia context for this topic (use real facts from here, not invented ones):\n" +
        "COP31 was the 31st United Nations Climate Change conference, held in Antalya, Turkey in November 2026.";

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

    // --- (a) the forced-topic context shape ---------------------------------

    [Fact]
    public void ForcedTopicContext_ArticleText_ReachesPrompt_AndSurvivesGate()
    {
        var context = ForcedTopicContext();
        // The prompt extractor finds the article section in a context that
        // has no headlines at all (forced-topic shape).
        var block = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "ArticleTextBlock", context);
        Assert.Contains("Full article text", block);
        Assert.Contains("35%", block);
        // The gate keeps the article section (it is not a Wikipedia block)
        // and the forced Wikipedia summary, so article numbers trace.
        var checkContext = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", context, null);
        Assert.Contains("35%", checkContext);
        Assert.Contains("120", checkContext);
        Assert.Contains("Antalya", checkContext);
        var passing = CheckJson(ScriptJson(
            "75 countries backed a goal to raise electricity's share to 35% by 2035."), checkContext);
        Assert.DoesNotContain(passing, f => f.Contains("35%"));
        // A number from nowhere still fails — the articles do not launder it.
        var failing = CheckJson(ScriptJson(
            "Delegates agreed to phase out fossil fuels entirely by 2030."), checkContext);
        Assert.Contains(failing, f => f.Contains("2030") && f.Contains("UntraceableNumber"));
    }

    [Fact]
    public void ForcedTopicContext_WithoutArticles_FallsBackToWikipedia()
    {
        // When the article fetch returns nothing, the forced-topic facts
        // block still carries the Wikipedia summary (it is the topic's own
        // background, not the top story's).
        const string context =
            "Wikipedia context for this topic (use real facts from here, not invented ones):\n" +
            "COP31 was held in Antalya, Turkey in November 2026.";
        var block = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "ArticleTextBlock", context);
        Assert.Equal("", block);
        var checkContext = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", context, null);
        Assert.Contains("Antalya", checkContext);
    }

    // --- (b) the prompt instruction -----------------------------------------

    [Fact]
    public void NewsPrompts_CarryTheFactsRule()
    {
        // The instruction the MODEL sees (not a code comment): every
        // number/date/percentage/attribution must appear in the FACTS, and
        // when one does not, describe it in words without a number.
        var text = File.ReadAllText(ScriptPath);
        Assert.Contains("FACTS RULE", text);
        Assert.Contains("must appear in the FACTS above", text);
        Assert.Contains("describe it in words without a number", text);
        Assert.Contains("Never invent a date, a figure, a percentage, or a source", text);
        // The continuation act gets its own rule aimed at the recap (it has
        // no facts block of its own — only the story so far).
        Assert.Contains("must already appear in the story so far above", text);
        Assert.Contains("Never introduce a NEW number, date, or source", text);
    }

    [Fact]
    public void FactsRule_IsInPromptText_NotJustAComment()
    {
        // The old "Numbers only from the supplied facts" line was a code
        // comment the model never saw. The rule must appear inside the
        // prompt literals (the $$""" blocks), i.e. as an interpolated
        // {{factsRule}} / {{continuationFactsRule}} hole in a prompt.
        var text = File.ReadAllText(ScriptPath);
        Assert.Contains("{{factsRule}}", text);
        Assert.Contains("{{continuationFactsRule}}", text);
        // And it is placed in the universal requirements of both prompts,
        // not only next to the FACTS block.
        Assert.True(text.IndexOf("{{factsRule}}", StringComparison.Ordinal) >= 0);
        var continuationIdx = text.IndexOf("{{continuationFactsRule}}", StringComparison.Ordinal);
        Assert.True(continuationIdx >= 0);
        Assert.Contains("Universal requirements (apply to every scene):\n- {{continuationFactsRule}}", text);
    }

    // --- (c) the gate guard ---------------------------------------------------

    [Fact]
    public void GateGuard_EmptyCheckContext_FailsLoudlyForNewsProfiles()
    {
        // Source guard for the bypass fix: when the check context is empty
        // for a NEWS profile, FinishPrepAsync must throw (FATAL) instead of
        // logging a skip warning and sailing through to TTS. The old skip
        // warning text must be gone.
        var text = File.ReadAllText(ProgramPath);
        Assert.Contains("no trend context available for this news prep", text);
        Assert.Contains("throw new InvalidOperationException(noContextReport)", text);
        Assert.DoesNotContain(text, "fact-traceability check skipped");
    }

    [Fact]
    public void BuildCheckContext_EmptyInputs_ReturnsEmpty_TheConditionTheGuardCatches()
    {
        // The precondition the guard catches: no snapshot, no picked
        // background, no article text → the check context is empty. (The
        // gate's own CheckJson returns no findings for an empty context by
        // design — the guard in FinishPrepAsync is what turns that into a
        // loud failure for news profiles.)
        var empty = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", null, null);
        Assert.Equal("", empty);
        var empty2 = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", "", null);
        Assert.Equal("", empty2);
    }

    // --- (d) the wiring -------------------------------------------------------

    [Fact]
    public void Wiring_ForcedTopicPrep_FetchesArticles_AndWritesSnapshotAfterAssembly()
    {
        // Source-scan guards for the two wiring points in RunPrepAsync:
        // the forced-topic branch fetches article text (mirroring
        // FetchReportAsync's top-story fetch), and the snapshot is written
        // after all context assembly (articles + analytics + Wikipedia),
        // not before — so a forced-topic prep always leaves a context
        // behind for resumes and the gate.
        var text = File.ReadAllText(ProgramPath);
        Assert.Contains("ArticleClient.FetchForTopicAsync(topic, cfg, context: PrepContext.Current)", text);
        Assert.Contains("ArticleClient.BuildArticleSection(await ArticleClient.FetchForTopicAsync(topic, cfg, context: PrepContext.Current))", text);
        // The snapshot write is guarded by a non-empty check (it fires for
        // forced topics too, where trendContext used to be null here) and
        // sits after the Wikipedia append in the file.
        Assert.Contains("if (!string.IsNullOrWhiteSpace(trendContext))", text);
        Assert.Contains("await File.WriteAllTextAsync(Path.Combine(workDir, \"trend-snapshot.txt\"), trendContext);", text);
        var wikiIdx = text.IndexOf("Wikipedia context for this topic (use real facts from here, not invented ones)", StringComparison.Ordinal);
        var snapshotIdx = text.IndexOf("trend-snapshot.txt", StringComparison.Ordinal);
        Assert.True(wikiIdx >= 0 && snapshotIdx > wikiIdx,
            "trend-snapshot.txt must be written after the Wikipedia append, so the snapshot carries the full built context");
    }

    [Fact]
    public void Wiring_DryScriptPath_FetchesArticlesForForcedTopic()
    {
        // The dry-script path had the same forced-topic gap (it skipped
        // FetchReportAsync too), so a dry run could not measure the fix.
        // It must fetch the forced-topic articles as well. Anchor on the
        // function DEFINITION, not the dispatch line (which mentions the
        // name earlier in the file).
        var text = File.ReadAllText(ProgramPath);
        var dryIdx = text.IndexOf("async Task<int> RunDryScriptAsync()", StringComparison.Ordinal);
        Assert.True(dryIdx >= 0, "RunDryScriptAsync definition not found");
        var fetchIdx = text.IndexOf("ArticleClient.FetchForTopicAsync(topic, cfg, context: PrepContext.Current)", dryIdx, StringComparison.Ordinal);
        Assert.True(fetchIdx > dryIdx,
            "RunDryScriptAsync must also fetch articles for a forced topic");
    }
}
