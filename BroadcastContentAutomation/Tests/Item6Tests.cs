// Regression tests for fix sequence #2 ITEM 6 (remaining bugs B6, B8–B11,
// B14–B16, B18, B21–B29, B31, R4–R8).
//
// Shell fixes (driver.sh / healthcheck.sh / render-chunks.sh) are tested
// by extracting the changed function verbatim from the script and running
// it against scratch dirs — the same pattern ITEM 4 used for
// prep_complete. C# fixes are tested through ReflectionHelper (VideoGen /
// Uploader internals) or directly against public Shared types. Python
// fixes (finish-chunks.py R5/R6/B29, chunk_ranges.py B24) are tested in
// production/test_finish_chunks.py; the C# side pins them with source
// guards here so a drift in either writer fails loudly.
//
// The sandbox runner invokes [Fact] methods without awaiting a returned
// Task, so these tests are synchronous and block on async calls with
// GetAwaiter().GetResult().
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;
using GiggleGarden.Uploader;
using GiggleGarden.Uploader.Publishing;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;

namespace BroadcastContentAutomation.Tests;

public class Item6Tests
{
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

    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tn-item6-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Logger NewLogger(string dir) => new(Path.Combine(dir, "logs"));

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

    private static T Block<T>(Task<T> task) => task.GetAwaiter().GetResult();

