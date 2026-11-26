// Image pool — Trendline Now scene-art provider chain (rebuilt 2026-11-24).
using GiggleGarden.Shared;
//
// Bake-off 2026-11-24 (contact sheet ~/workspace/tmp/bakeoff-2026-11-24/
// contact-sheet.jpg) ranked the legs and fixed the order:
//   1. Cloudflare FLUX — primary (4/4 pass, 1024x1024, ~3s/image)
//   2. Pexels stock photo — #2 for news scenes (real photos beat AI art)
//   3. OVHcloud SDXL — filler only (~10-15 scenes/video; SDXL retires 2027-02-10)
//   4. Pollinations keyed FLUX — DORMANT until Pollen exists (balance 0)
//   5. Leonardo — DORMANT until a Leonardo key + model id are set
// Plus a CONDITIONAL pre-leg, not in ChainOrder because it only runs
// when it applies (Rishi 2026-11-24 ~23:14): Wikimedia Commons — a real,
// freely-licensed photo of the actual person — is tried BEFORE FLUX when
// the script title or scene narration names a ranked star (StarRanking).
// A Commons miss falls through to FLUX as before. The leg lives in
// art_pool.py (--only wikimedia) and is wired in Program.cs
// GenerateSquareArtAsync, not in the fixed chain below.
// Z.ai CogView was leg 5 until 2026-11-24, when the activation test
// proved the free CogView models are not served on the international
// platform api.z.ai (HTTP 400 code 1211 "Unknown Model" for
// cogview-3-flash / cogview-3 / cogview-3-plus / cogview-4) — free
// CogView is a bigmodel.cn offering only. The leg was deleted from the
// chain, the client, and the helper (the Z.ai SCRIPT leg, GLM-4.7-Flash,
// passed and is untouched).
// Discarded legs (Pollinations anonymous, AI Horde) are never attempted:
// art_pool.py no longer contains them, and QualityGate errors on any art
// labelled from them. Cloudflare SDXL is gone entirely (deprecated
// 2026-11-20) — CharacterSource's twin chain lost it too.
//
// Quota arithmetic (the reason the chain MUST mix): FLUX costs ~173
// Workers AI neurons per 4-step 1024x1024 image against the ONE shared
// 10,000-neuron/day "cloudflare" bucket that the script pool's
// Cloudflare leg also draws (~926 neurons/act). A 60-scene video needs
// ~10,380 neurons of FLUX alone — more than the whole bucket — so FLUX
// can never cover a full video in one day even with zero script use.
// The governor acquire per image stops FLUX when the bucket is nearly
// out and the chain falls through to Pexels + OVH for the tail. That
// is the design, not a failure. Script + image can never exceed 10k/day
// combined because there is only one ledger entry ("cloudflare") —
// never a second 10k provider key for images.
//
// Two window caveats, documented not hidden: the governor's day window
// is midnight America/Toronto while Cloudflare's quota day resets at
// 00:00 UTC (19:00 EST), so the ledger can over-allow across that 5h
// boundary; and the 926/173 unit estimates are metered averages, not
// Cloudflare's exact per-call charge. The hard backstop for both is
// the live 4006: when FLUX actually refuses, the leg is parked until
// 00:00 UTC and the prep fails loudly (FluxQuotaExhaustedException)
// rather than silently shipping weak art.
static class ImagePool
{
    // Metered cost of one 4-step 1024x1024 FLUX image (bake-off
    // 2026-11-24): ~172.8 neurons, rounded to 173. Charged per image
    // against the shared "cloudflare" bucket (see GenConfig.
    // GovernorCloudflareUnitsPerImageCall).
    internal const int FluxUnitsPerImage = 173;

    internal const string CloudflareGovernorKey = "cloudflare";
    internal const string PollinationsKeyedGovernorKey = "pollinations-keyed";
    internal const string LeonardoImageGovernorKey = "leonardo-image";

