// Tests for the fact-gate rewrite pass (Rishi 2026-11-23): the gate still
// RUNS and still finds untraceable claims, but FinishPrepAsync rewrites
// them into attributed speculation instead of FATAL-ing the prep.
//
// Seams under test (no network — fixed inputs, reflection into VideoGen):
//   (a) template reframing of a flagged number/date claim;
//   (b) template reframing of a flagged attribution claim (non-person);
//   (c) a named-person claim is DROPPED, never reframed (rule 3);
//   (d) the speculative flag + badge path (IsSpeculative set, a data
//       visual demoted to art);
//   (e) the re-check exemption: a speculative scene with a recognised
//       attribution phrase passes, a named-person attribution does not;
//   (f) UntraceableSource handling: source stripped (narration kept), and
//       a named-person quote resting on an untraceable source drops the
//       scene;
//   (g) floor behaviour after drops (BelowFloor flag; repair wiring is a
//       source scan — the RepairThinActAsync append itself is covered by
//       LengthFixTests);
//   (h) the old hard-fail path is gone for news profiles (source scan);
//   (i) end-to-end on the two real gate-failed partials (Ukraine,
//       COP31): exact scene counts, drops, reframes, and a clean re-check.
using System.Reflection;
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class FactGateRewriteTests
{
    private static readonly DateTime PrepDate = new(2026, 11, 23);

    private const string UkrainePartialPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261123-150439/script-partial.json";
    private const string UkraineSnapshotPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261123-150439/trend-snapshot.txt";
    private const string UkrainePickedPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261123-150439/picked-topic-background.txt";

    private const string Cop31PartialPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261123-083645/script-partial.json";
    private const string Cop31SnapshotPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261123-083645/trend-snapshot.txt";
    private const string Cop31PickedPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261123-083645/picked-topic-background.txt";

    // --- reflection seams ---------------------------------------------------

    private static object RewriteScript(object script, List<string> findings, string context, DateTime prepDate, PrepIdentity? identity = null)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "FactGateRewrite");
        var method = type.GetMethod("Rewrite", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMethodException("FactGateRewrite.Rewrite");
        return method.Invoke(null, [script, findings, context, prepDate, identity, "rewrite"])!;
    }

    private static T ResultProp<T>(object result, string name) =>
        (T)result.GetType().GetProperty(name)!.GetValue(result)!;

    private static bool IsNamedPerson(string source) =>
        ReflectionHelper.Invoke<bool>(ReflectionHelper.VideoGen, "IsNamedPerson", source);

    private static bool ContainsAttributionPhrase(string narration) =>
        ReflectionHelper.Invoke<bool>(ReflectionHelper.VideoGen, "ContainsAttributionPhrase", narration);

    private static object DeserializeScript(string json)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "VideoScript");
        return JsonSerializer.Deserialize(json, type, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static string SerializeScript(object script) => JsonSerializer.Serialize(script);

    private static List<string> CheckJson(string scriptJson, string? context) =>
        ReflectionHelper.Invoke<List<string>>(
            ReflectionHelper.VideoGen, "CheckJson", scriptJson, context, PrepDate);

    private static List<string> CheckScript(object script, string context) =>
        CheckJson(SerializeScript(script), context);

    private static JsonElement SceneAt(string scriptJson, int index) =>
        JsonDocument.Parse(scriptJson).RootElement.GetProperty("Scenes")[index];

    private static List<string> Narrations(string scriptJson) =>
        JsonDocument.Parse(scriptJson).RootElement.GetProperty("Scenes")
            .EnumerateArray().Select(s => s.GetProperty("Narration").GetString()!).ToList();

    // --- script builders ----------------------------------------------------

    private static string SceneJson(string narration, string sceneKind = "art", string? visualJson = null, bool speculative = false) =>
        "{\"Narration\": " + JsonSerializer.Serialize(narration) +
        ", \"SceneKind\": " + JsonSerializer.Serialize(sceneKind) +
        ", \"IsSpeculative\": " + (speculative ? "true" : "false") +
        ", \"VisualSpec\": " + (visualJson ?? "null") + "}";

    private static string ScriptJson(bool longForm, params string[] sceneJsons) =>
        "{\"Title\": \"Test\", \"IsLongForm\": " + (longForm ? "true" : "false") +
        ", \"Scenes\": [" + string.Join(",", sceneJsons) + "], \"BriefScenes\": [], \"Brief\": \"\"}";

    private static string CalloutSpec(string text, string source, string subtext = "") =>
        "{\"Kind\": \"callout\", \"Title\": \"\", \"Labels\": [], \"Values\": [], \"Unit\": \"\", " +
        "\"Headers\": [], \"Rows\": [], \"Text\": " + JsonSerializer.Serialize(text) +
        ", \"Subtext\": " + JsonSerializer.Serialize(subtext) +
        ", \"Source\": " + JsonSerializer.Serialize(source) + ", \"Accent\": null}";

    private static string TraceableFiller(int n) =>
        // No numbers in the narration (a "Scene 10" would flag as an
        // untraceable number), and ~135 chars so 50 scenes clear the
        // 5,500-char floor.
        SceneJson("This scene carries the story forward with a verified fact from the supplied report, adding useful context and detail for the viewer.");

    // A rewrite of one scene: run the gate, rewrite, re-check, and return
    // the rewritten script JSON plus the rewrite result.
    private static (string ScriptJson, object Result) RewriteOnce(string scriptJson, string context)
    {
        var script = DeserializeScript(scriptJson);
        var findings = CheckScript(script, context);
        Assert.NotEmpty(findings);
        var result = RewriteScript(script, findings, context, PrepDate);
        var rewritten = SerializeScript(script);
        Assert.Empty(CheckJson(rewritten, context));
        return (rewritten, result);
    }

    // --- (c) named-person classification ------------------------------------

    [Fact]
    public void IsNamedPerson_PersonNames_ReturnsTrue()
    {
        Assert.True(IsNamedPerson("Zelenskiy"));
        Assert.True(IsNamedPerson("Darragh O'Brien"));
        Assert.True(IsNamedPerson("Guterres"));
        Assert.True(IsNamedPerson("Antonio Guterres"));
        Assert.True(IsNamedPerson("As Guterres"));
        Assert.True(IsNamedPerson("President Zelenskiy"));
    }

    [Fact]
    public void IsNamedPerson_OrgsGroupsOutlets_ReturnsFalse()
    {
        Assert.False(IsNamedPerson("NATO never"));
        Assert.False(IsNamedPerson("NATO"));
        Assert.False(IsNamedPerson("Rockstar Games"));
        Assert.False(IsNamedPerson("GameSpot"));
        Assert.False(IsNamedPerson("Maritime insurers"));
        Assert.False(IsNamedPerson("Ireland's environment minister"));
        Assert.False(IsNamedPerson("UNFCCC rules"));
        Assert.False(IsNamedPerson("Social media"));
        Assert.False(IsNamedPerson("Irish statement"));
        Assert.False(IsNamedPerson("critics"));
        Assert.False(IsNamedPerson("Ukraine"));
        Assert.False(IsNamedPerson("United Nations"));
    }

    [Fact]
    public void ContainsAttributionPhrase_RecognisedPhrases()
    {
        Assert.True(ContainsAttributionPhrase("Reports suggest the drones moved."));
        Assert.True(ContainsAttributionPhrase("People are discussing whether the deal holds."));
        Assert.True(ContainsAttributionPhrase("It remains unclear whether the annex passes."));
        Assert.True(ContainsAttributionPhrase("The theory going around is that the summit failed."));
        Assert.False(ContainsAttributionPhrase("The attacks happened at dawn."));
        Assert.False(ContainsAttributionPhrase("NATO confirmed the operation."));
    }

    // --- (a) number / date reframing -----------------------------------------

    [Fact]
    public void Reframe_NumberCentury_ReframedAsSpeculation()
    {
        // Ukraine scene 7, verbatim: the "19" in "19th-century" is the only
        // untraceable token; "historic" is already there, so the century
        // is deleted rather than doubled.
        const string context =
            "Russian ships were struck by drones near Libya. The Merkury is a Russian ship. " +
            "Russia's fleet operates in the Black Sea.";
        var json = ScriptJson(false,
            TraceableFiller(1),
            SceneJson("The Merkury was a recent addition to Russia’s fleet, named after a historic 19th-century ship."));
        var (rewritten, result) = RewriteOnce(json, context);
        Assert.Contains(2, ResultProp<List<int>>(result, "ReframedScenes"));
        var scene = SceneAt(rewritten, 1);
        Assert.Equal("Reports suggest the Merkury was a recent addition to Russia's fleet, named after a historic ship.",
            scene.GetProperty("Narration").GetString());
        Assert.True(scene.GetProperty("IsSpeculative").GetBoolean());
    }

    [Fact]
    public void Reframe_NumberYearBeforeNoun_ReframedAsRecent()
    {
        // COP31 scene 25, verbatim: "The 2022 meeting" -> "the recent meeting".
        const string context =
            "The meeting in Sharm El-Sheikh saw parties sign a net-zero pledge, yet the text avoided firm fossil-fuel cuts.";
        var json = ScriptJson(false,
            TraceableFiller(1),
            SceneJson("The 2022 meeting in Sharm El-Sheikh saw a record number of parties sign a net-zero pledge, yet the text still avoided firm fossil-fuel cuts."));
        var (rewritten, result) = RewriteOnce(json, context);
        Assert.Contains(2, ResultProp<List<int>>(result, "ReframedScenes"));
        var scene = SceneAt(rewritten, 1);
        Assert.Equal("Reports suggest the recent meeting in Sharm El-Sheikh saw a record number of parties sign a net-zero pledge, yet the text still avoided firm fossil-fuel cuts.",
            scene.GetProperty("Narration").GetString());
        Assert.True(scene.GetProperty("IsSpeculative").GetBoolean());
    }

    [Fact]
    public void Reframe_NumberEarlyDecade_ReframedAsEarlierDecades()
    {
        // COP31 scene 30, verbatim: "the early 2000s" -> "earlier decades".
        const string context =
            "Break-away moments trace back decades, when developing nations first demanded financial support for adaptation.";
        var json = ScriptJson(false,
            TraceableFiller(1),
            SceneJson("Those break-away moments trace back to the early 2000s, when developing nations first demanded financial support for adaptation."));
        var (rewritten, _) = RewriteOnce(json, context);
        Assert.Equal("Reports suggest those break-away moments trace back to the earlier decades, when developing nations first demanded financial support for adaptation.",
            SceneAt(rewritten, 1).GetProperty("Narration").GetString());
        Assert.True(SceneAt(rewritten, 1).GetProperty("IsSpeculative").GetBoolean());
    }

    [Fact]
    public void Reframe_NumberTargetYear_ReframedAsYearsAhead()
    {
        // COP31 scene 49, verbatim: "targets for 2050" -> "targets for the
        // years ahead". Only "2050" was flagged — "three" is a single
        // digit, which the gate ignores, and it stays untouched.
        const string context =
            "The push has forced three major oil companies to announce net-zero targets, though critics say the plans lack detail.";
        var json = ScriptJson(false,
            TraceableFiller(1),
            SceneJson("The push has already forced three major oil companies to announce net-zero targets for 2050, though critics say the plans lack detail."));
        var (rewritten, _) = RewriteOnce(json, context);
        var narration = SceneAt(rewritten, 1).GetProperty("Narration").GetString()!;
        Assert.Equal("Reports suggest the push has already forced three major oil companies to announce net-zero targets for the years ahead, though critics say the plans lack detail.",
            narration);
        Assert.Contains("three", narration);
        Assert.DoesNotContain("2050", narration);
    }

    // --- (b) attribution reframing (non-person) ------------------------------

    [Fact]
    public void Reframe_AttributionOrg_NegatedConfirmationReframed()
    {
        // Ukraine scene 23, verbatim: "NATO never confirmed ..." is an
        // attribution to an organisation, not a person, so it is reframed
        // (open question + reported tail) rather than dropped.
        const string context =
            "Ukraine's drones struck Russian ships near Libya. The strategy raised questions about borders and escalation.";
        var json = ScriptJson(false,
            TraceableFiller(1),
            SceneJson("This strategy worked, but it also raised questions. Was Ukraine operating inside NATO territory? Were alliance members complicit? NATO never confirmed any joint operations, but the drones were undeniably crossing borders, and the lines between defense and escalation blurred."));
        var (rewritten, result) = RewriteOnce(json, context);
        Assert.Contains(2, ResultProp<List<int>>(result, "ReframedScenes"));
        var scene = SceneAt(rewritten, 1);
        Assert.Equal("This strategy worked, but it also raised questions. Was Ukraine operating inside NATO territory? Were alliance members complicit? It remains unclear whether any joint operations took place. Reports suggest the drones were crossing borders, and the lines between defense and escalation blurred.",
            scene.GetProperty("Narration").GetString());
        Assert.True(scene.GetProperty("IsSpeculative").GetBoolean());
    }

    [Fact]
    public void Attribution_GenericSource_NotFlagged()
    {
        const string context =
            "The attacks prove Russia has no safe routes, even beyond the Black Sea. Analysts discussed the strikes.";
        var json = ScriptJson(false,
            TraceableFiller(1),
            SceneJson("Analysts said the attacks prove Russia has no safe routes, even beyond the Black Sea."));
        // "Analysts" is generic, so the gate does not flag it at all — the
        // rewrite is a no-op and the scene passes untouched.
        var script = DeserializeScript(json);
        Assert.Empty(CheckScript(script, context));
    }

    // --- (c) named-person claims are dropped, never reframed -----------------

    [Fact]
    public void Rewrite_NamedPersonAttribution_SceneDropped_NotReframed()
    {
        // Ukraine scenes 6 and 42, verbatim: the whole scene IS the
        // attribution to Zelenskiy, so dropping the sentence drops the scene.
        const string context =
            "Drone attacks struck Russian ships near Libya. Russia has naval routes beyond the Black Sea.";
        var json = ScriptJson(false,
            TraceableFiller(1),
            SceneJson("Zelenskiy said the attacks prove Russia has no safe routes, even beyond the Black Sea."),
            TraceableFiller(3));
        var (rewritten, result) = RewriteOnce(json, context);
        Assert.Contains(2, ResultProp<List<int>>(result, "DroppedScenes"));
        Assert.Empty(ResultProp<List<int>>(result, "ReframedScenes"));
        var narrations = Narrations(rewritten);
        Assert.Equal(2, narrations.Count);
        Assert.DoesNotContain(narrations, n => n.Contains("Zelenskiy said"));
    }

    [Fact]
    public void Rewrite_NamedPersonAttribution_MultiSentenceScene_KeepsRest()
    {
        // When only one sentence of a scene attributes to a named person,
        // that sentence is dropped and the rest of the scene survives.
        const string context =
            "Drone attacks struck Russian ships near Libya. The fleet was already stretched thin before the strikes.";
        var json = ScriptJson(false,
            SceneJson("The fleet was already stretched thin before the strikes. Zelenskiy said the attacks prove Russia has no safe routes. The Black Sea routes were the first to close."));
        var (rewritten, result) = RewriteOnce(json, context);
        Assert.Empty(ResultProp<List<int>>(result, "DroppedScenes"));
        Assert.Equal("The fleet was already stretched thin before the strikes. The Black Sea routes were the first to close.",
            Narrations(rewritten)[0]);
        Assert.DoesNotContain(Narrations(rewritten)[0], "Zelenskiy");
    }

    // --- (d) speculative flag + badge path -----------------------------------

    [Fact]
    public void Rewrite_ReframedVisualScene_DemotedToArt()
    {
        // A "visual" scene reframed as speculation loses its chart (the
        // existing rule: a theory never gets a chart or callout) and is
        // flagged IsSpeculative, which is what VideoAssembler passes to
        // Remotion for the red SPECULATION badge.
        const string context =
            "The fleet operates in the Black Sea. Axios reported on the naval build-up.";
        var json = ScriptJson(false,
            SceneJson("The fleet grew by 40 ships in a single month, a record pace.", "visual",
                "{\"Kind\": \"callout\", \"Title\": \"\", \"Labels\": [], \"Values\": [], \"Unit\": \"\", " +
                "\"Headers\": [], \"Rows\": [], \"Text\": \"A big number\", \"Subtext\": \"\", " +
                "\"Source\": \"Axios, 2026-11-22\", \"Accent\": null}"));
        var (rewritten, _) = RewriteOnce(json, context);
        var scene = SceneAt(rewritten, 0);
        Assert.True(scene.GetProperty("IsSpeculative").GetBoolean());
        Assert.Equal("art", scene.GetProperty("SceneKind").GetString());
        Assert.Equal(JsonValueKind.Null, scene.GetProperty("VisualSpec").ValueKind);
        Assert.Contains("several ships", scene.GetProperty("Narration").GetString()!);
    }

    [Fact]
    public void FactGateRewrite_SummaryIncludesDemotedCount()
    {
        // L72: a script with one untraceable visual number must demote the
        // visual, log the demotion with job + scene inside Rewrite() (no
        // caller loop needed), and carry the demoted count in the OK
        // summary line.
        const string context = "The market kept growing through the year.";
        var json = ScriptJson(false,
            TraceableFiller(1),
            SceneJson("The market kept growing through the year, with more buyers joining every month.",
                "visual", CalloutSpec("30% growth", "")));
        var script = DeserializeScript(json);
        var findings = CheckScript(script, context);
        Assert.NotEmpty(findings);
        var sb = new StringWriter();
        var oldOut = Console.Out;
        Console.SetOut(sb);
        object result;
        try
        {
            result = RewriteScript(script, findings, context, PrepDate,
                new PrepIdentity("job-20261126-test", "Test topic"));
        }
        finally
        {
            Console.SetOut(oldOut);
        }
        Assert.Contains(2, ResultProp<List<int>>(result, "DemotedScenes"));
        var log = sb.ToString();
        Assert.Contains("scene 2", log);
        Assert.Contains("demoted", log);
        Assert.Contains("job-20261126-test", log);
        var summary = ReflectionHelper.Invoke<string>(ReflectionHelper.VideoGen, "OkSummary",
            result, ResultProp<int>(result, "SceneCount"));
        Assert.Contains("1 visual(s) demoted to art", summary);
    }

    // --- (e) re-check exemption ----------------------------------------------

    [Fact]
    public void Recheck_SpeculativeOrgAttribution_Passes()
    {
        // The exemption: a speculative scene with a recognised attribution
        // phrase passes the attribution check (the label IS the traceability).
        const string context = "Joint operations were discussed in the region.";
        var json = ScriptJson(false,
            SceneJson("Reports suggest NATO confirmed joint operations in the region.", "art", null, speculative: true));
        Assert.Empty(CheckJson(json, context));
    }

    [Fact]
    public void Recheck_SpeculativeNamedPersonAttribution_StillFlagged()
    {
        // ...but the exemption never covers a named person (rule 3): the
        // finding survives, and the rewrite then drops the sentence.
        const string context = "The strikes were confirmed by officials in the region.";
        var json = ScriptJson(false,
            SceneJson("Reports suggest Zelenskiy confirmed the strikes.", "art", null, speculative: true));
        var findings = CheckJson(json, context);
        Assert.Contains(findings, f => f.Contains("UntraceableAttribution") && f.Contains("Zelenskiy"));
        var (rewritten, result) = RewriteOnce(json, context);
        Assert.Contains(1, ResultProp<List<int>>(result, "DroppedScenes"));
        Assert.Empty(Narrations(rewritten));
    }

    // --- (f) UntraceableSource handling --------------------------------------

    [Fact]
    public void Rewrite_UntraceableSource_SourceStripped_NarrationKept()
    {
        // COP31 scene 20: only the callout source is untraceable — the
        // narration has no flagged number or attribution. The source is
        // stripped, the callout and narration stay.
        const string context =
            "COP climate talks in Antalya ended without a deal. Delegates argued over fossil fuels.";
        var json = ScriptJson(false,
            SceneJson("Every final COP text must be approved by consensus – a single dissent can stall the whole document.",
                "art", CalloutSpec("Consensus required for any COP agreement", "UNFCCC rules")));
        var (rewritten, result) = RewriteOnce(json, context);
        Assert.Contains(1, ResultProp<List<int>>(result, "SourceFixedScenes"));
        Assert.Empty(ResultProp<List<int>>(result, "DroppedScenes"));
        var scene = SceneAt(rewritten, 0);
        Assert.Equal("Every final COP text must be approved by consensus – a single dissent can stall the whole document.",
            scene.GetProperty("Narration").GetString());
        Assert.Equal("", scene.GetProperty("VisualSpec").GetProperty("Source").GetString());
        Assert.Equal("Consensus required for any COP agreement",
            scene.GetProperty("VisualSpec").GetProperty("Text").GetString());
    }

    [Fact]
    public void Rewrite_UntraceableSource_SpeculativeScene_NarrationUntouched()
    {
        // COP31 scene 42, the surgical test: already IsSpeculative and
        // already framed as online theory — only the callout source
        // ("Social media") is fixed; the narration is NOT re-narrated.
        const string context =
            "The UN may allow an annex focused on fossil-fuel phase-out. Delegates discussed the proposal online.";
        var json = ScriptJson(false,
            SceneJson("A theory circulating online suggests the UN may allow a “fast-track” annex focused solely on fossil-fuel phase-out.",
                "art", CalloutSpec("Fast-track annex theory", "Social media", "Online speculation"), speculative: true));
        var (rewritten, result) = RewriteOnce(json, context);
        Assert.Contains(1, ResultProp<List<int>>(result, "SourceFixedScenes"));
        var scene = SceneAt(rewritten, 0);
        Assert.Equal("A theory circulating online suggests the UN may allow a “fast-track” annex focused solely on fossil-fuel phase-out.",
            scene.GetProperty("Narration").GetString());
        Assert.True(scene.GetProperty("IsSpeculative").GetBoolean());
        Assert.Equal("", scene.GetProperty("VisualSpec").GetProperty("Source").GetString());
    }

    [Fact]
    public void Rewrite_NamedPersonQuoteOnUntraceableSource_SceneDropped()
    {
        // COP31 scene 39: the callout quotes a NAMED PERSON (Darragh
        // O'Brien) and rests on the untraceable source "Irish statement",
        // and the narration IS that quote — rule 3 drops the scene.
        // (Darragh O'Brien is IN the context, so the quote itself traces;
        // only the source is untraceable — exactly the live case.)
        const string context =
            "Ireland’s environment minister, Darragh O’Brien, speaking for the EU, said the text was a missed opportunity. " +
            "Delegates argued over fossil-fuel cuts in Antalya.";
        var json = ScriptJson(false,
            SceneJson("Ireland’s environment minister reiterated his earlier criticism, calling any text that omits fossil-fuel cuts a “missed opportunity.”",
                "art", CalloutSpec("“missed opportunity” – Darragh O’Brien", "Irish statement")));
        var script = DeserializeScript(json);
        var findings = CheckScript(script, context);
        Assert.Contains(findings, f => f.Contains("UntraceableSource") && f.Contains("Irish statement"));
        var result = RewriteScript(script, findings, context, PrepDate);
        var rewritten = SerializeScript(script);
        Assert.Empty(CheckJson(rewritten, context));
        Assert.Contains(1, ResultProp<List<int>>(result, "DroppedScenes"));
        Assert.Empty(Narrations(rewritten));
    }

    [Fact]
    public void Rewrite_FutureDatedSource_DateStripped()
    {
        // The outlet (GameSpot) IS in the context, so the quote traces and
        // the only finding is the future-dated source (2026-11-24, after
        // the 2026-11-23 prep date). The date is stripped, the source and
        // the callout survive, and the narration is untouched.
        const string context =
            "GameSpot reviewed the new Vice City. Axios reported on the launch.";
        var json = ScriptJson(false,
            SceneJson("Critics praised the dynamic weather in the new Vice City.",
                "art", CalloutSpec("\u201cThe city feels alive,\u201d \u2013 GameSpot review", "GameSpot, 2026-11-24")));
        var script = DeserializeScript(json);
        var findings = CheckScript(script, context);
        Assert.Single(findings);
        Assert.Contains(findings, f => f.Contains("FutureDated"));
        var result = RewriteScript(script, findings, context, PrepDate);
        var rewritten = SerializeScript(script);
        Assert.Empty(CheckJson(rewritten, context));
        Assert.Empty(ResultProp<List<int>>(result, "DroppedScenes"));
        var scene = SceneAt(rewritten, 0);
        Assert.Equal("GameSpot", scene.GetProperty("VisualSpec").GetProperty("Source").GetString());
        Assert.Equal("\u201cThe city feels alive,\u201d \u2013 GameSpot review",
            scene.GetProperty("VisualSpec").GetProperty("Text").GetString());
        Assert.Equal("Critics praised the dynamic weather in the new Vice City.",
            scene.GetProperty("Narration").GetString());
        Assert.False(scene.GetProperty("IsSpeculative").GetBoolean());
    }

    // --- (g) floor behaviour -------------------------------------------------

    [Fact]
    public void Rewrite_DropsAboveFloor_BelowFloorFalse()
    {
        // 51 scenes, one named-person drop -> 50 scenes: exactly at the
        // floor, no repair needed.
        var scenes = Enumerable.Range(1, 50).Select(TraceableFiller).ToList();
        scenes.Add(SceneJson("Zelenskiy said the attacks prove Russia has no safe routes."));
        const string context = "Drone attacks struck Russian ships near Libya. The fleet was already stretched thin.";
        var json = ScriptJson(true, scenes.ToArray());
        var script = DeserializeScript(json);
        var findings = CheckScript(script, context);
        Assert.Single(findings);
        var result = RewriteScript(script, findings, context, PrepDate);
        Assert.Empty(CheckJson(SerializeScript(script), context));
        Assert.False(ResultProp<bool>(result, "BelowFloor"));
        Assert.Equal(50, ResultProp<int>(result, "SceneCount"));
        Assert.True(ResultProp<int>(result, "TotalChars") > 5500);
    }

    [Fact]
    public void Rewrite_DropsBelowFloor_BelowFloorTrue()
    {
        // 50 scenes, one named-person drop -> 49 scenes: below the floor,
        // so the caller must repair (RepairThinActAsync) instead of
        // proceeding silently.
        var scenes = Enumerable.Range(1, 49).Select(TraceableFiller).ToList();
        scenes.Add(SceneJson("Zelenskiy said the attacks prove Russia has no safe routes."));
        const string context = "Drone attacks struck Russian ships near Libya. The fleet was already stretched thin.";
        var json = ScriptJson(true, scenes.ToArray());
        var script = DeserializeScript(json);
        var findings = CheckScript(script, context);
        Assert.Single(findings);
        var result = RewriteScript(script, findings, context, PrepDate);
        Assert.Empty(CheckJson(SerializeScript(script), context));
        Assert.True(ResultProp<bool>(result, "BelowFloor"));
        Assert.Equal(49, ResultProp<int>(result, "SceneCount"));
    }

    [Fact]
    public void FloorRepair_WiredIntoFinishPrep()
    {
        // The repair path is wired, not just documented: FinishPrepAsync
        // calls RepairAfterGateDropsAsync when the rewrite leaves the
        // script below the floor, and that helper regenerates scenes
        // through RepairThinActAsync (the thin-act path).
        const string path =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/Program.cs";
        var text = File.ReadAllText(path);
        var finishPrep = text.IndexOf("FinishPrepAsync(string workDir", StringComparison.Ordinal);
        Assert.True(finishPrep >= 0);
        var gate = text.IndexOf("FactGateRewrite.Rewrite(script", finishPrep, StringComparison.Ordinal);
        Assert.True(gate > finishPrep, "FinishPrepAsync must call FactGateRewrite.Rewrite after the gate finds claims.");
        var repair = text.IndexOf("RepairAfterGateDropsAsync(script", finishPrep, StringComparison.Ordinal);
        Assert.True(repair > gate, "FinishPrepAsync must repair below-floor scripts after the rewrite.");
        var helper = text.IndexOf("RepairAfterGateDropsAsync(VideoScript script", StringComparison.Ordinal);
        Assert.True(helper >= 0);
        Assert.Contains("RepairThinActAsync(script.Scenes", text[helper..]);
    }

    [Fact]
    public void FloorRepair_RepairChunkIsCappedAtMissing()
    {
        // Regression: a 1-scene repair once asked for a FULL act (the
        // continuation prompt picks its count line by whether
        // actScenesSoFar is empty, and the repair passed []) and the model
        // returned 20 scenes, bloating COP31 to 70. The repair must pass a
        // non-empty act-so-far (so the prompt asks for exactly `missing`)
        // AND truncate the returned chunk to `missing` as a safety net.
        const string path =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/Program.cs";
        var text = File.ReadAllText(path);
        var helper = text.IndexOf("RepairAfterGateDropsAsync(VideoScript script", StringComparison.Ordinal);
        Assert.True(helper >= 0);
        var region = text[helper..];
        Assert.Contains("Take(missing)", region);
        Assert.DoesNotContain("repairProvider, script, [],", region);
    }

    // --- (h) the old hard-fail path is gone ---------------------------------

    [Fact]
    public void HardFailGone_FindingsRewriteInsteadOfThrowing()
    {
        // The findings branch in FinishPrepAsync must rewrite (not throw
        // the old InvalidOperationException report), while the no-context
        // guard — zero source material, a different failure mode — still
        // fails loudly.
        const string path =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/Program.cs";
        var text = File.ReadAllText(path);
        var finishPrep = text.IndexOf("FinishPrepAsync(string workDir", StringComparison.Ordinal);
        var firstSave = text.IndexOf("await SaveScriptAsync(workDir, script);", finishPrep, StringComparison.Ordinal);
        var block = text[finishPrep..firstSave];
        Assert.Contains("FactGateRewrite.Rewrite(script", block);
        Assert.DoesNotContain("throw new InvalidOperationException(report)", block);
        Assert.Contains("no trend context available", block);
        Assert.Contains("throw new InvalidOperationException(noContextReport)", block);
        Assert.Contains("fact-check-rewritten.txt", block);
    }

    [Fact]
    public void RewriteGateCli_Exists()
    {
        // The one-off path for gate-failed partials: --rewrite-gate loads
        // script-partial.json, runs gate+rewrite from the saved snapshot,
        // and saves script.json — with no TTS, visuals, render, or upload.
        const string path =
            "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/Program.cs";
        var text = File.ReadAllText(path);
        Assert.Contains("\"--rewrite-gate\"", text);
        Assert.Contains("RunRewriteGateAsync()", text);
        Assert.Contains("LoadPartialScriptAsync(workDir)", text);
        Assert.Contains("trend-snapshot.txt", text);
        // Floors are enforced on the final shape (after intro + dedup):
        // the CLI repairs a below-floor script through the same path
        // FinishPrepAsync uses, counting story scenes only (introScenes: 1).
        Assert.Contains("RepairAfterGateDropsAsync(partial, checkContext, prepDate, trendContext, introScenes: 1)", text);
    }

    // --- (i) end-to-end on the real gate-failed partials ---------------------

    private static string RealCheckContext(string snapshotPath, string pickedPath)
    {
        var snapshot = File.ReadAllText(snapshotPath);
        var picked = File.ReadAllText(pickedPath);
        return ReflectionHelper.Invoke<string>(
            ReflectionHelper.VideoGen, "BuildCheckContext", snapshot, picked);
    }

    [Fact]
    public void RealData_Ukraine_RewriteMatchesExpectedOutcome()
    {
        // job-20261123-150439: 58 scenes, 4 findings (scenes 6, 7, 23, 42).
        // Expected: scenes 6 + 42 DROPPED (Zelenskiy, rule 3); scenes 7 +
        // 23 REFRAMED as speculation. 56 scenes, chars above 5,500,
        // re-check clean.
        var scriptJson = File.ReadAllText(UkrainePartialPath);
        var context = RealCheckContext(UkraineSnapshotPath, UkrainePickedPath);
        var script = DeserializeScript(scriptJson);
        var findings = CheckScript(script, context);
        Assert.Equal(4, findings.Count);
        Assert.Contains(findings, f => f.StartsWith("scene 6 ", StringComparison.Ordinal) && f.Contains("UntraceableAttribution"));
        Assert.Contains(findings, f => f.StartsWith("scene 7 ", StringComparison.Ordinal) && f.Contains("UntraceableNumber"));
        Assert.Contains(findings, f => f.StartsWith("scene 23 ", StringComparison.Ordinal) && f.Contains("UntraceableAttribution"));
        Assert.Contains(findings, f => f.StartsWith("scene 42 ", StringComparison.Ordinal) && f.Contains("UntraceableAttribution"));

        var result = RewriteScript(script, findings, context, PrepDate);
        var rewritten = SerializeScript(script);
        Assert.Empty(CheckJson(rewritten, context));
        Assert.Empty(ResultProp<List<string>>(result, "RemainingFindings"));
        Assert.Equal([6, 42], ResultProp<List<int>>(result, "DroppedScenes").OrderBy(n => n).ToList());
        Assert.Equal([7, 23], ResultProp<List<int>>(result, "ReframedScenes").OrderBy(n => n).ToList());
        Assert.Equal(56, ResultProp<int>(result, "SceneCount"));
        Assert.True(ResultProp<int>(result, "TotalChars") > 5500);
        Assert.False(ResultProp<bool>(result, "BelowFloor"));

        var narrations = Narrations(rewritten);
        Assert.DoesNotContain(narrations, n => n.Contains("Zelenskiy said"));
        Assert.Contains(narrations, n => n ==
            "Reports suggest the Merkury was a recent addition to Russia's fleet, named after a historic ship.");
        Assert.Contains(narrations, n => n.Contains("It remains unclear whether any joint operations took place.") &&
            n.Contains("Reports suggest the drones were crossing borders, and the lines between defense and escalation blurred."));
    }

    [Fact]
    public void RealData_Cop31_RewriteMatchesExpectedOutcome()
    {
        // job-20261123-083645: 51 scenes, 6 findings (scenes 20, 25, 30,
        // 39, 42, 49). Expected: scene 39 DROPPED (Darragh O'Brien quote
        // on an untraceable source, rule 3); scenes 25, 30, 49 REFRAMED;
        // scenes 20 + 42 keep their narration with the visual source
        // stripped. 50 scenes, chars above 5,500, re-check clean.
        var scriptJson = File.ReadAllText(Cop31PartialPath);
        var context = RealCheckContext(Cop31SnapshotPath, Cop31PickedPath);
        var script = DeserializeScript(scriptJson);
        var findings = CheckScript(script, context);
        Assert.Equal(6, findings.Count);

        var result = RewriteScript(script, findings, context, PrepDate);
        var rewritten = SerializeScript(script);
        Assert.Empty(CheckJson(rewritten, context));
        Assert.Empty(ResultProp<List<string>>(result, "RemainingFindings"));
        Assert.Equal([39], ResultProp<List<int>>(result, "DroppedScenes"));
        Assert.Equal(50, ResultProp<int>(result, "SceneCount"));
        Assert.True(ResultProp<int>(result, "TotalChars") > 5500);
        Assert.False(ResultProp<bool>(result, "BelowFloor"));

        var narrations = Narrations(rewritten);
        Assert.DoesNotContain(narrations, n => n.Contains("missed opportunity"));
        Assert.Contains(narrations, n => n ==
            "Reports suggest the recent meeting in Sharm El-Sheikh saw a record number of parties sign a net-zero pledge, yet the text still avoided firm fossil-fuel cuts.");
        Assert.Contains(narrations, n => n ==
            "Reports suggest those break-away moments trace back to the earlier decades, when developing nations first demanded financial support for adaptation.");
        Assert.Contains(narrations, n => n ==
            "Reports suggest the push has already forced three major oil companies to announce net-zero targets for the years ahead, though critics say the plans lack detail.");
        // Scenes 20 + 42: narration untouched, visual source stripped.
        var doc = JsonDocument.Parse(rewritten);
        var scenes = doc.RootElement.GetProperty("Scenes");
        var consensus = scenes.EnumerateArray()
            .First(s => s.GetProperty("Narration").GetString()!
                .StartsWith("Every final COP text must be approved by consensus", StringComparison.Ordinal));
        Assert.Equal("", consensus.GetProperty("VisualSpec").GetProperty("Source").GetString());
        var theory = scenes.EnumerateArray()
            .First(s => s.GetProperty("Narration").GetString()!
                .StartsWith("A theory circulating online suggests", StringComparison.Ordinal));
        Assert.Equal("", theory.GetProperty("VisualSpec").GetProperty("Source").GetString());
        Assert.True(theory.GetProperty("IsSpeculative").GetBoolean());
    }
}
