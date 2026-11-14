// Groq script provider via the vault-backed helper (set up 2026-11-11).
using GiggleGarden.Shared;

// Same chat-completions request as GroqScriptProvider (model from
// cfg.GroqModel, max_completion_tokens sized off the prompt, low-effort
// reasoning for gpt-oss models), but the API key comes from Muse's secure
// vault (custom.groq) through the groq_chat.py helper's authd surrogate
// exchange instead of from GenConfig.GroqApiKey. No key value is handled
// here; the prompt travels as one argv element (no shell). Selected via
// GenConfig.ScriptProvider = "groq-helper", or as the first leg of
// "hybrid-helper" (see ScriptProviderFactory).
class GroqHelperScriptProvider(GenConfig cfg) : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.GroqHelperPath, "chat",
            "--prompt", prompt,
            "--model", cfg.GroqModel,
        ], timeoutMinutes: 10);
        if (result.ExitCode != 0)
            throw new Exception($"Groq helper script generation failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 600)}");

        var text = result.Stdout.Replace("```json", "").Replace("```", "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