    // Governor key per image leg. FLUX shares the script pool's
    // "cloudflare" key on purpose (one bucket); the pool legs are
    // governed inside art_pool.py, so they have no C# acquire.
    internal static string GovernorKeyForLeg(string leg) => leg switch
    {
        "cloudflare-flux" => CloudflareGovernorKey,
        "pollinations-keyed" => PollinationsKeyedGovernorKey,
        "leonardo-image" => LeonardoImageGovernorKey,
        _ => leg,
    };

    // The chain order GenerateSquareArtAsync attempts, first to last.
    // Dormant legs are always listed — they fail fast once per process
    // (see the dormant cache below) and are then skipped, so a missing
    // key costs one cheap failure per video, never one per scene.
    internal static readonly string[] ChainOrder =
    [
        "cloudflare-flux",
        "pexels",
        "ovhcloud",
        "pollinations-keyed",
        "leonardo-image",
    ];

    // Legs that must never be attempted: the bake-off discarded them,
    // and the gate errors on their art. A source guard in the tests
    // asserts none of these strings appears in the chain code.
    internal static readonly string[] DiscardedLegs = ["pollinations", "ai-horde"];

    // ------------------------------------------------------------------
    // Dormant legs: key missing = clean skip, never an error that burns
    // retries. The first attempt in a process classifies the leg (a
    // key-blocked error: 401/402/403, "no connection registered",
    // "surrogate error"); later scenes skip it without calling. A key
    // added mid-run activates on the NEXT process, same as the script
    // pool's key-blocked legs. Tests reset this via ResetForTests.
    // ------------------------------------------------------------------
    private static readonly HashSet<string> _dormant = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsDormant(string leg) => _dormant.Contains(leg);

    internal static void NoteDormant(string leg) => _dormant.Add(leg);

    internal static void ResetForTests()
    {
        _dormant.Clear();
        _governor = null;
        _governorPath = null;
    }

    // One governor client per process (its constructor logs once when
    // the helper is missing — a per-scene client would log once per
    // scene). The client is stateless, so caching it is safe.
    private static GovernorClient? _governor;
    private static string? _governorPath;

    internal static GovernorClient GovernorFor(GenConfig cfg)
    {
        if (_governor is null || !string.Equals(_governorPath, cfg.GovernorHelperPath, StringComparison.Ordinal))
        {
            _governor = GovernorClient.FromConfig(cfg);
            _governorPath = cfg.GovernorHelperPath;
        }
        // Assigned above whenever it was null (the field, not a local,
        // is what the compiler sees — hence the null-forgiving operator).
        return _governor!;
    }

