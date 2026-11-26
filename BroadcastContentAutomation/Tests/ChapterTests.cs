// Regression tests for chapters (fix #8).
// Program.BuildChapters builds the long-form chapter list from VisualGroup
// boundaries: chapter 1 at 0:00 (the brief teaser + intro sting), then one
// chapter per visual beat, capped at 12, each at least 10s long — YouTube
// ignores the whole list if any chapter is shorter. Labels are the first
// complete sentence of the scene's narration: abbreviations ("Mr.", "U.S.")
// must not cut a label short, and a pathological first sentence is cut at
// a word boundary, never mid-word and never with an ellipsis.
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class ChapterTests
{
    private const string Video3ScriptPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261122-115430/script.json";

    // The old 3.5s intro sting length (removed from the profile 2026-11-25):
    // video #3 shipped with it, so its chapter timestamps are computed
    // with it here. New videos pass 0.
    private const double IntroSeconds = 3.5;

    private static readonly Type SceneType =
        ReflectionHelper.FindType(ReflectionHelper.VideoGen, "Scene");

    private static readonly double TransitionSeconds = TransitionSecondsConst();

    private static double TransitionSecondsConst()
    {
        var assembler = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "VideoAssembler");
        var field = assembler.GetField("TransitionSeconds",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return field is null ? 0.2 : (double)field.GetValue(null)!;
    }

    private static List<string> BuildChapters(Array briefScenes, double introSeconds, Array scenes) =>
        ReflectionHelper.Invoke<List<string>>(
            ReflectionHelper.VideoGen, "BuildChapters", briefScenes, introSeconds, scenes, TransitionSeconds);

    private static Array MakeScenes(params (string Narration, double Duration, int Group)[] defs)
    {
        var arr = Array.CreateInstance(SceneType, defs.Length);
        for (var i = 0; i < defs.Length; i++)
        {
            var scene = Activator.CreateInstance(SceneType, nonPublic: true)!;
            SceneType.GetProperty("Narration")!.SetValue(scene, defs[i].Narration);
            SceneType.GetProperty("DurationSeconds")!.SetValue(scene, defs[i].Duration);
            SceneType.GetProperty("VisualGroup")!.SetValue(scene, defs[i].Group);
            arr.SetValue(scene, i);
        }
        return arr;
    }

    private static Array EmptyScenes() => Array.CreateInstance(SceneType, 0);

    private static (Array Brief, Array Scenes)? LoadVideo3Scenes()
    {
        if (!File.Exists(Video3ScriptPath))
        {
            Console.WriteLine($"SKIP: video #3 script.json not found at {Video3ScriptPath}");
            return null;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Video3ScriptPath));
        var root = doc.RootElement;
        return (Load(root, "BriefScenes"), Load(root, "Scenes"));

        Array Load(JsonElement rootElement, string property)
        {
            var items = rootElement.GetProperty(property).EnumerateArray().ToArray();
            var arr = Array.CreateInstance(SceneType, items.Length);
            for (var i = 0; i < items.Length; i++)
            {
                var scene = Activator.CreateInstance(SceneType, nonPublic: true)!;
                SceneType.GetProperty("Narration")!.SetValue(scene, items[i].GetProperty("Narration").GetString() ?? "");
                SceneType.GetProperty("DurationSeconds")!.SetValue(scene, items[i].GetProperty("DurationSeconds").GetDouble());
                SceneType.GetProperty("VisualGroup")!.SetValue(scene, items[i].GetProperty("VisualGroup").GetInt32());
                arr.SetValue(scene, i);
            }
            return arr;
        }
    }

    // Chapters are "M:SS - label". Split off the timestamp and label.
    private static (double Seconds, string Label) ParseChapter(string chapter)
    {
        var dash = chapter.IndexOf(" - ", StringComparison.Ordinal);
        Assert.True(dash > 0, $"chapter not in 'M:SS - label' format: '{chapter}'");
        var ts = chapter[..dash];
        var parts = ts.Split(':');
        var seconds = double.Parse(parts[0]) * 60 + double.Parse(parts[1]);
        return (seconds, chapter[(dash + 3)..]);
    }

    [Fact]
    public void BuildChapters_Video3RealScript_ReturnsExactlyTwelve()
    {
        // 40 scenes, 16 VisualGroups, 2 brief scenes: 16 boundaries would
        // overflow the 12-chapter cap, so the time-spaced selection keeps 11
        // plus the 0:00 chapter.
        var data = LoadVideo3Scenes();
        if (data is null) return;
        var chapters = BuildChapters(data.Value.Brief, IntroSeconds, data.Value.Scenes);
        Assert.Equal(12, chapters.Count);
    }

    [Fact]
    public void BuildChapters_Video3RealScript_FirstChapterAtZero()
    {
        var data = LoadVideo3Scenes();
        if (data is null) return;
        var chapters = BuildChapters(data.Value.Brief, IntroSeconds, data.Value.Scenes);
        var (seconds, _) = ParseChapter(chapters[0]);
        Assert.Equal(0, seconds);
    }

    [Fact]
    public void BuildChapters_Video3RealScript_LabelsAreCompleteSentences()
    {
        // A label cut mid-sentence (or with a trailing ellipsis) reads as a
        // broken fragment in YouTube's chapter list.
        var data = LoadVideo3Scenes();
        if (data is null) return;
        var chapters = BuildChapters(data.Value.Brief, IntroSeconds, data.Value.Scenes);
        foreach (var chapter in chapters)
        {
            var (_, label) = ParseChapter(chapter);
            Assert.DoesNotContain("…", label);
            Assert.True(label.Length > 0, $"empty label in '{chapter}'");
            Assert.True(label[^1] is '.' or '!' or '?',
                $"label is not a complete sentence: '{label}'");
        }
    }

    [Fact]
    public void BuildChapters_Video3RealScript_EveryGapAtLeastTenSeconds()
    {
        // YouTube requires every chapter to be >= 10s or it ignores the
        // whole list.
        var data = LoadVideo3Scenes();
        if (data is null) return;
        var chapters = BuildChapters(data.Value.Brief, IntroSeconds, data.Value.Scenes);
        var times = chapters.Select(c => ParseChapter(c).Seconds).ToList();
        for (var i = 1; i < times.Count; i++)
            Assert.True(times[i] - times[i - 1] >= 10.0,
                $"gap between chapter {i} and {i + 1} is {times[i] - times[i - 1]:F1}s");
    }

    [Fact]
    public void BuildChapters_SeventySevenScenes_CapsAtTwelve()
    {
        // Video #1's shape (77 scenes, one group each): the cap, not the
        // group count, decides the chapter count. One brief scene pushes
        // the first boundary past 10s — without it the first boundary
        // lands 3.3s in (right after the 3.5s intro sting) and YouTube's
        // 10s rule drops it, which would give 11, not 12.
        var brief = MakeScenes(("The teaser hooks you in. Watch what happens next.", 8.0, 0));
        var scenes = MakeScenes(Enumerable.Range(0, 77)
            .Select(i => ($"Scene {i + 1} tells the next part of the story. More detail follows here.", 8.0, i))
            .ToArray());
        var chapters = BuildChapters(brief, IntroSeconds, scenes);
        Assert.Equal(12, chapters.Count);
    }

    [Fact]
    public void BuildChapters_Abbreviations_KeptWholeInLabel()
    {
        // "Mr." and "U.S." end in periods but are not sentence ends — the
        // label must run to the real sentence end, "works."
        var brief = MakeScenes(("Mr. Smith said the U.S. plan works. The rest of the story follows.", 6.0, 0));
        var chapters = BuildChapters(brief, IntroSeconds, EmptyScenes());
        Assert.Single(chapters);
        var (_, label) = ParseChapter(chapters[0]);
        Assert.Equal("Mr. Smith said the U.S. plan works.", label);
    }

    [Fact]
    public void BuildChapters_FewGroups_KeepsAllBoundaries()
    {
        // 3 visual groups + brief: only 4 chapters, nothing is dropped to
        // meet the cap — the cap is a ceiling, not a target.
        var brief = MakeScenes(
            ("The teaser hooks you in. Watch what happens next.", 5.0, 0),
            ("A second teaser beat. The story is just starting.", 5.0, 0));
        var scenes = MakeScenes(
            ("First beat opens the story. The scene is set.", 10.0, 0),
            ("Still the first beat. More context here.", 10.0, 0),
            ("Second beat shifts the focus. A new angle emerges.", 10.0, 1),
            ("Still the second beat. The plot thickens.", 10.0, 1),
            ("Third beat brings the twist. Everything changes now.", 10.0, 2),
            ("Still the third beat. The fallout begins.", 10.0, 2));
        var chapters = BuildChapters(brief, IntroSeconds, scenes);
        Assert.Equal(4, chapters.Count);
    }

    [Fact]
    public void BuildChapters_LongFirstSentence_CutAtWordBoundaryWithoutEllipsis()
    {
        // Safety net for a pathological first sentence: cut at a word
        // boundary at <= 200 chars, never mid-word, never with "…".
        var sentence = string.Join(" ", Enumerable.Repeat("word", 50)) + ". A short second sentence.";
        var brief = MakeScenes((sentence, 6.0, 0));
        var chapters = BuildChapters(brief, IntroSeconds, EmptyScenes());
        Assert.Single(chapters);
        var (_, label) = ParseChapter(chapters[0]);
        Assert.True(label.Length <= 200, $"label is {label.Length} chars");
        Assert.DoesNotContain("…", label);
        // The cut fell on a space in the original sentence: the label ends
        // at a whole word, not mid-word.
        Assert.True(label.Length < sentence.Length, "expected the long sentence to be cut");
        Assert.Equal(' ', sentence[label.Length]);
    }
}
