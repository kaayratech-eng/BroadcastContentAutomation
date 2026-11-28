// L21 — no downstream prep log line could name the job: the only job
// identifier was the `Workspace:` line. PrepIdentity (job folder name +
// topic + act) is now threaded through CompleteAsync, the Wikipedia /
// article fetch clients, GovernorClient and the RetryHandler callbacks,
// and the top-level FATAL names the job + first stack frame + next action.
// These tests drive the pool/fetch/governor with a stub leg that throws
// and assert every captured [script-pool]/[trends]/[wikipedia]/[articles]/
// [governor] line contains the job folder name; the FATAL test runs a
// failing prep as a subprocess and reads only its prep.log.
using System.Diagnostics;
using System.Text;

namespace BroadcastContentAutomation.Tests;

public class PrepContextTests
{
    private sealed class FailingProvider(string message) : IScriptProvider
    {
        public Task<string> CompleteAsync(string prompt, PrepIdentity? context = null) =>
            Task.FromException<string>(new Exception(message));
    }

    private sealed class AnsweringProvider(string answer) : IScriptProvider
    {
        public Task<string> CompleteAsync(string prompt, PrepIdentity? context = null) => Task.FromResult(answer);
    }

    private sealed class CannedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request);
    }

    [Fact]
    public async Task PrepLogLines_CarryJobId()
    {
        // A stub leg that throws, so the pool logs a [script-pool] failure
        // and falls through; Wikipedia 500s; the article helper is missing;
        // the governor helper is missing. Every line must carry the job id.
        var jobId = "job-20261126-testjob";
        var context = new PrepIdentity(jobId, "Test Topic");
        var sb = new StringBuilder();
        var oldOut = Console.Out;
        var oldContext = PrepContext.Current;
        try
        {
            Console.SetOut(new StringWriter(sb));
            PrepContext.Current = null;

            var pool = new FallbackScriptProvider([
                new FailingProvider("groq 429"),
                new AnsweringProvider("{\"scenes\": []}"),
            ]);
            Assert.Equal("{\"scenes\": []}", await pool.CompleteAsync("act prompt", context));

            using var client500 = new HttpClient(new CannedHandler(_ => Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                { Content = new StringContent("oops", Encoding.UTF8, "text/plain") })));
            Assert.Null(await WikipediaClient.FetchSummaryForTitleAsync("GTA_6", client500, context));

            var cfg = new GenConfig
            {
                ArticleHelperPath = "/nonexistent/article_fetch.py",
                ArticleHelperPythonPath = "/nonexistent/python",
                GovernorHelperPath = "/nonexistent/governor.py",
            };
            Assert.Empty(await ArticleClient.FetchForTopicAsync("Test Topic", cfg, context: context));
            _ = new GovernorClient(cfg, context: context);

            var text = sb.ToString();
            // Every captured line that names a subsystem must name the job.
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Contains("[script-pool]", StringComparison.Ordinal)
                    || line.Contains("[wikipedia]", StringComparison.Ordinal)
                    || line.Contains("[articles]", StringComparison.Ordinal)
                    || line.Contains("[governor]", StringComparison.Ordinal))
                    Assert.Contains(jobId, line);
            }
            Assert.Contains("[script-pool]", text);
            Assert.Contains("[wikipedia]", text);
            Assert.Contains("[articles]", text);
            Assert.Contains("[governor]", text);
            Assert.Contains("HTTP 500", text);
        }
        finally
        {
            Console.SetOut(oldOut);
            PrepContext.Current = oldContext;
        }
    }

    [Fact]
    public void PrepFatal_NamesJobStackFrameAndNextAction()
    {
        // A --prep --job run against a dir that already has a script.json
        // fails fast at the top level: the FATAL line in prep.log must name
        // the job folder, carry a stack frame, and say what happens next.
        var jobId = $"job-{DateTime.Now:yyyyMMdd-HHmmss}-prepfatal";
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
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(120_000),
                "VideoGen --prep --job did not exit in 120s.");
            Assert.NotEqual(0, proc.ExitCode);
            var log = Path.Combine(jobDir, "prep.log");
            Assert.True(File.Exists(log),
                $"prep.log was not written. STDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            var text = File.ReadAllText(log);
            var fatal = text.Split('\n').FirstOrDefault(l => l.Contains("FATAL:", StringComparison.Ordinal));
            Assert.NotNull(fatal);
            Assert.Contains(jobId, fatal);
            Assert.Contains("at ", fatal);
            Assert.Contains("re-run --prep --job", fatal);
        }
        finally
        {
            Directory.Delete(jobDir, recursive: true);
        }
    }

    // Same layout helpers as PrepLogTests: the Tests project lives at
    // <repo>/BroadcastContentAutomation/Tests, so the repo root is three
    // levels up from the test binaries.
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
}
