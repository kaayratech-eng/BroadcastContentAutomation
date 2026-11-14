// Gemini script provider via the vault-backed helper (set up 2026-11-11).
using GiggleGarden.Shared;
//
// Same generateContent request as GeminiScriptProvider (JSON mode, low
// thinking, 12000 output tokens), but the API key comes from Muse's secure
// vault (custom.gemini) through the gemini_image.py helper's authd
// surrogate exchange instead of from GenConfig.GeminiApiKey. No key value
// is handled here; the prompt travels as one argv element (no shell), and
// the model name comes from cfg.GeminiModel. Selected via
// GenConfig.ScriptProvider = "gemini-helper" (see ScriptProviderFactory).
// Use this whenever the Gemini key lives in the vault and not in config.
class GeminiHelperScriptProvider(GenConfig cfg) : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.GeminiHelperPath, "text",
            "--prompt", prompt,
            "--model", cfg.GeminiModel,
            "--max-tokens", "12000",
        ], timeoutMinutes: 10);
        if (result.ExitCode != 0)
            throw new Exception($"Gemini helper script generation failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 600)}");

        var text = result.Stdout.Replace("```json", "").Replace("```", "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
