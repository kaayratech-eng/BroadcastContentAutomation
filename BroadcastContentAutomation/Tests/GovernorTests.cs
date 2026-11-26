// Tests for the Trendline Now usage-governor wiring (2026-11-23):
// GovernorClient (parse / fail-open / rate-limit detector), the
// governor gates in FallbackScriptProvider and FallbackTtsProvider, and
// source guards that the factories pass the governor client in.
//
// No network, no live governor: the governor subprocess is replaced by
// a fake runner, and the pool legs are fake providers whose class names
// match the real legs (the pools map leg -> governor key by class name,
// so a nested fake named GroqHelperScriptProvider is governed as groq).
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class GovernorTests
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

    // ------------------------------------------------------------------
    // Fake governor subprocess + JSON helpers
    // ------------------------------------------------------------------

    private sealed class FakeRunner
    {
        public List<string[]> Calls { get; } = [];
        public Func<string[], ProcessHelper.Result> Respond { get; set; } =
            _ => new ProcessHelper.Result(0, AllowJson("groq"), "");

        public Task<ProcessHelper.Result> Run(string exe, IEnumerable<string> args)
        {
            Assert.Equal("python3", exe);
            var argv = args.ToArray();
            Calls.Add(argv);
            return Task.FromResult(Respond(argv));
        }
    }

    private static string AllowJson(string provider, double usage = 0, double soft = 0.9) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["allowed"] = true, ["provider"] = provider, ["reason"] = null,
            ["resets_at"] = null, ["usage_fraction"] = usage,
            ["soft_cap_fraction"] = soft, ["recorded"] = true, ["cooled"] = false,
        });

    private static string DenyJson(string provider, string reason, string resetsAt, double usage = 1.0) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["allowed"] = false, ["provider"] = provider, ["reason"] = reason,
            ["resets_at"] = resetsAt, ["usage_fraction"] = usage,
            ["soft_cap_fraction"] = 0.9, ["recorded"] = false, ["cooled"] = false,
        });

    private static string? ArgValue(string[] argv, string name)
    {
        var i = Array.IndexOf(argv, name);
        return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
    }

    private static string? ProviderOf(string[] argv) => ArgValue(argv, "--provider");

    // The client checks the helper file exists at construction; the fake
    // runner never executes it, so a shared stub file is enough.
    private static string StubHelperPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "governor-test-stub.py");
        File.WriteAllText(path, "# stub governor helper for GovernorTests\n");
        return path;
    }

    private static GovernorClient NewClient(FakeRunner fake) =>
        new(new GenConfig { GovernorHelperPath = StubHelperPath() }, fake.Run);

    // ------------------------------------------------------------------
    // Fake pool legs (class names match the real legs, so the pools'
    // type-name -> governor-key mapping governs them as the real legs)
    // ------------------------------------------------------------------

    private abstract class ScriptLegBase(Func<string, Task<string>> impl) : IScriptProvider
    {
        public bool Called { get; private set; }
        public Task<string> CompleteAsync(string prompt)
        {
            Called = true;
            return impl(prompt);
        }
    }

    private sealed class GroqHelperScriptProvider(Func<string, Task<string>> impl) : ScriptLegBase(impl) { }
    private sealed class GeminiHelperScriptProvider(Func<string, Task<string>> impl) : ScriptLegBase(impl) { }
    private sealed class KiloGatewayScriptProvider(Func<string, Task<string>> impl) : ScriptLegBase(impl) { }
    private sealed class CloudflareHelperScriptProvider(Func<string, Task<string>> impl) : ScriptLegBase(impl) { }

    private static Func<string, Task<string>> FailWith(string message) =>
        _ => Task.FromException<string>(new Exception(message));

    private static Func<string, Task<string>> Answer(string text) =>
        _ => Task.FromResult(text);

    private abstract class TtsLegBase(Func<Task> impl) : ITtsProvider
    {
        public bool Called { get; private set; }
        public Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null)
        {
            Called = true;
            return impl();
        }
    }

    private sealed class AzureHelperTtsProvider(Func<Task> impl) : TtsLegBase(impl) { }
    private sealed class GoogleHelperTtsProvider(Func<Task> impl) : TtsLegBase(impl) { }

    private static Func<Task> TtsFail(string message) =>
        () => Task.FromException(new Exception(message));

    private static Func<Task> TtsOk() => () => Task.CompletedTask;

    // ------------------------------------------------------------------
    // GovernorClient: parsing
    // ------------------------------------------------------------------

    [Fact]
    public async Task GovernorClient_ParsesAllowJson()
    {
        var fake = new FakeRunner();
        fake.Respond = _ => new ProcessHelper.Result(0, AllowJson("groq", usage: 0.42), "");
        var decision = await NewClient(fake).AcquireAsync("groq", tokens: 7000);
        Assert.True(decision.Allowed);
        Assert.False(decision.FailOpen);
        Assert.False(decision.Cooled);
        Assert.Null(decision.Reason);
        Assert.Null(decision.ResetsAt);
        Assert.Equal(0.42, decision.UsageFraction, 4);
        Assert.Equal(0.9, decision.SoftCapFraction, 4);
    }

    [Fact]
    public async Task GovernorClient_ParsesDenyJsonDespiteNonZeroExit()
    {
        // The governor exits 1 when denied but still prints a JSON
        // verdict — the exit code is NOT the signal, the JSON is.
        var fake = new FakeRunner();
        fake.Respond = _ => new ProcessHelper.Result(1,
            DenyJson("groq", "tokens_per_day budget 200000 exceeded (199000 used + 7000 asked)", "2026-11-24T00:00:00-05:00"), "");
        var decision = await NewClient(fake).AcquireAsync("groq", tokens: 7000);
        Assert.False(decision.Allowed);
        Assert.False(decision.FailOpen);
        Assert.Contains("tokens_per_day", decision.Reason);
        Assert.Equal("2026-11-24T00:00:00-05:00", decision.ResetsAt);
        Assert.Equal(1.0, decision.UsageFraction, 4);
    }

    [Fact]
    public async Task GovernorClient_CheckIsReadOnly()
    {
        var fake = new FakeRunner();
        var decision = await NewClient(fake).CheckAsync("groq");
        Assert.True(decision.Allowed);
        Assert.Equal("check", fake.Calls[0][1]);
    }

    // ------------------------------------------------------------------
    // GovernorClient: fail-open
    // ------------------------------------------------------------------

    [Fact]
    public async Task GovernorClient_FailOpenOnMissingHelperFile()
    {
        var fake = new FakeRunner { Respond = _ => throw new Exception("runner must not be called") };
        var missing = Path.Combine(Path.GetTempPath(), $"governor-missing-{Guid.NewGuid():N}.py");
        var client = new GovernorClient(new GenConfig { GovernorHelperPath = missing }, fake.Run);
        var decision = await client.AcquireAsync("groq", tokens: 7000);
        Assert.True(decision.Allowed);
        Assert.True(decision.FailOpen);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task GovernorClient_FailOpenOnBadJson()
    {
        var fake = new FakeRunner();
        fake.Respond = _ => new ProcessHelper.Result(0, "not json at all", "");
        var decision = await NewClient(fake).AcquireAsync("groq", tokens: 7000);
        Assert.True(decision.Allowed);
        Assert.True(decision.FailOpen);
    }

    [Fact]
    public async Task GovernorClient_FailOpenOnNonZeroExitWithoutJson()
    {
        var fake = new FakeRunner();
        fake.Respond = _ => new ProcessHelper.Result(1, "", "boom");
        var decision = await NewClient(fake).AcquireAsync("groq", tokens: 7000);
        Assert.True(decision.Allowed);
        Assert.True(decision.FailOpen);
    }

    [Fact]
    public async Task GovernorClient_FailOpenOnRunnerThrow()
    {
        var fake = new FakeRunner();
        fake.Respond = _ => throw new TimeoutException("governor timed out");
        var decision = await NewClient(fake).AcquireAsync("groq", tokens: 7000);
        Assert.True(decision.Allowed);
        Assert.True(decision.FailOpen);
    }

    [Fact]
    public async Task GovernorClient_MarkCooled_SendsProviderAndReason()
    {
        var fake = new FakeRunner();
        await NewClient(fake).MarkCooledAsync("groq", "HTTP 429");
        var call = Assert.Single(fake.Calls);
        Assert.Equal("mark-cooled", call[1]);
        Assert.Equal("groq", ProviderOf(call));
        Assert.Equal("HTTP 429", ArgValue(call, "--reason"));
    }

    [Fact]
    public async Task GovernorClient_MarkCooled_NeverThrows()
    {
        var fake = new FakeRunner { Respond = _ => throw new TimeoutException("boom") };
        await NewClient(fake).MarkCooledAsync("groq", "HTTP 429");
        Assert.Single(fake.Calls);
    }

    // ------------------------------------------------------------------
    // Rate-limit detector
    // ------------------------------------------------------------------

    [Fact]
    public void IsRateLimitFailure_TrueCases()
    {
        foreach (var message in new[]
        {
            "HTTP 429 Too Many Requests",
            "429",
            "rate limit exceeded",
            "Rate_Limit hit on groq",
            "too many requests",
            "Cloudflare 4006 quota error",
            "quota exceeded",
            "Quota Exhausted for today",
        })
            Assert.True(GovernorClient.IsRateLimitFailure(message), message);
    }

    [Fact]
    public void IsRateLimitFailure_FalseCases()
    {
        foreach (var message in new[]
        {
            "401 Unauthorized",
            "402 Payment Required",
            "403 Forbidden",
            "surrogate error: no connection registered",
            "no connection registered for zai",
            "plain failure",
            "",
        })
            Assert.False(GovernorClient.IsRateLimitFailure(message), message == "" ? "(empty)" : message);
        Assert.False(GovernorClient.IsRateLimitFailure(null));
    }

    // ------------------------------------------------------------------
    // FallbackScriptProvider + governor
    // ------------------------------------------------------------------

    [Fact]
    public async Task ScriptPool_CooledLegSkippedWithoutFailure()
    {
        var fake = new FakeRunner();
        fake.Respond = argv =>
        {
            if (argv[1] == "acquire" && ProviderOf(argv) == "groq")
                return new ProcessHelper.Result(1, DenyJson("groq", "cooled: rate limit", "2026-11-24T00:00:00-05:00"), "");
            return new ProcessHelper.Result(0, AllowJson(ProviderOf(argv) ?? "groq"), "");
        };
        var groq = new GroqHelperScriptProvider(FailWith("must not be called"));
        var kilo = new KiloGatewayScriptProvider(Answer("{\"scenes\": []}"));
        var pool = new FallbackScriptProvider([groq, kilo], NewClient(fake));
        Assert.Equal("{\"scenes\": []}", await pool.CompleteAsync("act prompt"));
        Assert.False(groq.Called);
        Assert.True(kilo.Called);
        var groqAcquire = Assert.Single(fake.Calls.Where(c => c[1] == "acquire" && ProviderOf(c) == "groq"));
        Assert.Equal("7000", ArgValue(groqAcquire, "--tokens"));
        Assert.Null(ArgValue(groqAcquire, "--units"));
    }

    [Fact]
    public async Task ScriptPool_AllCooledThrowsGovernorAllCooledException()
    {
        var fake = new FakeRunner();
        fake.Respond = argv => new ProcessHelper.Result(1,
            DenyJson(ProviderOf(argv) ?? "?", "cooled: rate limit", "2026-11-24T00:00:00-05:00"), "");
        var pool = new FallbackScriptProvider([
            new GroqHelperScriptProvider(FailWith("must not be called")),
            new KiloGatewayScriptProvider(FailWith("must not be called")),
        ], NewClient(fake));
        var ex = await Assert.ThrowsAsync<GovernorAllCooledException>(() => pool.CompleteAsync("act prompt"));
        Assert.Equal("2026-11-24T00:00:00-05:00", ex.ResetsAt);
    }

    [Fact]
    public async Task ScriptPool_RateLimitFailureMarksCooledAndFallsThrough()
    {
        var fake = new FakeRunner();
        var groq = new GroqHelperScriptProvider(FailWith("HTTP 429 Too Many Requests"));
        var kilo = new KiloGatewayScriptProvider(Answer("next leg answered"));
        var pool = new FallbackScriptProvider([groq, kilo], NewClient(fake));
        Assert.Equal("next leg answered", await pool.CompleteAsync("act prompt"));
        var cooled = Assert.Single(fake.Calls.Where(c => c[1] == "mark-cooled"));
        Assert.Equal("groq", ProviderOf(cooled));
        Assert.Contains("429", ArgValue(cooled, "--reason") ?? "");
    }

    [Fact]
    public async Task ScriptPool_KeyBlockedFailureDoesNotMarkCooled()
    {
        var fake = new FakeRunner();
        var gemini = new GeminiHelperScriptProvider(FailWith("surrogate error: no connection registered for gemini"));
        var kilo = new KiloGatewayScriptProvider(Answer("next leg answered"));
        var pool = new FallbackScriptProvider([gemini, kilo], NewClient(fake));
        Assert.Equal("next leg answered", await pool.CompleteAsync("act prompt"));
        Assert.DoesNotContain(fake.Calls, c => c[1] == "mark-cooled");
    }

    [Fact]
    public async Task ScriptPool_CloudflareAcquirePassesUnitsNotTokens()
    {
        var fake = new FakeRunner();
        var cloudflare = new CloudflareHelperScriptProvider(Answer("cf answered"));
        var pool = new FallbackScriptProvider([cloudflare], NewClient(fake));
        Assert.Equal("cf answered", await pool.CompleteAsync("act prompt"));
        var acquire = Assert.Single(fake.Calls.Where(c => c[1] == "acquire"));
        Assert.Equal("cloudflare", ProviderOf(acquire));
        Assert.Equal("926", ArgValue(acquire, "--units"));
        Assert.Null(ArgValue(acquire, "--tokens"));
    }

    // ------------------------------------------------------------------
    // FallbackTtsProvider + governor
    // ------------------------------------------------------------------

    [Fact]
    public async Task TtsPool_AzureAtSoftCapSkipped_GoogleVoices()
    {
        var fake = new FakeRunner();
        fake.Respond = argv =>
        {
            var provider = ProviderOf(argv) ?? "";
            if (argv[1] == "check" && provider == "azure-tts")
                return new ProcessHelper.Result(0, AllowJson("azure-tts", usage: 0.9, soft: 0.9), "");
            return new ProcessHelper.Result(0, AllowJson(provider, usage: 0.1), "");
        };
        var azure = new AzureHelperTtsProvider(TtsFail("must not be called"));
        var google = new GoogleHelperTtsProvider(TtsOk());
        var pool = new FallbackTtsProvider([azure, google], NewClient(fake));
        await pool.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%");
        Assert.False(azure.Called);
        Assert.True(google.Called);
    }

    [Fact]
    public async Task TtsPool_AzureHardDeniedSkipped_GoogleVoices()
    {
        var fake = new FakeRunner();
        fake.Respond = argv =>
        {
            if (argv[1] == "check" && ProviderOf(argv) == "azure-tts")
                return new ProcessHelper.Result(1,
                    DenyJson("azure-tts", "chars_per_month budget 500000 exceeded", "2026-12-01T00:00:00-05:00"), "");
            return new ProcessHelper.Result(0, AllowJson(ProviderOf(argv) ?? "google-tts"), "");
        };
        var azure = new AzureHelperTtsProvider(TtsFail("must not be called"));
        var google = new GoogleHelperTtsProvider(TtsOk());
        var pool = new FallbackTtsProvider([azure, google], NewClient(fake));
        await pool.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%");
        Assert.False(azure.Called);
        Assert.True(google.Called);
    }

    [Fact]
    public async Task TtsPool_CharsRecordedThroughAcquire()
    {
        var fake = new FakeRunner();
        var azure = new AzureHelperTtsProvider(TtsOk());
        var pool = new FallbackTtsProvider([azure], NewClient(fake));
        await pool.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%");
        Assert.True(azure.Called);
        Assert.Equal("check", fake.Calls[0][1]);
        var acquire = Assert.Single(fake.Calls.Where(c => c[1] == "acquire"));
        Assert.Equal("azure-tts", ProviderOf(acquire));
        Assert.Equal("11", ArgValue(acquire, "--chars"));
    }

    [Fact]
    public async Task TtsPool_RateLimitFailureMarksCooledAndFallsThrough()
    {
        var fake = new FakeRunner();
        var azure = new AzureHelperTtsProvider(TtsFail("HTTP 429 rate limit"));
        var google = new GoogleHelperTtsProvider(TtsOk());
        var pool = new FallbackTtsProvider([azure, google], NewClient(fake));
        await pool.SynthesizeAsync("hello world", "en", "/tmp/out.mp3", "+10%", "0%");
        Assert.True(google.Called);
        var cooled = Assert.Single(fake.Calls.Where(c => c[1] == "mark-cooled"));
        Assert.Equal("azure-tts", ProviderOf(cooled));
    }

    // ------------------------------------------------------------------
    // Config + factory wiring (source guards)
    // ------------------------------------------------------------------

    [Fact]
    public void GenConfig_GovernorDefaults()
    {
        var cfg = new GenConfig();
        Assert.Equal("/home/hatch/workspace/BroadcastContentAutomation/trendline/governor.py", cfg.GovernorHelperPath);
        Assert.Equal(7000, cfg.GovernorTokensPerActCall);
        Assert.Equal(926, cfg.GovernorCloudflareUnitsPerActCall);
        var source = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "GenConfig.cs");
        Assert.Contains("GovernorHelperPath", source);
        Assert.Contains("trendline/governor.py", source);
    }

    [Fact]
    public void FactoryPoolHelper_PassesGovernorClient()
    {
        var factory = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "ScriptProviderFactory.cs");
        var poolIdx = factory.IndexOf("\"pool-helper\" => new FallbackScriptProvider", StringComparison.Ordinal);
        Assert.True(poolIdx >= 0, "pool-helper case missing from ScriptProviderFactory");
        var hybridIdx = factory.IndexOf("\"hybrid\" =>", poolIdx, StringComparison.Ordinal);
        var govIdx = factory.IndexOf("GovernorClient.FromConfig(cfg)", poolIdx, StringComparison.Ordinal);
        Assert.True(govIdx > 0 && govIdx < hybridIdx,
            "pool-helper must pass GovernorClient.FromConfig(cfg) into FallbackScriptProvider");
    }

    [Fact]
    public void FactoryFallbackHelper_PassesGovernorClient()
    {
        var factory = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "TtsProviderFactory.cs");
        var fbIdx = factory.IndexOf("fallback-helper", StringComparison.Ordinal);
        Assert.True(fbIdx >= 0, "fallback-helper case missing from TtsProviderFactory");
        var googleIdx = factory.IndexOf("if (mode == \"google\")", fbIdx, StringComparison.Ordinal);
        var govIdx = factory.IndexOf("GovernorClient.FromConfig(cfg)", fbIdx, StringComparison.Ordinal);
        Assert.True(govIdx > fbIdx && govIdx < googleIdx,
            "fallback-helper must pass GovernorClient.FromConfig(cfg) into FallbackTtsProvider");
    }
}
