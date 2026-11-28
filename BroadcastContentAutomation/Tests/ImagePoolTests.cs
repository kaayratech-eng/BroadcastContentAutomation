// Tests for the rebuilt Trendline Now image pool (2026-11-24 bake-off):
// chain order (FLUX first, no SDXL, no discarded leg reachable), Pexels
// query derivation, prompt-suffix idempotence, FLUX quota detection +
// the loud resumable failure, governor shared-bucket wiring, and
// dormant legs skipping cleanly without keys.
//
// The live-chain tests invoke Program.cs's GenerateSquareArtAsync via
// reflection (it is a top-level local function) with stub helper
// scripts in a temp dir — no network, no live provider, no real job
// touched. They are async and awaited: Tests.Runner awaits async
// [Fact]s (fixed 2026-11-24 — the old runner dropped the Task, so an
// async test could never fail).
using System.Reflection;
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class ImagePoolTests
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

    // Real 1024x1024 scene art, reused read-only as the stub helpers'
    // fixture (same file QualityGateTests uses).
    private const string FixtureImage =
        "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261123-150439/scene00-art-square.jpg";

    // ------------------------------------------------------------------
    // Chain order (pure + source guards)
    // ------------------------------------------------------------------

    [Fact]
    public void ChainOrder_IsFluxPexelsOvhThenDormantLegs()
    {
        Assert.Equal(
            new[] { "cloudflare-flux", "pexels", "ovhcloud", "pollinations-keyed", "leonardo-image" },
            ImagePool.ChainOrder);
    }

    [Fact]
    public void ChainOrder_HasNoDiscardedLeg_AndNoSdxl()
    {
        Assert.DoesNotContain("pollinations", ImagePool.ChainOrder);
        Assert.DoesNotContain("ai-horde", ImagePool.ChainOrder);
        Assert.DoesNotContain("cloudflare-sdxl", ImagePool.ChainOrder);
        Assert.DoesNotContain("art-pool", ImagePool.ChainOrder);
    }

    [Fact]
    public void ChainSourceGuard_ProgramAttemptsInOrder_AndNoSdxlOrDiscardedAttempt()
    {
        var program = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "Program.cs");
        var start = program.IndexOf("static async Task<(string Path, string? ArtSource, ArtProvenance? Provenance)> GenerateSquareArtAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "GenerateSquareArtAsync not found in Program.cs");
        var region = program[start..];
        var end = region.IndexOf("Exception? lastError = null;", StringComparison.Ordinal);
        Assert.True(end > 0, "attempts list end not found");
        region = region[..end];

        var flux = region.IndexOf("TryFluxAsync", StringComparison.Ordinal);
        var pexels = region.IndexOf("only: \"pexels\"", StringComparison.Ordinal);
        var ovh = region.IndexOf("only: \"ovh\"", StringComparison.Ordinal);
        var pollinations = region.IndexOf("\"pollinations-keyed\"", StringComparison.Ordinal);
        var leonardo = region.IndexOf("\"leonardo-image\"", StringComparison.Ordinal);
        Assert.True(flux >= 0 && pexels > flux && ovh > pexels && pollinations > ovh && leonardo > pollinations,
            $"attempt order wrong: flux={flux} pexels={pexels} ovh={ovh} pollinations={pollinations} leonardo={leonardo}");
        Assert.DoesNotContain("cloudflare-sdxl", region);
        Assert.DoesNotContain("\"art-pool\"", region);
        Assert.DoesNotContain("horde", region);
        Assert.DoesNotContain("\"pollinations\"", region);
        Assert.DoesNotContain("GenerateAsync(", region);
        Assert.DoesNotContain("primaryIsPollinations", region);
        // CogView dropped 2026-11-24 (model not served on api.z.ai):
        // no Z.ai image attempt may remain in the chain.
        Assert.DoesNotContain("\"zai-image\"", region);
        Assert.DoesNotContain("ZaiImageClient", region);
    }

    [Fact]
    public void ChainSourceGuard_NoSdxlStepInCloudflareClientOrCharacterSource()
    {
        // No SDXL *call* remains (comments may still name the removed
        // model — the guard is about code, not prose).
        var client = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "CloudflareArtClient.cs");
        Assert.DoesNotContain("GenerateAsync(", client);
        Assert.DoesNotContain("CloudflareSdxl", client);
        Assert.DoesNotContain("CloudflareImageModel", client);
        var character = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "CharacterSource.cs");
        Assert.DoesNotContain("GenerateAsync(", character);
        Assert.DoesNotContain("CloudflareSdxl", character);
        Assert.DoesNotContain("CloudflareImageModel", character);
        var artPool = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "ArtPoolClient.cs");
        Assert.DoesNotContain("pollinations\" =>", artPool);
        Assert.DoesNotContain("horde\" =>", artPool);
    }

    [Fact]
    public void ChainSourceGuard_ArtPoolPassesOnlyQueryAndSeenFile()
    {
        var artPool = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "ArtPoolClient.cs");
        Assert.Contains("\"--only\"", artPool);
        Assert.Contains("\"--query\"", artPool);
        Assert.Contains("\"--seen-file\"", artPool);
    }

    // ------------------------------------------------------------------
    // Pexels query derivation
    // ------------------------------------------------------------------

    [Fact]
    public void PexelsQuery_ClimateScene_IsClimateQuery()
    {
        var q = ImagePool.BuildPexelsQuery(
            "COP31 Climate Summit Reaches a Historic Deal",
            ["COP31", "climate", "summit", "news"],
            "Delegates in Belém agreed a new finance pact after two weeks of talks at the summit.");
        Assert.Contains("COP31", q);
        Assert.Contains("Climate", q);
        Assert.Contains("Summit", q);
        Assert.DoesNotContain("wide", q);
        Assert.DoesNotContain("editorial", q);
        Assert.DoesNotContain("photograph", q);
        Assert.DoesNotContain("news", q, StringComparison.OrdinalIgnoreCase);
        Assert.True(q.Split(' ').Length <= 5, $"query too long: {q}");
    }

    [Fact]
    public void PexelsQuery_BisleriScene_NamesBisleri()
    {
        var q = ImagePool.BuildPexelsQuery(
            "Bisleri Bottled Water Empire Faces a New Challenge",
            ["Bisleri", "water", "business"],
            "Ramesh Chauhan built Bisleri into India's biggest bottled water brand.");
        Assert.Contains("Bisleri", q);
        Assert.DoesNotContain("wide", q);
    }

    [Fact]
    public void PexelsQuery_GenericTitle_YieldsEmpty()
    {
        var q = ImagePool.BuildPexelsQuery(
            "Big News Today",
            ["news", "today"],
            "Something happened somewhere.");
        Assert.Equal("", q);
    }

    [Fact]
    public void PexelsQuery_Dedupes_AndCapsAtFive()
    {
        var q = ImagePool.BuildPexelsQuery(
            "COP31 COP31 Climate Summit Deal Deal Deal More Words Here",
            ["COP31", "climate"],
            "Delegates met in Belém.");
        var words = q.Split(' ');
        Assert.True(words.Length <= 5, q);
        Assert.Equal(words.Length, words.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.StartsWith("COP31", q);
    }

    [Fact]
    public void PexelsQuery_StripsHashTags_AndUsesNarrationEntities()
    {
        var q = ImagePool.BuildPexelsQuery(
            "Summit Ends",
            ["#COP31"],
            "Delegates in Belém signed the pact.");
        Assert.Contains("COP31", q);
        Assert.DoesNotContain("#", q);
        Assert.Contains("Belém", q);
    }

    // ------------------------------------------------------------------
    // Prompt suffix: exactly once through every funnel
    // ------------------------------------------------------------------

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    [Fact]
    public void Sanitize_IsIdempotent()
    {
        var prompts = new[]
        {
            "Delegates at a climate summit in a large hall",
            QualityGate.FallbackArtPrompt,
            "Breaking headline: \"SUMMIT DEAL\" with lettering and a tweet quote",
            "Wide shot of delegates at the summit",
            "",
        };
        foreach (var prompt in prompts)
        {
            var once = QualityGate.SanitizeImagePrompt(prompt).Prompt;
            var twice = QualityGate.SanitizeImagePrompt(once).Prompt;
            Assert.Equal(once, twice);
            Assert.Equal(1, CountOccurrences(once, QualityGate.NoTextSuffix));
        }
    }

    [Fact]
    public void Sanitize_WideShotOnlyPrompt_GainsFullSuffixExactlyOnce()
    {
        // The old guard ("wide shot" present) under-appended here: the
        // prompt reached the model without "no text, no words, ...".
        var clean = QualityGate.SanitizeImagePrompt("Wide shot of delegates at the summit").Prompt;
        Assert.Contains("no text, no words, no letters, no signage", clean);
        Assert.Equal(1, CountOccurrences(clean, QualityGate.NoTextSuffix));
    }

    [Fact]
    public void Sanitize_AlreadySuffixedPrompt_Unchanged()
    {
        var prompt = $"Delegates at a climate summit, {QualityGate.NoTextSuffix}";
        Assert.Equal(prompt, QualityGate.SanitizeImagePrompt(prompt).Prompt);
    }

    // ------------------------------------------------------------------
    // Quota detection + loud resumable failure
    // ------------------------------------------------------------------

    [Fact]
    public void IsFluxQuotaError_TrueCases()
    {
        foreach (var message in new[]
        {
            "cloudflare flux helper exit 1: HTTP 403 in 1.2s: {\"success\": false, \"errors\": [{\"code\": 4006, \"message\": \"Daily limit exceeded\"}]}",
            "Cloudflare 4006 quota error",
            "quota exceeded",
            "Quota Exhausted for today",
        })
            Assert.True(ImagePool.IsFluxQuotaError(message), message);
    }

    [Fact]
    public void IsFluxQuotaError_FalseCases()
    {
        foreach (var message in new[]
        {
            "403 Forbidden",
            "402 Payment Required",
            "401 Unauthorized",
            "surrogate error: no connection registered",
            "plain failure",
            "",
        })
            Assert.False(ImagePool.IsFluxQuotaError(message), message == "" ? "(empty)" : message);
        Assert.False(ImagePool.IsFluxQuotaError(null));
    }

    [Fact]
    public void IsKeyBlockedError_Cases()
    {
        foreach (var message in new[]
        {
            "pollinations helper exit 1: HTTP 402 in 0.8s: no Pollen balance",
            "surrogate error: no connection registered for custom.zai",
            "401 Unauthorized",
            "403 Forbidden",
        })
            Assert.True(ImagePool.IsKeyBlockedError(message), message);
        foreach (var message in new[]
        {
            "cloudflare flux helper exit 1: HTTP 403 in 1.2s: {\"code\": 4006}",
            "HTTP 429 Too Many Requests",
            "plain failure",
            "",
        })
            Assert.False(ImagePool.IsKeyBlockedError(message), message == "" ? "(empty)" : message);
        Assert.False(ImagePool.IsKeyBlockedError(null));
    }

    [Fact]
    public void FluxQuotaException_MessageHasMarkerAndReset()
    {
        var ex = new FluxQuotaExhaustedException("scene12-art-square");
        Assert.Contains(FluxQuotaExhaustedException.Marker, ex.Message);
        Assert.Contains("FLUX_QUOTA_EXHAUSTED", ex.Message);
        Assert.Contains("resume after 00:00 UTC", ex.Message);
        Assert.Contains("scene12-art-square", ex.Message);
    }

    [Fact]
    public void SecondsUntilUtcMidnight_Cases()
    {
        Assert.Equal(14400, ImagePool.SecondsUntilUtcMidnight(new DateTime(2026, 11, 24, 20, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(1800, ImagePool.SecondsUntilUtcMidnight(new DateTime(2026, 11, 24, 23, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(86400, ImagePool.SecondsUntilUtcMidnight(new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void RateLimitDetector_4006WinsOver403()
    {
        // The quota error arrives wrapped in an HTTP 403 line; the old
        // detector (negative markers first) missed every real refusal.
        Assert.True(GovernorClient.IsRateLimitFailure(
            "HTTP 403 in 1.2s: {\"success\": false, \"errors\": [{\"code\": 4006}]}"));
        Assert.False(GovernorClient.IsRateLimitFailure("403 Forbidden"));
        Assert.False(GovernorClient.IsRateLimitFailure("402 Payment Required"));
    }

    // ------------------------------------------------------------------
    // Governor wiring: one shared Cloudflare bucket
    // ------------------------------------------------------------------

    [Fact]
    public void GovernorUnitsPerImageCall_Is173()
    {
        Assert.Equal(173, ImagePool.FluxUnitsPerImage);
        var cfg = new GenConfig();
        Assert.Equal(173, cfg.GovernorCloudflareUnitsPerImageCall);
        Assert.Equal(926, cfg.GovernorCloudflareUnitsPerActCall);
        Assert.Equal(173, new GovernorClient(cfg).CloudflareUnitsPerImageCall);
    }

    [Fact]
    public void GovernorConfig_ImageStage_IsNewChain_AndDiscardedProvidersGone()
    {
        var json = ReadRepoFile("trendline", "governor-config.json");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var image = root.GetProperty("stages").GetProperty("image");
        // Spotlight Now pivot (2026-11-24 ~23:14, Rishi): Wikimedia Commons
        // real star photos lead the image stage — tried before FLUX, only
        // when a scene names a ranked star. The rest of the chain is
        // unchanged from the 2026-11-24 rebuild.
        Assert.Equal(
            new[] { "wikimedia", "cloudflare", "pexels", "ovhcloud-image", "pollinations-keyed", "leonardo-image" },
            image.EnumerateArray().Select(e => e.GetString()).ToArray());
        var providers = root.GetProperty("providers");
        Assert.False(providers.TryGetProperty("ai-horde", out _));
        Assert.False(providers.TryGetProperty("pollinations-image", out _));
        Assert.True(providers.TryGetProperty("pollinations-keyed", out _));
        Assert.False(providers.TryGetProperty("zai-image", out _));
        Assert.True(providers.TryGetProperty("leonardo-image", out _));
        Assert.True(providers.TryGetProperty("wikimedia", out _));
        // One bucket: the image leg charges the same provider key as
        // the script leg, with no second 10k budget anywhere.
        var cloudflare = providers.GetProperty("cloudflare");
        Assert.Equal(10000, cloudflare.GetProperty("budgets").GetProperty("units_per_day").GetInt32());
        Assert.Contains("SHARED", cloudflare.GetProperty("note").GetString());
        Assert.Equal(173, cloudflare.GetProperty("units_per_image_estimate").GetInt32());
    }

    [Fact]
    public async Task GovernorClient_MarkCooled_PassesUntil()
    {
        var calls = new List<string[]>();
        Task<ProcessHelper.Result> Runner(string exe, IEnumerable<string> args)
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new ProcessHelper.Result(0, "{\"cooled_until\": \"2026-11-25T00:00:00-05:00\"}", ""));
        }
        var tmp = Path.Combine(Path.GetTempPath(), "imagepool-markcooled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var helper = Path.Combine(tmp, "governor.py");
            File.WriteAllText(helper, "# stub\n");
            var cfg = new GenConfig { GovernorHelperPath = helper };
            var client = new GovernorClient(cfg, Runner);
            await client.MarkCooledAsync("cloudflare", "quota 4006", until: "14400");
            Assert.Single(calls);
            Assert.Contains("--until", calls[0]);
            Assert.Contains("14400", calls[0]);
            Assert.Contains("cloudflare", calls[0]);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // Dormant cache
    // ------------------------------------------------------------------

    [Fact]
    public void DormantCache_NoteAndReset()
    {
        ImagePool.ResetForTests();
        Assert.False(ImagePool.IsDormant("leonardo-image"));
        ImagePool.NoteDormant("leonardo-image");
        Assert.True(ImagePool.IsDormant("leonardo-image"));
        ImagePool.ResetForTests();
        Assert.False(ImagePool.IsDormant("leonardo-image"));
    }

    // ------------------------------------------------------------------
    // Live chain via reflection + stub helpers
    // ------------------------------------------------------------------

    private sealed class ChainHarness : IDisposable
    {
        public string Dir { get; }
        public string LogPath { get; }
        public string GovernorStub { get; }
        public string FluxStub { get; }
        public string PoolStub { get; }
        public string DormantStub { get; }

        public ChainHarness()
        {
            Dir = Path.Combine(Path.GetTempPath(), "imagepool-chain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            LogPath = Path.Combine(Dir, "calls.log");
            GovernorStub = Path.Combine(Dir, "governor-stub.py");
            FluxStub = Path.Combine(Dir, "flux-stub.py");
            PoolStub = Path.Combine(Dir, "pool-stub.py");
            DormantStub = Path.Combine(Dir, "dormant-stub.py");
            // Governor stub: log argv, always allow (valid verdict JSON).
            File.WriteAllText(GovernorStub, $$"""
                import json, sys
                open(r"{{LogPath}}", "a").write("gov: " + " ".join(sys.argv[1:]) + "\n")
                print(json.dumps({"allowed": True, "provider": "cloudflare", "reason": None, "resets_at": None, "usage_fraction": 0.0, "soft_cap_fraction": 0.9, "cooled": False}))
                """);
            // Dormant stub: log argv, fail fast with a surrogate error.
            File.WriteAllText(DormantStub, $$"""
                import sys
                open(r"{{LogPath}}", "a").write("dormant: " + " ".join(sys.argv[1:]) + "\n")
                print("surrogate error: no connection registered", file=sys.stderr)
                sys.exit(1)
                """);
        }

        public void WriteFluxOk() => File.WriteAllText(FluxStub, $$"""
            import shutil, sys
            args = sys.argv[1:]
            out = args[args.index("--out") + 1]
            open(r"{{LogPath}}", "a").write("flux: " + " ".join(args) + "\n")
            shutil.copyfile(r"{{FixtureImage}}", out)
            print(f"OK 1.0s -> {out}")
            """);

        public void WriteFluxFailPlain() => File.WriteAllText(FluxStub, $$"""
            import sys
            open(r"{{LogPath}}", "a").write("flux: " + " ".join(sys.argv[1:]) + "\n")
            print('HTTP 500 in 0.5s: {"success": false}', file=sys.stderr)
            sys.exit(1)
            """);

        public void WriteFluxQuota() => File.WriteAllText(FluxStub, $$"""
            import sys
            open(r"{{LogPath}}", "a").write("flux: " + " ".join(sys.argv[1:]) + "\n")
            print('HTTP 403 in 1.2s: {"success": false, "errors": [{"code": 4006, "message": "Daily limit exceeded"}]}')
            sys.exit(1)
            """);

        public void WritePoolOk(string provider) => File.WriteAllText(PoolStub, $$"""
            import shutil, sys
            args = sys.argv[1:]
            out = args[args.index("--out") + 1]
            open(r"{{LogPath}}", "a").write("pool: " + " ".join(args) + "\n")
            shutil.copyfile(r"{{FixtureImage}}", out)
            print(f"artpool: {{provider}} -> {out}")
            """);

        public void WritePoolFail() => File.WriteAllText(PoolStub, """
            import sys
            print("artpool: all providers failed", file=sys.stderr)
            sys.exit(1)
            """);

        // Pool stub that fails Pexels but serves OVH — proves the full
        // FLUX → Pexels → OVH fallthrough.
        public void WritePoolFailPexelsOkOvh() => File.WriteAllText(PoolStub, $$"""
            import shutil, sys
            args = sys.argv[1:]
            out = args[args.index("--out") + 1]
            only = args[args.index("--only") + 1]
            open(r"{{LogPath}}", "a").write("pool: " + " ".join(args) + "\n")
            if only == "pexels":
                print("artpool: no pexels photo", file=sys.stderr)
                sys.exit(1)
            shutil.copyfile(r"{{FixtureImage}}", out)
            print(f"artpool: ovh -> {out}")
            """);

        public GenConfig Config(string leonardoModelId = "test-model") => new()
        {
            CloudflareHelperPath = FluxStub,
            ArtPoolHelperPath = PoolStub,
            PollinationsHelperPath = DormantStub,
            LeonardoHelperPath = DormantStub,
            GovernorHelperPath = GovernorStub,
            CloudflareAccountId = "test-account",
            LeonardoModelId = leonardoModelId,
        };

        public string[] LogLines() =>
            File.Exists(LogPath) ? File.ReadAllLines(LogPath) : [];

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    private static Task<(string Path, string? Source, ArtProvenance? Provenance)> GenerateSquareArt(
        GenConfig cfg, string prompt, string squareBase, int sceneIndex = -1,
        string? rawPrompt = null, string? title = null,
        IReadOnlyList<string>? tags = null, string? narration = null)
    {
        var method = ReflectionHelper.VideoGen.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            .FirstOrDefault(m => m.Name.Contains("GenerateSquareArtAsync", StringComparison.Ordinal)
                && m.GetParameters().Length == 8)
            ?? throw new MissingMethodException("GenerateSquareArtAsync (8-param) not found in VideoGen.dll.");
        return (Task<(string, string?, ArtProvenance?)>)method.Invoke(null,
            new object?[] { cfg, prompt, squareBase, sceneIndex, rawPrompt, title, tags, narration })!;
    }

    private const string TestPrompt =
        "Cinematic editorial news illustration of a climate summit hall, dark navy and red tones, dramatic lighting";

    [Fact]
    public async Task Chain_FluxFirst_OnLiveCall()
    {
        using var h = new ChainHarness();
        h.WriteFluxOk();
        h.WritePoolOk("ovh");
        ImagePool.ResetForTests();
        var squareBase = Path.Combine(h.Dir, "scene00-art-square");
        var (path, source, _) = await GenerateSquareArt(h.Config(), TestPrompt, squareBase, 0, TestPrompt,
            "COP31 Climate Summit Reaches a Historic Deal", ["COP31", "climate"], "Delegates in Belém agreed a pact.");
        Assert.Equal("cloudflare-flux", source);
        Assert.True(File.Exists(path));
        var log = h.LogLines();
        Assert.Contains(log, l => l.StartsWith("flux:"));
        Assert.DoesNotContain(log, l => l.StartsWith("pool:"));
        Assert.DoesNotContain(log, l => l.StartsWith("dormant:"));
        // The FLUX acquire charges 173 units against the shared bucket.
        Assert.Contains(log, l => l.StartsWith("gov:") && l.Contains("--provider cloudflare") && l.Contains("--units 173"));
    }

    [Fact]
    public async Task Chain_FallsThroughToPexels_WhenFluxFails()
    {
        using var h = new ChainHarness();
        h.WriteFluxFailPlain();
        h.WritePoolOk("pexels");
        ImagePool.ResetForTests();
        var squareBase = Path.Combine(h.Dir, "scene00-art-square");
        var (path, source, _) = await GenerateSquareArt(h.Config(), TestPrompt, squareBase, 0, TestPrompt,
            "COP31 Climate Summit Reaches a Historic Deal", ["COP31", "climate"], "Delegates in Belém agreed a pact.");
        Assert.Equal("pexels-stock", source);
        Assert.True(File.Exists(path));
        var log = h.LogLines();
        var fluxAt = Array.FindIndex(log, l => l.StartsWith("flux:"));
        var poolAt = Array.FindIndex(log, l => l.StartsWith("pool:"));
        Assert.True(fluxAt >= 0 && poolAt > fluxAt, "FLUX must be attempted before Pexels");
        var poolLine = log[poolAt];
        Assert.Contains("--only pexels", poolLine);
        Assert.Contains("--query COP31 Climate Summit", poolLine);
        Assert.Contains("--seen-file", poolLine);
        Assert.Contains("pexels-used.json", poolLine);
    }

    [Fact]
    public async Task Chain_FallsThroughToOvh_WhenFluxAndPexelsFail()
    {
        using var h = new ChainHarness();
        h.WriteFluxFailPlain();
        h.WritePoolFailPexelsOkOvh();
        ImagePool.ResetForTests();
        var squareBase = Path.Combine(h.Dir, "scene00-art-square");
        var (path, source, _) = await GenerateSquareArt(h.Config(), TestPrompt, squareBase, 0, TestPrompt,
            "COP31 Climate Summit Reaches a Historic Deal", ["COP31", "climate"], "Delegates in Belém agreed a pact.");
        Assert.Equal("ovhcloud", source);
        Assert.True(File.Exists(path));
        var log = h.LogLines();
        var fluxAt = Array.FindIndex(log, l => l.StartsWith("flux:"));
        var pexelsAt = Array.FindIndex(log, l => l.StartsWith("pool:") && l.Contains("--only pexels"));
        var ovhAt = Array.FindIndex(log, l => l.StartsWith("pool:") && l.Contains("--only ovh"));
        Assert.True(fluxAt >= 0 && pexelsAt > fluxAt && ovhAt > pexelsAt,
            "order must be FLUX, then Pexels, then OVH");
        Assert.DoesNotContain(log, l => l.StartsWith("dormant:"));
        Assert.DoesNotContain(log, l => l.Contains("zai"));
    }

    [Fact]
    public async Task Chain_QuotaExhaustion_FailsLoud_AndDormantLegsAttemptedOnce()
    {
        using var h = new ChainHarness();
        h.WriteFluxQuota();
        h.WritePoolFail();
        ImagePool.ResetForTests();
        var squareBase = Path.Combine(h.Dir, "scene00-art-square");
        // Scene 0: every leg fails — the quota marker must fire.
        var ex = await Assert.ThrowsAsync<FluxQuotaExhaustedException>(() =>
            GenerateSquareArt(h.Config(), TestPrompt, squareBase, 0, TestPrompt,
                "COP31 Climate Summit Reaches a Historic Deal", ["COP31"], "Delegates met."));
        Assert.Contains("FLUX_QUOTA_EXHAUSTED", ex.Message);
        Assert.Contains("resume after 00:00 UTC", ex.Message);
        var log = h.LogLines();
        Assert.Single(log, l => l.StartsWith("flux:"));
        Assert.Equal(2, log.Count(l => l.StartsWith("dormant:")));
        Assert.Contains(log, l => l.StartsWith("gov:") && l.Contains("mark-cooled") && l.Contains("--until"));
        // Scene 1 (same process): FLUX is still quota-dead, but the
        // dormant legs must NOT be called again — one cheap failure
        // per leg per run, never one per scene.
        var squareBase1 = Path.Combine(h.Dir, "scene01-art-square");
        await Assert.ThrowsAsync<FluxQuotaExhaustedException>(() =>
            GenerateSquareArt(h.Config(), TestPrompt, squareBase1, 1, TestPrompt,
                "COP31 Climate Summit Reaches a Historic Deal", ["COP31"], "Delegates met."));
        var log2 = h.LogLines();
        Assert.Equal(2, log2.Count(l => l.StartsWith("dormant:")));
        Assert.Equal(2, log2.Count(l => l.StartsWith("flux:")));
    }

    [Fact]
    public async Task Chain_Resume_SkipsOnDiskArtWithoutCallingAnyProvider()
    {
        using var h = new ChainHarness();
        h.WriteFluxOk();
        h.WritePoolOk("ovh");
        ImagePool.ResetForTests();
        var squareBase = Path.Combine(h.Dir, "scene00-art-square");
        var (path, source, _) = await GenerateSquareArt(h.Config(), TestPrompt, squareBase, 0, TestPrompt,
            "COP31 Climate Summit Reaches a Historic Deal", ["COP31"], "Delegates met.");
        Assert.Equal("cloudflare-flux", source);
        var linesAfterFirst = h.LogLines().Length;
        // A resume finds the square already on disk: no provider, no
        // governor call, no regeneration.
        var (path2, source2, _) = await GenerateSquareArt(h.Config(), TestPrompt, squareBase, 0, TestPrompt,
            "COP31 Climate Summit Reaches a Historic Deal", ["COP31"], "Delegates met.");
        Assert.Equal(path, path2);
        Assert.Null(source2);
        Assert.Equal(linesAfterFirst, h.LogLines().Length);
    }

    [Fact]
    public async Task Chain_LeonardoWithoutModelId_IsSkippedWithoutACall()
    {
        using var h = new ChainHarness();
        h.WriteFluxFailPlain();
        h.WritePoolFail();
        ImagePool.ResetForTests();
        var squareBase = Path.Combine(h.Dir, "scene00-art-square");
        // Pollinations fails dormant via the stub; Leonardo has
        // no model id, so it must be skipped before any call — the
        // failure is the generic all-providers one, not the quota one
        // (FLUX failed plain, not quota).
        var ex = await Assert.ThrowsAsync<Exception>(() =>
            GenerateSquareArt(h.Config(leonardoModelId: ""), TestPrompt, squareBase, 0, TestPrompt,
                "COP31 Climate Summit Reaches a Historic Deal", ["COP31"], "Delegates met."));
        Assert.DoesNotContain("FLUX_QUOTA_EXHAUSTED", ex.Message);
        Assert.Contains("All scene-art providers failed", ex.Message);
        var log = h.LogLines();
        Assert.Equal(1, log.Count(l => l.StartsWith("dormant:")));
    }

    // L48 — a per-leg failure in the chain must name the scene and the
    // leg: with 5-6 legs x 55 scenes the old bare line was unactionable.
    [Fact]
    public async Task GenerateSquareArt_LogsSceneAndLegOnLegFailure()
    {
        using var h = new ChainHarness();
        h.WriteFluxFailPlain();
        h.WritePoolFailPexelsOkOvh();
        ImagePool.ResetForTests();
        var squareBase = Path.Combine(h.Dir, "scene07-art-square");
        var sb = new System.Text.StringBuilder();
        var oldOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter(sb));
            var (path, source, _) = await GenerateSquareArt(h.Config(), TestPrompt, squareBase, 7, TestPrompt,
                "COP31 Climate Summit Reaches a Historic Deal", ["COP31", "climate"], "Delegates in Belém agreed a pact.");
            Assert.Equal("ovhcloud", source);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Console.SetOut(oldOut);
        }
        var log = sb.ToString();
        Assert.Contains("scene 7", log);
        Assert.Contains("leg pexels", log);
        Assert.Contains("leg cloudflare-flux", log);
    }

    // L49 — the FLUX quota line must name the scene, the 4006 detail and
    // the park duration (seconds until the 00:00 UTC reset).
    [Fact]
    public async Task GenerateSquareArt_FluxQuotaLogNamesSceneAndReset()
    {
        using var h = new ChainHarness();
        h.WriteFluxQuota();
        h.WritePoolFail();
        ImagePool.ResetForTests();
        var squareBase = Path.Combine(h.Dir, "scene03-art-square");
        var sb = new System.Text.StringBuilder();
        var oldOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter(sb));
            await Assert.ThrowsAsync<FluxQuotaExhaustedException>(() =>
                GenerateSquareArt(h.Config(), TestPrompt, squareBase, 3, TestPrompt,
                    "COP31 Climate Summit Reaches a Historic Deal", ["COP31"], "Delegates met."));
        }
        finally
        {
            Console.SetOut(oldOut);
        }
        var log = sb.ToString();
        Assert.Contains("scene 3", log);
        Assert.Contains("4006", log);
        Assert.Contains("parked for ", log);
        Assert.Contains("s until 00:00 UTC", log);
    }

    // L50 — the terminal art-pool failure must name the scene file, the
    // leg and the helper's last error, not just "failed after 2 attempts"
    // (the old summary throw was dead code: the last attempt's exception
    // propagated raw).
    [Fact]
    public async Task ArtPoolClient_FinalFailureIncludesSceneLegAndLastError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "artpool-final-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stub = Path.Combine(dir, "pool-fail.py");
            await File.WriteAllTextAsync(stub,
                "import sys\nprint('artpool: boom test failure', file=sys.stderr)\nsys.exit(1)\n");
            var output = Path.Combine(dir, "scene07-art-square.jpg");
            var cfg = new GenConfig { ArtPoolHelperPath = stub };
            var sb = new System.Text.StringBuilder();
            var oldOut = Console.Out;
            Exception ex;
            try
            {
                Console.SetOut(new StringWriter(sb));
                ex = await Assert.ThrowsAsync<Exception>(() =>
                    ArtPoolClient.GenerateSquareWithSourceAsync(cfg, "a test prompt", output, maxAttempts: 2));
            }
            finally
            {
                Console.SetOut(oldOut);
            }
            Assert.Contains("failed after 2 attempts", ex.Message);
            Assert.Contains("scene07-art-square.jpg", ex.Message);
            Assert.Contains("art-pool", ex.Message);
            Assert.Contains("boom test failure", ex.Message);
            var log = sb.ToString();
            Assert.Contains("art leg art-pool scene scene07-art-square.jpg failed after 2 attempts", log);
            Assert.Contains("boom test failure", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
