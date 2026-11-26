// Regression tests for the chunk path (production/finish-chunks.py) —
// the production writer for every shipped video. Review #2 (R2) found
// fixes #8 (chapters), #9 (Short title) and #10 (canonical hashtags)
// landed in the --assemble writer (VideoGen/Program.cs) only, while
// finish-chunks.py still shipped the old chapters (16, truncated at 40
// chars), the " #Shorts" title suffix, and the model's drifted hashtags.
// These guards pin the ported behaviour in the .py source, and the last
// test runs the Python behavioural suite (production/test_finish_chunks.py)
// so the chapter/hashtag logic itself — not just its spelling — is
// verified on video #3's real script.json.
//
// The SRT last-cue guard (fix #4, chunk path) also lives here: the
// --assemble path never had the bug, so SrtTests.cs alone cannot catch a
// regression in finish-chunks.py's cue-end test.
using System.Diagnostics;

namespace BroadcastContentAutomation.Tests;

public class ChunkPathTests
{
    // Repo layout, resolved from the test assembly's location: the Tests
    // project lives at <repo>/BroadcastContentAutomation/Tests, so the repo
    // root is three levels up from the test binaries.
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

    private static string FinishChunksSource() =>
        File.ReadAllText(Path.Combine(RepoRoot, "production", "finish-chunks.py"));

    // ------------------------------------------------------------------
    // Fix #8 port — chapters: <=12, first 0:00, first-sentence labels
    // ------------------------------------------------------------------

    [Fact]
    public void FinishChunks_Chapters_CappedAtTwelve_FirstAtZero()
    {
        // Mirrors Program.cs BuildChapters: a 12-chapter cap and a first
        // chapter at 0:00 labelled from the brief (or "Intro").
        var source = FinishChunksSource();
        Assert.Contains("MAX_CHAPTERS = 12", source);
        Assert.Contains("0:00 - ", source);
        Assert.Contains("chapter_label(", source);
    }

    [Fact]
    public void FinishChunks_Chapters_NoFortyCharTruncation()
    {
        // The old build_chapters cut every label at 40 chars with "…"
        // ("Rockstar finally released GTA 6, and it…"). Labels must be
        // the first complete sentence, never a truncated fragment.
        var source = FinishChunksSource();
        Assert.DoesNotContain("nar[:40]", source);
        Assert.DoesNotContain("[:40].rstrip()", source);
    }

    [Fact]
    public void FinishChunks_Chapters_UsesVisualGroupBoundariesAndTimeSpacing()
    {
        // Same chapter semantics as BuildChapters: one chapter per
        // VisualGroup boundary, time-spaced selection when boundaries
        // exceed the remaining slots, 10s minimum gap.
        var source = FinishChunksSource();
        Assert.Contains("VisualGroup", source);
        Assert.Contains("time-spaced", source);
        Assert.Contains("10.0", source);
    }

    [Fact]
    public void FinishChunks_Chapters_BriefLabelsFirstChapter()
    {
        // The 0:00 chapter covers the brief teaser + intro sting and is
        // labelled from the brief's opening line, like BuildChapters.
        var source = FinishChunksSource();
        Assert.Contains("brief_scenes", source);
        Assert.Contains("\"Intro\"", source);
    }

    // ------------------------------------------------------------------
    // Fix #9 port — Short title (see also ShortTitleTests, which scans
    // finish-chunks.py for any Title line mentioning #Shorts)
    // ------------------------------------------------------------------

    [Fact]
    public void FinishChunks_ShortTitle_IsPlainTitleFitTo100()
    {
        // The vertical sidecar Title is the plain script title, fit to
        // 100 chars — the old `fit(script["Title"] + " #Shorts", 100)`
        // must be gone.
        var source = FinishChunksSource();
        Assert.Contains("\"Title\": fit(script[\"Title\"], 100)", source);
        Assert.DoesNotContain("fit(script[\"Title\"] + \" #Shorts\"", source);
    }

    // ------------------------------------------------------------------
    // Fix #10 port — canonical hashtags from the long-form Description
    // ------------------------------------------------------------------

    [Fact]
    public void FinishChunks_Hashtags_CanonicalizedFromDescription()
    {
        // Mirrors Program.cs CanonicalHashtags / ExtractHashtags /
        // WithCanonicalHashtags: the Short's captions reuse the
        // long-form Description's hashtag line, Reels up to 8 tags /
        // 300 chars, TikTok up to 4 tags / 150 chars.
        var source = FinishChunksSource();
        Assert.Contains("def canonical_hashtags(", source);
        Assert.Contains("def extract_hashtags(", source);
        Assert.Contains("def with_canonical_hashtags(", source);
        Assert.Contains("canonical_hashtags(script[\"Description\"])", source);
        Assert.Contains("max_hashtags=8, max_length=300", source);
        Assert.Contains("max_hashtags=4, max_length=150", source);
    }

    [Fact]
    public void FinishChunks_Hashtags_CaptionOverridesUseCanonicalCaptions()
    {
        // The vertical CaptionOverrides must carry the rebuilt captions,
        // not the model's raw ReelsCaption/TikTokCaption.
        var source = FinishChunksSource();
        Assert.Contains("\"instagram\": short_reels", source);
        Assert.Contains("\"facebook\": short_reels", source);
        Assert.Contains("\"tiktok\": short_tiktok", source);
        Assert.DoesNotContain("\"instagram\": script[\"ReelsCaption\"]", source);
        Assert.DoesNotContain("\"tiktok\": script[\"TikTokCaption\"]", source);
    }

    [Fact]
    public void FinishChunks_Hashtags_NoCanonicalLeavesCaptionAsWritten()
    {
        // The no-canonical fallback (Program.cs WithCanonicalHashtags):
        // with no hashtags in the Description the caption is left
        // exactly as the model wrote it.
        var source = FinishChunksSource();
        Assert.Contains("if not canonical:", source);
    }

    // ------------------------------------------------------------------
    // Fix #4, chunk path — SRT last cue ends at the outro start
    // ------------------------------------------------------------------

    [Fact]
    public void FinishChunks_Srt_LastCueUsesSceneCountNotStartsLength()
    {
        // The old test `i + 1 < len(starts)` was always true (starts has
        // n+1 entries), so the last cue ran to the video total and
        // spanned the outro — video #3's cue 42 ended at 330.4s. The
        // fix tests against the scene count instead.
        var source = FinishChunksSource();
        Assert.Contains("i + 1 < len(prop_scenes)", source);
        Assert.DoesNotContain("i + 1 < len(starts)", source);
    }

    // ------------------------------------------------------------------
    // Behavioural suite — runs the Python tests against the real logic
    // ------------------------------------------------------------------

    [Fact]
    public void FinishChunks_PythonBehaviouralTestsPass()
    {
        // production/test_finish_chunks.py calls build_chapters,
        // chapter_label, canonical_hashtags and with_canonical_hashtags
        // on video #3's real script.json (12 chapters, first 0:00,
        // complete-sentence labels, canonical hashtags). No network, no
        // ffmpeg, no render — pure functions, runs in under a second.
        var start = new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = "test_finish_chunks.py",
            WorkingDirectory = Path.Combine(RepoRoot, "production"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start python3.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0,
            $"test_finish_chunks.py exited {proc.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }
}
