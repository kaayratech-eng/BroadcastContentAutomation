// Regression tests for the fixes review #2 noted had NO test at all:
// #1 (script pool under the driver), #3 (Pollinations script leg) and
// #6 (no sting, no brief — removed 2026-11-25, Rishi).
//
// #1 and #3 are pinned as source guards plus live-value checks: the pool
// order and the Pollinations endpoint/unwrap shape are the contract, and
// a live call would burn provider quota and need the network, so the
// suite asserts the wiring instead of calling out. #6 is pinned against
// the real TrendlineNowProfile value and the assemble-path wiring in
// Program.cs.
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class ScriptPoolTests
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
    // Fix #1 — script pool under the driver (Groq -> Cloudflare ->
    // Pollinations, per act, via pool-helper)
    // ------------------------------------------------------------------

    [Fact]
    public void ScriptPool_DriverExportsPoolHelper()
    {
        // trendline/driver.sh is the single source of truth for the prep
        // env (config.json prep_env was removed 2026-11-22, B10 — the
        // driver never read it). The export must name pool-helper.
        var driver = ReadRepoFile("trendline", "driver.sh");
        Assert.Contains("export GIGGLE_ScriptProvider=pool-helper", driver);
    }

    [Fact]
    public void ScriptPool_FactoryPoolHelper_OrdersGroqCloudflarePollinations()
    {
        // ScriptProviderFactory's "pool-helper" leg must try Groq first,
        // then Cloudflare Workers AI, then Pollinations — in that order.
        // Anchor on the FallbackScriptProvider case itself, not the first
        // "pool-helper" mention — the header comment also names
        // pool-helper, and the individual pin lines above the case list
        // the providers in a different order.
        var factory = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "ScriptProviderFactory.cs");
        var poolIdx = factory.IndexOf("\"pool-helper\" => new FallbackScriptProvider", StringComparison.Ordinal);
        Assert.True(poolIdx >= 0, "pool-helper case missing from ScriptProviderFactory");
        var groqIdx = factory.IndexOf("GroqHelperScriptProvider", poolIdx, StringComparison.Ordinal);
        var cloudflareIdx = factory.IndexOf("CloudflareHelperScriptProvider", poolIdx, StringComparison.Ordinal);
        var pollinationsIdx = factory.IndexOf("PollinationsScriptProvider", poolIdx, StringComparison.Ordinal);
        Assert.True(groqIdx > 0 && cloudflareIdx > groqIdx && pollinationsIdx > cloudflareIdx,
            "pool-helper must list Groq, then Cloudflare, then Pollinations, in that order");
    }

    [Fact]
    public void ScriptPool_FallbackTriesProvidersInOrderAndThrowsOnlyWhenAllFail()
    {
        // FallbackScriptProvider: return the first provider that answers;
        // only throw when every provider failed, listing each failure.
        var fallback = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "FallbackScriptProvider.cs");
        Assert.Contains("foreach (var provider in providers)", fallback);
        Assert.Contains("return await provider.CompleteAsync(prompt)", fallback);
        Assert.Contains("All script providers failed", fallback);
    }

    [Fact]
    public void ScriptPool_AppSettingsBeltsAndBracesPoolHelper()
    {
        // VideoGen/appsettings.Local.json also names pool-helper, so a
        // prep launched without the driver export still uses the pool.
        var settings = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "appsettings.Local.json");
        using var doc = JsonDocument.Parse(settings);
        Assert.Equal("pool-helper", doc.RootElement.GetProperty("ScriptProvider").GetString());
    }

    // ------------------------------------------------------------------
    // Script-pool expansion (2026-11-23) — 8 legs: Groq, Gemini
    // (new-project key), Z.ai, Cloudflare, OpenRouter, Kilo, OVHcloud,
    // Pollinations. Live-verified legs: Kilo (openrouter/free router,
    // 22 scenes in 38.6s), Pollinations (20 scenes in 10.5s),
    // OVHcloud endpoint+model present but the anonymous bucket 429s.
    // Key-blocked legs (Gemini, Z.ai, OpenRouter) fail fast and activate
    // automatically when the key is added. Pinned as source guards plus
    // a live FallbackScriptProvider behaviour test (fake providers, no
    // network): the pool order and the new legs' endpoints/models are
    // the contract, and a live call would burn provider quota.
    // ------------------------------------------------------------------

    [Fact]
    public void ScriptPool_FactoryPoolHelper_FullEightLegOrder()
    {
        // The expanded pool order (2026-11-23): Groq, then Gemini, then
        // Z.ai, then Cloudflare, then OpenRouter, then Kilo, then
        // OVHcloud, then Pollinations. Anchor on the FallbackScriptProvider
        // case itself, not the first "pool-helper" mention — the header
        // comment also names pool-helper, and the individual pin lines
        // above the case list the providers in a different order.
        var factory = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "ScriptProviderFactory.cs");
        var poolIdx = factory.IndexOf("\"pool-helper\" => new FallbackScriptProvider", StringComparison.Ordinal);
        Assert.True(poolIdx >= 0, "pool-helper case missing from ScriptProviderFactory");
        var groqIdx = factory.IndexOf("GroqHelperScriptProvider", poolIdx, StringComparison.Ordinal);
        var geminiIdx = factory.IndexOf("GeminiHelperScriptProvider", poolIdx, StringComparison.Ordinal);
        var zaiIdx = factory.IndexOf("ZaiHelperScriptProvider", poolIdx, StringComparison.Ordinal);
        var cloudflareIdx = factory.IndexOf("CloudflareHelperScriptProvider", poolIdx, StringComparison.Ordinal);
        var openRouterIdx = factory.IndexOf("OpenRouterHelperScriptProvider", poolIdx, StringComparison.Ordinal);
        var kiloIdx = factory.IndexOf("KiloGatewayScriptProvider", poolIdx, StringComparison.Ordinal);
        var ovhIdx = factory.IndexOf("OvhCloudScriptProvider", poolIdx, StringComparison.Ordinal);
        var pollinationsIdx = factory.IndexOf("PollinationsScriptProvider", poolIdx, StringComparison.Ordinal);
        Assert.True(groqIdx > 0 && geminiIdx > groqIdx && zaiIdx > geminiIdx
            && cloudflareIdx > zaiIdx && openRouterIdx > cloudflareIdx
            && kiloIdx > openRouterIdx && ovhIdx > kiloIdx && pollinationsIdx > ovhIdx,
            "pool-helper must list Groq, Gemini, Z.ai, Cloudflare, OpenRouter, Kilo, OVHcloud, Pollinations, in that order");
    }

    [Fact]
    public void ScriptPool_FactoryHasIndividualPinsForNewLegs()
    {
        // Each new leg can also be pinned alone for testing.
        var factory = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "ScriptProviderFactory.cs");
        Assert.Contains("\"zai-helper\" => new ZaiHelperScriptProvider(cfg)", factory);
        Assert.Contains("\"openrouter-helper\" => new OpenRouterHelperScriptProvider(cfg)", factory);
        Assert.Contains("\"kilo\" => new KiloGatewayScriptProvider()", factory);
        Assert.Contains("\"ovhcloud\" => new OvhCloudScriptProvider()", factory);
    }

    [Fact]
    public void Kilo_PostsToGatewayEndpointWithOpenRouterFree()
    {
        // The gateway endpoint carries the /api/gateway path — the bare
        // /chat/completions URL 404s on every call (verified 2026-11-23).
        // The model is the openrouter/free router, NOT a pinned model:
        // pinning nvidia/nemotron-3-ultra-550b-a55b:free timed out at
        // 180s the same day.
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "KiloGatewayScriptProvider.cs");
        Assert.Contains("https://api.kilo.ai/api/gateway/chat/completions", provider);
        Assert.Contains("[\"model\"] = \"openrouter/free\"", provider);
        // Exactly one model assignment in the file, and it is the router:
        // the pinned nvidia/nemotron-3-ultra-550b-a55b:free model is named
        // in the comment only, as the model NOT to use (it timed out at
        // 180s, verified 2026-11-23).
        var modelAssignments = provider.Split("[\"model\"] = ", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, modelAssignments);
    }

    [Fact]
    public void Kilo_DisablesReasoning()
    {
        // Reasoning trap (2026-11-23): the openrouter/free router can
        // pick a reasoning model that burns the whole max_tokens budget
        // on thinking and returns content=null. The payload must disable
        // reasoning - verified live: reasoning_tokens went 8192 -> 0 and
        // the leg returned 12 valid scenes in 15s on a real 13,111-char
        // act-1 prompt.
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "KiloGatewayScriptProvider.cs");
        Assert.Contains("[\"reasoning\"] = new { enabled = false }", provider);
    }

    [Fact]
    public void Kilo_ThrowsOnNullContentInsteadOfFalseParsingRawBody()
    {
        // A wrapper that parsed with null/empty content is a hard
        // failure, not a fall back to the raw body: in that case the raw
        // body is the model's reasoning trace, and falling back
        // false-parsed a 65K span into a 0-scene script (dry-run FATAL,
        // 2026-11-23). The same guard is pinned on the OVH leg, whose
        // gpt-oss-120b is also a reasoning model.
        var kilo = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "KiloGatewayScriptProvider.cs");
        Assert.Contains("returned no content", kilo);
        var ovh = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "OvhCloudScriptProvider.cs");
        Assert.Contains("returned no content", ovh);
    }

    [Fact]
    public void Kilo_UnwrapsChoicesMessageContent()
    {
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "KiloGatewayScriptProvider.cs");
        Assert.Contains("TryGetProperty(\"choices\"", provider);
        Assert.Contains("TryGetProperty(\"message\"", provider);
        Assert.Contains("TryGetProperty(\"content\"", provider);
        Assert.Contains("catch (JsonException)", provider);
    }

    [Fact]
    public void OvhCloud_PostsToKeplerEndpointWithGptOss120b()
    {
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "OvhCloudScriptProvider.cs");
        Assert.Contains("https://oai.endpoints.kepler.ai.cloud.ovh.net/v1/chat/completions", provider);
        Assert.Contains("\"gpt-oss-120b\"", provider);
    }

    [Fact]
    public void OvhCloud_UnwrapsChoicesMessageContent()
    {
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "OvhCloudScriptProvider.cs");
        Assert.Contains("TryGetProperty(\"choices\"", provider);
        Assert.Contains("TryGetProperty(\"message\"", provider);
        Assert.Contains("TryGetProperty(\"content\"", provider);
        Assert.Contains("catch (JsonException)", provider);
    }

    [Fact]
    public void ZaiHelper_RunsZaiChatWithGlmFlash()
    {
        // Z.ai leg: vault-backed helper, glm-4.7-flash, $0 in/out.
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "ZaiHelperScriptProvider.cs");
        Assert.Contains("cfg.ZaiHelperPath", provider);
        Assert.Contains("\"glm-4.7-flash\"", provider);
        var config = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "GenConfig.cs");
        Assert.Contains("ZaiHelperPath", config);
        Assert.Contains("custom.zai/bin/zai_chat.py", config);
    }

    [Fact]
    public void OpenRouterHelper_RunsOpenRouterChatWithFreeRouter()
    {
        // OpenRouter leg: vault-backed helper, the openrouter/free router
        // (never a pinned :free model — the roster rotates fast).
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "OpenRouterHelperScriptProvider.cs");
        Assert.Contains("cfg.OpenRouterHelperPath", provider);
        Assert.Contains("\"openrouter/free\"", provider);
        var config = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "GenConfig.cs");
        Assert.Contains("OpenRouterHelperPath", config);
        Assert.Contains("custom.openrouter/bin/openrouter_chat.py", config);
    }

    [Fact]
    public void CloudflareTextModel_DefaultsToLlama()
    {
        // The Cloudflare text model is a config knob, defaulting to
        // Llama 3.3 70B. The gpt-oss-120b switch (~400 neurons/act vs
        // ~926) stays a config change until the neuron rate is verified
        // in the dashboard — the model name was accepted by the API but
        // the neuron burn is dashboard-only.
        var config = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "GenConfig.cs");
        Assert.Contains("CloudflareTextModel", config);
        Assert.Contains("@cf/meta/llama-3.3-70b-instruct-fp8-fast", config);
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "CloudflareHelperScriptProvider.cs");
        Assert.Contains("cfg.CloudflareTextModel", provider);
    }

    private sealed class FailingProvider(string message) : IScriptProvider
    {
        public Task<string> CompleteAsync(string prompt) =>
            Task.FromException<string>(new Exception(message));
    }

    private sealed class AnsweringProvider(string answer) : IScriptProvider
    {
        public Task<string> CompleteAsync(string prompt) => Task.FromResult(answer);
    }

    [Fact]
    public async Task Fallback_FallsThroughToNextLegOnFailure()
    {
        // A dead leg (e.g. a key-blocked helper that fails fast) must not
        // stop the pool — the next leg's answer is returned.
        var pool = new FallbackScriptProvider([
            new FailingProvider("surrogate error: no connection registered"),
            new AnsweringProvider("{\"scenes\": []}"),
        ]);
        Assert.Equal("{\"scenes\": []}", await pool.CompleteAsync("act prompt"));
    }

    [Fact]
    public async Task Fallback_ThrowsListingAllFailuresWhenEveryLegFails()
    {
        var pool = new FallbackScriptProvider([
            new FailingProvider("groq 429"),
            new FailingProvider("cloudflare 4006"),
        ]);
        var ex = await Assert.ThrowsAsync<Exception>(() => pool.CompleteAsync("act prompt"));
        Assert.Contains("All script providers failed", ex.Message);
        Assert.Contains("groq 429", ex.Message);
        Assert.Contains("cloudflare 4006", ex.Message);
    }

    // ------------------------------------------------------------------
    // Fix #3 — Pollinations script leg posts to /openai and unwraps
    // choices[0].message.content
    // ------------------------------------------------------------------

    [Fact]
    public void Pollinations_PostsToOpenAiEndpoint()
    {
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "PollinationsScriptProvider.cs");
        Assert.Contains("https://text.pollinations.ai/openai", provider);
        Assert.Contains("PostAsync", provider);
    }

    [Fact]
    public void Pollinations_UnwrapsChoicesMessageContent()
    {
        // /openai returns a chat-completion wrapper; the leg must unwrap
        // choices[0].message.content (and fall back to the raw body when
        // the shape is not a wrapper).
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "PollinationsScriptProvider.cs");
        Assert.Contains("TryGetProperty(\"choices\"", provider);
        Assert.Contains("TryGetProperty(\"message\"", provider);
        Assert.Contains("TryGetProperty(\"content\"", provider);
    }

    [Fact]
    public void Pollinations_FallsBackToRawBodyOnNonJson()
    {
        var provider = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "PollinationsScriptProvider.cs");
        Assert.Contains("catch (JsonException)", provider);
    }

    // ------------------------------------------------------------------
    // No sting, no brief (Rishi 2026-11-25 13:55 EST): the 3.5s logo sting
    // and the 10-20s teaser brief are gone — videos open directly with the
    // hook. The intro settings are removed from the profile entirely, and
    // the assemble path carries no intro references.
    // ------------------------------------------------------------------

    [Fact]
    public void NoSting_ProfileHasNoIntroSettings()
    {
        // IntroSeconds / VerticalIntroSeconds / IntroLogoPath are gone from
        // ContentProfile — no profile can ask for a sting any more.
        var profile = ReflectionHelper.TrendlineNowProfile();
        var type = profile.GetType();
        Assert.Null(type.GetProperty("IntroSeconds"));
        Assert.Null(type.GetProperty("VerticalIntroSeconds"));
        Assert.Null(type.GetProperty("IntroLogoPath"));
    }

    [Fact]
    public void ShortOutro_EndCardDirectsToYouTube()
    {
        // Rishi 2026-11-25 13:56 EST: every Short ends with a catchy line
        // sending viewers to the full story on YouTube — spoken AND on the
        // end card. On-screen carries the emoji + literal handle; spoken
        // says the channel name (no "@" for TTS to spell out).
        var profile = ReflectionHelper.TrendlineNowProfile();
        var line = (string?)profile.GetType().GetProperty("ShortOutroLine")?.GetValue(profile);
        var spoken = (string?)profile.GetType().GetProperty("ShortOutroSpoken")?.GetValue(profile);
        Assert.Equal("🎬 Want the full story? Watch it on YouTube — @spotlightnowofficial", line);
        Assert.Equal("Want the full story? Watch it on YouTube — Spotlight Now.", spoken);
    }

    [Fact]
    public void AssemblePath_HasNoIntroReferences()
    {
        // The assemble/write-props path must not resolve or pass any intro:
        // no sting to fail fast on, no vertical intro length, no effective
        // landscape intro.
        var program = ReadRepoFile("BroadcastContentAutomation", "VideoGen", "Program.cs");
        Assert.DoesNotContain("IntroSeconds", program);
        Assert.DoesNotContain("IntroLogoPath", program);
        Assert.DoesNotContain("VerticalIntroSeconds", program);
        Assert.DoesNotContain("EffectiveLandscapeIntroSeconds", program);
        Assert.DoesNotContain("BuildBriefScenes", program);
        Assert.DoesNotContain("ResolveBriefImages", program);
        // The Short's end card comes from the profile, not a hardcoded line.
        Assert.Contains("profile.ShortOutroLine", program);
        Assert.Contains("profile.ShortOutroSpoken", program);
    }
}
