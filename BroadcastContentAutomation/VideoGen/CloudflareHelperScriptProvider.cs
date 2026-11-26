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
    public async Task<string> CompleteAsync(string prompt)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.CloudflareHelperPath, "text",
            "--account-id", cfg.CloudflareAccountId,
            "--prompt", prompt,
            "--model", cfg.CloudflareTextModel,
            "--max-tokens", "8192",
        ], timeoutMinutes: 10);
        if (result.ExitCode != 0)
            throw new Exception($"Cloudflare helper script generation failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 600)}");

        var text = result.Stdout.Replace("```json", "").Replace("```", "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
