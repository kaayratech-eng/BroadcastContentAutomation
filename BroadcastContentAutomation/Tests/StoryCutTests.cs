// Regression tests for the 60s Instagram Story cut (Rishi 2026-11-26):
// when a vertical Short runs longer than Instagram's 60s Story cap, the
// pipeline cuts the first 60s into a sibling file and the Uploader uploads
// the cut for the instagram_story target instead of skipping it.
//
// Part A — StoryCut naming (Shared): one definition of the sibling name,
// used by the finish step, the uploader scan/dispatch, and the Story gate.
//
// Part B — InstagramStoryPublisher.Accepts: the gate must probe the actual
// file when the path is a cut, because the sidecar still carries the FULL
// Short's duration (79.3s) — trusting the sidecar would skip every cut.
// The accept case uses a REAL ffmpeg-generated probe file (2s, 1080x1920,
// H.264/AAC) so the probe path is exercised, not stubbed.
//
// Part C — source guard for finish-chunks.py: the vertical finish must
// write the cut (first 59.5s, re-encoded) and remove a stale cut when
// the Short now fits in 60s.
using System.Diagnostics;
using GiggleGarden.Shared;
using GiggleGarden.Uploader;

namespace BroadcastContentAutomation.Tests;

public class StoryCutTests
{
    // ------------------------------------------------------------------
    // Part A — StoryCut naming
    // ------------------------------------------------------------------

    [Fact]
    public void PathFor_DerivesStory60SiblingFromShort()
    {
        Assert.Equal(
            Path.Combine("/tmp", "lionel-messi-s-mls-playoff-chase-the-big-change-th-en-short-story60.mp4"),
            StoryCut.PathFor("/tmp/lionel-messi-s-mls-playoff-chase-the-big-change-th-en-short.mp4"));
    }

    [Fact]
    public void IsStoryCut_MatchesSuffixOnly()
    {
        Assert.True(StoryCut.IsStoryCut("/tmp/slug-en-short-story60.mp4"));
        Assert.False(StoryCut.IsStoryCut("/tmp/slug-en-short.mp4"));
        Assert.False(StoryCut.IsStoryCut("/tmp/slug-en.mp4"));
        Assert.False(StoryCut.IsStoryCut("/tmp/slug-en-short-story60.json"));
    }

    // ------------------------------------------------------------------
    // Part B — InstagramStoryPublisher.Accepts with a real probe file
    // ------------------------------------------------------------------

    private static string TempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "storycut-tests", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A real 2s vertical clip (1080x1920 H.264 + AAC) for the probe path.
    // Synchronous: the sandbox runner does not await Task-returning [Fact]s.
    private static string MakeProbeClip(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        var psi = new ProcessStartInfo("ffmpeg", "-y -v error " +
            "-f lavfi -i testsrc=size=1080x1920:rate=30:duration=2 " +
            "-f lavfi -i sine=frequency=440:duration=2 " +
            "-c:v libx264 -preset veryfast -crf 28 -c:a aac -shortest " +
            $"\"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, $"ffmpeg probe-clip failed: {proc.StandardError.ReadToEnd()}");
        Assert.True(File.Exists(path), "ffmpeg did not write the probe clip");
        return path;
    }

    private static object StoryPublisher(string dir)
    {
        // InstagramStoryPublisher / InstagramConfig are internal to the
        // Uploader assembly: construct them via reflection, the same
        // pattern UploaderTests uses for MetaGraph. Logger is public.
        var asm = ReflectionHelper.Uploader;
        var cfgType = ReflectionHelper.FindType(asm, "InstagramConfig");
        var cfg = Activator.CreateInstance(cfgType)!;
        var pubType = ReflectionHelper.FindType(asm, "InstagramStoryPublisher");
        return Activator.CreateInstance(pubType, cfg, new Logger(dir))!;
    }

    private static (bool Accepted, string? Reason) Accepts(object publisher, Sidecar sidecar, string videoPath)
    {
        var method = publisher.GetType().GetMethod("Accepts")
            ?? throw new MissingMethodException("InstagramStoryPublisher.Accepts not found");
        var args = new object?[] { sidecar, videoPath, null };
        var accepted = (bool)method.Invoke(publisher, args)!;
        return (accepted, (string?)args[2]);
    }

    private static Sidecar VerticalSidecar(double? durationSeconds) =>
        new() { Aspect = "vertical", DurationSeconds = durationSeconds };

    [Fact]
    public void Accepts_StoryCutWithLongSidecar_Accepts()
    {
        // The cut case: the sidecar still says 79.3s (the full Short),
        // but the file handed to the gate is the ≤60s cut — the gate must
        // probe the file, not trust the sidecar, and accept.
        var dir = TempDir("accept-cut");
        var cut = MakeProbeClip(dir, "slug-en-short-story60.mp4");
        var (accepted, reason) = Accepts(StoryPublisher(dir), VerticalSidecar(79.3), cut);
        Assert.True(accepted, reason);
    }

    [Fact]
    public void Accepts_FullShortOver60_Skips()
    {
        // No cut: the full 79.3s Short against the gate must still skip,
        // with the existing reason text.
        var dir = TempDir("skip-full");
        var (accepted, reason) = Accepts(StoryPublisher(dir), VerticalSidecar(79.3),
            Path.Combine(dir, "slug-en-short.mp4"));
        Assert.False(accepted);
        Assert.Contains("capped at 60s", reason);
    }

    [Fact]
    public void Accepts_ShortUnder60_Accepts()
    {
        // A Short that already fits needs no cut: the sidecar value alone
        // accepts it, unchanged behaviour.
        var dir = TempDir("accept-short");
        var (accepted, reason) = Accepts(StoryPublisher(dir), VerticalSidecar(45),
            Path.Combine(dir, "slug-en-short.mp4"));
        Assert.True(accepted, reason);
    }

    // ------------------------------------------------------------------
    // Part C — finish-chunks.py writes the cut (source guard, same
    // pattern as UploaderTests Part C: a live finish run is not part of
    // the suite, so the block is pinned deterministically here and
    // verified behaviourally against the Messi Short by hand — see report).
    // ------------------------------------------------------------------

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "production", "finish-chunks.py")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate repo root (production/finish-chunks.py).");
        }
    }

    [Fact]
    public void FinishChunks_WritesStoryCutForOver60sVertical()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "production", "finish-chunks.py"));
        // Sibling name mirrors StoryCut.PathFor.
        Assert.Contains("\"-story60.mp4\"", source);
        // First 59.5s, re-encoded (not stream-copied) — 59.5 stays safely
        // under the 60s gate under any probe rounding.
        Assert.Contains("\"-t\", \"59.5\"", source);
        // Only for vertical over-60s Shorts.
        Assert.Contains("if orientation == \"vertical\":", source);
        Assert.Contains("total_frames / fps > 60", source);
        // A stale cut from an earlier longer finish is removed.
        Assert.Contains("removing stale story cut", source);
    }
}
