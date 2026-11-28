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
    public async Task<string> CompleteAsync(string prompt, PrepIdentity? context = null)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.GroqHelperPath, "chat",
            "--prompt", prompt,
            "--model", cfg.GroqModel,
        ], timeoutMinutes: 10, what: $"Groq helper ({cfg.GroqHelperPath})", context: context);
        if (result.ExitCode != 0)
        {
            // L63: one logged non-retry — a transient helper crash is one
            // failure, not a retried one; the pool falls through to the
            // next leg.
            Console.WriteLine($"  {PrepContext.PrefixFor(context)}[groq] helper failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 300)} — no retry at this leg, falling through to next leg");
            throw new Exception($"Groq helper script generation failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 600)}");
        }

        var text = result.Stdout.Replace("```json", "").Replace("```", "").Trim();
        // L3: an exit-0 helper with empty/garbage stdout used to return ""
        // (or a refusal) as a script, and the error only surfaced later as
        // a generic parse failure with the leg name lost. Fail here, with
        // the leg named, so the pool falls through to the next leg.
        if (string.IsNullOrWhiteSpace(text))
            throw new Exception("Groq helper returned empty stdout (0 chars) — falling through to next leg");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new Exception($"Groq helper returned no JSON object (output had no {{...}} block, {text.Length} chars) — falling through to next leg");
        return text[start..(end + 1)];
    }
}
