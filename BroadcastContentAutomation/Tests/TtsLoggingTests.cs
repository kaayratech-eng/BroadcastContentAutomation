// L42–L47 — TTS failure logging: every provider log line must name the
// scene (and, via PrepContext.Prefix, the job), skipped-leg aborts get a
// summary line, throttle/retry loops log the 429 detail and the final
// outcome, and direct-HTTP providers log retry exhaustion with the scene.
//
// No network, no live helpers: helper providers run through an injected
// fake runner (same shape as GovernorClient's), direct HTTP providers
// through an injected canned handler with Retry-After: 0 so no test
// sleeps on backoff.
using System.Net;
using System.Text;
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class TtsLoggingTests
{
    // Two failing legs whose class names the pool logs; each fails with a
    // distinct reason so the test can tell them apart in the log.
    private sealed class AzureHelperTtsProvider(string message) : ITtsProvider
    {
        public Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null, string? sceneLabel = null) =>
            Task.FromException(new Exception(message));
    }

    private sealed class GoogleHelperTtsProvider(string message) : ITtsProvider
    {
        public Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null, string? sceneLabel = null) =>
            Task.FromException(new Exception(message));
    }

    private sealed class CannedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return respond(request);
        }
    }

    // Captures Console.Out for the duration of `action`, returning the
    // text even when the action throws (the pool's failure cases throw).
    private static async Task<(string Log, Exception? Error)> CaptureConsoleAsync(Func<Task> action)
    {
        var oldOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        Exception? error = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            Console.SetOut(oldOut);
        }
        return (writer.ToString(), error);
    }

    // L42 — a 2-provider failure on scene 12 of job X: the log must tie
    // the failure to the scene and the job, name both providers, and
    // carry the reason.
    [Fact]
    public async Task FallbackTtsProvider_LogsSceneAndJobOnFailure()
    {
        var jobId = "job-20261126-tts42";
        var oldContext = PrepContext.Current;
        try
        {
            PrepContext.Current = new PrepIdentity(jobId, "Test Topic");
            var pool = new FallbackTtsProvider([
                new AzureHelperTtsProvider("azure boom: simulated outage"),
                new GoogleHelperTtsProvider("google boom: simulated outage"),
            ]);
            var (log, error) = await CaptureConsoleAsync(() =>
                pool.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%", sceneLabel: "12/55"));
            Assert.NotNull(error);
            Assert.Contains("scene 12", log);
            Assert.Contains(jobId, log);
            Assert.Contains("AzureHelperTtsProvider", log);
            Assert.Contains("GoogleHelperTtsProvider", log);
            Assert.Contains("azure boom", log);
            Assert.Contains("google boom", log);
            Assert.Contains("AzureHelperTtsProvider", error.Message);
            Assert.Contains("GoogleHelperTtsProvider", error.Message);
        }
        finally
        {
            PrepContext.Current = oldContext;
        }
    }

    // L43 — all legs denied by the governor: one summary line must name
    // every skipped leg and the abort, not just the scattered skip lines.
    [Fact]
    public async Task FallbackTtsProvider_LogsAllSkippedSummary()
    {
        // GovernorClient fails open when its helper file is missing, so
        // point it at a dummy file that exists; the fake runner denies
        // every check.
        var dir = Path.Combine(Path.GetTempPath(), "tts-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var helperPath = Path.Combine(dir, "governor.py");
            File.WriteAllText(helperPath, "# dummy governor helper\n");
            string ProviderOf(string[] argv)
            {
                var i = Array.IndexOf(argv, "--provider");
                return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : "?";
            }
            Task<ProcessHelper.Result> deny(string exe, IEnumerable<string> args)
            {
                var argv = args.ToArray();
                var json = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["allowed"] = false, ["provider"] = ProviderOf(argv),
                    ["reason"] = $"cooled: {ProviderOf(argv)} rate limit",
                    ["resets_at"] = "2026-11-27T00:00:00-05:00",
                    ["usage_fraction"] = 1.0, ["soft_cap_fraction"] = 0.9,
                    ["recorded"] = false, ["cooled"] = true,
                });
                return Task.FromResult(new ProcessHelper.Result(1, json, ""));
            }
            var governor = new GovernorClient(new GenConfig { GovernorHelperPath = helperPath }, deny);
            var pool = new FallbackTtsProvider([
                new AzureHelperTtsProvider("must not be called"),
                new GoogleHelperTtsProvider("must not be called"),
            ], governor);
            var (log, error) = await CaptureConsoleAsync(() =>
                pool.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%", sceneLabel: "12/55"));
            Assert.IsType<GovernorAllCooledException>(error);
            Assert.Contains("TTS all legs skipped", log);
            Assert.Contains("azure-tts: cooled: azure-tts rate limit", log);
            Assert.Contains("google-tts: cooled: google-tts rate limit", log);
            Assert.Contains("aborting scene 12/55", log);
            Assert.Contains("no provider attempted", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // L77 — the governor-skip lines themselves must name the scene and
    // the job, not just the all-skipped summary (L43). The lines already
    // carry both (L42 threaded the scene label + PrepContext prefix in);
    // this test pins that behaviour so a later edit cannot drop it.
    [Fact]
    public async Task GovernorSkip_LogsSceneAndJob()
    {
        var jobId = "job-20261126-tts77";
        var oldContext = PrepContext.Current;
        var dir = Path.Combine(Path.GetTempPath(), "tts-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PrepContext.Current = new PrepIdentity(jobId, "Test Topic");
            var helperPath = Path.Combine(dir, "governor.py");
            File.WriteAllText(helperPath, "# dummy governor helper\n");
            string ProviderOf(string[] argv)
            {
                var i = Array.IndexOf(argv, "--provider");
                return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : "?";
            }
            Task<ProcessHelper.Result> deny(string exe, IEnumerable<string> args)
            {
                var argv = args.ToArray();
                var json = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["allowed"] = false, ["provider"] = ProviderOf(argv),
                    ["reason"] = $"cooled: {ProviderOf(argv)} rate limit",
                    ["resets_at"] = "2026-11-27T00:00:00-05:00",
                    ["usage_fraction"] = 1.0, ["soft_cap_fraction"] = 0.9,
                    ["recorded"] = false, ["cooled"] = true,
                });
                return Task.FromResult(new ProcessHelper.Result(1, json, ""));
            }
            var governor = new GovernorClient(new GenConfig { GovernorHelperPath = helperPath }, deny);
            var pool = new FallbackTtsProvider([
                new AzureHelperTtsProvider("must not be called"),
                new GoogleHelperTtsProvider("must not be called"),
            ], governor);
            var (log, error) = await CaptureConsoleAsync(() =>
                pool.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%", sceneLabel: "12/55"));
            Assert.IsType<GovernorAllCooledException>(error);
            Assert.Contains("governor: azure-tts skipped scene 12/55", log);
            Assert.Contains("governor: google-tts skipped scene 12/55", log);
            Assert.Contains($"[{jobId}]", log);
        }
        finally
        {
            PrepContext.Current = oldContext;
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Dictionary<string, (string Locale, string Voice, string? Style)> TestVoices() => new()
    {
        ["en"] = ("en-US", "en-US-DavisNeural", null),
    };

    // A fake helper runner: returns the queued results in order, writing
    // the output file on a success result (as the real helper would).
    private sealed class FakeHelperRunner(Queue<ProcessHelper.Result> results)
    {
        public int Calls { get; private set; }

        public Task<ProcessHelper.Result> Run(string exe, IEnumerable<string> args)
        {
            Calls++;
            var argv = args.ToArray();
            var result = results.Count > 0 ? results.Dequeue() : new ProcessHelper.Result(0, "", "");
            if (result.ExitCode == 0)
            {
                var outIdx = Array.IndexOf(argv, "--out");
                if (outIdx >= 0 && outIdx + 1 < argv.Length)
                    File.WriteAllBytes(argv[outIdx + 1], "FAKE-MP3"u8.ToArray());
            }
            return Task.FromResult(result);
        }
    }

    // L44 — stub helper emits 429 twice then succeeds: the log must carry
    // the 429 detail and the scene on the throttle lines, plus a recovery
    // line on success.
    [Fact]
    public async Task AzureHelper_ThrottleLogIncludesSceneAnd429Detail()
    {
        var jobId = "job-20261126-tts44";
        var oldContext = PrepContext.Current;
        var dir = Path.Combine(Path.GetTempPath(), "tts-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PrepContext.Current = new PrepIdentity(jobId, "Test Topic");
            var runner = new FakeHelperRunner(new Queue<ProcessHelper.Result>([
                new(1, "", "HTTP 429 Downstream Service Throttled: too many requests"),
                new(1, "", "HTTP 429 Downstream Service Throttled: too many requests"),
                new(0, "", ""),
            ]));
            var provider = new global::AzureHelperTtsProvider(
                new GenConfig { AzureSpeechRegion = "eastus" }, TestVoices(), runner.Run, [0, 0, 0, 0]);
            var outPath = Path.Combine(dir, "scene11.mp3");
            var (log, error) = await CaptureConsoleAsync(() =>
                provider.SynthesizeAsync("hello world", "en", outPath, "+10%", "0%", sceneLabel: "12/55"));
            Assert.Null(error);
            Assert.True(File.Exists(outPath));
            Assert.Equal(3, runner.Calls);
            Assert.Contains("Azure TTS throttled scene 12/55 (attempt 1/5)", log);
            Assert.Contains("Azure TTS throttled scene 12/55 (attempt 2/5)", log);
            Assert.Contains("429 Downstream Service Throttled", log);
            Assert.Contains("Azure TTS recovered after 3 attempts, scene 12/55", log);
            Assert.Contains(jobId, log);
        }
        finally
        {
            PrepContext.Current = oldContext;
            Directory.Delete(dir, recursive: true);
        }
    }

    // L45 — terminal failure names the scene and the attempt count, and
    // says the pool falls through to the next provider: both for a 429
    // exhausted after all 5 attempts and for an immediate non-throttle
    // failure after 1 attempt.
    [Fact]
    public async Task AzureHelper_FinalFailureNamesSceneAndAttempts()
    {
        var exhausted = new FakeHelperRunner(new Queue<ProcessHelper.Result>(
            Enumerable.Repeat(new ProcessHelper.Result(1, "", "HTTP 429 Downstream Service Throttled"), 5)));
        var provider = new global::AzureHelperTtsProvider(
            new GenConfig { AzureSpeechRegion = "eastus" }, TestVoices(), exhausted.Run, [0, 0, 0, 0]);
        var (exhaustedLog, exhaustedError) = await CaptureConsoleAsync(() =>
            provider.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%", sceneLabel: "12/55"));
        Assert.NotNull(exhaustedError);
        Assert.Equal(5, exhausted.Calls);
        Assert.Contains("Azure TTS helper failed scene 12/55 after 5 attempts (exit 1)", exhaustedError.Message);
        Assert.Contains("429 Downstream Service Throttled", exhaustedError.Message);
        Assert.Contains("falling through to next TTS provider", exhaustedError.Message);
        Assert.Contains("Azure TTS throttled scene 12/55 (attempt 4/5)", exhaustedLog);

        var immediate = new FakeHelperRunner(new Queue<ProcessHelper.Result>([
            new(1, "", "simulated provider outage"),
        ]));
        var provider2 = new global::AzureHelperTtsProvider(
            new GenConfig { AzureSpeechRegion = "eastus" }, TestVoices(), immediate.Run, [0, 0, 0, 0]);
        var (_, immediateError) = await CaptureConsoleAsync(() =>
            provider2.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%", sceneLabel: "12/55"));
        Assert.NotNull(immediateError);
        Assert.Equal(1, immediate.Calls);
        Assert.Contains("Azure TTS helper failed scene 12/55 after 1 attempt (exit 1)", immediateError.Message);
        Assert.Contains("simulated provider outage", immediateError.Message);
        Assert.Contains("falling through to next TTS provider", immediateError.Message);
    }

    // L46 — Google helper emits 429 then succeeds: it must retry with
    // backoff (not fail the scene instantly) and log the throttle line.
    [Fact]
    public async Task GoogleHelper_Retries429WithBackoff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tts-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var runner = new FakeHelperRunner(new Queue<ProcessHelper.Result>([
                new(1, "", "HTTP 429 Too Many Requests: quota exceeded"),
                new(0, "", ""),
            ]));
            var provider = new global::GoogleHelperTtsProvider(
                new GenConfig(), runner.Run, [0, 0, 0, 0]);
            var outPath = Path.Combine(dir, "scene11.mp3");
            var (log, error) = await CaptureConsoleAsync(() =>
                provider.SynthesizeAsync("hello world", "en", outPath, "+10%", "0%", sceneLabel: "12/55"));
            Assert.Null(error);
            Assert.True(File.Exists(outPath));
            Assert.Equal(2, runner.Calls);
            Assert.Contains("Google Cloud TTS throttled scene 12/55 (attempt 1/5)", log);
            Assert.Contains("429 Too Many Requests", log);
            Assert.Contains("Google Cloud TTS recovered after 2 attempts, scene 12/55", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // L46 — Google 429 on every attempt: the terminal failure must name
    // the scene and the attempt count, and say the pool falls through.
    [Fact]
    public async Task GoogleHelper_429ExhaustionNamesScene()
    {
        var runner = new FakeHelperRunner(new Queue<ProcessHelper.Result>(
            Enumerable.Repeat(new ProcessHelper.Result(1, "", "HTTP 429 Too Many Requests"), 5)));
        var provider = new global::GoogleHelperTtsProvider(
            new GenConfig(), runner.Run, [0, 0, 0, 0]);
        var (log, error) = await CaptureConsoleAsync(() =>
            provider.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%", sceneLabel: "12/55"));
        Assert.NotNull(error);
        Assert.Equal(5, runner.Calls);
        Assert.Contains("Google Cloud TTS helper failed scene 12/55 after 5 attempts (exit 1)", error.Message);
        Assert.Contains("429 Too Many Requests", error.Message);
        Assert.Contains("falling through to next TTS provider", error.Message);
        Assert.Contains("Google Cloud TTS throttled scene 12/55 (attempt 4/5)", log);
    }

    // L47 — stub 429 x 4 through the direct HTTP providers: the retry
    // handler's exhaustion line and the provider's exhaustion line must
    // both name the scene (Retry-After: 0 so the test does not sleep).
    [Fact]
    public async Task RetryHandler_LogsExhaustionWithScene()
    {
        HttpResponseMessage Throttled()
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("quota exceeded", Encoding.UTF8, "text/plain"),
            };
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return resp;
        }

        var azureCalls = new CannedHandler(_ => Task.FromResult(Throttled()));
        var azure = new global::AzureTtsProvider(
            new GenConfig { AzureSpeechKey = "fake-key", AzureSpeechRegion = "eastus" },
            TestVoices(), innerHandler: azureCalls);
        var (azureLog, azureError) = await CaptureConsoleAsync(() =>
            azure.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%", sceneLabel: "12/55"));
        Assert.NotNull(azureError);
        Assert.Equal(4, azureCalls.Calls);
        Assert.Contains("Azure TTS failed (429)", azureError.Message);
        Assert.Contains("scene 12/55", azureLog);
        Assert.Contains("gave up after 4 attempts", azureLog);
        Assert.Contains("[azure-tts] scene 12/55: retries exhausted after 4 attempts (HTTP 429)", azureLog);

        var googleCalls = new CannedHandler(_ => Task.FromResult(Throttled()));
        var google = new global::GoogleTtsProvider(
            new GenConfig { GoogleCloudTtsApiKey = "fake-key" }, innerHandler: googleCalls);
        var (googleLog, googleError) = await CaptureConsoleAsync(() =>
            google.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%", sceneLabel: "12/55"));
        Assert.NotNull(googleError);
        Assert.Equal(4, googleCalls.Calls);
        Assert.Contains("Google TTS failed (429)", googleError.Message);
        Assert.Contains("scene 12/55", googleLog);
        Assert.Contains("gave up after 4 attempts", googleLog);
        Assert.Contains("[google-tts] scene 12/55: retries exhausted after 4 attempts (HTTP 429)", googleLog);
    }

    // L78 — a malformed rate ("fast") must not synthesize silently at
    // 1.0x: the fallback line names the bad value and the scene.
    [Fact]
    public async Task GoogleTts_RateToSpeakingRate_LogsMalformedRate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tts-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outPath = Path.Combine(dir, "out.mp3");
            var audio = Convert.ToBase64String(new byte[] { 1, 2, 3 });
            var ok = new CannedHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { audioContent = audio }), Encoding.UTF8, "application/json")
            }));
            var google = new global::GoogleTtsProvider(
                new GenConfig { GoogleCloudTtsApiKey = "fake-key" }, innerHandler: ok);
            var (log, error) = await CaptureConsoleAsync(() =>
                google.SynthesizeAsync("hello world", "en", outPath, "fast", "0%", sceneLabel: "12/55"));
            Assert.Null(error);
            Assert.True(File.Exists(outPath));
            Assert.Contains("unparseable rate", log);
            Assert.Contains("\"fast\"", log);
            Assert.Contains("scene 12/55", log);
            Assert.Contains("using 1.0x", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
