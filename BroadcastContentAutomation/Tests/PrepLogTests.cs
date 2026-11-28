// L10 — VideoGen had no log file at all: every failure line lived or
// died with stdout redirection. PrepLog mirrors every Console line into
// <job>/prep.log. This test runs a failing prep as a subprocess with
// stdout discarded and asserts the failure line (with the job id) is
// still in the log file.
using System.Diagnostics;

namespace BroadcastContentAutomation.Tests;

public class PrepLogTests
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
            return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
        }
    }

    private static string DotnetExe()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var exe = Path.Combine(root, "dotnet");
            if (File.Exists(exe))
                return exe;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Path.Combine(home, ".dotnet", "dotnet");
        if (File.Exists(local))
            return local;
        return "dotnet";
    }

    [Fact]
    public void PrepLog_WrittenToJobLogFile()
    {
        // A script.json already present makes --prep --job fail fast
        // (before any network/script work) with the job dir in the
        // message — the failure line must land in <job>/prep.log even
        // though this test discards stdout.
        var jobId = $"job-{DateTime.Now:yyyyMMdd-HHmmss}-preplog";
        var jobDir = Path.Combine(Path.GetTempPath(), jobId);
        Directory.CreateDirectory(jobDir);
        try
        {
            File.WriteAllText(Path.Combine(jobDir, "script.json"), "{}");
            var dll = Path.Combine(RepoRoot, "BroadcastContentAutomation", "VideoGen",
                "bin", "Release", "net8.0", "VideoGen.dll");
            Assert.True(File.Exists(dll),
                $"VideoGen.dll not built at {dll} - run the Release build first.");
            var start = new ProcessStartInfo
            {
                FileName = DotnetExe(),
                Arguments = $"\"{dll}\" --prep --job \"{jobDir}\"",
                WorkingDirectory = Path.GetDirectoryName(dll),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start dotnet.");
            // stdout/stderr are read only to drain them; the assertions
            // below read nothing but the log file.
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(120_000),
                "VideoGen --prep --job did not exit in 120s.");
            Assert.NotEqual(0, proc.ExitCode);
            var log = Path.Combine(jobDir, "prep.log");
            Assert.True(File.Exists(log),
                $"prep.log was not written. STDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            var text = File.ReadAllText(log);
            Assert.Contains(jobId, text);
            Assert.Contains("already has a script.json", text);
        }
        finally
        {
            Directory.Delete(jobDir, recursive: true);
        }
    }
}
