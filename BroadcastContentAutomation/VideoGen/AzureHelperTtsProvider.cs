// Azure Speech TTS provider via the vault-backed helper (set up 2026-11-14).
using GiggleGarden.Shared;

// Same synthesize request as AzureTtsProvider (SSML <voice> + optional
// <mstts:express-as style> + <prosody rate/pitch>, MP3), but the Azure
// Speech key comes from Muse's secure vault (custom.azure-speech) through
// the azure_tts.py helper's authd surrogate exchange instead of from
// GenConfig.AzureSpeechKey. No key value is handled here; the narration
// travels as one argv element (no shell). Selected via
// GenConfig.TtsProvider = "azure-helper" (see TtsProviderFactory).
// Per-scene mood variation works exactly as in AzureTtsProvider: the
// caller's styleOverride (Scene.Mood, already validated against the
// profile's NarrationMoodStyles by ScriptGenerator.NormalizeSceneMoods)
// wins, else the profile's fixed VoiceOverride style. Only styles verified
// live against Azure for a given voice ever reach the helper, since an
// unverified value fails the Azure call outright.
//
// `runner` / `retryDelaysSeconds` (L44/L46) are test seams, same shape as
// GovernorClient's runner: production passes neither, tests inject a fake
// runner and zero delays so a throttle retry does not stall the suite on
// the real 30/60/120/240s ladder.
class AzureHelperTtsProvider(
    GenConfig cfg,
    IReadOnlyDictionary<string, (string Locale, string Voice, string? Style)> voices,
    Func<string, IEnumerable<string>, Task<ProcessHelper.Result>>? runner = null,
    int[]? retryDelaysSeconds = null) : ITtsProvider
{
    private readonly Func<string, IEnumerable<string>, Task<ProcessHelper.Result>> _runner =
        runner ?? ((exe, args) => ProcessHelper.RunAsync(exe, args, timeoutMinutes: 5));
    private readonly int[] _delays = retryDelaysSeconds ?? [30, 60, 120, 240];

    // Explicit two-arg form: Activator.CreateInstance (used by the
    // reflection-based PrepHardening tests) does not fill optional
    // parameters, so the primary constructor alone is not enough.
    public AzureHelperTtsProvider(GenConfig cfg, IReadOnlyDictionary<string, (string Locale, string Voice, string? Style)> voices)
        : this(cfg, voices, null, null) { }

    public async Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null, string? sceneLabel = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Cannot synthesize empty narration.", nameof(text));

        var (locale, voice, profileStyle) = voices.TryGetValue(language, out var v) ? v : voices["en"];
        var style = styleOverride ?? profileStyle;

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // rate/pitch come from the caller's resolved ContentFormatDef, same
        // as AzureTtsProvider - passed through unchanged into the helper's
        // SSML, so the two providers sound identical.
        var args = new List<string>
        {
            cfg.AzureTtsHelperPath, "synthesize",
            "--text", text,
            "--locale", locale,
            "--voice", voice,
            "--rate", rate,
            "--pitch", pitch,
            "--region", cfg.AzureSpeechRegion,
            "--out", outputPath,
        };
        if (style is not null)
        {
            args.Add("--style");
            args.Add(style);
        }

        // Azure Free F0 throttles with HTTP 429 ("Downstream Service Throttled")
        // after ~20-35 rapid synth calls - the first long-form prep died at
        // 34/43. A throttled call is not a failure: wait it out and retry with
        // backoff (30s, 60s, 120s, 240s) instead of failing the whole run.
        // Anything that is NOT a throttle throws immediately, as before.
        // L42/L44/L45: every line names the scene (job id via PrepContext.Prefix);
        // the throttle line carries the actual 429 detail, success after a
        // throttle logs recovery, and the terminal failure names the scene,
        // the attempt count, and the fall-through to the next provider.
        var scene = PrepContext.ScenePhrase(sceneLabel);
        var delays = _delays;
        for (var attempt = 0; ; attempt++)
        {
            var result = await _runner("python3", args);
            if (result.ExitCode == 0)
            {
                ProcessHelper.RequireFile(outputPath, "Azure TTS helper");
                if (attempt > 0)
                    Console.WriteLine($"  {PrepContext.Prefix}Azure TTS recovered after {attempt + 1} attempts, {scene}.");
                return;
            }

            var detail = Text.Tail(result.Stderr + result.Stdout, 500);
            var throttled = detail.Contains("429", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("throttl", StringComparison.OrdinalIgnoreCase);
            if (!throttled || attempt >= delays.Length)
                throw new Exception($"Azure TTS helper failed {scene} after {attempt + 1} attempt{(attempt == 0 ? "" : "s")} (exit {result.ExitCode}): {detail} — falling through to next TTS provider");

            var wait = delays[attempt];
            Console.WriteLine(
                $"  {PrepContext.Prefix}Azure TTS throttled {scene} (attempt {attempt + 1}/{delays.Length + 1}): {detail.Replace('\n', ' ').Replace('\r', ' ')} — waiting {wait}s before retrying...");
            await Task.Delay(TimeSpan.FromSeconds(wait));
        }
    }
}
