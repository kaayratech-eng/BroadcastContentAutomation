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

        var result = await ProcessHelper.RunAsync("python3", args, timeoutMinutes: 5);
        if (result.ExitCode != 0)
            throw new Exception($"Azure TTS helper failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 500)}");
        ProcessHelper.RequireFile(outputPath, "Azure TTS helper");
    }
}
