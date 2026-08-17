using System.Text;
using System.Text.RegularExpressions;
using GiggleGarden.Shared;

// Azure Speech REST API — no SDK dependency needed for simple synthesis. Kept as the
// mandatory fallback: it is the only provider covering pa-IN (no free Standard/WaveNet
// Punjabi voice exists anywhere), and the forced-fallback path for en/hi via
// GenConfig.TtsProvider = "azure". See TtsProviderFactory for routing.
partial class AzureTtsProvider(GenConfig cfg, IReadOnlyDictionary<string, (string Locale, string Voice, string? Style)> voices) : ITtsProvider
{
    // Azure reads an all-caps token as an initialism and spells it out letter by letter,
    // so a scripted "AH-CHOO!" comes back as "A-H-C-H-O-O". Scripts legitimately use caps
    // for emphasis and that reads well burned into the frame, so the fix is to soften the
    // case for speech only - the subtitle keeps the original text.
    //
    // Runs of two or more capitals are the trigger; single capitals are left alone so an
    // ordinary sentence start, "I", or a deliberately spelled-out "A, B, C" still works.
    private static string SpeakableCase(string text) =>
        AllCapsRun().Replace(text, m =>
            m.Value[0] + m.Value[1..].ToLowerInvariant());

    [GeneratedRegex(@"\p{Lu}{2,}")]
    private static partial Regex AllCapsRun();

    public async Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Cannot synthesize empty narration.", nameof(text));

        var (locale, voice, style) = voices.TryGetValue(language, out var v) ? v : voices["en"];
        var escaped = System.Security.SecurityElement.Escape(SpeakableCase(text));

        // Rate/pitch come from the caller's resolved ContentFormatDef (see
        // ScriptGenerator.PickFormat) so a format's pacing can't drift between what the
        // script says (NarrationStyle) and what actually gets spoken. The voice's
        // express-as style (below) is deliberately left untouched by format - only styles
        // verified live against Azure for a given voice (e.g. "cheerful" for GiggleGarden's
        // voices, "narration-professional" for en-US-AriaNeural) should ever appear in a
        // profile's VoiceOverride, since an unverified style value risks a failed Azure call
        // outright.
        var prosody = $"<prosody rate='{rate}' pitch='{pitch}'>{escaped}</prosody>";
        var voiceContent = style is null
            ? prosody
            : $"<mstts:express-as style='{style}' styledegree='2'>{prosody}</mstts:express-as>";

        var ssml = $"""
<speak version='1.0' xml:lang='{locale}' xmlns:mstts='http://www.w3.org/2001/mstts'>
  <voice name='{voice}'>
    {voiceContent}
  </voice>
</speak>
""";

        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  [azure-tts] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", cfg.AzureSpeechKey);
        http.DefaultRequestHeaders.Add("X-Microsoft-OutputFormat", "audio-24khz-96kbitrate-mono-mp3");
        http.DefaultRequestHeaders.Add("User-Agent", "GiggleGardenVideoGen");

        var url = $"https://{cfg.AzureSpeechRegion}.tts.speech.microsoft.com/cognitiveservices/v1";
        var resp = await http.PostAsync(url, new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"));

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Azure TTS failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 400)}");

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0) throw new Exception("Azure TTS returned an empty audio stream.");

        await File.WriteAllBytesAsync(outputPath, bytes);
    }
}
