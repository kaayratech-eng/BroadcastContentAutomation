// Google Cloud TTS provider via the vault-backed helper (set up 2026-11-11).
using GiggleGarden.Shared;

// Same synthesize request as GoogleTtsProvider (SSML <prosody rate/pitch>,
// Standard voices, MP3), but the GCP API key comes from Muse's secure
// vault (custom.googlecloudtts) through the gcp_tts.py helper's authd
// surrogate exchange instead of from GenConfig.GoogleCloudTtsApiKey. No key
// value is handled here; the narration travels as one argv element (no
// shell). Selected via GenConfig.TtsProvider = "google-helper" (see
// TtsProviderFactory): en/hi route here, pa still routes to Azure, since
// no free Standard Punjabi voice exists on Google Cloud TTS.
//
// `runner` / `retryDelaysSeconds` (L46) are test seams, same shape as
// AzureHelperTtsProvider's: production passes neither, tests inject a fake
// runner and zero delays so a throttle retry does not stall the suite on
// the real 30/60/120/240s ladder.
class GoogleHelperTtsProvider(
    GenConfig cfg,
    Func<string, IEnumerable<string>, Task<ProcessHelper.Result>>? runner = null,
    int[]? retryDelaysSeconds = null) : ITtsProvider
{
    private readonly Func<string, IEnumerable<string>, Task<ProcessHelper.Result>> _runner =
        runner ?? ((exe, args) => ProcessHelper.RunAsync(exe, args, timeoutMinutes: 5));
    private readonly int[] _delays = retryDelaysSeconds ?? [30, 60, 120, 240];

    // Explicit one-arg form: Activator.CreateInstance (used by the
    // reflection-based PrepHardening tests) does not fill optional
    // parameters, so the primary constructor alone is not enough.
    public GoogleHelperTtsProvider(GenConfig cfg) : this(cfg, null, null) { }

    // Same voice map as GoogleTtsProvider - Standard is the tier with the
    // largest free allowance (4M chars/mo).
    private static readonly Dictionary<string, (string LanguageCode, string VoiceName)> Voices = new()
    {
        ["en"] = ("en-US", "en-US-Standard-C"),
        ["hi"] = ("hi-IN", "hi-IN-Standard-A"),
    };

    public async Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null, string? sceneLabel = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Cannot synthesize empty narration.", nameof(text));

        if (!Voices.TryGetValue(language, out var voice))
            throw new NotSupportedException(
                $"GoogleHelperTtsProvider has no free Standard voice for language \"{language}\" - route it to " +
                "AzureTtsProvider instead (see TtsProviderFactory).");

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // rate/pitch come from the caller's resolved ContentFormatDef, same
        // as GoogleTtsProvider - passed through unchanged into the helper's
        // SSML, so the two providers sound identical.
        var args = new List<string>
        {
            cfg.GoogleTtsHelperPath, "synthesize",
            "--text", text,
            "--language-code", voice.LanguageCode,
            "--voice", voice.VoiceName,
            "--rate", rate,
            "--pitch", pitch,
            "--out", outputPath,
        };

        // L46: Google had no throttle handling at all — a single 429 failed
        // the scene instantly. Mirror AzureHelperTtsProvider's loop: retry
        // 429/throttle responses with the same 30/60/120/240s backoff shape,
        // logging each attempt with the scene (job id via PrepContext.Prefix),
        // a recovery line on success after a throttle, and the scene +
        // attempt count + fall-through on terminal failure.
        var scene = PrepContext.ScenePhrase(sceneLabel);
        var delays = _delays;
        for (var attempt = 0; ; attempt++)
        {
            var result = await _runner("python3", args);
            if (result.ExitCode == 0)
            {
                ProcessHelper.RequireFile(outputPath, "Google Cloud TTS helper");
                if (attempt > 0)
                    Console.WriteLine($"  {PrepContext.Prefix}Google Cloud TTS recovered after {attempt + 1} attempts, {scene}.");
                return;
            }

            var detail = Text.Tail(result.Stderr + result.Stdout, 500);
            var throttled = detail.Contains("429", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("throttl", StringComparison.OrdinalIgnoreCase);
            if (!throttled || attempt >= delays.Length)
                throw new Exception($"Google Cloud TTS helper failed {scene} after {attempt + 1} attempt{(attempt == 0 ? "" : "s")} (exit {result.ExitCode}): {detail} — falling through to next TTS provider");

            var wait = delays[attempt];
            Console.WriteLine(
                $"  {PrepContext.Prefix}Google Cloud TTS throttled {scene} (attempt {attempt + 1}/{delays.Length + 1}): {detail.Replace('\n', ' ').Replace('\r', ' ')} — waiting {wait}s before retrying...");
            await Task.Delay(TimeSpan.FromSeconds(wait));
        }
    }
}
