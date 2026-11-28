// Regression tests for the backstory fix (2026-11-23).
//
// The fact gate was stuck at 6 findings on every topic (COP31 and the
// auto-pick Nepal story both failed with 6): backstory years the event
// articles don't contain (COP31: 2000, 2020, 2019, 2027) plus invented
// attributions. Two root causes, both in how the backstory reaches the
// model:
//   (a) The forced-topic Wikipedia fetch took the FIRST search result
//       blindly (FetchSummaryAsync, unguarded) — for COP31 that was
//       "2026", a calendar-year article, not the conference — so Act 1
//       had no backstory years and the model invented them.
//   (b) The continuation acts never saw the FACTS at all: the Act 1
//       facts block (articles + Wikipedia) was passed to
//       GenerateNewsContinuationActAsync only for visual validation, and
//       the prompt itself carried just the recap. Act 2 (the backstory)
//       was asked to write history it had never been given.
//   (c) A third gap in the same area: ForcedTopicFactsBlock returned ONLY
//       the Wikipedia block when one existed, so for a forced topic the
//       model never saw the fetched articles in Act 1 either.
//   (d) And the gate dropped the forced-topic background whenever a
//       picked background existed (else-if in BuildCheckContext), so a
//       number the model took from the forced-topic backstory failed even
//       though it was supplied.
//
// Seams under test (no live helper calls, no network):
//   (a) ForcedTopicFactsBlock carries BOTH the Wikipedia block and the
//       article text (not Wikipedia-only).
//   (b) The backstory (Wikipedia) block is extractable from the Act 1
//       facts block, so the continuation prompt can carry it; the prompt
//       template carries {{backstorySection}} / {{backstoryRule}} and the
//       BACKSTORY RULE text.
//   (c) BuildCheckContext keeps BOTH the forced-topic block and the
//       picked background (no else-if drop).
//   (d) The guard rejects the "2026" year article for a COP31 topic
//       (its lead carries no COP31 key term) and accepts the real
//       conference/parent article.
//   (e) The wiring — Program.cs fetches the guarded plural backgrounds
//       for a forced topic (prep + dry-script paths), not the unguarded
//       single summary.
using System.Reflection;

namespace BroadcastContentAutomation.Tests;

public class BackstoryFixTests
{
    private const string ProgramPath =
        "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/Program.cs";
    private const string ScriptPath =
        "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/ScriptGenerator.cs";
    private const string FactPath =
        "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/FactTraceability.cs";

    // A forced-topic trend context as RunPrepAsync now builds it: article
    // section, analytics brief, then the guarded plural Wikipedia
    // backgrounds under the forced-topic marker.
    private const string ForcedContext =
        "Full article text for the top stories (use ONLY these facts, plus the headlines above, for the story you pick — numbers, quotes and attributions must come from here or the headlines, never from memory):\n" +
        "[The Guardian] \"Finance, fossil fuels and the ‘overshoot’: six key takeaways from Cop31\" — https://www.theguardian.com/environment/2026/nov/22/cop31-takeaways\n" +
        "Close to 200 countries have been meeting in Antalya, Turkey, for the annual UN climate summit. " +
        "75 countries backed Türkiye’s goal to raise electricity’s share of global energy to 35% by 2035. " +
        "A proposed $120 billion annual adaptation target was dropped, replaced by a call to triple funding by 2035.\n" +
        "What worked on this channel before:\nGTA 6 video: 10,000 views in 24 hours.\n" +
        "Wikipedia context for this topic (use real facts from here, not invented ones):\n" +
        "2026 United Nations Climate Change Conference: The 2026 United Nations Climate Change Conference or Conference of the Parties to the UNFCCC, more commonly known as COP31, was held at the Expo Center in Antalya, Turkey. It was the 31st session of the United Nations Climate Change Conference. Talks were scheduled to take place from 9 to 20 November 2026 but overran due to the failure to agree a deal.\n" +
        "United Nations Climate Change Conference: The United Nations Climate Change Conferences are yearly conferences held as part of the United Nations Framework Convention on Climate Change (UNFCCC). They serve as the formal meeting of the UNFCCC parties to assess progress in dealing with climate change, and beginning in the mid-1990s, to negotiate the Kyoto Protocol.";

    // --- (a) the forced-topic facts block carries both sources ----------

