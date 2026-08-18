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
static class ScriptProviderFactory
{
    public static IScriptProvider Create(GenConfig cfg) => cfg.ScriptProvider.ToLowerInvariant() switch
    {
        "groq" => new GroqScriptProvider(cfg),
        "gemini" => new GeminiScriptProvider(cfg),
        "hybrid" => new HybridScriptProvider(new GroqScriptProvider(cfg), new GeminiScriptProvider(cfg), firstProviderCalls: 2),
        _ => new ClaudeScriptProvider(cfg),
    };
}
