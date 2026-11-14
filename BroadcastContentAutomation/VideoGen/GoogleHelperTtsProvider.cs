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
class GoogleHelperTtsProvider(GenConfig cfg) : ITtsProvider
{
    // Same voice map as GoogleTtsProvider - Standard is the tier with the
    // largest free allowance (4M chars/mo).
    private static readonly Dictionary<string, (string LanguageCode, string VoiceName)> Voices = new()
    {
        ["en"] = ("en-US", "en-US-Standard-C"),
        ["hi"] = ("hi-IN", "hi-IN-Standard-A"),
    };

    public async Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null)
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
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.GoogleTtsHelperPath, "synthesize",
            "--text", text,
            "--language-code", voice.LanguageCode,
            "--voice", voice.VoiceName,
            "--rate", rate,
            "--pitch", pitch,
            "--out", outputPath,
        ], timeoutMinutes: 5);
        if (result.ExitCode != 0)
            throw new Exception($"Google Cloud TTS helper failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 500)}");
        ProcessHelper.RequireFile(outputPath, "Google Cloud TTS helper");
    }
}
