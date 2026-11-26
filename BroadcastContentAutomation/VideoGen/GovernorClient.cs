// Thin subprocess client for the Trendline Now usage governor
// (trendline/governor.py, 2026-11-23). The governor is the single source
// of truth for free-tier budgets (Groq 200K tokens/day, Azure TTS 500K
// chars/month, Cloudflare 10K neurons/day, ...): before a pool leg is
// called, the caller asks the governor whether the leg has room, and
// after a 429/limit failure it parks the leg (mark-cooled) instead of
// retrying it in a tight loop.
//
// The governor must never halt the channel by itself, so this client
// NEVER throws: a missing helper file, a non-zero exit, unparseable
// output, or a timeout all fail OPEN (Allowed=true, FailOpen=true) with
// one log line. The helper path comes from GenConfig.GovernorHelperPath;
// when it is missing at construction time, one line is logged there and
// every call fails open without spawning a process.
using System.Text.Json;
using GiggleGarden.Shared;

// One governor verdict. ResetsAt is the ISO reset instant when the leg
// is denied (null when allowed); SoftCapFraction is the usage fraction
// at which the caller should switch providers before the hard cap
// (Azure TTS: 0.9 — preserve the last 10% of the monthly budget).
record GovernorDecision(
    bool Allowed,
    string? Reason,
    string? ResetsAt,
    double UsageFraction,
    double SoftCapFraction,
    bool Cooled,
    bool FailOpen);

// Thrown by a fallback pool when EVERY leg was skipped by the governor
// (none attempted) — distinct from "all providers failed", so the driver
// can tell a cooled/budgeted-out pool from a broken one. ResetsAt is the
// earliest reset instant reported by a skipped leg, if any.
class GovernorAllCooledException : Exception
{
    public string? ResetsAt { get; }

    public GovernorAllCooledException(string? resetsAt)
        : base(resetsAt is null
            ? "All providers were skipped by the usage governor (every leg cooled or over budget)."
            : $"All providers were skipped by the usage governor (every leg cooled or over budget; resets {resetsAt}).")
    {
        ResetsAt = resetsAt;
    }
}

class GovernorClient
{
    private readonly GenConfig _cfg;
    private readonly Func<string, IEnumerable<string>, Task<ProcessHelper.Result>> _runner;
    private readonly bool _helperMissing;

    // Per-call amounts the pools charge (GenConfig defaults): every
    // script leg charges ~7,000 tokens/act except Cloudflare, which
    // charges ~926 Workers AI neurons/act; every FLUX image charges
    // ~173 neurons against the SAME "cloudflare" bucket (see ImagePool).
    public int TokensPerActCall => _cfg.GovernorTokensPerActCall;
    public int CloudflareUnitsPerActCall => _cfg.GovernorCloudflareUnitsPerActCall;
    public int CloudflareUnitsPerImageCall => _cfg.GovernorCloudflareUnitsPerImageCall;

    public GovernorClient(GenConfig cfg, Func<string, IEnumerable<string>, Task<ProcessHelper.Result>>? runner = null)
    {
        _cfg = cfg;
        _runner = runner ?? ((exe, args) => ProcessHelper.RunAsync(exe, args));
        _helperMissing = !File.Exists(cfg.GovernorHelperPath);
        if (_helperMissing)
            Console.WriteLine($"  [governor] helper missing: {cfg.GovernorHelperPath} — calls fail open");
    }

    // A client is always returned, even when the helper file is missing:
    // that client fails open on every call (see the constructor log — one
    // line only, not one per call).
    public static GovernorClient? FromConfig(GenConfig cfg) => new GovernorClient(cfg);

    // `acquire` = check + record in one locked step: 1 request plus the
    // passed amounts. The governor exits 1 when denied but still prints
    // a JSON verdict, so the exit code is NOT the signal — the parsed
    // JSON is. Only unparseable output fails open.
    public async Task<GovernorDecision> AcquireAsync(string provider, int tokens = 0, int chars = 0, int units = 0)
    {
        if (_helperMissing) return FailOpen("governor helper missing");
        var args = new List<string> { _cfg.GovernorHelperPath, "acquire", "--provider", provider };
        if (tokens > 0) { args.Add("--tokens"); args.Add(tokens.ToString()); }
        if (chars > 0) { args.Add("--chars"); args.Add(chars.ToString()); }
        if (units > 0) { args.Add("--units"); args.Add(units.ToString()); }
        return await RunGovernorAsync(provider, args);
    }

