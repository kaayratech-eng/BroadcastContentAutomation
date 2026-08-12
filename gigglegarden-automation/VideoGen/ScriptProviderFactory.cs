// Picks which IScriptProvider writes the script. "claude" (default) is the
// zero-risk path unchanged from before this seam existed; "groq" opts into the
// free-tier alternative - see GroqScriptProvider for the terms that make it safe
// to use for real (not just prototyping) at low volume.
static class ScriptProviderFactory
{
    public static IScriptProvider Create(GenConfig cfg) =>
        cfg.ScriptProvider.ToLowerInvariant() == "groq"
            ? new GroqScriptProvider(cfg)
            : new ClaudeScriptProvider(cfg);
}
