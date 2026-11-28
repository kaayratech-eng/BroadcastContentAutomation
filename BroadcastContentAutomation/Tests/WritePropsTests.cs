using System.Text.Json;
using Xunit;

namespace BroadcastContentAutomation.Tests;

// --write-props (2026-11-23): the laptop-render path needs both props files
// written WITHOUT rendering — the VM must never render, and --assemble would
// render. RunWritePropsAsync mirrors the RunAssembleAsync preamble (outro
// resolution, vertical trim) and calls BuildAndWritePropsAsync for both
// orientations. No sting, no brief (removed 2026-11-25): neither path
// passes an intro or a brief any more.
public class WritePropsTests
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

    private static string ReadRepoFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. parts]));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "write-props-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Scene MakeScene(string imagePath, string audioPath, double duration, string narration) =>
        new Scene
        {
            Narration = narration,
            DurationSeconds = duration,
            ImagePath = imagePath,
            AudioPath = audioPath,
            SceneKind = "character",
        };

    private static (string WorkDir, List<Scene> Scenes) Fixture()
    {
        var workDir = NewTempDir();
        // Fake media files: the props builder only relativizes image/audio
        // paths (existence is checked only for landscape variants and
        // clips), but real files keep the fixture honest.
        var art1 = Path.Combine(workDir, "scene00-art.png");
        var art2 = Path.Combine(workDir, "scene01-art.png");
        File.WriteAllText(art1, "fake png");
        File.WriteAllText(art2, "fake png");
        var mp3 = Path.Combine(workDir, "scene00.mp3");
        File.WriteAllText(mp3, "fake mp3");
        var scenes = new List<Scene>
        {
            MakeScene(art1, mp3, 5.0, "First scene narration."),
            MakeScene(art2, mp3, 7.5, "Second scene narration."),
        };
        return (workDir, scenes);
    }

    private static ContentProfile NoMusicProfile()
    {
        // The real Trendline Now profile with the music pool switched off
        // (empty path = ResolveBackgroundMusic returns null): the props
        // builder must not need ffmpeg loudness measurement or the real
        // music folder for these tests.
        var profile = ContentProfileRegistry.Get("trendlinenow");
        return profile with { BackgroundMusicPath = "" };
    }

    [Fact]
    public async Task BuildAndWriteProps_WritesValidVerticalProps()
    {
        // The vertical half of what --write-props does: 1080x1920, the
        // trimmed scene list, no brief and no intro (both removed
        // 2026-11-25 — the Short opens with the hook like the long-form).
        var (workDir, scenes) = Fixture();

        var (propsPath, music) = await VideoAssembler.BuildAndWritePropsAsync(
            NoMusicProfile(), new GenConfig(), scenes, workDir, 1080, 1920, "tense atmospheric",
            outroLines: ["LIKE, FOLLOW & SUBSCRIBE"], outroAudioPath: null, isCalm: false,
            outroHandles: null);

        Assert.Equal(Path.Combine(workDir, "remotion-props-1080x1920.json"), propsPath);
        Assert.Null(music);
        Assert.True(File.Exists(propsPath), "Vertical props file must exist after the call.");
        // No render is invoked by the props builder: no mp4 anywhere.
        Assert.Empty(Directory.EnumerateFiles(workDir, "*.mp4"));

        using var doc = JsonDocument.Parse(File.ReadAllText(propsPath));
        var root = doc.RootElement;
        Assert.Equal(1080, root.GetProperty("widthPx").GetInt32());
        Assert.Equal(1920, root.GetProperty("heightPx").GetInt32());
        Assert.Equal(30, root.GetProperty("fps").GetInt32());
        Assert.Equal(2, root.GetProperty("scenes").GetArrayLength());
        // No sting, no brief: the props carry no intro/brief fields at all.
        Assert.False(root.TryGetProperty("briefScenes", out _));
        Assert.False(root.TryGetProperty("introSeconds", out _));
        Assert.False(root.TryGetProperty("introLogoPath", out _));

        var first = root.GetProperty("scenes")[0];
        Assert.Equal("First scene narration.", first.GetProperty("captionText").GetString());
        Assert.Equal("scene00-art.png", first.GetProperty("path").GetString());
    }

    // L54 — the visual-missing reuse line must name the scene: with 55
    // scenes, "a scene has no visual" was unactionable.
    [Fact]
    public async Task BuildAndWriteProps_VisualMissingLogNamesScene()
    {
        var (workDir, scenes) = Fixture();
        scenes.Add(new Scene
        {
            Narration = "Third scene narration.",
            DurationSeconds = 6.0,
            SceneKind = "character",
            VisualMissing = true,
        });

        var sb = new System.Text.StringBuilder();
        var oldOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter(sb));
            await VideoAssembler.BuildAndWritePropsAsync(
                NoMusicProfile(), new GenConfig(), scenes, workDir, 1080, 1920, "tense atmospheric",
                outroLines: null, outroAudioPath: null, isCalm: false,
                outroHandles: null);
        }
        finally
        {
            Console.SetOut(oldOut);
        }

        var log = sb.ToString();
        Assert.Contains("[visual-missing]", log);
        Assert.Contains("scene 3", log);
    }

    [Fact]
    public void WriteProps_ModeWritesBothPropsFilesWithoutRendering()
    {
        // Source-level guard: RunWritePropsAsync must write BOTH props
        // files and must not invoke any render. (The live dry-run on a
        // copy of the video #4 job proves the bytes match --assemble's.)
        var source = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "Program.cs");

        var writeProps = source.IndexOf("async Task<int> RunWritePropsAsync()", StringComparison.Ordinal);
        Assert.True(writeProps >= 0, "RunWritePropsAsync must exist.");

        var assemble = source.IndexOf("async Task<int> RunAssembleAsync()", StringComparison.Ordinal);
        Assert.True(assemble > writeProps, "RunWritePropsAsync must sit before RunAssembleAsync.");
        var body = source[writeProps..assemble];

        Assert.Contains("BuildAndWritePropsAsync(profile, cfg, script.Scenes, workDir, 1920, 1080", body);
        Assert.Contains("BuildAndWritePropsAsync(profile, cfg, shortScenes, workDir, 1080, 1920", body);
        // No sting, no brief: neither props call passes an intro or a brief.
        Assert.DoesNotContain("briefScenes:", body);
        Assert.DoesNotContain("introSeconds:", body);
        Assert.DoesNotContain("AssembleWithRemotionAsync", body);
        Assert.DoesNotContain("AssembleFromClipsAsync", body);
        Assert.DoesNotContain("NormalizeLoudnessAsync", body);
        Assert.DoesNotContain("WriteSrtAsync", body);

        // Wiring: the flag, the dispatch, and the help line.
        Assert.Contains("case \"--write-props\": mode = \"write-props\"; break;", source);
        Assert.Contains("\"write-props\" => await RunWritePropsAsync(),", source);
        Assert.Contains("VideoGen --write-props --job job-YYYYMMDD-HHMMSS", source);
    }

    [Fact]
    public void WriteProps_ModeRefusesNonRemotionProfiles()
    {
        // The Vidu/clips path never reads a props file, so --write-props
        // must fail loudly there instead of writing props a render would
        // ignore. (No registered profile is non-Remotion today, so this is
        // a source guard; the live dry-run covers the Remotion path.)
        var source = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "Program.cs");

        var writeProps = source.IndexOf("async Task<int> RunWritePropsAsync()", StringComparison.Ordinal);
        Assert.True(writeProps >= 0, "RunWritePropsAsync must exist.");
        var assemble = source.IndexOf("async Task<int> RunAssembleAsync()", StringComparison.Ordinal);
        var body = source[writeProps..assemble];

        Assert.Contains("if (!profile.UsesRemotionStills)", body);
        Assert.Contains("InvalidOperationException", body);
        Assert.Contains("--write-props is only for Remotion profiles", body);
    }
}
