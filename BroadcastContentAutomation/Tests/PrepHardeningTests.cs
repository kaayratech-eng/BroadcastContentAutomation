// Regression tests for ITEM 4 (fix sequence #2): prep hardening —
// B12 (a single missing visual must skip-and-flag, not kill the prep),
// B17/A5 (a floor failure must not re-feed the same failing partial 3x;
// a scene-count floor of >=50 for long-form), and B7 (FallbackTtsProvider,
// Azure helper first, Google Cloud TTS helper for `en` on failure).
//
// Seams: FlagVisualFailure / DeletePartialScriptFiles / SaveScriptAsync are
// top-level local functions in Program.cs (found by substring, the
// established ReflectionHelper pattern); ValidateLongFormFloors is an
// internal static on ScriptGenerator; FallbackTtsProvider is constructed
// via reflection around the real Azure/Google helper providers pointed at
// fake helper scripts (no provider quota, no network).
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class PrepHardeningTests
{
    private const string Video3ScriptPath =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261122-115430/script.json";

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

    private static string Region(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Marker not found: {startMarker}");
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found after {startMarker}: {endMarker}");
        return text[start..end];
    }

    // Invoke a static method found by substring and unwrap the
    // TargetInvocationException so the test sees the real failure.
    private static object? InvokeStatic(Assembly assembly, string namePart, params object?[] args)
    {
        var method = ReflectionHelper.FindMethod(assembly, namePart);
        try
        {
            return method.Invoke(null, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static void Block(Task task) => task.GetAwaiter().GetResult();

    private static T Block<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void SetProp(object obj, string name, object? value)
    {
        var prop = obj.GetType().GetProperty(name)
            ?? throw new MissingMemberException(obj.GetType().Name, name);
        prop.SetValue(obj, value);
    }

    private static object? GetProp(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name)
            ?? throw new MissingMemberException(obj.GetType().Name, name);
        return prop.GetValue(obj);
    }

    private static Type SceneType => ReflectionHelper.FindType(ReflectionHelper.VideoGen, "Scene");
    private static Type ScriptType => ReflectionHelper.FindType(ReflectionHelper.VideoGen, "VideoScript");
    private static Type GenConfigType => ReflectionHelper.FindType(ReflectionHelper.VideoGen, "GenConfig");

    private static object NewScene(string narration, string kind = "art")
    {
        var scene = Activator.CreateInstance(SceneType, nonPublic: true)!;
        SetProp(scene, "Narration", narration);
        SetProp(scene, "SceneKind", kind);
        return scene;
    }

    private static object NewScript(params object[] scenes)
    {
        var script = Activator.CreateInstance(ScriptType, nonPublic: true)!;
        var listType = typeof(List<>).MakeGenericType(SceneType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var scene in scenes) list.Add(scene);
        SetProp(script, "Scenes", list);
        return script;
    }

    private static object SceneAt(object script, int index)
    {
        var scenes = (System.Collections.IList)GetProp(script, "Scenes")!;
        return scenes[index]!;
    }

    // ------------------------------------------------------------------
    // B12 — skip-and-flag a missing visual
    // ------------------------------------------------------------------

    [Fact]
    public void Scene_CarriesVisualMissingFlag()
    {
        // The flag must exist on Scene and persist through script.json
        // (it is how a reviewer sees the failure at review time).
        var visualMissing = SceneType.GetProperty("VisualMissing")
            ?? throw new MissingMemberException("Scene", "VisualMissing");
        Assert.Equal(typeof(bool), visualMissing.PropertyType);
        var reason = SceneType.GetProperty("VisualMissingReason")
            ?? throw new MissingMemberException("Scene", "VisualMissingReason");
        Assert.Equal(typeof(string), reason.PropertyType);
    }

    [Fact]
    public void VisualFailure_FlagsSceneAndRecordsFile()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "prep-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var script = NewScript(NewScene("First scene."), NewScene("Second scene."));
            InvokeStatic(ReflectionHelper.VideoGen, "FlagVisualFailure",
                workDir, script, 1, "Pexels had no match for \"giraffe parade\".");

            var flagged = SceneAt(script, 1);
            Assert.True((bool)GetProp(flagged, "VisualMissing")!,
                "Flagged scene must have VisualMissing=true");
            Assert.Contains("giraffe parade", (string)GetProp(flagged, "VisualMissingReason")!);
            // The other scene is untouched.
            Assert.False((bool)GetProp(SceneAt(script, 0), "VisualMissing")!);

            var report = Path.Combine(workDir, "visual-failures.txt");
            Assert.True(File.Exists(report), "visual-failures.txt must be written in the job dir");
            var text = File.ReadAllText(report);
            Assert.Contains("scene 2", text);
            Assert.Contains("giraffe parade", text);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void VisualFailure_PersistsInScriptJson()
    {
        // SaveScriptAsync is a top-level local that captures jsonOptions,
        // so it is an instance method on a compiler-generated display
        // class and cannot be reached by the static-method finder — the
        // property that matters here is that the flag survives a
        // script.json round-trip with the real serializer options
        // (Sidecar.Options, the same options SaveScriptAsync uses).
        var workDir = Path.Combine(Path.GetTempPath(), "prep-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var script = NewScript(NewScene("Only scene."));
            InvokeStatic(ReflectionHelper.VideoGen, "FlagVisualFailure",
                workDir, script, 0, "art providers all failed.");
            var json = JsonSerializer.Serialize(script, GiggleGarden.Shared.Sidecar.Options);
            File.WriteAllText(Path.Combine(workDir, "script.json"), json);

            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(workDir, "script.json")));
            var scene = doc.RootElement.GetProperty("Scenes")[0];
            Assert.True(scene.GetProperty("VisualMissing").GetBoolean(),
                "VisualMissing must survive a script.json round-trip");
            Assert.Contains("art providers all failed",
                scene.GetProperty("VisualMissingReason").GetString());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void VisualLoop_SkipsAndFlagsInsteadOfThrowing()
    {
        // The per-scene resolution in RunVisualLoopAsync must catch a
        // failure, flag the scene, write the record, and continue — never
        // let one bad stockQuery / dead art provider kill the whole prep.
        // The Pexels no-match throw stays as the reason carrier, but it
        // must sit inside the guarded region (a bare rethrow would defeat
        // the whole point).
        var program = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "Program.cs");
        var loop = Region(program, "async Task RunVisualLoopAsync", "// Automated providers are");
        Assert.Contains("catch (Exception ex)", loop);
        Assert.Contains("FlagVisualFailure(", loop);
        Assert.Contains("visual-failures", loop);
        Assert.Contains("CreatePlaceholderImageAsync", loop);
        Assert.Contains("Pexels had no match", loop);
        // ONE narrow exception to the never-rethrow rule (image pool,
        // 2026-11-24): FLUX quota exhaustion is a day-level failure, not
        // a per-scene one — FLUX is out until 00:00 UTC and the fallbacks
        // could not cover this scene, so every remaining scene would fail
        // identically and the prep would "complete" full of placeholders.
        // The catch rethrows ONLY a FluxQuotaExhaustedException on a scene
        // with no resolved visual (a distinctness-variant failure on a
        // scene whose main visual already resolved still flags and
        // continues); any other failure must never escape the loop. The
        // rethrow sits before any flagging, so the aborted scene carries
        // no placeholder and no stale VisualMissing flag into --resume.
        var quotaGuard = loop.IndexOf("ex is FluxQuotaExhaustedException", StringComparison.Ordinal);
        Assert.True(quotaGuard >= 0, "quota rethrow guard missing from the visual-loop catch");
        var rethrow = loop.IndexOf("throw;", quotaGuard, StringComparison.Ordinal);
        Assert.True(rethrow > quotaGuard, "quota guard must rethrow");
        Assert.True(rethrow < loop.IndexOf("FlagVisualFailure(", StringComparison.Ordinal),
            "quota rethrow must happen before any flagging");
        Assert.Equal(1, loop.Split("throw;").Length - 1);
    }

    [Fact]
    public void PlaceholderImage_IsCreatedByFfmpeg()
    {
        // The prep-time fallback for a flagged scene: a plain solid-colour
        // frame, so assemble + prep_complete still have a real visual file.
        var dir = Path.Combine(Path.GetTempPath(), "prep-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = Activator.CreateInstance(GenConfigType, nonPublic: true)!;
            var outPath = Path.Combine(dir, "scene00-art-placeholder.png");
            var method = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "VideoAssembler")
                .GetMethod("CreatePlaceholderImageAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("VideoAssembler.CreatePlaceholderImageAsync");
            var task = (Task<string>)method.Invoke(null, [cfg, outPath])!;
            var created = Block(task);
            Assert.Equal(outPath, created);
            Assert.True(File.Exists(outPath) && new FileInfo(outPath).Length > 0,
                "Placeholder image must exist and be non-empty");
            var header = File.ReadAllBytes(outPath).Take(8).ToArray();
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Assemble_ToleratesFlaggedSceneWithoutVisual()
    {
        // A flagged scene with no visual file must not throw in the
        // assemble path: BuildAssetAsync falls back to the previous
        // scene's asset (last resort — the prep-time placeholder is the
        // normal path) instead of the old "neither a ClipPath nor an
        // ImagePath" throw.
        var assembler = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "VideoAssembler.cs");
        var build = Region(assembler, "async Task<RemotionSceneAsset> BuildAssetAsync", "var assets = new List<RemotionSceneAsset>();");
        Assert.Contains("VisualMissing", build);
        Assert.Contains("neither a ClipPath nor an ImagePath", build);
    }

    [Fact]
    public void PrepComplete_AllowsFlaggedSceneWithoutVisual()
    {
        // prep_complete() in the driver must waive the visual check for a
        // scene flagged VisualMissing (audio is still required) — otherwise
        // a flagged prep could never advance to prep_done.
        var driver = ReadRepoFile("trendline", "driver.sh");
        var prep = Region(driver, "prep_complete() {", "\n}\n");
        Assert.Contains("VisualMissing", prep);
    }

    [Fact]
    public void DriverScript_ParsesCleanly()
    {
        var psi = new ProcessStartInfo("bash", ["-n", Path.Combine(RepoRoot, "trendline", "driver.sh")])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30_000), "bash -n timed out");
        Assert.Equal(0, p.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(err), $"bash -n errors: {err}");
    }

    // ------------------------------------------------------------------
    // B17/A5 — scene-count floor + floor failure invalidates the partial
    // ------------------------------------------------------------------

    [Fact]
    public void SceneCountFloor_FortyScenesFails()
    {
        // Video #3: 40 scenes / 4,236 chars — both floors must catch a
        // script like that.
        var ex = Assert.ThrowsAny<Exception>(() =>
            InvokeStatic(ReflectionHelper.VideoGen, "ValidateLongFormFloors", 40, 4236));
        Assert.Contains("Long-form script is far too short", ex.Message);
        Assert.Contains("50", ex.Message);
    }

    [Fact]
    public void SceneCountFloor_FailsRegardlessOfCharCount()
    {
        // A 40-scene script at 150 chars/scene (6,000 chars) passes the
        // old chars-only floor but still misses the 50–60 scene target.
        var ex = Assert.ThrowsAny<Exception>(() =>
            InvokeStatic(ReflectionHelper.VideoGen, "ValidateLongFormFloors", 40, 9000));
        Assert.Contains("50", ex.Message);
    }

    [Fact]
    public void SceneCountFloor_FiftyScenesPasses()
    {
        InvokeStatic(ReflectionHelper.VideoGen, "ValidateLongFormFloors", 50, 7500);
        // 50 scenes at 6,000 chars: count floor passes, chars only warn.
        InvokeStatic(ReflectionHelper.VideoGen, "ValidateLongFormFloors", 50, 6000);
    }

    [Fact]
    public void CharFloor_StillEnforced()
    {
        // The existing <5,500-char hard fail is unchanged: 55 scenes but
        // only 5,499 chars still fails.
        var ex = Assert.ThrowsAny<Exception>(() =>
            InvokeStatic(ReflectionHelper.VideoGen, "ValidateLongFormFloors", 55, 5499));
        Assert.Contains("Long-form script is far too short", ex.Message);
    }

    [Fact]
    public void Video3Script_FailsBothFloors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Video3ScriptPath));
        var scenes = doc.RootElement.GetProperty("Scenes");
        var totalChars = scenes.EnumerateArray()
            .Sum(s => s.GetProperty("Narration").GetString()!.Length);
        Assert.Equal(40, scenes.GetArrayLength());
        Assert.Equal(4236, totalChars);
        var ex = Assert.ThrowsAny<Exception>(() =>
            InvokeStatic(ReflectionHelper.VideoGen, "ValidateLongFormFloors",
                scenes.GetArrayLength(), totalChars));
        Assert.Contains("Long-form script is far too short", ex.Message);
    }

    [Fact]
    public void FloorFailure_IsFloorFailureDiscriminates()
    {
        // Only a floor failure invalidates the partial — a provider
        // outage mid-act must keep it so the prep resumes from the
        // checkpoint instead of regenerating finished acts.
        var isFloorFailure = ReflectionHelper.FindMethod(ReflectionHelper.VideoGen, "IsFloorFailure");
        bool Check(string message) =>
            (bool)isFloorFailure.Invoke(null, [new Exception(message)])!;
        Assert.True(Check("Long-form script is far too short: 40 scenes, 4236 chars of narration"));
        Assert.True(Check("Long-form script is far too short: 40 scenes, 9000 chars of narration"));
        Assert.False(Check("Groq request failed with HTTP 429"));
        Assert.False(Check("All script providers failed (3)"));
    }

    [Fact]
    public void FloorFailure_DeletesPartialFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prep-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "script-partial.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "script-progress.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "keep");
            InvokeStatic(ReflectionHelper.VideoGen, "DeletePartialScriptFiles", dir);
            Assert.False(File.Exists(Path.Combine(dir, "script-partial.json")));
            Assert.False(File.Exists(Path.Combine(dir, "script-progress.json")));
            Assert.True(File.Exists(Path.Combine(dir, "keep.txt")),
                "Only the partial checkpoint files are deleted");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FloorFailure_PrepPathsInvalidatePartial()
    {
        // Both --prep entry points that generate from a partial must
        // delete it when the floor fails, so the driver relaunches a
        // FRESH prep instead of resuming the same thin acts verbatim.
        var program = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "Program.cs");
        var fresh = Region(program, "async Task<int> RunPrepAsync()", "// Shared tail of --prep");
        var resumed = Region(program, "async Task<int> RunPrepResumeAsync", "// The branded intro bumper");
        foreach (var (name, region) in new[] { ("RunPrepAsync", fresh), ("RunPrepResumeAsync", resumed) })
        {
            Assert.Contains("IsFloorFailure", region);
            Assert.Contains("DeletePartialScriptFiles(workDir)", region);
            Assert.True(region.Contains("Long-form script is far too short") || region.Contains("FloorFailure"),
                $"{name} must recognise the floor failure before deleting the partial");
        }
    }

    [Fact]
    public void FloorFailure_DriverDropsPartialOnFloorMarker()
    {
        // Belt-and-braces in the driver: if the prep log carries the
        // floor-failure line and the partial is still on disk (killed
        // mid-cleanup, old binary), the driver drops it before deciding
        // resume-vs-relaunch — a resume would fail the same floor again.
        var driver = ReadRepoFile("trendline", "driver.sh");
        Assert.Contains("Long-form script is far too short", driver);
        Assert.Contains("script-partial.json", driver);
        var idx = driver.IndexOf("Long-form script is far too short", StringComparison.Ordinal);
        var window = driver[idx..Math.Min(driver.Length, idx + 600)];
        Assert.Contains("rm -f", window);
        Assert.Contains("script-partial.json", window);
    }

    // ------------------------------------------------------------------
    // B7 — FallbackTtsProvider (Azure helper -> Google helper for en)
    // ------------------------------------------------------------------

    private static object NewGenConfig(string azureHelper, string googleHelper)
    {
        var cfg = Activator.CreateInstance(GenConfigType, nonPublic: true)!;
        SetProp(cfg, "AzureTtsHelperPath", azureHelper);
        SetProp(cfg, "GoogleTtsHelperPath", googleHelper);
        SetProp(cfg, "AzureSpeechRegion", "eastus");
        return cfg;
    }

    private static object NewProvider(string typeName, params object?[] args)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, typeName);
        return Activator.CreateInstance(type,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            binder: null, args: args, culture: null)!;
    }

    private static object NewFallbackTts(IReadOnlyList<object> providers)
    {
        var itts = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "ITtsProvider");
        var listType = typeof(List<>).MakeGenericType(itts);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var p in providers) list.Add(p);
        var fallbackType = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "FallbackTtsProvider");
        return Activator.CreateInstance(fallbackType,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            binder: null, args: [list], culture: null)!;
    }

    private static void Synthesize(object provider, string outPath)
    {
        var method = provider.GetType().GetMethod("SynthesizeAsync")
            ?? throw new MissingMethodException(provider.GetType().Name, "SynthesizeAsync");
        var task = (Task)method.Invoke(provider,
            ["test narration for the fallback check", "en", outPath, "+10%", "-4%", null])!;
        Block(task);
    }

    private static string WriteHelperScript(string dir, string name, bool succeed)
    {
        var path = Path.Combine(dir, name);
        if (succeed)
        {
            File.WriteAllText(path,
                "import os, sys\n" +
                "out = sys.argv[sys.argv.index('--out') + 1]\n" +
                "open(out, 'wb').write(b'FAKE-MP3')\n" +
                "open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'called.marker'), 'w').write('called')\n");
        }
        else
        {
            // NOTE: no 429/throttle wording — a throttled Azure call is
            // retried with backoff inside the provider, not a failure.
            File.WriteAllText(path,
                "import sys\n" +
                "sys.stderr.write('simulated provider outage\\n')\n" +
                "sys.exit(1)\n");
        }
        return path;
    }

    private static string CaptureConsole(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }

    [Fact]
    public void TtsFallback_AzureFailsGoogleUsed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prep-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var azureDir = Path.Combine(dir, "azure"); Directory.CreateDirectory(azureDir);
            var googleDir = Path.Combine(dir, "google"); Directory.CreateDirectory(googleDir);
            var cfg = NewGenConfig(
                WriteHelperScript(azureDir, "fail.py", succeed: false),
                WriteHelperScript(googleDir, "ok.py", succeed: true));
            var voices = new Dictionary<string, (string Locale, string Voice, string? Style)>
            {
                ["en"] = ("en-US", "en-US-DavisNeural", null),
            };
            var fallback = NewFallbackTts([
                NewProvider("AzureHelperTtsProvider", cfg, voices),
                NewProvider("GoogleHelperTtsProvider", cfg),
            ]);
            var outPath = Path.Combine(dir, "scene00.mp3");
            var log = CaptureConsole(() => Synthesize(fallback, outPath));

            Assert.True(File.Exists(outPath) && new FileInfo(outPath).Length > 0,
                "Fallback must produce the audio file via Google");
            Assert.True(File.Exists(Path.Combine(googleDir, "called.marker")),
                "Google helper must actually have been invoked");
            Assert.Contains("GoogleHelperTtsProvider", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TtsFallback_AllFailThrowsListingBoth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prep-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var azureDir = Path.Combine(dir, "azure"); Directory.CreateDirectory(azureDir);
            var googleDir = Path.Combine(dir, "google"); Directory.CreateDirectory(googleDir);
            var cfg = NewGenConfig(
                WriteHelperScript(azureDir, "fail.py", succeed: false),
                WriteHelperScript(googleDir, "fail.py", succeed: false));
            var voices = new Dictionary<string, (string Locale, string Voice, string? Style)>
            {
                ["en"] = ("en-US", "en-US-DavisNeural", null),
            };
            var fallback = NewFallbackTts([
                NewProvider("AzureHelperTtsProvider", cfg, voices),
                NewProvider("GoogleHelperTtsProvider", cfg),
            ]);
            var ex = Assert.ThrowsAny<Exception>(() =>
                Synthesize(fallback, Path.Combine(dir, "scene00.mp3")));
            Assert.Contains("AzureHelperTtsProvider", ex.Message);
            Assert.Contains("GoogleHelperTtsProvider", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TtsFallback_AzureSuccessDoesNotCallGoogle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prep-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var azureDir = Path.Combine(dir, "azure"); Directory.CreateDirectory(azureDir);
            var googleDir = Path.Combine(dir, "google"); Directory.CreateDirectory(googleDir);
            var cfg = NewGenConfig(
                WriteHelperScript(azureDir, "ok.py", succeed: true),
                WriteHelperScript(googleDir, "ok.py", succeed: true));
            var voices = new Dictionary<string, (string Locale, string Voice, string? Style)>
            {
                ["en"] = ("en-US", "en-US-DavisNeural", null),
            };
            var fallback = NewFallbackTts([
                NewProvider("AzureHelperTtsProvider", cfg, voices),
                NewProvider("GoogleHelperTtsProvider", cfg),
            ]);
            var outPath = Path.Combine(dir, "scene00.mp3");
            var log = CaptureConsole(() => Synthesize(fallback, outPath));

            Assert.True(File.Exists(outPath));
            Assert.False(File.Exists(Path.Combine(googleDir, "called.marker")),
                "Google must not be called when Azure succeeds");
            Assert.Contains("AzureHelperTtsProvider", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TtsFallback_FactoryWiresFallbackHelper()
    {
        // Same wiring shape as pool-helper for scripts: a named mode that
        // lists Azure first, Google second, in order.
        var factory = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "TtsProviderFactory.cs");
        var idx = factory.IndexOf("\"fallback-helper\"", StringComparison.Ordinal);
        Assert.True(idx >= 0, "fallback-helper mode missing from TtsProviderFactory");
        var azureIdx = factory.IndexOf("AzureHelperTtsProvider", idx, StringComparison.Ordinal);
        var googleIdx = factory.IndexOf("GoogleHelperTtsProvider", idx, StringComparison.Ordinal);
        Assert.True(azureIdx > 0 && googleIdx > azureIdx,
            "fallback-helper must list Azure first, then Google");
        Assert.Contains("FallbackTtsProvider", factory[idx..]);
    }

    [Fact]
    public void TtsFallback_IsLiveDefault()
    {
        // The live default mirrors pool-helper for scripts: a prep
        // launched without an env override uses the fallback pool.
        var settings = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "appsettings.Local.json");
        using var doc = JsonDocument.Parse(settings);
        Assert.Equal("fallback-helper", doc.RootElement.GetProperty("TtsProvider").GetString());
    }
}
