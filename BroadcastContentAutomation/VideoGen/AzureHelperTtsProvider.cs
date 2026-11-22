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
class AzureHelperTtsProvider(GenConfig cfg, IReadOnlyDictionary<string, (string Locale, string Voice, string? Style)> voices) : ITtsProvider
{
    public async Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null)
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
        var delays = new[] { 30, 60, 120, 240 };
        for (var attempt = 0; ; attempt++)
        {
            var result = await ProcessHelper.RunAsync("python3", args, timeoutMinutes: 5);
            if (result.ExitCode == 0)
            {
                ProcessHelper.RequireFile(outputPath, "Azure TTS helper");
                return;
            }

            var detail = Text.Tail(result.Stderr + result.Stdout, 500);
            var throttled = detail.Contains("429", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("throttl", StringComparison.OrdinalIgnoreCase);
            if (!throttled || attempt >= delays.Length)
                throw new Exception($"Azure TTS helper failed (exit {result.ExitCode}): {detail}");

            var wait = delays[attempt];
            Console.WriteLine(
                $"  Azure TTS throttled (attempt {attempt + 1}/{delays.Length + 1}) - waiting {wait}s before retrying...");
            await Task.Delay(TimeSpan.FromSeconds(wait));
        }
    }
}