    // ------------------------------------------------------------------
    // Quota detection. How a FLUX quota error surfaces (read from
    // cf_workers_ai.py cmd_flux): the helper prints
    // "HTTP {status} in {dt}s: {json}" and exits 1, where the JSON is
    // {"success": false, "errors": [{"code": 4006, "message": ...}]}.
    // 4006 is Cloudflare's daily-limit code (verified 2026-11-23 probe).
    // It arrives wrapped in an HTTP 403 line, so 4006 must win over the
    // generic 403: a bare 403 is an auth failure, but 4006 is
    // unambiguously the quota. (GovernorClient.IsRateLimitFailure checks
    // the negative markers first and would miss a 403-wrapped 4006 —
    // see the fix there, which makes 4006 win too.)
    // ------------------------------------------------------------------
    internal static bool IsFluxQuotaError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message.ToLowerInvariant();
        if (m.Contains("4006")) return true;
        if (m.Contains("401") || m.Contains("402") || m.Contains("403")
            || m.Contains("no connection registered") || m.Contains("surrogate error"))
            return false;
        return m.Contains("quota") && (m.Contains("exceeded") || m.Contains("exhausted"));
    }

    // Key-blocked = the leg has no usable credential, so it is dormant,
    // not broken: 401/402/403, or the surrogate reporting no connection.
    // Pollinations with a $0 Pollen balance fails with HTTP 402. A 4006
    // is a quota signal, never a missing key — it must not be misread
    // as dormant (a 403-wrapped 4006 carries both markers).
    internal static bool IsKeyBlockedError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message.ToLowerInvariant();
        if (m.Contains("4006")) return false;
        return m.Contains("401") || m.Contains("402") || m.Contains("403")
            || m.Contains("no connection registered") || m.Contains("surrogate error");
    }

    // ------------------------------------------------------------------
    // Pexels query: topic/entity keywords from the scene, never the art
    // prompt's first words (the old first-words query returned
    // off-topic photos — a hunter with a rifle for a climate-summit
    // prompt). Priority: title words, then script tags, then the
    // narration's mid-sentence capitalised entities (people, places,
    // organisations) via QualityGate.ConcreteSubjects. Generic news
    // words ("news", "today", "breaking") are dropped — they are not
    // what the photo is of. Max 5 content words. "" when nothing
    // topical survives: the caller then omits --query and the pool
    // helper derives one from the prompt instead.
    // ------------------------------------------------------------------
    private static readonly HashSet<string> QueryStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "in", "on", "at", "to", "for", "with",
        "and", "or", "is", "are", "was", "were", "be", "by", "from", "as",
        "it", "its", "this", "that", "these", "those", "over", "under",
        "into", "about", "after", "before", "during", "between", "against",
        "without", "within", "near", "above", "below", "behind",
        "news", "today", "breaking", "live", "update", "updates", "video",
        "story", "stories", "report", "reports", "explained", "trend",
        "trending", "daily", "latest", "watch", "big", "top", "major",
    };

    internal static string BuildPexelsQuery(
        string? title, IReadOnlyList<string>? tags, string? narration)
    {
        var words = new List<string>();
        void AddWords(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var token in System.Text.RegularExpressions.Regex.Split(text, @"[^\p{L}0-9]+"))
            {
                if (token.Length < 2) continue;
                if (token.All(char.IsDigit)) continue;
                if (QueryStopwords.Contains(token)) continue;
                if (words.Any(w => w.Equals(token, StringComparison.OrdinalIgnoreCase))) continue;
                words.Add(token);
                if (words.Count >= 5) return;
            }
        }

        AddWords(title);
        if (tags is not null)
            foreach (var tag in tags)
            {
                AddWords(tag.TrimStart('#'));
                if (words.Count >= 5) break;
            }
        if (words.Count < 5 && !string.IsNullOrWhiteSpace(narration))
            foreach (var subject in QualityGate.ConcreteSubjects(narration))
            {
                AddWords(subject);
                if (words.Count >= 5) break;
            }
        return string.Join(" ", words);
    }

    // Seconds from now (UTC) until the next 00:00 UTC — the instant the
    // Cloudflare daily quota resets. At least 60s: a quota error seen
    // in the same second as the reset still parks the leg briefly
    // instead of recording a zero-second cooldown.
    internal static int SecondsUntilUtcMidnight(DateTime utcNow)
    {
        var next = utcNow.Date.AddDays(1);
        return Math.Max(60, (int)(next - utcNow).TotalSeconds);
    }
}

// Thrown when a scene ends art-less after the FLUX daily quota ran out:
// the prep must fail LOUDLY (not silently fall through to weak legs —
// none remain) with a marker the driver can recognise, and resume
// cleanly from script.json + the art already on disk. The console line
// and the exception message both carry the marker plus the reset time.
class FluxQuotaExhaustedException : Exception
{
    internal const string Marker = "FLUX_QUOTA_EXHAUSTED";

    public FluxQuotaExhaustedException(string sceneLabel, Exception? inner = null)
        : base($"{Marker}: {sceneLabel} has no art — the Cloudflare FLUX daily quota " +
               $"(10,000 neurons/day) is exhausted, and the Pexels + OVH fallbacks could not " +
               $"cover this scene. Fill the remaining scenes with Pexels + passing OVH images " +
               $"or resume after 00:00 UTC, when the quota resets. Already-generated art is on " +
               $"disk; --resume picks it up without regenerating it.",
               inner)
    {
    }
}

// A leg was skipped for a reason already logged by the leg itself
// (governor denial, dormant key, quota park) — the chain moves on to
// the next leg quietly instead of logging the same skip twice.
class ImagePoolSkipException : Exception
{
    public ImagePoolSkipException(string message) : base(message)
    {
    }
}
