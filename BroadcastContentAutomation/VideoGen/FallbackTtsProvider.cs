// Per-call fallback pool for TTS (review #2 B7, fix sequence #2 ITEM 4):
// mirrors FallbackScriptProvider for scripts. TTS was single-provider
// (azure-helper only), so one Azure F0 outage halted all prep and the
// driver just backed off 45 min and retried the same provider. This tries
// each provider in order until one synthesizes the audio; a call that
// fails on one provider falls through to the next, and the NEXT call
// starts from the first provider again — so if Azure 429s/hard-fails on
// scene 12, Google voices scene 12 and Azure is retried for scene 13.
// Only throws when every provider failed, listing each one's failure.
// Logs which provider actually voiced each call, so a review of the prep
// log shows exactly where the voice changed.
//
// The voice itself is unchanged on the primary leg: Azure still speaks
// DavisNeural at the profile's rate/pitch (the caller passes rate/pitch
// through unchanged). The Google leg speaks its own Standard voice
// (en-US-Standard-C) — a different voice, but only ever on Azure failure,
// which previously killed the whole prep outright.
//
// Usage governor (2026-11-23, Trendline Now — see GovernorClient.cs):
// when a governor client is supplied, each leg is checked before it is
// called. A denied leg (hard cap) is SKIPPED, not attempted and not
// counted as a failure. Azure is additionally skipped once its monthly
// usage reaches the soft cap (0.9): the Google voice takes over BEFORE
// the budget is gone, preserving the last 10% for scenes only Azure can
// voice. An allowed leg is acquired with the scene's char count
// (acquire records it), so the monthly budget stays honest. A leg that
// fails with a rate-limit error is parked (mark-cooled) before falling
// through; key-blocked failures are never cooled. When every leg was
// skipped, GovernorAllCooledException is thrown instead of "All TTS
// providers failed".
using GiggleGarden.Shared;

class FallbackTtsProvider(IReadOnlyList<ITtsProvider> providers, GovernorClient? governor = null) : ITtsProvider
{
    // Explicit one-arg form: Activator.CreateInstance (used by the
    // reflection-based PrepHardening tests) does not fill optional
    // parameters, so the primary constructor alone is not enough.
    public FallbackTtsProvider(IReadOnlyList<ITtsProvider> providers) : this(providers, null) { }
    // Governor key per leg class name; unknown types are not governed.
    internal static string? GovernorKeyFor(string typeName) => typeName switch
    {
        "AzureHelperTtsProvider" or "AzureTtsProvider" => "azure-tts",
        "GoogleHelperTtsProvider" or "GoogleTtsProvider" => "google-tts",
        _ => null,
    };

    public async Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null)
    {
        var failures = new List<string>();
        var attempted = 0;
        var skipped = 0;
        string? resetsAt = null;
        foreach (var provider in providers)
        {
            var name = provider.GetType().Name;
            var key = GovernorKeyFor(name);
            if (governor is not null && key is not null)
            {
                // Check/Acquire never throw (they fail open), so a denied
                // leg is the only outcome that skips the call below.
                var check = await governor.CheckAsync(key);
                if (!check.Allowed)
                {
                    Console.WriteLine($"governor: {key} skipped: {check.Reason} (resets {check.ResetsAt ?? "unknown"})");
                    skipped++;
                    resetsAt ??= check.ResetsAt;
                    continue;
                }
                if (key == "azure-tts" && !check.FailOpen && check.SoftCapFraction > 0
                    && check.UsageFraction >= check.SoftCapFraction)
                {
                    Console.WriteLine($"governor: azure-tts skipped: usage {check.UsageFraction:P0} at soft cap {check.SoftCapFraction:P0} — preserving the Azure monthly budget, Google voices this scene");
                    skipped++;
                    resetsAt ??= check.ResetsAt;
                    continue;
                }
                var acquire = await governor.AcquireAsync(key, chars: text.Length);
                if (!acquire.Allowed)
                {
                    Console.WriteLine($"governor: {key} skipped: {acquire.Reason} (resets {acquire.ResetsAt ?? "unknown"})");
                    skipped++;
                    resetsAt ??= acquire.ResetsAt;
                    continue;
                }
            }

            attempted++;
            try
            {
                await provider.SynthesizeAsync(text, language, outputPath, rate, pitch, styleOverride);
                Console.WriteLine($"  [tts-fallback] {name} voiced this scene.");
                return;
            }
            catch (Exception ex)
            {
                var reason = Text.Tail(ex.Message.Replace('\n', ' ').Replace('\r', ' '), 200);
                if (governor is not null && key is not null && GovernorClient.IsRateLimitFailure(ex.Message))
                    await governor.MarkCooledAsync(key, reason);
                Console.WriteLine($"  [tts-fallback] {name} failed: {reason}, trying next");
                failures.Add($"{name}: {reason}");
            }
        }

        if (skipped > 0 && attempted == 0)
            throw new GovernorAllCooledException(resetsAt);

        throw new Exception(
            $"All TTS providers failed ({failures.Count}): {string.Join(" | ", failures)}");
    }
}