    [Fact]
    public void ForcedTopicFactsBlock_IncludesWikipediaAndArticles()
    {
        // The old version returned ONLY the Wikipedia block when one
        // existed, so the model never saw the fetched articles in Act 1.
        // Both must be present now: the backstory years (Wikipedia) and
        // the event numbers (articles).
        var block = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "ForcedTopicFactsBlock", ForcedContext);
        Assert.Contains("COP31", block);
        Assert.Contains("Antalya", block);
        Assert.Contains("35%", block);
        Assert.Contains("120", block);
    }

    [Fact]
    public void ForcedTopicFactsBlock_ArticlesOnly_WhenNoWikipedia()
    {
        // No Wikipedia background: the articles alone still reach the
        // model (the old fallback path, unchanged).
        const string context =
            "Full article text for the top stories (use ONLY these facts, plus the headlines above, for the story you pick):\n" +
            "[BBC News] \"COP31 talks collapse\" — https://www.bbc.co.uk/news/articles/x\n" +
            "Delegates left Antalya without a deal on 20 November 2026.";
        var block = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "ForcedTopicFactsBlock", context);
        Assert.Contains("Antalya", block);
        Assert.Contains("20 November 2026", block);
    }

    // --- (b) the backstory reaches the continuation prompt --------------

    [Fact]
    public void BackstoryBlock_ExtractableFromFactsBlock()
    {
        // The continuation prompt pulls the Wikipedia background out of
        // the Act 1 facts block (sourceFacts) so Act 2 has real backstory
        // years. The extractor must find it in both marker shapes:
        // the forced-topic marker and the top-story marker.
        var forced = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "WikipediaBackgroundBlock", ForcedContext);
        Assert.Contains("COP31", forced);
        Assert.Contains("Antalya", forced);
        const string topStoryContext =
            "Background facts (Wikipedia) for the top story \"Nepal PM India visit\":\n" +
            "Balendra Shah is a Nepali politician who has served as the prime minister of Nepal since 2026.";
        var top = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "WikipediaBackgroundBlock", topStoryContext);
        Assert.Contains("Balendra Shah", top);
        Assert.Contains("2026", top);
    }

    [Fact]
    public void ContinuationPrompt_CarriesBackstorySection_AndRule()
    {
        // The prompt text the model sees: a backstory facts section after
        // the recap, plus the Act 2 BACKSTORY RULE (use only the years in
        // the backstory facts; no year at all when the one wanted is not
        // there). Both must be interpolated into the continuation prompt,
        // not left as code comments.
        var text = File.ReadAllText(ScriptPath);
        Assert.Contains("{{backstorySection}}", text);
        Assert.Contains("{{backstoryRule}}", text);
        Assert.Contains("BACKSTORY RULE", text);
        Assert.Contains("Backstory facts (Wikipedia background for this topic", text);
        Assert.Contains("do NOT use a year at all", text);
        Assert.Contains("Never invent a year, a count, or a source for the backstory", text);
    }

    // --- (c) the gate keeps both backgrounds ----------------------------

    [Fact]
    public void BuildCheckContext_KeepsForcedTopicBlock_AndPickedBackground()
    {
        // The old else-if dropped the forced-topic block whenever a picked
        // background existed, so a number the model took from the
        // forced-topic backstory failed even though it was supplied. Both
        // must survive now.
        const string pickedBackground =
            "Background facts (Wikipedia) for the picked story \"COP31 Climate Talks Collapse\":\n" +
            "Greta Thunberg is a Swedish activist. She gained global attention in 2018.";
        var checkContext = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", ForcedContext, pickedBackground);
        // Forced-topic backstory (what the model saw before generation).
        Assert.Contains("COP31", checkContext);
        Assert.Contains("Antalya", checkContext);
        Assert.Contains("mid-1990s", checkContext);
        // Picked background (fetched after generation, on the script title).
        Assert.Contains("Greta Thunberg", checkContext);
        Assert.Contains("2018", checkContext);
        // And the article numbers still trace.
        Assert.Contains("35%", checkContext);
    }

    [Fact]
    public void BuildCheckContext_ForcedTopicBlockOnly_WhenNoPickedBackground()
    {
        // No picked background: the forced-topic block alone still stands
        // (the previous behaviour, unchanged).
        var checkContext = ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", ForcedContext, null);
        Assert.Contains("COP31", checkContext);
        Assert.Contains("Antalya", checkContext);
        Assert.Contains("35%", checkContext);
    }

    // --- (d) the guard rejects the year article, accepts the real one ---

    [Fact]
    public void Guard_RejectsYearArticle_ForCop31Topic()
    {
        // The unguarded single fetch took the first search result, "2026"
        // — a calendar-year article whose lead carries no COP31 key term
        // (no "cop31", "climate", "talks" or "fail"). The guarded plural
        // fetch must reject it, or the backstory is a year article again.
        const string yearExtract =
            "2026 (MMXXVI) is the current year, and is a common year starting on Thursday of the Gregorian calendar, " +
            "the 2026th year of the Common Era (CE) and Anno Domini (AD) designations, the 26th year of the 3rd millennium " +
            "and the 21st century, and the 7th year of the 2020s decade.";
        var accepted = ReflectionHelper.Invoke<bool>(
            ReflectionHelper.VideoGen, "BackgroundMatchesTopic",
            "2026", yearExtract, "COP31 climate talks fail");
        Assert.False(accepted);
    }

    [Fact]
    public void Guard_AcceptsConferenceArticle_ForCop31Topic()
    {
        // The real article: its title carries the "climate" key term, so
        // the guard accepts it and the backstory gets the conference's
        // real years (2026, 31st session, 9-20 November).
        const string extract =
            "The 2026 United Nations Climate Change Conference or Conference of the Parties to the UNFCCC, " +
            "more commonly known as COP31, was held at the Expo Center in Antalya, Turkey. " +
            "It was the 31st session of the United Nations Climate Change Conference. " +
            "Talks were scheduled to take place from 9 to 20 November 2026 but overran due to the failure to agree a deal.";
        var accepted = ReflectionHelper.Invoke<bool>(
            ReflectionHelper.VideoGen, "BackgroundMatchesTopic",
            "2026 United Nations Climate Change Conference", extract, "COP31 climate talks fail");
        Assert.True(accepted);
    }

    [Fact]
    public void Guard_AcceptsParentArticle_ForCop31Topic()
    {
        // The parent/framework article is also a valid backstory source:
        // its title carries "climate" and its extract carries the
        // mid-1990s / 2005 history the backstory needs.
        const string extract =
            "The United Nations Climate Change Conferences are yearly conferences held as part of the United Nations " +
            "Framework Convention on Climate Change (UNFCCC). They serve as the formal meeting of the UNFCCC parties " +
            "to assess progress in dealing with climate change, and beginning in the mid-1990s, to negotiate the " +
            "Kyoto Protocol. Starting in 2005 the conferences have also served as the Conference of the Parties " +
            "Serving as the Meeting of Parties to the Kyoto Protocol.";
        var accepted = ReflectionHelper.Invoke<bool>(
            ReflectionHelper.VideoGen, "BackgroundMatchesTopic",
            "United Nations Climate Change Conference", extract, "COP31 climate talks fail");
        Assert.True(accepted);
    }

    // --- (e) the wiring ---------------------------------------------------

    [Fact]
    public void Wiring_ForcedTopicPrep_FetchesGuardedBackgrounds()
    {
        // Source-scan guards: the forced-topic branch in RunPrepAsync must
        // fetch the guarded plural backgrounds (FetchBackgroundForPickedTopicAsync),
        // not the unguarded single summary — and the dry-script path must
        // do the same, so a dry run measures the backstory fix.
        var text = File.ReadAllText(ProgramPath);
        Assert.Contains("FetchBackgroundForPickedTopicAsync(topic, null, PrepContext.Current)", text);
        Assert.DoesNotContain(text, "FetchSummaryAsync(topic)");
        // Both call sites present (prep + dry-script).
        var first = text.IndexOf("FetchBackgroundForPickedTopicAsync(topic, null, PrepContext.Current)", StringComparison.Ordinal);
        var second = text.IndexOf("FetchBackgroundForPickedTopicAsync(topic, null, PrepContext.Current)", first + 1, StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first,
            "both the prep path and the dry-script path must fetch the guarded backgrounds for a forced topic");
        // And the forced-topic marker is what carries them (so the gate
        // keeps the block via forcedTopicBlock).
        Assert.Contains("Wikipedia context for this topic (use real facts from here, not invented ones):", text);
    }

    [Fact]
    public void Wiring_BuildCheckContext_IncludesBothBlocks()
    {
        // Source guard for the gate fix: the else-if that dropped the
        // forced-topic block when a picked background existed must be gone
        // — both blocks are appended now.
        var text = File.ReadAllText(FactPath);
        Assert.DoesNotContain(text, "else if (forcedTopicBlock is not null)");
        Assert.Contains("if (forcedTopicBlock is not null)", text);
    }
}
