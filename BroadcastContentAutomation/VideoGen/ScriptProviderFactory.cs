// Picks which IScriptProvider writes the script. "hybrid" (default) splits a
// single long-form script's acts across both free-tier providers - Groq for
// acts 1-2, Gemini for acts 3-4 - so each one's daily quota only has to cover
// half a script's requests (see HybridScriptProvider). On short-form scripts
// (a single CompleteAsync call) hybrid behaves the same as groq alone, since
// the call count never reaches the handoff point. "groq"/"gemini" pin to one
// free-tier provider alone - see GroqScriptProvider/GeminiScriptProvider for
// the terms/limits that make each one safe to use for real (not just
// prototyping) at low volume. "claude" is the zero-free-tier-quota-risk,
// paid fallback.
// The "-helper" variants ("gemini-helper", "groq-helper", "hybrid-helper")
// are the same providers routed through the vault-backed Python helpers
// (custom.gemini / custom.groq) instead of a key in config - use them
// whenever the key lives in Muse's secure vault. "gemini-helper" is the
// Muse-system default (see appsettings.Local.json): the Gemini key is in
// the vault, and a short-form test needs only one provider.
// "pool-helper" (2026-11-22, Trendline Now - the working default, expanded
// 2026-11-23 to 8 legs, 2026-11-25 to 9 legs) is a per-call fallback pool (see
// FallbackScriptProvider): Groq first, then Gemini on a new project
// (Flash-Lite, 500 requests/day - KEY-BLOCKED until Rishi adds the new
// project key, fails fast on HTTP 402), then Z.ai GLM-4.7-Flash ($0,
// KEY-BLOCKED, fails fast with no connection), then Cloudflare Workers AI
// (Llama 3.3 70B, free 10k neurons/day), then OpenRouter :free (50
// requests/day unfunded, KEY-BLOCKED, fails fast with no connection),
// then NVIDIA NIM Nemotron 3 Ultra 550B (trial credits, ~40 requests/min,
// KEY-BLOCKED, fails fast with no connection), then Kilo Gateway
// (anonymous, openrouter/free router), then OVHcloud (anonymous,
// gpt-oss-120b), then Pollinations text (anonymous, free).
// Each act is tried against the pool independently, so a Groq 413/429 on
// one act falls through to the next leg for that act only, and Groq is
// retried on the next act. A leg whose key is missing fails fast (the
// helper exits in ~0.2s on the surrogate error) and costs the pool one
// cheap failure per act; it activates automatically the moment the key
// is added, with no code change. "cloudflare-helper" and "pollinations"
// pin to one fallback leg alone, for testing; "zai-helper",
// "openrouter-helper", "kilo" and "ovhcloud" do the same for the new legs.
static class ScriptProviderFactory
{
    // L21: the prep identity, when the caller knows it (Program.cs passes
    // PrepContext.Current) — the pool's GovernorClient logs [jobId] with it.
    public static IScriptProvider Create(GenConfig cfg, PrepIdentity? context = null) => cfg.ScriptProvider.ToLowerInvariant() switch
    {
        "groq" => new GroqScriptProvider(cfg),
        "gemini" => new GeminiScriptProvider(cfg),
        "gemini-helper" => new GeminiHelperScriptProvider(cfg),
        "groq-helper" => new GroqHelperScriptProvider(cfg),
        "cloudflare-helper" => new CloudflareHelperScriptProvider(cfg),
        "zai-helper" => new ZaiHelperScriptProvider(cfg),
        "openrouter-helper" => new OpenRouterHelperScriptProvider(cfg),
        "nvidia-helper" => new NvidiaHelperScriptProvider(cfg),
        "kilo" => new KiloGatewayScriptProvider(),
        "ovhcloud" => new OvhCloudScriptProvider(),
        "pollinations" => new PollinationsScriptProvider(),
        "pool-helper" => new FallbackScriptProvider([
            new GroqHelperScriptProvider(cfg),
            new GeminiHelperScriptProvider(cfg),
            new ZaiHelperScriptProvider(cfg),
            new CloudflareHelperScriptProvider(cfg),
            new OpenRouterHelperScriptProvider(cfg),
            new NvidiaHelperScriptProvider(cfg),
            new KiloGatewayScriptProvider(),
            new OvhCloudScriptProvider(),
            new PollinationsScriptProvider(),
        ], GovernorClient.FromConfig(cfg, context)),
        "hybrid" => new HybridScriptProvider(new GroqScriptProvider(cfg), new GeminiScriptProvider(cfg), firstProviderCalls: 2),
        "hybrid-helper" => new HybridScriptProvider(new GroqHelperScriptProvider(cfg), new GeminiHelperScriptProvider(cfg), firstProviderCalls: 2),
        _ => UnknownProviderFallback(cfg, context),
    };

    // L66: a typo in GenConfig.ScriptProvider (or a stale value) used to
    // silently select the paid Claude provider — no log line, a cost +
    // correctness failure with zero evidence. Log the unknown value and
    // the fallback; the selection itself is unchanged (logging only).
    private static IScriptProvider UnknownProviderFallback(GenConfig cfg, PrepIdentity? context)
    {
        Console.WriteLine($"  {PrepContext.PrefixFor(context)}[script-pool] unknown ScriptProvider \"{cfg.ScriptProvider}\" — falling back to Claude (paid); check config");
        return new ClaudeScriptProvider(cfg);
    }
}
