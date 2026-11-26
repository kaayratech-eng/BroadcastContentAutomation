// Regression tests for fix sequence #2 ITEM 3 — one fresh visual per scene
// + hook audible inside 5 seconds in the long-form.
//
// Video #3 shipped 21 distinct visuals across 40 scenes (16 VisualGroups,
// longest run 5 scenes / 42.6s on one image; scenes 1-3 shared one image for
// 24.6s) and its hook landed at ~15.6s (12.1s brief + 3.5s sting). The fix:
//   * every scene gets its own VisualGroup (script time, enforced in
//     ScriptGenerator.EnsureDistinctVisualGroups) and the prep visual loop
//     never reuses the previous scene's resolved image/clip;
//   * the 3.5s intro sting and the 10-20s brief are GONE (Rishi 2026-11-25
//     13:55 EST: "we do not need to use the sting at all" — videos open
//     directly with the hook, scene 1, at 0:00). The timeline helpers still
//     accept brief/intro parameters so old scripts (videos #1-#4, COP31)
//     keep their correct timestamps; the new assemble path passes empty/0.
//   * the hook window is now the first 10-20 seconds (first 2-3 scenes):
//     the story's most entertaining peak moment must land inside it, and
//     the opening frames a bigger promise that pays off at the end.
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class VisualCadenceTests
{
    private const string RepoRoot = "/home/hatch/workspace/BroadcastContentAutomation";
    private const string Video3ScriptPath = RepoRoot + "/work/trendlinenow/job-20261122-115430/script.json";

    private static readonly Type SceneType =
        ReflectionHelper.FindType(ReflectionHelper.VideoGen, "Scene");

    private static readonly Type SpecType =
        ReflectionHelper.FindType(ReflectionHelper.VideoGen, "VisualSpec");

    private static readonly Type AssemblerType =
        ReflectionHelper.FindType(ReflectionHelper.VideoGen, "VideoAssembler");

    private static double AssemblerConst(string name)
    {
        var field = AssemblerType.GetField(name,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException($"VideoAssembler.{name}");
        return (double)field.GetValue(null)!;
    }

    private static double TransitionSeconds => AssemblerConst("TransitionSeconds");

    // One rendered segment = narration + 0.5s breathing room + hold.
    private static double Rendered(double narrationSeconds) =>
        narrationSeconds + 0.5 + AssemblerConst("PostNarrationHoldSeconds");

    private static object MakeSpec(string kind, string? title)
    {
        // VisualSpec is a positional record: (Kind, Title, Labels, Values,
        // Unit, Headers, Rows, Text, Subtext, Source, Accent).
        var ctor = SpecType.GetConstructors().Single();
        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = kind;
        args[1] = title;
        return ctor.Invoke(args);
    }

    private static Array MakeScenes(params (string Narration, double Duration, int Group, string Kind, object? Spec)[] defs)
    {
        var arr = Array.CreateInstance(SceneType, defs.Length);
        for (var i = 0; i < defs.Length; i++)
        {
            var scene = Activator.CreateInstance(SceneType, nonPublic: true)!;
            SceneType.GetProperty("Narration")!.SetValue(scene, defs[i].Narration);
            SceneType.GetProperty("DurationSeconds")!.SetValue(scene, defs[i].Duration);
            SceneType.GetProperty("VisualGroup")!.SetValue(scene, defs[i].Group);
            SceneType.GetProperty("SceneKind")!.SetValue(scene, defs[i].Kind);
            if (defs[i].Spec is not null)
                SceneType.GetProperty("VisualSpec")!.SetValue(scene, defs[i].Spec);
            arr.SetValue(scene, i);
        }
        return arr;
    }

    private static int[] GroupsOf(Array scenes)
    {
        var groups = new int[scenes.Length];
        for (var i = 0; i < scenes.Length; i++)
            groups[i] = (int)SceneType.GetProperty("VisualGroup")!.GetValue(scenes.GetValue(i)!)!;
        return groups;
    }

    private static void EnsureDistinctVisualGroups(Array scenes) =>
        ReflectionHelper.FindMethod(ReflectionHelper.VideoGen, "EnsureDistinctVisualGroups")
            .Invoke(null, [scenes]);

    private static int LongestRun(int[] groups)
    {
        var best = 0;
        var run = 0;
        var prev = int.MinValue;
        foreach (var g in groups)
        {
            run = g == prev ? run + 1 : 1;
            prev = g;
            best = Math.Max(best, run);
        }
        return best;
    }

    private static (int[] Groups, string[] Kinds, double[] Durations, double[] BriefDurations)? LoadVideo3()
    {
        if (!File.Exists(Video3ScriptPath))
        {
            Console.WriteLine($"SKIP: video #3 script.json not found at {Video3ScriptPath}");
            return null;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Video3ScriptPath));
        var root = doc.RootElement;
        var scenes = root.GetProperty("Scenes");
        var groups = new int[scenes.GetArrayLength()];
        var kinds = new string[scenes.GetArrayLength()];
        var durations = new double[scenes.GetArrayLength()];
        for (var i = 0; i < scenes.GetArrayLength(); i++)
        {
            groups[i] = scenes[i].GetProperty("VisualGroup").GetInt32();
            kinds[i] = scenes[i].GetProperty("SceneKind").GetString()!;
            durations[i] = scenes[i].GetProperty("DurationSeconds").GetDouble();
        }
        var brief = root.GetProperty("BriefScenes");
        var briefDurations = new double[brief.GetArrayLength()];
        for (var i = 0; i < brief.GetArrayLength(); i++)
            briefDurations[i] = brief[i].GetProperty("DurationSeconds").GetDouble();
        return (groups, kinds, durations, briefDurations);
    }

    private static Array MakeBriefScenes(params (string Narration, double Duration)[] defs)
    {
        var arr = Array.CreateInstance(SceneType, defs.Length);
        for (var i = 0; i < defs.Length; i++)
        {
            var scene = Activator.CreateInstance(SceneType, nonPublic: true)!;
            SceneType.GetProperty("Narration")!.SetValue(scene, defs[i].Narration);
            SceneType.GetProperty("DurationSeconds")!.SetValue(scene, defs[i].Duration);
            arr.SetValue(scene, i);
        }
        return arr;
    }

    private static List<double> SegmentStarts(Array brief, double introSeconds, Array scenes) =>
        ReflectionHelper.Invoke<List<double>>(
            ReflectionHelper.VideoGen, "ComputeSegmentStarts", brief, introSeconds, scenes, TransitionSeconds);

    private static List<string> BuildChapters(Array brief, double introSeconds, Array scenes) =>
        ReflectionHelper.Invoke<List<string>>(
            ReflectionHelper.VideoGen, "BuildChapters", brief, introSeconds, scenes, TransitionSeconds);

    // --- (1) VisualGroup reuse cap ---------------------------------------

    [Fact]
    public void EnsureDistinctVisualGroups_GivesEverySceneItsOwnGroup()
    {
        var scenes = MakeScenes(
            ("First beat opens the story.", 7.0, 0, "art", null),
            ("Still the same beat, more detail.", 7.0, 1, "art", null),
            ("Same beat again, third scene.", 7.0, 1, "art", null),
            ("Another beat, same group again.", 7.0, 1, "art", null),
            ("A new beat at last.", 7.0, 2, "art", null),
            ("And one more on that beat.", 7.0, 2, "context", null));
        EnsureDistinctVisualGroups(scenes);
        var groups = GroupsOf(scenes);
        Assert.Equal(scenes.Length, groups.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, scenes.Length).ToArray(), groups);
    }

    [Fact]
    public void EnsureDistinctVisualGroups_SharedDataVisualKeepsItsGroup()
    {
        // The one deliberate exception: a "visual" data-callout scene that
        // draws the exact same chart/table as the previous scene may share
        // that scene's group — the on-screen visual really is the same data.
        var spec = MakeSpec("bar", "Immersion scores");
        var scenes = MakeScenes(
            ("The launch broke records.", 7.0, 0, "art", null),
            ("Here is the chart behind it.", 7.0, 1, "visual", spec),
            ("The same chart, one more beat.", 7.0, 1, "visual", spec),
            ("Back to the story.", 7.0, 2, "art", null));
        EnsureDistinctVisualGroups(scenes);
        Assert.Equal([1, 2, 2, 3], GroupsOf(scenes));
    }

    [Fact]
    public void EnsureDistinctVisualGroups_DifferentDataVisualGetsNewGroup()
    {
        // Two adjacent "visual" scenes with DIFFERENT specs are different
        // pictures (different charts), so they must not share a group.
        var scenes = MakeScenes(
            ("Here is the first chart.", 7.0, 0, "visual", MakeSpec("bar", "Immersion scores")),
            ("And here is a second chart.", 7.0, 0, "visual", MakeSpec("line", "Concurrency over time")));
        EnsureDistinctVisualGroups(scenes);
        Assert.Equal([1, 2], GroupsOf(scenes));
    }

    [Fact]
    public void Video3_GroupsBeforeFix_AreReused()
    {
        // Characterization of the shipped defect: video #3 has 40 scenes but
        // only 16 VisualGroups, with a 5-scene run on a single image.
        var data = LoadVideo3();
        if (data is null) return;
        Assert.Equal(16, data.Value.Groups.Distinct().Count());
        Assert.Equal(5, LongestRun(data.Value.Groups));
    }

    [Fact]
    public void Video3_EnsureDistinctVisualGroups_GivesFortyDistinctVisuals()
    {
        var data = LoadVideo3();
        if (data is null) return;
        var defs = data.Value.Groups.Select((g, i) =>
            ($"Scene {i}.", data.Value.Durations[i], g, data.Value.Kinds[i], (object?)null)).ToArray();
        var scenes = MakeScenes(defs);
        EnsureDistinctVisualGroups(scenes);
        var groups = GroupsOf(scenes);
        Assert.Equal(40, groups.Distinct().Count());
        Assert.Equal(1, LongestRun(groups));
    }

    [Fact]
    public void VisualLoop_NeverReusesPreviousSceneVisual()
    {
        // The prep visual loop (Program.cs RunVisualLoopAsync) is the place
        // that actually decided what the viewer sees: it reused the previous
        // scene's resolved image/clip whenever the group matched. That reuse
        // must be gone — every scene resolves its own visual — and the
        // fallback for a scene that would otherwise repeat the previous
        // scene's visual must be present and logged.
        var program = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation/VideoGen/Program.cs"));
        Assert.DoesNotContain("canReuse", program);
        Assert.DoesNotContain("reused visual group", program);
        Assert.Contains("same visual as the previous scene", program);
    }

    [Fact]
    public void NewsPrompts_AskForOneVisualGroupPerScene()
    {
        // Both news prompts (Act 1 and the continuation acts) must tell the
        // model one group per scene — the old "cluster into 3 to 5 groups"
        // instruction is what produced the 16-groups-for-40-scenes scripts.
        var source = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation/VideoGen/ScriptGenerator.cs"));
        Assert.Contains("its OWN visualGroup", source);
        Assert.DoesNotContain("Cluster THIS ACT into roughly 3 to 5 visual groups", source);
        Assert.DoesNotContain("in the same visualGroup if they share the same shot", source);
    }

    // --- (2) No sting, no brief (Rishi 2026-11-25 13:55 EST) -------------

    [Fact]
    public void NoSting_AssemblePathPassesNoIntro()
    {
        // The assemble/write-props path must carry no intro at all: no
        // landscape or vertical intro variable, no named intro argument,
        // and the chapter call passes an empty brief and 0 intro — the
        // video opens directly with the hook.
        var program = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation/VideoGen/Program.cs"));
        Assert.DoesNotContain("landscapeIntroSeconds", program);
        Assert.DoesNotContain("verticalIntroSeconds", program);
        Assert.DoesNotContain("introSeconds:", program);
        Assert.Contains("BuildChapters([], 0, script.Scenes", program);
    }

    [Fact]
    public void NoSting_RemotionPropsHaveNoIntroOrBriefFields()
    {
        // schema.ts must define no intro/brief fields (zod strips them
        // from old props files), and Assembly.tsx must contain no Intro
        // component and no intro/brief wiring — scene 0 is the hook.
        var schema = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation/remotion-assembler/src/schema.ts"));
        Assert.DoesNotContain("introSeconds:", schema);
        Assert.DoesNotContain("introLogoPath:", schema);
        Assert.DoesNotContain("briefScenes:", schema);
        var assembly = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation/remotion-assembler/src/Assembly.tsx"));
        Assert.DoesNotContain("Intro", assembly);
        Assert.DoesNotContain("introSeconds", assembly);
        Assert.DoesNotContain("briefScenes", assembly);
        Assert.Contains("isHook={i === 0}", assembly);
    }

    [Fact]
    public void HookOpensAtZero_FirstSceneStartsAtZero()
    {
        // No brief, no sting: segment 0 (the hook) starts at exactly 0:00
        // — the old ~3.5s-early caption/chapter gap is gone.
        var scenes = MakeScenes(
            ("Rockstar finally released GTA 6.", 7.0, 1, "art", null),
            ("It returns to a brand-new Vice City.", 7.0, 2, "art", null));
        var starts = SegmentStarts(MakeBriefScenes(), 0, scenes);
        Assert.Equal(0, starts[0], precision: 2);
    }

    [Fact]
    public void HookTiming_WithBriefAndNoSting_SceneOneStartsRightAfterBrief()
    {
        // Video #3's real brief (8.16s + 3.95s) with the sting skipped: the
        // first spoken segment (brief sentence 1 — the hook) starts at 0:00,
        // and scene 1 starts right after the brief, with no 3.5s sting in
        // between. Rendered brief = 8.91 + 4.70 = 13.61s; two transitions
        // shared before scene 1, so its start is 13.61 - 2*0.2 = 13.21s.
        var brief = MakeBriefScenes(
            ("Rockstar's long-awaited GTA 6 landed in 2026.", 8.16),
            ("The hype is massive.", 3.95));
        var scenes = MakeScenes(
            ("Rockstar finally released GTA 6.", 7.0, 1, "art", null),
            ("It returns to a brand-new Vice City.", 7.0, 2, "art", null));
        var starts = SegmentStarts(brief, 0, scenes);
        Assert.Equal(0, starts[0], precision: 2); // hook audible at 0:00
        Assert.True(starts[0] < 5, "Hook (brief sentence 1) must be audible inside 5 seconds.");
        Assert.Equal(13.21, starts[2], precision: 2); // first main scene
    }

    [Fact]
    public void Video3_HookTiming_BriefPlusNoSting()
    {
        // Real video #3 data end to end: brief 12.11s + no sting puts scene
        // 1 at ~13.21s instead of the shipped ~16.51s, and the hook (brief
        // sentence 1) is audible at 0:00 — inside 5 seconds.
        var data = LoadVideo3();
        if (data is null) return;
        var brief = MakeBriefScenes(
            ("Brief sentence one.", data.Value.BriefDurations[0]),
            ("Brief sentence two.", data.Value.BriefDurations[1]));
        var scenes = MakeScenes(data.Value.Durations.Select((d, i) =>
            ($"Scene {i}.", d, i + 1, data.Value.Kinds[i], (object?)null)).ToArray());
        var withoutSting = SegmentStarts(brief, 0, scenes);
        var withSting = SegmentStarts(brief, 3.5, scenes);
        Assert.Equal(0, withoutSting[0], precision: 2);
        // Real brief durations carry sub-hundredth precision in script.json,
        // so assert to 1 decimal place here (13.2 / 16.5).
        Assert.Equal(13.2, withoutSting[2], precision: 1);
        Assert.Equal(16.5, withSting[3], precision: 1);
    }

    [Fact]
    public void Chapters_WithBriefAndNoSting_FirstChapterStillZero()
    {
        // The chapter list must stay consistent with the new timeline: first
        // chapter at 0:00, labelled from the brief, even with no sting.
        var brief = MakeBriefScenes(
            ("Rockstar's long-awaited GTA 6 landed in 2026.", 8.16),
            ("The hype is massive.", 3.95));
        var scenes = MakeScenes(
            ("Rockstar finally released GTA 6, and it returns to Vice City.", 7.0, 1, "art", null),
            ("The map is bigger than ever before.", 7.0, 2, "art", null));
        var chapters = BuildChapters(brief, 0, scenes);
        Assert.True(chapters.Count > 0, "Expected at least one chapter.");
        Assert.StartsWith("0:00", chapters[0]);
    }

    // --- (3) Hook window: peak moment inside the first 10-20 seconds ----

    [Fact]
    public void HookPrompt_OpeningWindowCarriesPeakMoment()
    {
        // Rishi 2026-11-25 13:58 EST: the story OPENS with its most
        // entertaining part — the single most dramatic, entertaining peak
        // moment — and it must land inside the first 10-20 seconds (the
        // first 2-3 scenes), not just the first sentence. The brief beat
        // itself is gone; the opening scenes carry the hook. The profile
        // guidance must say so explicitly, and the old brief wording
        // ("single most shocking TRUE fact" as a teaser sentence) is gone
        // from the script prompt.
        var profile = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation/VideoGen/TrendlineNowProfile.cs"));
        Assert.Contains("FIRST 10-20 SECONDS", profile);
        Assert.Contains("THE HOOK WINDOW", profile);
        Assert.Contains("most entertaining part", profile);
        Assert.Contains("peak moment", profile);
        Assert.Contains("SOMETHING BIGGER", profile);
        Assert.Contains("This isn't just about", profile);
        var generator = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation/VideoGen/ScriptGenerator.cs"));
        Assert.DoesNotContain("single most shocking TRUE fact", generator);
        Assert.DoesNotContain("\"brief\"", generator);
    }
}
