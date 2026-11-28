// Cloudflare Workers AI script provider via the vault-backed helper
// (text command added to cf_workers_ai.py 2026-11-22, verified live:
// valid JSON back in ~1.2s on the free tier, 10,000 neurons/day).
using GiggleGarden.Shared;

// Same shape as GroqHelperScriptProvider - the prompt travels as one argv
// element (no shell) to the cf_workers_ai.py `text` command, which posts to
// Workers AI (@cf/meta/llama-3.3-70b-instruct-fp8-fast by default) through
// the custom.cloudflare connection's authd surrogate exchange. The account
// id is a non-secret identifier and comes from cfg.CloudflareAccountId.
// --max-tokens 8192: a 20-24 scene act needs up to ~4,000 output tokens,
// and the headroom covers reasoning-heavy replies. Selected via
// GenConfig.ScriptProvider = "cloudflare-helper", or as the second leg of
// "pool-helper" (see ScriptProviderFactory / FallbackScriptProvider).
class CloudflareHelperScriptProvider(GenConfig cfg) : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt, PrepIdentity? context = null)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.CloudflareHelperPath, "text",
            "--account-id", cfg.CloudflareAccountId,
            "--prompt", prompt,
            "--model", cfg.CloudflareTextModel,
            "--max-tokens", "8192",
        ], timeoutMinutes: 10, what: $"Cloudflare helper ({cfg.CloudflareHelperPath})", context: context);
        if (result.ExitCode != 0)
        {
            // L63: one logged non-retry — a transient helper crash is one
            // failure, not a retried one; the pool falls through to the
            // next leg.
            Console.WriteLine($"  {PrepContext.PrefixFor(context)}[cloudflare] helper failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 300)} — no retry at this leg, falling through to next leg");
            throw new Exception($"Cloudflare helper script generation failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 600)}");
        }

        var text = result.Stdout.Replace("```json", "").Replace("```", "").Trim();
        // L3: an exit-0 helper with empty/garbage stdout used to return ""
        // (or a refusal) as a script, and the error only surfaced later as
        // a generic parse failure with the leg name lost. Fail here, with
        // the leg named, so the pool falls through to the next leg.
        if (string.IsNullOrWhiteSpace(text))
            throw new Exception("Cloudflare helper returned empty stdout (0 chars) — falling through to next leg");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new Exception($"Cloudflare helper returned no JSON object (output had no {{...}} block, {text.Length} chars) — falling through to next leg");
        return text[start..(end + 1)];
    }
}
