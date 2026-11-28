using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GiggleGarden.Shared;

// Azure Speech REST API — no SDK dependency needed for simple synthesis. Kept as the
// mandatory fallback: it is the only provider covering pa-IN (no free Standard/WaveNet
// Punjabi voice exists anywhere), and the forced-fallback path for en/hi via
// GenConfig.TtsProvider = "azure". See TtsProviderFactory for routing.
partial class AzureTtsProvider(GenConfig cfg, IReadOnlyDictionary<string, (string Locale, string Voice, string? Style)> voices, HttpMessageHandler? innerHandler = null) : ITtsProvider
{
    // L47: the retry budget the provider logs exhaustion against — kept
    // in sync with the RetryHandler it constructs below.
    private const int MaxAttempts = 4;

    // Explicit two-arg form: Activator.CreateInstance does not fill
    // optional parameters, so the primary constructor alone is not enough
    // for reflection-based construction.
    public AzureTtsProvider(GenConfig cfg, IReadOnlyDictionary<string, (string Locale, string Voice, string? Style)> voices)
        : this(cfg, voices, null) { }

    // L47: the statuses RetryHandler retries (mirrors its IsTransient),
    // so the exhaustion line is only logged when retries really ran out.
    private static bool IsTransientStatus(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout
             or HttpStatusCode.TooManyRequests
             or HttpStatusCode.InternalServerError
             or HttpStatusCode.BadGateway
             or HttpStatusCode.ServiceUnavailable
             or HttpStatusCode.GatewayTimeout
        || (int)code == 529;
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

    public async Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null, string? sceneLabel = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Cannot synthesize empty narration.", nameof(text));

        var (locale, voice, profileStyle) = voices.TryGetValue(language, out var v) ? v : voices["en"];
        var escaped = System.Security.SecurityElement.Escape(SpeakableCase(text));

        // Rate/pitch come from the caller's resolved ContentFormatDef (see
        // ScriptGenerator.PickFormat) so a format's pacing can't drift between what the
        // script says (NarrationStyle) and what actually gets spoken. The style is either
        // this scene's own mood tag (styleOverride, see Scene.Mood/ContentProfile.
        // NarrationMoodStyles - already validated against the voice's real StyleList by
        // ScriptGenerator before it gets here) or, absent one, the profile's fixed
        // VoiceOverride style - only styles verified live against Azure for a given voice
        // should ever appear in either, since an unverified value risks a failed Azure call
        // outright.
        var style = styleOverride ?? profileStyle;
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

        // L42/L47: the scene label threads into the RetryHandler log
        // callback, so every retry line — and the handler's exhaustion
        // line — names the scene (job id via PrepContext.Prefix).
        // `innerHandler` is a test seam: tests inject a canned handler so
        // no live Azure call is made.
        var sceneSuffix = string.IsNullOrWhiteSpace(sceneLabel) ? "" : $" scene {sceneLabel.Trim()}";
        var retryHandler = new RetryHandler(maxAttempts: MaxAttempts,
            log: m => Console.WriteLine($"  {PrepContext.Prefix}[azure-tts]{sceneSuffix} {m}"));
        if (innerHandler is not null) retryHandler.InnerHandler = innerHandler;
        using var http = new HttpClient(retryHandler)
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", cfg.AzureSpeechKey);
        http.DefaultRequestHeaders.Add("X-Microsoft-OutputFormat", "audio-24khz-96kbitrate-mono-mp3");
        http.DefaultRequestHeaders.Add("User-Agent", "GiggleGardenVideoGen");

        var url = $"https://{cfg.AzureSpeechRegion}.tts.speech.microsoft.com/cognitiveservices/v1";
        var resp = await http.PostAsync(url, new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"));

        if (!resp.IsSuccessStatusCode)
        {
            // L47: the handler already logged "gave up after 4 attempts";
            // the provider adds the scene-named exhaustion outcome before
            // throwing, so the give-up is visible at the provider level too.
            if (IsTransientStatus(resp.StatusCode))
                Console.WriteLine($"  {PrepContext.Prefix}[azure-tts]{sceneSuffix}: retries exhausted after {MaxAttempts} attempts (HTTP {(int)resp.StatusCode}) — falling through to next TTS provider");
            throw new Exception($"Azure TTS failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 400)}");
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0) throw new Exception("Azure TTS returned an empty audio stream.");

        await File.WriteAllBytesAsync(outputPath, bytes);
    }
}
