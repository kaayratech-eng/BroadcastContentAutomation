// Per-call fallback pool for script generation (mirrors the ArtPool
// pattern for images): try each provider in order until one returns text.
// A call that fails on one provider falls through to the next, and the
// NEXT call starts from the first provider again - so if Groq 413/429s
// on Act 2, Cloudflare writes Act 2 and Groq is retried for Act 3. No
// cross-act state is needed: every continuation prompt already carries a
// recap of the prior acts' scenes, so whichever provider wrote an act,
// the next act's prompt has the full story. Only throws when every
// provider failed, listing each one's failure.
//
// Usage governor (2026-11-23, Trendline Now — see GovernorClient.cs):
// when a governor client is supplied, each leg is gated before it is
// called (`acquire` records 1 request + the leg's estimated usage:
// ~7,000 tokens/act, or ~926 neurons for Cloudflare). A denied leg is
// SKIPPED — it is not attempted and not counted as a failure — and when
// every leg was skipped, GovernorAllCooledException is thrown instead of
// "All script providers failed", so the driver can tell a cooled pool
// from a broken one. A leg that fails with a rate-limit error is parked
// (mark-cooled) before falling through; a key-blocked leg (401/402/403,
// "no connection registered", "surrogate error") is never cooled, so a
// key added later activates it immediately.
using GiggleGarden.Shared;

class FallbackScriptProvider(IReadOnlyList<IScriptProvider> providers, GovernorClient? governor = null) : IScriptProvider
{
    // Explicit one-arg form: Activator.CreateInstance (used by the
    // reflection-based PrepHardening tests) does not fill optional
    // parameters, so the primary constructor alone is not enough.
    public FallbackScriptProvider(IReadOnlyList<IScriptProvider> providers) : this(providers, null) { }
    // Governor key per leg class name; unknown types are not governed.
    internal static string? GovernorKeyFor(string typeName) => typeName switch
    {
        "GroqHelperScriptProvider" => "groq",
        "GeminiHelperScriptProvider" => "gemini",
        "ZaiHelperScriptProvider" => "zai",
        "CloudflareHelperScriptProvider" => "cloudflare",
        "OpenRouterHelperScriptProvider" => "openrouter",
        "KiloGatewayScriptProvider" => "kilo",
        "OvhCloudScriptProvider" => "ovhcloud",
        "PollinationsScriptProvider" => "pollinations-text",
        _ => null,
    };

    // L59/L68: the leg that produced the last successful answer, so a
    // 0-scene result can name its winner instead of hiding behind the
    // pool. Null until a leg succeeds on this pool instance.
    internal string? LastWinningLegName { get; private set; }

    public async Task<string> CompleteAsync(string prompt, PrepIdentity? context = null)
    {
        // L21: the explicit context wins; the ambient prep identity is the
        // fallback (set at the prep entry point in Program.cs). The
        // governor must log with the job id too, and it is constructed
        // before this call, so hand it the effective context here.
        var effective = context ?? PrepContext.Current;
        governor?.SetContext(effective);
        var prefix = PrepContext.PrefixFor(effective);
        var act = effective?.Act;
        var failures = new List<string>();
        var attempted = 0;
        var skipped = 0;
        string? resetsAt = null;
        // L59: every leg's outcome, for the summary logged before either
        // throw below — a reader must see which legs were attempted or
        // skipped and why, not just the aggregate count.
        var outcomes = new List<string>();
        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            var name = provider.GetType().Name;
            // Legs after this one, still untried.
            var remaining = providers.Count - i - 1;
            var key = GovernorKeyFor(name);
            if (governor is not null && key is not null)
            {
                // Acquire never throws (it fails open), so a denied leg is
                // the only outcome that skips the call below.
                var decision = key == "cloudflare"
                    ? await governor.AcquireAsync(key, units: governor.CloudflareUnitsPerActCall)
                    : await governor.AcquireAsync(key, tokens: governor.TokensPerActCall);
                if (!decision.Allowed)
                {
                    Console.WriteLine($"{prefix}governor: {key} skipped: {decision.Reason} (resets {decision.ResetsAt ?? "unknown"}) — trying next leg ({remaining} remaining)");
                    skipped++;
                    resetsAt ??= decision.ResetsAt;
                    outcomes.Add($"{name}: skipped — {decision.Reason} (resets {decision.ResetsAt ?? "unknown"})");
                    continue;
                }
            }

            attempted++;
            try
            {
                // L3: a leg that returns empty output is a leg failure, not
                // a script — fall through to the next leg instead of
                // returning "" as a script (the error then surfaced later
                // as a generic parse failure with the leg name lost).
                var text = await provider.CompleteAsync(prompt, effective);
                if (string.IsNullOrWhiteSpace(text))
                    throw new Exception($"{name} returned empty output (0 chars) — falling through to next leg");
                LastWinningLegName = name;
                Console.WriteLine($"  {prefix}[script-pool] {(act is null ? "" : $"act {act}: ")}{name} succeeded ({text.Length} chars)");
                return text;
            }
            catch (Exception ex)
            {
                var reason = Text.Tail(ex.Message.Replace('\n', ' ').Replace('\r', ' '), 200);
                if (governor is not null && key is not null && GovernorClient.IsRateLimitFailure(ex.Message))
                    await governor.MarkCooledAsync(key, reason);
                Console.WriteLine($"  {prefix}[script-pool] {(act is null ? "" : $"act {act}: ")}{name} failed: {reason} — trying next leg ({remaining} remaining)");
                failures.Add($"{name}: {reason}");
                outcomes.Add($"{name}: attempted — failed: {reason}");
            }
        }

        if (skipped > 0 && attempted == 0)
        {
            Console.WriteLine($"  {prefix}[script-pool] all legs skipped by the governor ({skipped} skipped, 0 attempted): {string.Join("; ", outcomes)} — aborting prep, no leg was attempted");
            throw new GovernorAllCooledException(resetsAt);
        }

        Console.WriteLine($"  {prefix}[script-pool] all legs failed ({failures.Count} attempted, {skipped} skipped): {string.Join("; ", outcomes)} — aborting prep, no further retry");
        throw new Exception(
            $"All script providers failed ({failures.Count}): {string.Join(" | ", failures)}");
    }
}