    // --- bash harness ----------------------------------------------------
    // Extract one shell function verbatim from a script file (brace-depth
    // scan from its "name() {" line to the matching close) so the test
    // runs the REAL function, not a copy.
    private static string ExtractBashFunction(string script, string name)
    {
        var start = script.IndexOf(name + "() {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"bash function {name} not found");
        var lines = script[start..].Split('\n');
        var depth = 0;
        var taken = new List<string>();
        foreach (var line in lines)
        {
            taken.Add(line);
            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
            if (taken.Count > 1 && depth == 0) break;
        }
        Assert.True(depth == 0, $"bash function {name} never closes");
        return string.Join('\n', taken);
    }

    private static (int Exit, string Out, string Err) RunBash(string script)
    {
        var psi = new ProcessStartInfo("bash", ["-c", script])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    private static string DriverScript => ReadRepoFile("trendline", "driver.sh");
    private static string HealthcheckScript => ReadRepoFile("trendline", "healthcheck.sh");
    private static string ThrottleScript => ReadRepoFile("trendline", "throttle.sh");

    // log() stub the extracted driver functions call (the real one tees
    // into the driver log; tests just need the text on stdout).
    private const string LogStub = "log() { echo \"[test-log] $*\"; }\n";

    // ------------------------------------------------------------------
    // B8 — backpressure: no new prep while a completed video waits
    // ------------------------------------------------------------------

    [Fact]
    public void Backpressure_UnapprovedCount_CountsOnlyUnapprovedSidecars()
    {
        // Watch dir with: one Approved:true video, one Approved:false
        // video, one mp4 with no sidecar at all (finish not done — not
        // counted), one Approved:false video. Expect exactly 2.
        var dir = TempDir("b8-count");
        try
        {
            var watch = Path.Combine(dir, "trendlinenow");
            Directory.CreateDirectory(watch);
            File.WriteAllText(Path.Combine(watch, "a-en.mp4"), "");
            File.WriteAllText(Path.Combine(watch, "a-en.json"), """{"Approved": true}""");
            File.WriteAllText(Path.Combine(watch, "b-en.mp4"), "");
            File.WriteAllText(Path.Combine(watch, "b-en.json"), """{"Approved": false}""");
            File.WriteAllText(Path.Combine(watch, "c-en.mp4"), "");
            File.WriteAllText(Path.Combine(watch, "d-en.mp4"), "");
            File.WriteAllText(Path.Combine(watch, "d-en.json"), """{"Approved": null}""");

            var fn = ExtractBashFunction(DriverScript, "unapproved_completed_count");
            var script = LogStub + fn + $"\nunapproved_completed_count \"{dir}\"";
            var (exit, stdout, _) = RunBash(script);
            Assert.Equal(0, exit);
            Assert.Equal("2", stdout.Trim());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Backpressure_UnapprovedCount_ZeroWhenAllApproved()
    {
        var dir = TempDir("b8-zero");
        try
        {
            var watch = Path.Combine(dir, "trendlinenow");
            Directory.CreateDirectory(watch);
            File.WriteAllText(Path.Combine(watch, "a-en.mp4"), "");
            File.WriteAllText(Path.Combine(watch, "a-en.json"), """{"Approved": true}""");

            var fn = ExtractBashFunction(DriverScript, "unapproved_completed_count");
            var script = LogStub + fn + $"\nunapproved_completed_count \"{dir}\"";
            var (exit, stdout, _) = RunBash(script);
            Assert.Equal(0, exit);
            Assert.Equal("0", stdout.Trim());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Backpressure_DriverGatesIdleOnUnapproved()
    {
        // The idle branch must consult the count and refuse to start new
        // work while any completed video waits for review.
        Assert.Contains("UNAPPROVED=$(unapproved_completed_count)", DriverScript);
        Assert.Contains("backpressure:", DriverScript);
        Assert.Contains("not starting new work until one is approved/published", DriverScript);
    }

    // ------------------------------------------------------------------
    // B6 — silent --assemble deaths leave memory + frame + FATAL evidence
    // ------------------------------------------------------------------

    [Fact]
    public void SilentDeath_LastRenderFrame_ExtractsLastFrame()
    {
        var dir = TempDir("b6-frame");
        try
        {
            var logPath = Path.Combine(dir, "assemble.log");
            File.WriteAllText(logPath,
                "start\nRendered 127/2430\nRendered 128/2430\nRendered 129/2430\n");
            var fn = ExtractBashFunction(DriverScript, "last_render_frame");
            var (exit, stdout, _) = RunBash(LogStub + fn + $"\nlast_render_frame \"{logPath}\"");
            Assert.Equal(0, exit);
            Assert.Equal("129/2430", stdout.Trim());

            var emptyPath = Path.Combine(dir, "empty.log");
            File.WriteAllText(emptyPath, "no frames here\n");
            var (_, stdout2, _) = RunBash(LogStub + fn + $"\nlast_render_frame \"{emptyPath}\"");
            Assert.Equal("none", stdout2.Trim());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SilentDeath_MemoryContext_LogsFreeMemory()
    {
        var fn = ExtractBashFunction(DriverScript, "log_memory_context");
        var (exit, stdout, _) = RunBash(LogStub + fn + "\nlog_memory_context \"test-tag\"");
        Assert.Equal(0, exit);
        Assert.Contains("memory[test-tag]:", stdout);
        Assert.Contains("mem total=", stdout);
    }

    [Fact]
    public void SilentDeath_DriverDeathPath_LogsFatalWithFrame()
    {
        // The render_assemble death path must record the memory picture,
        // the last frame, and a FATAL line naming the job — the frame
        // 129/2430 death left none of these.
        Assert.Contains("log_memory_context \"assemble-dead", DriverScript);
        Assert.Contains("LAST_FRAME=$(last_render_frame", DriverScript);
        Assert.Contains("FATAL: --assemble for $JOB_ID", DriverScript);
        Assert.Contains("log_memory_context \"before-assemble", DriverScript);
    }

    // ------------------------------------------------------------------
    // B15 — one shared 429 rule (whole log, same pattern in both scripts)
    // ------------------------------------------------------------------

    [Fact]
    public void ThrottleRule_SharedByBothScripts()
    {
        Assert.Contains("THROTTLE_PATTERN=", ThrottleScript);
        Assert.Contains("prepayment credits are depleted", ThrottleScript);
        Assert.Contains("HTTP 429", ThrottleScript);
        Assert.Contains(". \"$THROTTLE_SH\"", DriverScript);
        Assert.Contains(". \"$THROTTLE_SH\"", HealthcheckScript);
        Assert.Contains("log_has_throttle \"$STEP_LOG\"", DriverScript);
        Assert.Contains("log_has_throttle \"$CURRENT_LOG\"", HealthcheckScript);
        // No script keeps its own inline 429 pattern for the throttle check.
        Assert.DoesNotContain("tail -200 \"$STEP_LOG\"", DriverScript);
        Assert.DoesNotContain("tail -200 \"$CURRENT_LOG\"", HealthcheckScript);
    }

    [Fact]
    public void ThrottleRule_WholeLogFindsOldMarker_NoFrameCounterFalsePositive()
    {
        var dir = TempDir("b15");
        try
        {
            // Marker on line 1 of a 300-line log: the old tail-200 check
            // missed it; the whole-log check must find it.
            var oldLog = Path.Combine(dir, "old.log");
            var lines = new List<string> { "HTTP 429 Too Many Requests" };
            lines.AddRange(Enumerable.Repeat("Rendered 100/2430", 299));
            File.WriteAllLines(oldLog, lines);
            // Frame counters alone must NOT read as a throttle.
            var framesLog = Path.Combine(dir, "frames.log");
            File.WriteAllLines(framesLog, Enumerable.Repeat("Rendered 429/2633", 50));

            var script = ThrottleScript + $"\nlog_has_throttle \"{oldLog}\" && echo FOUND-OLD";
            var (exit, stdout, _) = RunBash(script);
            Assert.Equal(0, exit);
            Assert.Contains("FOUND-OLD", stdout);

            var script2 = ThrottleScript + $"\nlog_has_throttle \"{framesLog}\" && echo FOUND-FRAMES || echo NO-THROTTLE";
            var (_, stdout2, _) = RunBash(script2);
            Assert.Contains("NO-THROTTLE", stdout2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // B25 — manual-render collision guard on every launch site
    // ------------------------------------------------------------------

    [Fact]
    public void CollisionGuard_LaunchSitesCheckProcessTable()
    {
        Assert.Contains("pipeline_step_running \"$JOB_ID\"", DriverScript);
        // prep_done launch, prep resume/relaunch, and the helper itself.
        Assert.Equal(2, DriverScript.Split("pipeline_step_running \"$JOB_ID\"").Length - 1);
        Assert.Contains("pgrep -f \"VideoGen.dll --assemble.*$job\"", DriverScript);
        Assert.Contains("pgrep -f \"VideoGen.dll --prep.*$job\"", DriverScript);
        Assert.Contains("pgrep -f \"remotion render.*$job\"", DriverScript);
    }

    [Fact]
    public void CollisionGuard_SeesManualStep_AndClearsWhenGone()
    {
        // NOTE: the guard MUST be run from a script FILE, not via
        // `bash -c` with the function source inline: pgrep -f matches a
        // process's own command line, and the function source contains
        // the pattern text ("VideoGen.dll --assemble.*$job" — where the
        // mid-pattern $ is a literal $, so the line matches its own
        // regex), so an inline run self-matches and never reports CLEAR.
        // The production driver is a file (cmdline "bash driver.sh"),
        // so it has no such self-match; the test runs it the same way.
        var dir = TempDir("b25-live");
        try
        {
            var fn = ExtractBashFunction(DriverScript, "pipeline_step_running");
            var job = "job-item6-test";
            var scriptPath = Path.Combine(dir, "guard.sh");
            File.WriteAllText(scriptPath, LogStub + fn +
                $"\npipeline_step_running \"{job}\" && echo SEEN || echo CLEAR\n");
            // A process whose command line carries the pattern (the manual
            // --assemble takeover shape) must be seen. NOTE: the stand-in
            // is python, not `bash -c 'sleep 60 # ...'`: bash execs the
            // sleep with a fresh argv (the # comment never survives into
            // the process table), so that shape is invisible to pgrep and
            // the earlier inline version only "saw" it via self-match.
            // python keeps its -c string verbatim in argv, like a real
            // `dotnet VideoGen.dll --assemble --job X` command line.
            var sleeper = Process.Start(new ProcessStartInfo("python3",
                ["-c", $"import time; time.sleep(60)  # VideoGen.dll --assemble --job {job}"])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            try
            {
                var seen = RunBash($"bash \"{scriptPath}\"");
                Assert.Contains("SEEN", seen.Out);
            }
            finally
            {
                sleeper.Kill(entireProcessTree: true);
                sleeper.WaitForExit();
            }
            // ...and once it is gone the guard must clear.
            var clear = RunBash($"bash \"{scriptPath}\"");
            Assert.Contains("CLEAR", clear.Out);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // B16 — state.json hygiene (mktemp writes, live top-level updated_at)
    // ------------------------------------------------------------------

    [Fact]
    public void StateHygiene_SaveState_UsesMktempAndUpdatesTopLevel()
    {
        var dir = TempDir("b16");
        try
        {
            var state = Path.Combine(dir, "state.json");
            File.WriteAllText(state,
                """{"paused": false, "updated_at": "2026-11-21T19:45:53-05:00", "current": {"phase": "idle", "updated_at": "2026-11-21T19:45:53-05:00"}}""");
            var fn = ExtractBashFunction(DriverScript, "save_state");
            var script = LogStub + fn + $"\nSTATE=\"{state}\"\nsave_state '.current.phase = \"prep\"'";
            var (exit, _, stderr) = RunBash(script);
            Assert.Equal(0, exit);
            Assert.DoesNotContain("No such file", stderr);
            using var doc = JsonDocument.Parse(File.ReadAllText(state));
            Assert.Equal("prep", doc.RootElement.GetProperty("current").GetProperty("phase").GetString());
            Assert.NotEqual("2026-11-21T19:45:53-05:00",
                doc.RootElement.GetProperty("updated_at").GetString());
            Assert.False(File.Exists(state + ".tmp"), "no shared $STATE.tmp file must remain");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StateHygiene_HealthcheckWritesViaMktemp()
    {
        Assert.Contains("mktemp \"$STATE.tmp.XXXXXX\"", HealthcheckScript);
        Assert.DoesNotContain("> \"$STATE.tmp\"", HealthcheckScript);
        Assert.Contains(".updated_at = $ts", HealthcheckScript);
        Assert.DoesNotContain("> \"$STATE.tmp\"", DriverScript);
    }

    // ------------------------------------------------------------------
    // B26 — retention: keep 5 most recent, prune old finished jobs only
    // ------------------------------------------------------------------

    [Fact]
    public void Retention_PruneKeepsRecentProtectedUnknown_PruneOldFinished()
    {
        var dir = TempDir("b26");
        try
        {
            var work = Path.Combine(dir, "work", "trendlinenow");
            Directory.CreateDirectory(work);
            var state = Path.Combine(dir, "state.json");
            var completed = new List<string>();
            // 8 job dirs: newest first by mtime (minutes ago).
            var ages = new[] { 1, 2, 3, 4, 5, 6, 8 * 24 * 60, 9 * 24 * 60 };
            for (var i = 0; i < ages.Length; i++)
            {
                var job = $"job-20261120-00000{i}";
                var jobDir = Path.Combine(work, job);
                Directory.CreateDirectory(jobDir);
                File.WriteAllText(Path.Combine(jobDir, "script.json"), "{}");
                File.SetLastWriteTimeUtc(jobDir, DateTime.UtcNow.AddMinutes(-ages[i]));
                // The two oldest are completed in state.json (prunable
                // once past the grace period); the rest are recent keeps.
                if (i >= 6) completed.Add(job);
            }
            // Protected job (old, must be kept) + current job (old, kept).
            var protectedJob = "job-20261121-085638";
            Directory.CreateDirectory(Path.Combine(work, protectedJob));
            File.SetLastWriteTimeUtc(Path.Combine(work, protectedJob), DateTime.UtcNow.AddDays(-30));
            var currentJob = "job-20261120-000000";
            File.SetLastWriteTimeUtc(Path.Combine(work, currentJob), DateTime.UtcNow.AddDays(-30));

            var completedJson = string.Join(",", completed.Select(j => $"{{\"job\": \"{j}\"}}"));
            File.WriteAllText(state,
                $"{{\"paused\": true, \"current\": {{\"job\": \"{currentJob}\", \"phase\": \"idle\"}}, " +
                $"\"completed\": [{completedJson}], \"abandoned\": []}}");

            // An old job state.json knows nothing about: never pruned,
            // however old — its provenance is unclear.
            var unknownJob = "job-20261120-000008";
            Directory.CreateDirectory(Path.Combine(work, unknownJob));
            File.SetLastWriteTimeUtc(Path.Combine(work, unknownJob), DateTime.UtcNow.AddDays(-10));

            var fn = ExtractBashFunction(DriverScript, "prune_old_jobs");
            var script = LogStub + fn + "\n" +
                $"REPO=\"{dir}\"\nSTATE=\"{state}\"\nPROTECTED_JOB=\"{protectedJob}\"\n" +
                $"JOB_ID=\"{currentJob}\"\nTMPDIR=\"{dir}\"\n" +
                "prune_old_jobs";
            var (exit, stdout, _) = RunBash(script);
            Assert.Equal(0, exit);
            // 5 most recent kept + protected + current + unknown old job.
            for (var i = 0; i < 6; i++)
                Assert.True(Directory.Exists(Path.Combine(work, $"job-20261120-00000{i}")),
                    $"job-20261120-00000{i} must be kept");
            Assert.True(Directory.Exists(Path.Combine(work, protectedJob)), "protected job must be kept");
            Assert.True(Directory.Exists(Path.Combine(work, unknownJob)),
                "a job state.json does not know must never be pruned");
            // The two oldest completed jobs (8d, 9d) are pruned.
            Assert.False(Directory.Exists(Path.Combine(work, "job-20261120-000006")),
                "old completed job must be pruned");
            Assert.False(Directory.Exists(Path.Combine(work, "job-20261120-000007")),
                "old completed job must be pruned");
            Assert.Contains("prune: deleting old job dir", stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Retention_PolicyDocumented()
    {
        Assert.Contains("PRUNE_KEEP_JOBS=5", DriverScript);
        Assert.Contains("PRUNE_MIN_AGE_DAYS=7", DriverScript);
    }

    // ------------------------------------------------------------------
    // B11 — ArtSource recorded per scene (stock never silently art)
    // ------------------------------------------------------------------

    private static string? ParsePoolProvider(string stdout)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "ArtPoolClient");
        var method = type.GetMethod("ParseProviderName", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("ArtPoolClient.ParseProviderName");
        return (string?)method.Invoke(null, [stdout]);
    }

    [Fact]
    public void ArtSource_PoolProviderNamesMapped()
    {
        Assert.Equal("pexels-stock", ParsePoolProvider("artpool: pexels -> /tmp/scene00-art-square.jpg"));
        Assert.Equal("ovhcloud", ParsePoolProvider("artpool: ovh -> /tmp/x.jpg"));
        Assert.Equal("wikimedia-commons", ParsePoolProvider("artpool: wikimedia -> /tmp/x.jpg (Commons: File:Shah Rukh Khan.jpg | CC BY-SA 4.0)"));
        // Rebuilt pool (2026-11-24): the helper only ever returns ovh,
        // pexels or wikimedia now — the discarded-leg mappings are
        // dropped, so an unexpected name passes through raw and the
        // quality gate rejects it as an unknown source ("pollinations"
        // is still a discarded-leg error; "horde" is an unknown source)
        // instead of being relabelled.
        Assert.Equal("pollinations", ParsePoolProvider("artpool: pollinations -> /tmp/x.jpg"));
        Assert.Equal("horde", ParsePoolProvider("artpool: horde -> /tmp/x.jpg"));
    }

    [Fact]
    public void ArtSource_ParseProviderName_NullWhenNoProviderLine()
    {
        Assert.Null(ParsePoolProvider("some other helper output"));
        Assert.Null(ParsePoolProvider(""));
    }

    private static Type SceneType => ReflectionHelper.FindType(ReflectionHelper.VideoGen, "Scene");
    private static Type ScriptType => ReflectionHelper.FindType(ReflectionHelper.VideoGen, "VideoScript");

    private static object NewScene(string narration, string kind = "art", double duration = 10.0, int visualGroup = 1)
    {
        var scene = Activator.CreateInstance(SceneType, nonPublic: true)!;
        SetProp(scene, "Narration", narration);
        SetProp(scene, "SceneKind", kind);
        SetProp(scene, "DurationSeconds", duration);
        SetProp(scene, "VisualGroup", visualGroup);
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

    [Fact]
    public void ArtSource_SceneCarriesProperty_PersistsInScriptJson()
    {
        var prop = SceneType.GetProperty("ArtSource")
            ?? throw new MissingMemberException("Scene", "ArtSource");
        Assert.Equal(typeof(string), prop.PropertyType);

        var scene = NewScene("A scene.");
        SetProp(scene, "ArtSource", "pexels-stock");
        var script = NewScript(scene);
        var json = JsonSerializer.Serialize(script, Sidecar.Options);
        Assert.Contains("\"ArtSource\": \"pexels-stock\"", json);

        // Old scripts (no ArtSource key) load as unknown, not as stock.
        var plain = NewScene("A scene.");
        var plainJson = JsonSerializer.Serialize(NewScript(plain), Sidecar.Options);
        Assert.DoesNotContain("ArtSource", plainJson);
    }

    [Fact]
    public void ArtSource_VisualLoopRecordsItInEveryBranch()
    {
        var program = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "Program.cs");
        Assert.Contains("scene.ArtSource = \"pexels-stock\"", program);
        Assert.Contains("scene.ArtSource = \"remotion-visual\"", program);
        Assert.Contains("scene.ArtSource = \"manual\"", program);
        Assert.Contains("if (artSource is not null) scene.ArtSource = artSource;", program);
        Assert.Contains("used Pexels stock (real photo/clip, not AI-generated art)", program);
    }

    [Fact]
    public void ArtSource_PropsCarryIt()
    {
        var assembler = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "VideoAssembler.cs");
        Assert.Contains("bool IsSpeculative = false, string? ArtSource = null", assembler);
        Assert.Contains("ArtSource: s.ArtSource", assembler);
    }

    [Fact]
    public void ArtSource_Video3HasNone_UnknownNotStock()
    {
        // Video #3 shipped before ArtSource existed: no scene carries
        // one, so the field reads as "unknown" for old scripts — a
        // reviewer must not mistake an old script's silence for stock.
        var path = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261122-115430/script.json";
        if (!File.Exists(path)) return; // skip gracefully if the job is gone
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("ArtSource", text);
    }

    // ------------------------------------------------------------------
    // B14 — thumbnail prefix scan matches only the video's own thumb
    // ------------------------------------------------------------------

    private static string StripShortSuffix(string baseName)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.Uploader, "YouTubePublisher");
        var method = type.GetMethod("StripShortSuffix", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("YouTubePublisher.StripShortSuffix");
        return (string)method.Invoke(null, [baseName])!;
    }

    [Fact]
    public void Thumbnail_StripShortSuffix_ExactSuffixOnly()
    {
        Assert.Equal("slug-en", StripShortSuffix("slug-en-short"));
        Assert.Equal("slug-en-short-v2", StripShortSuffix("slug-en-short-v2"));
        Assert.Equal("slug-en", StripShortSuffix("slug-en"));
    }

    [Fact]
    public void Thumbnail_Scan_IgnoresOtherVideosThumbs()
    {
        var dir = TempDir("b14");
        try
        {
            var video = Path.Combine(dir, "a-en-short.mp4");
            File.WriteAllText(video, "");
            // Another cut's thumb + another video's thumb: neither is this
            // video's own, so the scan must return null, not the first
            // prefix match.
            File.WriteAllText(Path.Combine(dir, "a-en-short-v2.thumb.jpg"), "");
            File.WriteAllText(Path.Combine(dir, "b-en.thumb.jpg"), "");
            var sidecar = new Sidecar();
            var type = ReflectionHelper.FindType(ReflectionHelper.Uploader, "YouTubePublisher");
            var method = type.GetMethod("ResolveThumbnailPath", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException("YouTubePublisher.ResolveThumbnailPath");
            var result = (string?)method.Invoke(null, [video, sidecar]);
            Assert.Null(result);

            // This video's own short thumb (found by the scan, not the
            // sibling path, which looks for a-en.thumb.jpg) must match.
            File.WriteAllText(Path.Combine(dir, "a-en-short.thumb.jpg"), "");
            result = (string?)method.Invoke(null, [video, sidecar]);
            Assert.Equal(Path.Combine(dir, "a-en-short.thumb.jpg"), result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // B18 — CaptionFor must not leak the CC BY music credit
    // ------------------------------------------------------------------

    [Fact]
    public void CaptionFor_StripsMusicCreditLine()
    {
        var sidecar = new Sidecar
        {
            Description = "GTA 6 story." + "\n\nMusic: \"Rising Light (Uplifting Orchestral)\" by AurosonMusic — https://example.test/x — CC BY 4.0\n\n"
                + Sidecar.TrendlineDisclaimer + "\n\n" + Sidecar.TrendlineFooter,
        };
        var caption = sidecar.CaptionFor("youtube", 1000);
        Assert.DoesNotContain("CC BY", caption);
        Assert.DoesNotContain("Music:", caption);
        Assert.DoesNotContain("Disclaimer:", caption);
        Assert.Contains("GTA 6 story.", caption);
    }

    [Fact]
    public void CaptionFor_ProseMentioningMusicIsUntouched()
    {
        // Only a real credit line (own paragraph, "Music: " + "CC BY")
        // is cut — a description that merely talks about music keeps it.
        var sidecar = new Sidecar
        {
            Description = "The scene's background music sets the mood. Full story here.",
        };
        Assert.Contains("background music sets the mood", sidecar.CaptionFor("youtube", 1000));
    }

    // ------------------------------------------------------------------
    // B21 — no-sidecar path fails fast with a clear message (trendlinenow)
    // ------------------------------------------------------------------

    [Fact]
    public void NoSidecar_TrendlineNow_FailsFastWithClearMessage()
    {
        var dir = TempDir("b21");
        try
        {
            var video = Path.Combine(dir, "trendlinenow", "slug-en.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(video)!);
            File.WriteAllText(video, "");
            var cfg = Activator.CreateInstance(
                ReflectionHelper.FindType(ReflectionHelper.Uploader, "AppConfig"))!;
            var log = NewLogger(dir);
            var emptyPublishers = new Dictionary<string, IReadOnlyDictionary<string, IPublisher>>();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Block((Task<Sidecar>)InvokeStatic(ReflectionHelper.Uploader, "ResolveSidecarAsync",
                    video, "trendlinenow", emptyPublishers, cfg, log)!));
            Assert.Contains("SIDECAR_REQUIRED", ex.Message);
            Assert.Contains("sidecar", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("No free metadata provider configured", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoSidecar_FailFastIsTrendlineOnly()
    {
        // The SIDECAR_REQUIRED throw must sit inside the trendlinenow
        // gate — other channels keep the Groq/Gemini fallback path — and
        // the per-video catch must move it straight to failed/ without
        // burning crash retries.
        var program = ReadRepoFile("BroadcastContentAutomation", "Uploader", "Program.cs");
        var idx = program.IndexOf("SIDECAR_REQUIRED:", StringComparison.Ordinal);
        Assert.True(idx > 0, "SIDECAR_REQUIRED throw missing");
        Assert.Contains("trendlinenow", program[..idx]);
        Assert.Contains("Not retrying", program);
    }

    // ------------------------------------------------------------------
    // B27 — MinFileAgeSeconds skip is logged by name
    // ------------------------------------------------------------------

    [Fact]
    public void MinAgeFilter_LogsSkippedByName_KeepsOldFiles()
    {
        var dir = TempDir("b27");
        try
        {
            var young = Path.Combine(dir, "young-en.mp4");
            var old = Path.Combine(dir, "old-en.mp4");
            File.WriteAllText(young, "");
            File.WriteAllText(old, "");
            File.SetLastWriteTimeUtc(young, DateTime.UtcNow.AddSeconds(-10));
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddSeconds(-200));

            var skipped = new List<string>();
            var files = new List<(string Path, string Channel)>
            {
                (young, "trendlinenow"),
                (old, "trendlinenow"),
            };
            var ready = (List<(string Path, string Channel)>)InvokeStatic(ReflectionHelper.Uploader,
                "FilterByMinAge", files, 90, DateTimeOffset.UtcNow, (Action<string>)(m => skipped.Add(m)))!;
            Assert.Single(ready);
            Assert.Equal(old, ready[0].Path);
            Assert.Single(skipped);
            Assert.Contains("young-en.mp4", skipped[0]);
            Assert.Contains("90s minimum file age", skipped[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // B28 — uploader single-instance lock
    // ------------------------------------------------------------------

    [Fact]
    public void SingleInstanceLock_SecondAcquireFails_UntilReleased()
    {
        var dir = TempDir("b28");
        try
        {
            var lockPath = Path.Combine(dir, "uploader.lock");
            var type = ReflectionHelper.FindType(ReflectionHelper.Uploader, "SingleInstanceLock");
            var acquire = type.GetMethod("TryAcquire", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("SingleInstanceLock.TryAcquire");
            var first = (IDisposable)acquire.Invoke(null, [lockPath])!;
            Assert.NotNull(first);
            var second = acquire.Invoke(null, [lockPath]);
            Assert.Null(second);
            first.Dispose();
            var third = acquire.Invoke(null, [lockPath]);
            Assert.NotNull(third);
            ((IDisposable)third!).Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SingleInstanceLock_MainAcquiresIt()
    {
        var program = ReadRepoFile("BroadcastContentAutomation", "Uploader", "Program.cs");
        Assert.Contains("SingleInstanceLock.TryAcquire", program);
        Assert.Contains("Another uploader run is already active", program);
    }

    // ------------------------------------------------------------------
    // B31 — YouTube quota estimate logged per run (no remaining API)
    // ------------------------------------------------------------------

    [Fact]
    public void QuotaEstimate_MathPerDispatch()
    {
        Assert.Equal(0, (int)InvokeStatic(ReflectionHelper.Uploader, "EstimateYouTubeQuotaUnits", 0)!);
        Assert.Equal(1700, (int)InvokeStatic(ReflectionHelper.Uploader, "EstimateYouTubeQuotaUnits", 1)!);
        Assert.Equal(3400, (int)InvokeStatic(ReflectionHelper.Uploader, "EstimateYouTubeQuotaUnits", 2)!);
    }

    [Fact]
    public void QuotaEstimate_LoggedEveryRun()
    {
        var program = ReadRepoFile("BroadcastContentAutomation", "Uploader", "Program.cs");
        Assert.Contains("YouTube quota estimate this run:", program);
        Assert.Contains("the API exposes no remaining-quota endpoint", program);
    }

    // ------------------------------------------------------------------
    // B9 / B10 / B22 / B23 / B24 / B29 — guards + docs
    // ------------------------------------------------------------------

    [Fact]
    public void SecretsBackup_VerifyScriptAndRestoreDocAgreeWithReality()
    {
        // The bundle is stale (pre-Trendline Now): the verify script must
        // say so, and RESTORE.md must record exactly that. If the bundle
        // is ever rebuilt, this test must flip with it — the doc and the
        // script must never disagree.
        var psi = new ProcessStartInfo("python3",
            ["secrets-store/verify-uploader-secrets.py"])
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var restore = ReadRepoFile("secrets-store", "RESTORE.md");
        if (p.ExitCode == 0)
        {
            Assert.DoesNotContain("STALE — the bundle does NOT cover Trendline Now", restore);
        }
        else
        {
            Assert.Contains("MISSING in bundle: trendlinenow.Facebook.AccessToken", stdout);
            Assert.Contains("STALE — the bundle does NOT cover Trendline Now", restore);
            Assert.Contains("rebuild the bundle", restore);
        }
    }

    [Fact]
    public void DualConfig_PrepEnvRemovedFromConfig()
    {
        var text = ReadRepoFile("trendline", "config.json");
        Assert.DoesNotContain("\"prep_env\"", text);
        Assert.Contains("driver.sh", text);
        Assert.Contains("never read it", text);
    }

    [Fact]
    public void HealthcheckProbes_Only2xxCountsAsReachable()
    {
        Assert.Contains("2*) note \"$svc endpoint reachable", HealthcheckScript);
        Assert.Contains("not a success", HealthcheckScript);
        Assert.DoesNotContain("|| echo \"000\"", HealthcheckScript);
        // Unreachable is a single "000" (curl failure / empty code); the
        // old "000000" artefact (exit code appended to the code) is gone.
        // The string "000000" still appears once in a comment naming that
        // artefact, so assert on the code, not the literal.
        Assert.Contains("code=\"000\"", HealthcheckScript);
        Assert.Contains("if [ $? -ne 0 ]", HealthcheckScript);
    }

    [Fact]
    public void PropsWrite_IsAtomic()
    {
        var assembler = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "VideoAssembler.cs");
        Assert.Contains("var propsTemp = propsPath + \".tmp\";", assembler);
        Assert.Contains("File.Move(propsTemp, propsPath, overwrite: true);", assembler);
    }

    [Fact]
    public void PropsHash_BoundaryOnly_KeptWhenBoundariesUnchanged()
    {
        var render = ReadRepoFile("production", "render-chunks.sh");
        Assert.Contains("props-boundary.sha256", render);
        Assert.Contains("frame boundaries are identical", render);
        Assert.Contains("--boundary-hash", render);
        var ranges = ReadRepoFile("production", "chunk_ranges.py");
        Assert.Contains("def boundary_hash(props):", ranges);
    }

    [Fact]
    public void LoudnormTemp_OutsideWatchDir()
    {
        var finish = ReadRepoFile("production", "finish-chunks.py");
        Assert.DoesNotContain("tmp = final + \".loud.mp4\"", finish);
        Assert.Contains("tempfile.gettempdir()", finish);
        Assert.Contains(".loud-", finish);
    }

    // ------------------------------------------------------------------
    // R8 — stale comments cleaned up
    // ------------------------------------------------------------------

    [Fact]
    public void StaleComments_PrepEnvCommentUpdated()
    {
        var tests = ReadRepoFile("BroadcastContentAutomation", "Tests", "ScriptPoolTests.cs");
        Assert.Contains("prep_env was removed 2026-11-22, B10", tests);
        Assert.DoesNotContain("prep_env is dead config", tests);
    }

    // ------------------------------------------------------------------
    // R4 (verify, don't re-implement) — cross-link update echoes the LIVE
    // snippet, so a manual Studio edit is never clobbered
    // ------------------------------------------------------------------

    private sealed class FakeVideo
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> Tags { get; set; } = [];
        public string CategoryId { get; set; } = "24";
    }

    private sealed class FakeYouTubeHandler : HttpMessageHandler
    {
        public Dictionary<string, FakeVideo> Videos { get; } = new(StringComparer.Ordinal);
        public List<(string Method, string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await ReadBodyAsync(request, ct);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            var path = request.RequestUri.AbsolutePath;

            if (request.Method == HttpMethod.Get && path.Contains("/youtube/v3/videos", StringComparison.Ordinal))
            {
                var id = GetQuery(request.RequestUri.Query, "id") ?? "";
                if (Videos.TryGetValue(id, out var v))
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        items = new[]
                        {
                            new
                            {
                                id,
                                snippet = new
                                {
                                    title = v.Title,
                                    description = v.Description,
                                    tags = v.Tags,
                                    categoryId = v.CategoryId,
                                    defaultLanguage = "en",
                                    defaultAudioLanguage = "en",
                                },
                            },
                        },
                    });
                    return JsonResponse(HttpStatusCode.OK, json);
                }
                return JsonResponse(HttpStatusCode.OK, """{"items":[]}""");
            }

            if (request.Method == HttpMethod.Put && path.Contains("/youtube/v3/videos", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.GetProperty("id").GetString()!;
                var snippet = doc.RootElement.GetProperty("snippet");
                if (Videos.TryGetValue(id, out var v))
                    v.Description = snippet.GetProperty("description").GetString() ?? "";
                return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { id }));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":{"code":404,"message":"not found"}}""", Encoding.UTF8, "application/json"),
            };
        }

        private static async Task<string> ReadBodyAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is null) return "";
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            if (request.Content.Headers.ContentEncoding.Contains("gzip"))
            {
                using var input = new MemoryStream(bytes);
                using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                return await reader.ReadToEndAsync(ct);
            }
            return Encoding.UTF8.GetString(bytes);
        }

        private static string? GetQuery(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && Uri.UnescapeDataString(kv[0]) == key)
                    return Uri.UnescapeDataString(kv[1]);
            }
            return null;
        }
    }

    private sealed class CannedHttpClientFactory : Google.Apis.Http.IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public CannedHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public Google.Apis.Http.ConfigurableHttpClient CreateHttpClient(Google.Apis.Http.CreateHttpClientArgs args) =>
            new(new Google.Apis.Http.ConfigurableMessageHandler(_handler));
    }

    private static YouTubeService CannedYouTube(FakeYouTubeHandler handler) =>
        new(new BaseClientService.Initializer
        {
            HttpClientFactory = new CannedHttpClientFactory(handler),
            ApplicationName = "item6-test",
        });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    private static object NewConfig(string typeName)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.Uploader, typeName);
        return Activator.CreateInstance(type)!;
    }

    private static object NewPublisher(string typeName, object config, Logger log)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.Uploader, typeName);
        return Activator.CreateInstance(type, config, log)!;
    }

    private static MethodInfo FindInstanceMethod(Type type, string name) =>
        type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingMethodException($"No instance method '{name}' on {type.Name}.");

    [Fact]
    public void CrossLinkUpdate_EchoesLiveTitleTagsCategory_NotSidecarStaleValues()
    {
        // R4, verified not re-implemented: the long-form was retitled and
        // retagged in Studio AFTER upload; the sidecar still carries the
        // stale values. Adding the Short link must echo the LIVE snippet
        // verbatim — sending the sidecar's values would clobber the
        // Studio edit. ITEM 5's worker claimed the shared helper fixes
        // this in mechanism; this test proves it against the real
        // YouTubePublisher with a canned API.
        var dir = TempDir("r4");
        var video = Path.Combine(dir, "slug-en.mp4");
        File.WriteAllText(video, "");
        var sidecar = new Sidecar
        {
            Title = "Stale sidecar title",
            Description = "Stale sidecar description.",
            Tags = ["stale", "tags"],
            Channel = "trendlinenow",
            Aspect = "landscape",
            Targets = [Platforms.YouTube],
        };
        sidecar.Publications[Platforms.YouTube] = new Publication
        {
            Status = PublishStatus.Published,
            Id = "vid1",
        };

        var handler = new FakeYouTubeHandler();
        handler.Videos["vid1"] = new FakeVideo
        {
            Title = "Studio Title (edited in Studio)",
            Description = "Live description, edited in Studio.",
            Tags = ["studio", "tag"],
            CategoryId = "25",
        };
        var youtube = CannedYouTube(handler);
        var publisher = NewPublisher("YouTubePublisher", NewConfig("YouTubeConfig"), NewLogger(dir));

        var method = FindInstanceMethod(publisher.GetType(), "EnsureLinkInDescriptionAsync");
        var updated = Block((Task<bool>)method.Invoke(publisher,
            [youtube, video, sidecar, "Watch the 60-second version: https://youtu.be/short999",
                "https://youtu.be/short999", CancellationToken.None])!);
        Assert.True(updated);

        var put = handler.Requests.Single(r => r.Method == "PUT");
        using var doc = JsonDocument.Parse(put.Body);
        var snippet = doc.RootElement.GetProperty("snippet");
        Assert.Equal("Studio Title (edited in Studio)", snippet.GetProperty("title").GetString());
        Assert.Equal("25", snippet.GetProperty("categoryId").GetString());
        Assert.Equal(["studio", "tag"],
            snippet.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Contains("https://youtu.be/short999", snippet.GetProperty("description").GetString());
        Assert.DoesNotContain("Stale sidecar title", put.Body);
        Assert.DoesNotContain("\"stale\"", put.Body);
    }

    // ------------------------------------------------------------------
    // R5 — BuildChapters edge cases (C#)
    // ------------------------------------------------------------------

    private static List<string> BuildChapters(object briefScenes, double introSeconds, object scenes, double transitionSeconds = 0.2)
    {
        var method = ReflectionHelper.FindMethod(ReflectionHelper.VideoGen, "BuildChapters");
        try
        {
            return (List<string>)method.Invoke(null, [briefScenes, introSeconds, scenes, transitionSeconds])!;
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static string ChapterLabel(string narration)
    {
        var method = ReflectionHelper.FindMethod(ReflectionHelper.VideoGen, "ChapterLabel");
        try
        {
            return (string)method.Invoke(null, [narration])!;
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    [Fact]
    public void ChapterLabel_NewsTitleAbbreviation_NotCutShort()
    {
        // "Sen. Cruz warned..." used to be cut to just "Sen." — the
        // abbreviation set lacked the news-common titles.
        Assert.Equal("Sen. Cruz warned the Senate.",
            ChapterLabel("Sen. Cruz warned the Senate. Next sentence here."));
        Assert.Equal("Rep. Patel filed the bill.",
            ChapterLabel("Rep. Patel filed the bill. Next sentence here."));
        Assert.Equal("Gov. Lee signed the order.",
            ChapterLabel("Gov. Lee signed the order. Next sentence here."));
    }

    [Fact]
    public void ChapterLabel_SentenceEndAtSecondToLastChar_AndAfterQuote()
    {
        // A sentence end at the second-to-last character (closing quote)
        // used to be missed, falling back to the whole narration.
        Assert.Equal("He said \"It works.\"",
            ChapterLabel("He said \"It works.\" Next part follows here."));
        Assert.Equal("Something happened.\"",
            ChapterLabel("Something happened.\" And then more follows here."));
    }

    [Fact]
    public void BuildChapters_RefillsAfterGapFilter_ToFullTwelve()
    {
        // 14 scenes x 30s, distinct groups: time-spacing picks 11 of 14
        // boundaries, the gap filter drops the 0s one, and without a
        // re-fill the list would stop at 11 chapters with a slot wasted.
        // The re-fill must pull an unselected boundary back in (12 total).
        var listType = typeof(List<>).MakeGenericType(SceneType);
        var scenes = (System.Collections.IList)Activator.CreateInstance(listType)!;
        for (var i = 0; i < 14; i++)
            scenes.Add(NewScene($"Scene {i + 1} happened here. More detail follows.", duration: 30.0, visualGroup: i + 1));
        var emptyBrief = (System.Collections.IList)Activator.CreateInstance(listType)!;
        var chapters = BuildChapters(emptyBrief, 0.0, scenes);
        Assert.Equal(12, chapters.Count);
        Assert.StartsWith("0:00 - ", chapters[0]);
        // Every chapter at least 10s after the previous one.
        var times = chapters.Select(c =>
        {
            var mmss = c.Split(" - ")[0].Split(':');
            return int.Parse(mmss[0]) * 60 + int.Parse(mmss[1]);
        }).ToList();
        for (var i = 1; i < times.Count; i++)
            Assert.True(times[i] - times[i - 1] >= 10,
                $"chapters {times[i - 1]}s and {times[i]}s are <10s apart");
    }

    // ------------------------------------------------------------------
    // R6 — WithCanonicalHashtags floor guard + inline hashtags (C#)
    // ------------------------------------------------------------------

    private static string WithCanonicalHashtags(string caption, List<string> canonical, int maxHashtags, int maxLength)
    {
        var method = ReflectionHelper.FindMethod(ReflectionHelper.VideoGen, "WithCanonicalHashtags");
        try
        {
            return (string)method.Invoke(null, [caption, canonical, maxHashtags, maxLength])!;
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static List<string> CanonicalHashtags(string description)
    {
        var method = ReflectionHelper.FindMethod(ReflectionHelper.VideoGen, "CanonicalHashtags");
        try
        {
            return (List<string>)method.Invoke(null, [description])!;
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    [Fact]
    public void CanonicalHashtags_InlineHashtagKept_TrailingStripped()
    {
        // "Watch #GTA6 gameplay now" keeps its inline #GTA6 — the old
        // regex deleted every hashtag anywhere in the caption.
        var result = WithCanonicalHashtags(
            "Watch #GTA6 gameplay now #news #tech",
            ["#TrendlineNow", "#GTA6"], 2, 150);
        Assert.Contains("#GTA6", result);
        Assert.Contains("#TrendlineNow", result);
        Assert.DoesNotContain("#news", result);
        Assert.DoesNotContain("#tech", result);
        Assert.StartsWith("Watch #GTA6 gameplay now", result);
    }

    [Fact]
    public void CanonicalHashtags_FloorGuard_KeepsCaptionText()
    {
        // Canonical tags longer than the caption budget used to wipe the
        // caption text entirely (hashtags only). The text must survive.
        var bigTags = Enumerable.Range(0, 8).Select(i => "#averylongtopichashtag" + i).ToList();
        var result = WithCanonicalHashtags("Watch the full story here.", bigTags, 8, 100);
        Assert.Contains("Watch the full story here.", result);
    }

    [Fact]
    public void CanonicalHashtags_PrefersLineTwoOverStuffingLine()
    {
        // Line 2 is the intended hashtag line; a keyword-stuffing line
        // elsewhere must not win the max-count contest.
        var description = "Title line\n#TrendlineNow #GTA6\nStuff #a #b #c #d #e #f #g #h #i #j";
        Assert.Equal(["#TrendlineNow", "#GTA6"], CanonicalHashtags(description));
    }

    // ------------------------------------------------------------------
    // R7 — TikTok InvalidOperationException becomes Retry, not a crash
    // ------------------------------------------------------------------

    [Fact]
    public async Task TikTokPriorCheck_MalformedShape_ReturnsRetryNotCrash()
    {
        // A prior-publish status response whose "data" is not an object
        // throws InvalidOperationException inside GetProperty — it used
        // to escape to the uploader's per-video catch (crash-retry, then
        // failed/). It must be a Retry like every other check failure.
        var dir = TempDir("r7");
        var cfg = NewConfig("TikTokConfig");
        SetProp(cfg, "ProcessingTimeoutSeconds", 600);
        var publisher = NewPublisher("TikTokPublisher", cfg, NewLogger(dir));
        using var http = new HttpClient(new CannedHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"data": "not-an-object"}""")));
        var method = FindInstanceMethod(publisher.GetType(), "CheckPriorPublishAsync");
        var task = (Task<PublishResult?>)method.Invoke(publisher, [http, "pub-1", CancellationToken.None])!;
        var result = await task;
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.False(result.Permanent);
    }
}
