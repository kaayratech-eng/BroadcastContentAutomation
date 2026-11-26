// Regression tests for the landscape-props chicken-and-egg (2026-11-23):
// the landscape chunk render needs remotion-props-1920x1080.json, but
// --assemble only wrote it after the vertical render finished — and the
// vertical render keeps dying from memory at frame ~50-96, so the props
// were never written and the driver looped prep_done → assemble →
// render_chunks → prep_done forever. The fix extracts the props-building
// half of AssembleWithRemotionAsync into BuildAndWritePropsAsync and
// calls it for the landscape orientation up front, before any rendering,
// so the driver can chunk-render the landscape even when the vertical
// assemble dies. These tests pin the extracted method (valid props, no
// render invoked, deterministic bytes, orientation-specific art) and the
// wiring (up-front call sits before the vertical render in
// RunAssembleAsync, with the same arguments the landscape render uses).
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class LandscapePropsUpfrontTests
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
        var dir = Path.Combine(Path.GetTempPath(), "landscape-props-tests", Guid.NewGuid().ToString("N"));
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

    private static async Task<(string PropsPath, string? MusicFileName)> BuildLandscapeProps(
        string workDir, List<Scene> scenes) =>
        await VideoAssembler.BuildAndWritePropsAsync(
            NoMusicProfile(), new GenConfig(), scenes, workDir, 1920, 1080, "tense atmospheric",
            outroLines: ["LIKE, FOLLOW & SUBSCRIBE"], outroAudioPath: null, isCalm: false,
            outroHandles: null);

    [Fact]
    public async Task BuildAndWriteProps_WritesValidLandscapeProps()
    {
        var (workDir, scenes) = Fixture();

        var (propsPath, music) = await BuildLandscapeProps(workDir, scenes);

        Assert.Equal(Path.Combine(workDir, "remotion-props-1920x1080.json"), propsPath);
        Assert.Null(music);
        Assert.True(File.Exists(propsPath), "Landscape props file must exist after the call.");
        // No render is invoked by the props builder: no mp4 anywhere.
        Assert.Empty(Directory.EnumerateFiles(workDir, "*.mp4"));

        using var doc = JsonDocument.Parse(File.ReadAllText(propsPath));
        var root = doc.RootElement;
        Assert.Equal(1920, root.GetProperty("widthPx").GetInt32());
        Assert.Equal(1080, root.GetProperty("heightPx").GetInt32());
        Assert.Equal(30, root.GetProperty("fps").GetInt32());
        Assert.Equal(2, root.GetProperty("scenes").GetArrayLength());
        // No sting, no brief (removed 2026-11-25, Rishi): the props carry
        // no intro/brief fields at all — the video opens with scene 0.
        Assert.False(root.TryGetProperty("briefScenes", out _));
        Assert.False(root.TryGetProperty("introSeconds", out _));
        Assert.False(root.TryGetProperty("introLogoPath", out _));

        var first = root.GetProperty("scenes")[0];
        Assert.Equal("First scene narration.", first.GetProperty("captionText").GetString());
        Assert.Equal("scene00-art.png", first.GetProperty("path").GetString());
        // Rendered duration = narration + 0.5s breathing room + hold.
        Assert.Equal(5.0 + 0.5 + VideoAssembler.PostNarrationHoldSeconds,
            first.GetProperty("durationInSeconds").GetDouble(), precision: 6);
        Assert.Equal("scene00.mp3", first.GetProperty("narrationAudioPath").GetString());
    }

    [Fact]
    public async Task BuildAndWriteProps_TwoCallsWriteIdenticalBytes()
    {
        // The up-front write and the render-time write go through the same
        // code with the same arguments, so they must produce identical
        // props — the driver can trust the up-front file as the render's own.
        var (workDir, scenes) = Fixture();
        var propsFile = Path.Combine(workDir, "remotion-props-1920x1080.json");

        await BuildLandscapeProps(workDir, scenes);
        var first = File.ReadAllBytes(propsFile);

        await BuildLandscapeProps(workDir, scenes);
        var second = File.ReadAllBytes(propsFile);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task BuildAndWriteProps_EmptyScenesThrows()
    {
        var workDir = NewTempDir();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            VideoAssembler.BuildAndWritePropsAsync(
                NoMusicProfile(), new GenConfig(), new List<Scene>(), workDir, 1920, 1080));
        Assert.False(File.Exists(Path.Combine(workDir, "remotion-props-1920x1080.json")));
    }

    [Fact]
    public async Task BuildAndWriteProps_LandscapePrefersLandscapeArt()
    {
        var (workDir, scenes) = Fixture();
        // Scene 0 has a real landscape asset: the landscape props must
        // point at it, while the vertical props keep the portrait asset.
        var landscapeArt = Path.Combine(workDir, "scene00-art-landscape.png");
        File.WriteAllText(landscapeArt, "fake landscape png");
        scenes[0].LandscapeImagePath = landscapeArt;

        await BuildLandscapeProps(workDir, scenes);
        using var landscape = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(workDir, "remotion-props-1920x1080.json")));
        Assert.Equal("scene00-art-landscape.png",
            landscape.RootElement.GetProperty("scenes")[0].GetProperty("path").GetString());

        await VideoAssembler.BuildAndWritePropsAsync(
            NoMusicProfile(), new GenConfig(), scenes, workDir, 1080, 1920);
        using var vertical = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(workDir, "remotion-props-1080x1920.json")));
        Assert.Equal("scene00-art.png",
            vertical.RootElement.GetProperty("scenes")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void RunAssemble_WritesLandscapePropsBeforeVerticalRender()
    {
        // The up-front call must sit before the vertical render call in
        // RunAssembleAsync — otherwise a dying vertical render still
        // blocks the landscape props. Source-level guard (same pattern as
        // ChunkPathTests): the landscape BuildAndWritePropsAsync call must
        // appear before the first vertical AssembleWithRemotionAsync call.
        var source = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "Program.cs");

        var upfront = source.IndexOf(
            "BuildAndWritePropsAsync(profile, cfg, script.Scenes, workDir, 1920, 1080",
            StringComparison.Ordinal);
        Assert.True(upfront >= 0,
            "RunAssembleAsync must call BuildAndWritePropsAsync for the landscape up front.");

        var verticalRender = source.IndexOf(
            "AssembleWithRemotionAsync(profile, cfg, shortScenes, workDir, vertical, 1080, 1920",
            StringComparison.Ordinal);
        Assert.True(verticalRender >= 0, "Expected the vertical render call in RunAssembleAsync.");

        Assert.True(upfront < verticalRender,
            "Landscape props must be written BEFORE the vertical render starts.");
    }

    [Fact]
    public void RunAssemble_UpfrontPropsUseLandscapeRenderArguments()
    {
        // The up-front props must be identical to what the landscape
        // render would write: same scene list, same outro — and no brief
        // or intro (both removed 2026-11-25). The up-front call and the
        // landscape render call must agree on those arguments, or the
        // driver would chunk-render from stale props.
        var source = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "Program.cs");

        var upfront = source.IndexOf(
            "BuildAndWritePropsAsync(profile, cfg, script.Scenes, workDir, 1920, 1080",
            StringComparison.Ordinal);
        Assert.True(upfront >= 0, "Expected the up-front landscape props call.");
        var upfrontBlock = source.Substring(upfront, Math.Min(600, source.Length - upfront));
        Assert.Contains("outroLines: baseOutroLines", upfrontBlock);
        Assert.Contains("outroAudioPath: baseOutroAudio", upfrontBlock);
        Assert.DoesNotContain("briefScenes:", upfrontBlock);
        Assert.DoesNotContain("introSeconds:", upfrontBlock);
    }
}
