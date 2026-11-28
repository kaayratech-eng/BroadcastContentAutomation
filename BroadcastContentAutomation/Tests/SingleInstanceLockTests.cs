// L1 (failure-logging plan): a bounced uploader run must name the lock
// holder (pid + age) and exit with a documented non-zero code, so a bounce
// is never mistaken for a clean run (the 19:54 EST Messi retry bounce
// exited 0 and looked like success).
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class SingleInstanceLockTests
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
        var dir = Path.Combine(Path.GetTempPath(), $"tn-lock-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SecondAcquire_LogsHolderPidAndAge()
    {
        var dir = TempDir("l1");
        try
        {
            var lockPath = Path.Combine(dir, "uploader.lock");
            var type = ReflectionHelper.FindType(ReflectionHelper.Uploader, "SingleInstanceLock");
            var acquire = type.GetMethod("TryAcquire", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("SingleInstanceLock.TryAcquire");
            var readHolder = type.GetMethod("ReadHolder", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("SingleInstanceLock.ReadHolder");
            var first = (IDisposable)acquire.Invoke(null, [lockPath])!;
            Assert.NotNull(first);
            try
            {
                // The holder info is readable while the lock is held, and
                // names this process + a fresh start time.
                var holder = readHolder.Invoke(null, [lockPath]);
                Assert.NotNull(holder);
                var pid = (int)holder.GetType().GetProperty("Pid")!.GetValue(holder)!;
                var started = (DateTimeOffset)holder.GetType().GetProperty("StartedUtc")!.GetValue(holder)!;
                Assert.Equal(Environment.ProcessId, pid);
                Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMinutes(1));

                // A second acquire bounces, and the holder info is still
                // readable on the bounce — that is what the bounce log
                // line names.
                var second = acquire.Invoke(null, [lockPath]);
                Assert.Null(second);
                var holderOnBounce = readHolder.Invoke(null, [lockPath]);
                Assert.NotNull(holderOnBounce);
                Assert.Equal(Environment.ProcessId,
                    (int)holderOnBounce.GetType().GetProperty("Pid")!.GetValue(holderOnBounce)!);
            }
            finally
            {
                first.Dispose();
            }
            var third = acquire.Invoke(null, [lockPath]);
            Assert.NotNull(third);
            ((IDisposable)third!).Dispose();

            // The bounce log line itself lives in Program.cs: assert its
            // shape (pid + age + next action) in source.
            var program = ReadRepoFile("BroadcastContentAutomation", "Uploader", "Program.cs");
            Assert.Contains("uploader lock held by pid", program);
            Assert.Contains("will retry on next scheduled tick", program);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadHolder_StalePid_ReturnsNull()
    {
        // A holder file naming a dead pid (killed run) must not be
        // reported as the current holder.
        var dir = TempDir("l1-stale");
        try
        {
            var lockPath = Path.Combine(dir, "uploader.lock");
            File.WriteAllText(lockPath + ".holder",
                "pid=999999\nstarted=2026-01-01T00:00:00.0000000+00:00\n");
            var type = ReflectionHelper.FindType(ReflectionHelper.Uploader, "SingleInstanceLock");
            var readHolder = type.GetMethod("ReadHolder", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("SingleInstanceLock.ReadHolder");
            Assert.Null(readHolder.Invoke(null, [lockPath]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class ProgramTests
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

    [Fact]
    public void LockBounce_ExitCodeIsDocumented()
    {
        // A bounced run did nothing, so it must not exit 0 — and the
        // reason must be documented at the return site, not implicit.
        var program = ReadRepoFile("BroadcastContentAutomation", "Uploader", "Program.cs");
        Assert.Contains("Exit code 2 (documented): lock bounce", program);
        Assert.Contains("return 2;", program);
    }

    [Fact]
    public void CorruptRetryMarker_LogsResetWarning()
    {
        // L11: a corrupt .retry.json used to silently reset the attempt
        // count to 0, so a repeatedly-crashing video never reached its
        // 3-attempt give-up. The reset still happens (a corrupt marker must
        // not block a retry), but the warning line must name the file and
        // the reset.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"retry-corrupt-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var video = Path.Combine(dir, "my-video-en.mp4");
            File.WriteAllText(Path.ChangeExtension(video, ".retry.json"), "{ corrupt");
            var logger = new GiggleGarden.Uploader.Logger(logDir);
            var state = ReflectionHelper.Invoke<object?>(
                ReflectionHelper.Uploader, "LoadCrashRetryState", video, logger);
            Assert.Null(state);
            var logFile = Directory.GetFiles(logDir, "upload-*.log").Single();
            var text = File.ReadAllText(logFile);
            Assert.Contains("corrupt retry marker for my-video-en.retry.json", text);
            Assert.Contains("treating as 0 attempts (count reset)", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CorruptSidecar_FailsFastToFailed()
    {
        // L11: a corrupt sidecar lands in failed/ on the first run, not
        // after 3 crash retries — the per-video catch mirrors the
        // SIDECAR_REQUIRED fail-fast branch for the SIDECAR_CORRUPT marker.
        var program = ReadRepoFile("BroadcastContentAutomation", "Uploader", "Program.cs");
        Assert.Contains("SIDECAR_CORRUPT:", program);
        Assert.Contains("ex.Message.StartsWith(\"SIDECAR_CORRUPT:\", StringComparison.Ordinal)", program);
        Assert.Contains("the sidecar is corrupt (see above) — moved to failed/.", program);
    }

    // L84 — uploader config must fail fast at startup with the field named
    // (exit 1, before any scan), not die later in an unrelated error.
    // Validate() is called from Program.cs right after the config loads,
    // inside the try whose catch logs and returns 1.
    [Fact]
    public void MissingWatchDir_FailsFastWithFieldName()
    {
        var cfg = new GiggleGarden.Uploader.AppConfig { WatchDirectory = "" };
        var ex = Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        Assert.Contains("WatchDirectory", ex.Message);
        Assert.Contains("fix appsettings.json and re-run", ex.Message);
    }

    [Fact]
    public void MissingYouTubeTokenPath_FailsFastWithFieldName()
    {
        var cfg = new GiggleGarden.Uploader.AppConfig
        {
            WatchDirectory = "/tmp/watch",
            Channels = new Dictionary<string, GiggleGarden.Uploader.PlatformsConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["trendlinenow"] = new GiggleGarden.Uploader.PlatformsConfig
                {
                    YouTube = new GiggleGarden.Uploader.YouTubeConfig
                    {
                        Enabled = true,
                        ClientSecretPath = "/tmp/client.json",
                        TokenStorePath = "",
                    },
                },
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        Assert.Contains("Channels.trendlinenow.YouTube.TokenStorePath", ex.Message);
    }

    [Fact]
    public void DisabledPublisher_EmptyTokenPath_Passes()
    {
        var cfg = new GiggleGarden.Uploader.AppConfig
        {
            WatchDirectory = "/tmp/watch",
            Channels = new Dictionary<string, GiggleGarden.Uploader.PlatformsConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["trendlinenow"] = new GiggleGarden.Uploader.PlatformsConfig
                {
                    YouTube = new GiggleGarden.Uploader.YouTubeConfig
                    {
                        Enabled = true,
                        ClientSecretPath = "/tmp/client.json",
                        TokenStorePath = "/tmp/token",
                    },
                    TikTok = new GiggleGarden.Uploader.TikTokConfig
                    {
                        Enabled = false,
                        TokenStorePath = "",
                    },
                },
            },
        };
        // Must not throw: a disabled publisher's token path is never read.
        cfg.Validate();
    }

    // ------------------------------------------------------------------
    // Shared harness for the L23–L30 RunAsync tests below: drive the
    // REAL uploader run loop (top-level Program.cs local function
    // RunAsync, reached via reflection) against a scratch watch dir.
    // ------------------------------------------------------------------

    private static MethodInfo FindProgramMethod(string namePart)
    {
        var asm = ReflectionHelper.Uploader;
        var programType = asm.GetTypes().First(t => t.Name == "Program");
        return programType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .First(m => m.Name.Contains(namePart, StringComparison.Ordinal));
    }

    private static void SetProp(object obj, string name, object? value)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(obj.GetType().Name, name);
        prop.SetValue(obj, value);
    }

    private static object NewConfig(string typeName)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.Uploader, typeName);
        return Activator.CreateInstance(type)!;
    }

    // AppConfig for a scratch watch dir: approval off, no minimum file
    // age, one trendlinenow channel with default publishers (Instagram /
    // Facebook / TikTok disabled by default; YouTube enabled but never a
    // target unless a test lists it).
    private static object BuildAppConfig(string watchDir, string logDir)
    {
        var cfg = NewConfig("AppConfig");
        SetProp(cfg, "WatchDirectory", watchDir);
        SetProp(cfg, "LogDirectory", logDir);
        SetProp(cfg, "RequireApproval", false);
        SetProp(cfg, "MinFileAgeSeconds", 0);
        SetProp(cfg, "MaxCrashRetries", 3);
        SetProp(cfg, "CrashRetryBackoffMinutes", 30);
        SetProp(cfg, "DefaultChannel", "trendlinenow");
        var channelsType = cfg.GetType().GetProperty("Channels")!.PropertyType;
        var channels = Activator.CreateInstance(channelsType)!;
        channelsType.GetMethod("Add")!.Invoke(channels, ["trendlinenow", NewConfig("PlatformsConfig")]);
        SetProp(cfg, "Channels", channels);
        return cfg;
    }

    private static int RunUploader(object cfg, GiggleGarden.Uploader.Logger log)
    {
        var method = FindProgramMethod("RunAsync");
        var task = (Task<int>)method.Invoke(null, [cfg, log])!;
        return task.GetAwaiter().GetResult();
    }

    private static string WriteVideoWithSidecar(string channelDir, string fileName, string[] targets, string aspect = "landscape")
    {
        Directory.CreateDirectory(channelDir);
        var videoPath = Path.Combine(channelDir, fileName);
        File.WriteAllText(videoPath, "");
        var sidecar = new Sidecar
        {
            Title = "Test video",
            Description = "Test description.",
            Channel = "trendlinenow",
            Aspect = aspect,
            Targets = [.. targets],
            Approved = true,
        };
        File.WriteAllText(Sidecar.PathFor(videoPath), JsonSerializer.Serialize(sidecar, Sidecar.Options));
        return videoPath;
    }

    private static string ReadLog(string logDir)
    {
        var logFile = Directory.GetFiles(logDir, "upload-*.log").Single();
        return File.ReadAllText(logFile);
    }

    [Fact]
    public async Task MissingAppsettings_LogsErrorToFileAndExits1()
    {
        // L25: a bad appsettings.json used to throw before any logger
        // existed — console stack trace only, no upload-*.log entry.
        // Drive the REAL built Uploader.dll in a scratch copy of the
        // output dir with a corrupt appsettings.json: it must exit 1 (not
        // an unhandled stack trace) and write the error to its log file.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l25-config-{Guid.NewGuid():N}")).FullName;
        var runDir = Path.Combine(dir, "run");
        try
        {
            CopyDirectory(AppContext.BaseDirectory, runDir);
            File.WriteAllText(Path.Combine(runDir, "appsettings.json"), "{ this is not valid json");

            var psi = new ProcessStartInfo(FindDotnet(), "Uploader.dll")
            {
                WorkingDirectory = runDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            Assert.True(proc.WaitForExit(120_000), "Uploader.dll did not exit in time");
            var output = await stdoutTask + await stderrTask;
            Assert.Equal(1, proc.ExitCode);
            Assert.DoesNotContain("Unhandled exception", output);

            var logFile = Directory.GetFiles(Path.Combine(runDir, "logs"), "upload-*.log").Single();
            var text = File.ReadAllText(logFile);
            Assert.Contains("appsettings.json missing or invalid", text);
            Assert.Contains("fix appsettings.json and re-run", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(source))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    private static string FindDotnet()
    {
        var fromPath = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .Select(p => Path.Combine(p, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .FirstOrDefault(File.Exists);
        if (fromPath is not null) return fromPath;
        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        return File.Exists(home) ? home : "dotnet";
    }

    [Fact]
    public void SidecarSaveFailure_LogsStepNotPublishFailure()
    {
        // L30: a sidecar-save failure used to throw into the per-video
        // catch and read as a video/publish failure. The call-site wrap
        // must log it as a "sidecar save" failure with the step and the
        // reason instead. Same .tmp-directory trick as the L23 test.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l30-save-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var channelDir = Path.Combine(dir, "watch", "trendlinenow");
            var videoPath = WriteVideoWithSidecar(channelDir, "my-video-en.mp4", ["instagram"]);
            Directory.CreateDirectory(Sidecar.PathFor(videoPath) + ".tmp");

            var logger = new GiggleGarden.Uploader.Logger(logDir);
            RunUploader(BuildAppConfig(Path.Combine(dir, "watch"), logDir), logger);

            var text = ReadLog(logDir);
            Assert.Contains("sidecar save failed for my-video-en.mp4 (disabled target) [instagram]", text);
            Assert.Contains("UnauthorizedAccessException", text);
            Assert.Contains("video left in watch dir, will retry next run", text);
            var failedLine = text.Split('\n').First(l => l.Contains("Failed my-video-en.mp4"));
            Assert.Contains("sidecar save", failedLine);
            Assert.DoesNotContain("publish", failedLine, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BackoffVideo_IsLoggedNotSilent()
    {
        // L29: a video inside its 30-min crash-retry backoff used to be
        // filtered silently, so a run looked empty. The scan line must
        // name it and its backoff expiry, and the empty-run line must
        // state the breakdown.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l29-backoff-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var channelDir = Path.Combine(dir, "watch", "trendlinenow");
            var videoPath = WriteVideoWithSidecar(channelDir, "my-video-en.mp4", ["instagram"]);
            File.WriteAllText(Path.ChangeExtension(videoPath, ".retry.json"),
                JsonSerializer.Serialize(new { Attempts = 1, LastAttemptUtc = DateTimeOffset.UtcNow }));

            var logger = new GiggleGarden.Uploader.Logger(logDir);
            var result = RunUploader(BuildAppConfig(Path.Combine(dir, "watch"), logDir), logger);
            Assert.Equal(0, result);

            var text = ReadLog(logDir);
            Assert.Contains("skipping my-video-en.mp4", text);
            Assert.Contains("in crash-retry backoff until", text);
            Assert.Contains("attempt 1/3", text);
            Assert.Contains("will retry next run", text);
            Assert.Contains("No videos to process. (0 ready, 0 too-new, 1 in crash-retry backoff)", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoPublishRun_SummaryNamesVideosAndReasons()
    {
        // L28: the aggregate "no published target" line used to carry only
        // a count — the summary a monitor reads must name the videos and
        // their reasons.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l28-summary-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var channelDir = Path.Combine(dir, "watch", "trendlinenow");
            WriteVideoWithSidecar(channelDir, "alpha-en.mp4", ["instagram"]);
            WriteVideoWithSidecar(channelDir, "beta-en.mp4", ["instagram"]);

            var logger = new GiggleGarden.Uploader.Logger(logDir);
            var result = RunUploader(BuildAppConfig(Path.Combine(dir, "watch"), logDir), logger);
            Assert.Equal(2, result);

            var text = ReadLog(logDir);
            var summaryLine = text.Split('\n').First(l => l.Contains("ended with no published target"));
            Assert.Contains("2 video(s)", summaryLine);
            Assert.Contains("alpha-en.mp4", summaryLine);
            Assert.Contains("beta-en.mp4", summaryLine);
            Assert.Contains("platform disabled in appsettings.json", summaryLine);
            Assert.Contains("moved to failed/", summaryLine);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DisabledPlatform_LogsSkipWithReason()
    {
        // L27: a disabled-publisher target used to be silently Skipped —
        // no log line at all, so a disabled platform looked identical to
        // "never a target". The skip must name the platform + video.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l27-disabled-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var channelDir = Path.Combine(dir, "watch", "trendlinenow");
            var videoPath = WriteVideoWithSidecar(channelDir, "my-video-en.mp4", ["instagram"]);

            var logger = new GiggleGarden.Uploader.Logger(logDir);
            var result = RunUploader(BuildAppConfig(Path.Combine(dir, "watch"), logDir), logger);
            Assert.Equal(1, result); // ended with no published target

            var text = ReadLog(logDir);
            Assert.Contains("skipping my-video-en.mp4", text);
            Assert.Contains("instagram", text);
            Assert.Contains("platform disabled in appsettings.json", text);
            Assert.Contains("target Skipped", text);
            Assert.True(File.Exists(Path.Combine(channelDir, "failed", "my-video-en", "my-video-en.mp4")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PerVideoCrash_LogsStepPlatformAndExceptionType()
    {
        // L23: a SaveAsync throw used to be logged as a bare
        // "Failed <file>: <message>" — indistinguishable from a publish
        // failure. The per-video catch must now name the step, the
        // platform/target, and the exception type (stack via L24).
        // Forced at the disabled-target sidecar save: a directory stands
        // where SaveAsync's .tmp file goes, so the save throws — the
        // recorded step is "sidecar save" on instagram, not a publish
        // error. (No fake-publisher seam exists to force the throw after
        // a real publish; the disabled-target save is the same SaveAsync
        // call site on the same dispatch path.)
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"l23-crash-{Guid.NewGuid():N}")).FullName;
        var logDir = Path.Combine(dir, "logs");
        try
        {
            var channelDir = Path.Combine(dir, "watch", "trendlinenow");
            var videoPath = WriteVideoWithSidecar(channelDir, "my-video-en.mp4", ["instagram"]);
            Directory.CreateDirectory(Sidecar.PathFor(videoPath) + ".tmp");

            var logger = new GiggleGarden.Uploader.Logger(logDir);
            var result = RunUploader(BuildAppConfig(Path.Combine(dir, "watch"), logDir), logger);
            Assert.Equal(0, result);

            var text = ReadLog(logDir);
            var failedLine = text.Split('\n').First(l => l.Contains("Failed my-video-en.mp4"));
            Assert.Contains("sidecar save", failedLine);
            Assert.Contains("[instagram]", failedLine);
            Assert.Contains("UnauthorizedAccessException", failedLine);
            Assert.Contains("at ", failedLine);
            Assert.DoesNotContain("publish", failedLine, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