    // Read-only check: same verdict shape, nothing recorded.
    public async Task<GovernorDecision> CheckAsync(string provider)
    {
        if (_helperMissing) return FailOpen("governor helper missing");
        return await RunGovernorAsync(provider, new[] { _cfg.GovernorHelperPath, "check", "--provider", provider });
    }

    // Best effort, never throws: a failed mark-cooled just means the leg
    // may be retried once more, it never blocks the pool. `until` is the
    // governor.py --until value ("midnight", "next-hour", or seconds):
    // a FLUX quota error parks Cloudflare until 00:00 UTC (seconds to
    // the reset), not the default 10-minute cooldown — retrying every
    // 10 minutes would burn a prep attempt each time until the reset.
    public async Task MarkCooledAsync(string provider, string reason, string? until = null)
    {
        if (_helperMissing) return;
        try
        {
            var args = new List<string> { _cfg.GovernorHelperPath, "mark-cooled", "--provider", provider, "--reason", reason };
            if (until is not null) { args.Add("--until"); args.Add(until); }
            await _runner("python3", args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [governor] mark-cooled {provider} failed: {Text.Tail(ex.Message.Replace('\n', ' ').Replace('\r', ' '), 200)}");
        }
    }

    private async Task<GovernorDecision> RunGovernorAsync(string provider, IEnumerable<string> args)
    {
        try
        {
            var result = await _runner("python3", args);
            var decision = TryParseDecision(result.Stdout);
            if (decision is not null) return decision;
            Console.WriteLine($"  [governor] {provider}: unparseable output (exit {result.ExitCode}) — failing open");
            return FailOpen("governor output unparseable");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [governor] {provider}: {Text.Tail(ex.Message.Replace('\n', ' ').Replace('\r', ' '), 200)} — failing open");
            return FailOpen("governor call failed");
        }
    }

    private static GovernorDecision FailOpen(string reason) =>
        new(true, reason, null, 0, 0.9, false, true);

    private static GovernorDecision? TryParseDecision(string stdout)
    {
        try
        {
            var text = stdout.Trim();
            if (text.Length == 0) return null;
            // Take the JSON object span if anything else leaked onto stdout.
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end < start) return null;
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("allowed", out var allowedEl)
                || (allowedEl.ValueKind != JsonValueKind.True && allowedEl.ValueKind != JsonValueKind.False))
                return null;
            return new GovernorDecision(
                allowedEl.GetBoolean(),
                GetString(root, "reason"),
                GetString(root, "resets_at"),
                GetDouble(root, "usage_fraction", 0),
                GetDouble(root, "soft_cap_fraction", 0.9),
                GetBool(root, "cooled", false),
                GetBool(root, "fail_open", false));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    private static double GetDouble(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble() : fallback;

    private static bool GetBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var el)
            && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean() : fallback;

    // Rate-limit detector (shared by the script and TTS pools): a failure
    // IS a rate limit when the message contains (case-insensitive) "429",
    // "rate limit", "rate_limit", "too many requests", Cloudflare "4006",
    // or "quota" + "exceeded"/"exhausted". It is NOT a limit when it
    // contains "401", "402", "403", "no connection registered", or
    // "surrogate error" — key-blocked legs fail fast and must NOT be
    // cooled, so a key added later activates the leg immediately.
    // One precedence exception: "4006" wins over a bare "403". The
    // Cloudflare quota error arrives wrapped in an HTTP 403 line
    // ("HTTP 403 ... {\"code\": 4006 ...}"), so checking 403 first
    // would miss every real quota refusal and never park the leg; 4006
    // is Cloudflare's daily-limit code and is unambiguous, while a 403
    // WITHOUT 4006 is still an auth failure, not a limit.
    public static bool IsRateLimitFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message.ToLowerInvariant();
        if (m.Contains("4006")) return true;
        if (m.Contains("401") || m.Contains("402") || m.Contains("403")
            || m.Contains("no connection registered") || m.Contains("surrogate error"))
            return false;
        if (m.Contains("429") || m.Contains("rate limit") || m.Contains("rate_limit")
            || m.Contains("too many requests"))
            return true;
        return m.Contains("quota") && (m.Contains("exceeded") || m.Contains("exhausted"));
    }
}
