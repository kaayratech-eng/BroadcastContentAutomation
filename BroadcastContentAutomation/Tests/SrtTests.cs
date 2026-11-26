// Regression tests for the SRT writer (fix #4).
// VideoAssembler.WriteSrtAsync writes one cue per segment (brief scenes
// first, then main scenes) on the same timeline the render uses: each cue
// ends where the next begins, and the LAST cue ends at the end of its own
// rendered segment — which is where the outro starts. The old bug ran the
// last cue to the video total, so the final caption lingered over the
// outro instead of ending with the narration.
using System.Globalization;

namespace BroadcastContentAutomation.Tests;

public class SrtTests
{
    private static readonly Type SceneType =
        ReflectionHelper.FindType(ReflectionHelper.VideoGen, "Scene");

    private static readonly Type AssemblerType =
        ReflectionHelper.FindType(ReflectionHelper.VideoGen, "VideoAssembler");

    private static double Const(string name)
    {
        var field = AssemblerType.GetField(name,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException($"VideoAssembler.{name}");
        return (double)field.GetValue(null)!;
    }

    // Each rendered segment is its narration duration + 0.5s + the
    // post-narration hold, and consecutive segments share a transition.
    private static double RenderedSeconds(double narrationSeconds) =>
        narrationSeconds + 0.5 + Const("PostNarrationHoldSeconds");

    private static Array MakeScenes(params (string Narration, double Duration)[] defs)
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

    private static Array EmptyScenes() => Array.CreateInstance(SceneType, 0);

    private static List<(double Start, double End, string Text)> ParseSrt(string path)
    {
        var cues = new List<(double, double, string)>();
        var blocks = File.ReadAllText(path).Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines.Length >= 3, $"malformed SRT cue block: '{block}'");
            var times = lines[1].Split("-->", StringSplitOptions.TrimEntries);
            cues.Add((ParseTime(times[0]), ParseTime(times[1]), string.Join("\n", lines.Skip(2))));
        }
        return cues;
    }

    private static double ParseTime(string s)
    {
        // SRT times are "HH:MM:SS,mmm".
        var t = TimeSpan.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
        return t.TotalSeconds;
    }

    [Fact]
    public async Task WriteSrt_LastCueEndsAtOutroStart_NotVideoTotal()
    {
        // 1 brief scene (6s) + intro sting (3.5s) + 3 scenes (10s, 12s,
        // 8s). The last cue must end where the last scene's rendered
        // segment ends — the outro starts there. If the outro ran 10s, the
        // video total would be 10s later; the last cue must NOT reach it.
        const double introSeconds = 3.5;
        const double outroSeconds = 10.0;
        var transition = Const("TransitionSeconds");
        var brief = MakeScenes(("Brief teaser hook.", 6.0));
        var scenes = MakeScenes(
            ("First scene narration.", 10.0),
            ("Second scene narration.", 12.0),
            ("Third scene narration.", 8.0));

        // Recompute the shared timeline from first principles: segments in
        // render order, each start pulled earlier by one shared transition
        // per preceding segment.
        var rendered = new List<double>
        {
            RenderedSeconds(6.0), introSeconds,
            RenderedSeconds(10.0), RenderedSeconds(12.0), RenderedSeconds(8.0),
        };
        var starts = new List<double>();
        var acc = 0.0;
        for (var j = 0; j < rendered.Count; j++)
        {
            starts.Add(Math.Max(0, acc - j * transition));
            acc += rendered[j];
        }
        // Cue order is brief then main scenes; the intro has a start entry
        // but no cue. Last cue starts at the last scene's start.
        var lastCueStart = starts[2 + 2];
        var expectedLastEnd = lastCueStart + RenderedSeconds(8.0);
        var videoTotalWithOutro = starts[^1] + RenderedSeconds(8.0) + outroSeconds;

        var dir = Path.Combine(Path.GetTempPath(), $"videogen-srt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var srtPath = Path.Combine(dir, "test.srt");
            var method = ReflectionHelper.FindMethod(ReflectionHelper.VideoGen, "WriteSrtAsync");
            var task = (Task)method.Invoke(null,
                new object?[] { scenes, srtPath, introSeconds, brief, transition })!;
            // Tests.Runner awaits async [Fact]s (fixed 2026-11-24), so
            // await here instead of blocking.
            await task;

            var cues = ParseSrt(srtPath);
            Assert.Equal(4, cues.Count); // 1 brief + 3 scenes
            Assert.Equal("Brief teaser hook.", cues[0].Text);
            Assert.Equal("Third scene narration.", cues[^1].Text);

            // Every cue ends where the next begins (shared transition).
            for (var i = 0; i < cues.Count - 1; i++)
                Assert.Equal(cues[i + 1].Start, cues[i].End, precision: 3);

            // The last cue ends at the outro start — the end of its own
            // rendered segment — not at the video total (outro included).
            Assert.Equal(expectedLastEnd, cues[^1].End, precision: 3);
            Assert.True(cues[^1].End < videoTotalWithOutro,
                $"last cue ends at {cues[^1].End:F3}s, video total with outro is {videoTotalWithOutro:F3}s");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteSrt_NoBriefNoIntro_CueCountMatchesScenes()
    {
        // Degenerate timeline (no brief, no intro): one cue per scene, each
        // ending where the next begins, last ending at its segment end.
        var scenes = MakeScenes(
            ("Scene one.", 5.0),
            ("Scene two.", 5.0));
        var transition = Const("TransitionSeconds");

        var dir = Path.Combine(Path.GetTempPath(), $"videogen-srt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var srtPath = Path.Combine(dir, "test.srt");
            var method = ReflectionHelper.FindMethod(ReflectionHelper.VideoGen, "WriteSrtAsync");
            var task = (Task)method.Invoke(null,
                new object?[] { scenes, srtPath, 0.0, EmptyScenes(), transition })!;
            await task;

            var cues = ParseSrt(srtPath);
            Assert.Equal(2, cues.Count);
            Assert.Equal(cues[1].Start, cues[0].End, precision: 3);
            Assert.Equal(cues[1].Start + RenderedSeconds(5.0), cues[1].End, precision: 3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteSrt_NoStingNoBrief_FirstCueStartsAtZero()
    {
        // No sting, no brief (removed 2026-11-25, Rishi): the video opens
        // directly with the hook, so the first caption starts at exactly
        // 00:00:00,000 — the old ~3.5s-early gap (captions ignoring the
        // intro sting) is gone by construction. This is the call shape the
        // assemble path now uses for both orientations.
        var scenes = MakeScenes(
            ("Scene one.", 5.0),
            ("Scene two.", 5.0));
        var transition = Const("TransitionSeconds");

        var dir = Path.Combine(Path.GetTempPath(), $"videogen-srt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var srtPath = Path.Combine(dir, "test.srt");
            var method = ReflectionHelper.FindMethod(ReflectionHelper.VideoGen, "WriteSrtAsync");
            var task = (Task)method.Invoke(null,
                new object?[] { scenes, srtPath, 0.0, EmptyScenes(), transition })!;
            await task;

            var cues = ParseSrt(srtPath);
            Assert.Equal(2, cues.Count);
            Assert.Equal(0.0, cues[0].Start, precision: 3);
            Assert.Equal("Scene one.", cues[0].Text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
