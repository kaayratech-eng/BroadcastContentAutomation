using System.Net;
using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

// Google Cloud Text-to-Speech REST API. Standard voices are free to 4M chars/mo,
// which is orders of magnitude above this channel's real usage (~1,000 chars/video)
// - see tasks/todo.md Part 2/6 for the sourced free-tier numbers. Covers en/hi only:
// there is no free (Standard or WaveNet) Punjabi voice on Google Cloud TTS, so pa
// always routes to AzureTtsProvider instead (see TtsProviderFactory).
//
// Auth is a bare restricted API key (?key=), the same low-friction shape as
// GenConfig.YouTubeApiKey - scope the key to "Cloud Text-to-Speech API" in GCP
// Console. If that ever stops working against the live API, the fallback is a
// service-account JSON key + self-signed-JWT exchange, not implemented here
// because the simple key has not been shown to fail.
class GoogleTtsProvider(GenConfig cfg, HttpMessageHandler? innerHandler = null) : ITtsProvider
{
    // L47: the retry budget the provider logs exhaustion against — kept
    // in sync with the RetryHandler it constructs below.
    private const int MaxAttempts = 4;

    // Explicit one-arg form: Activator.CreateInstance does not fill
    // optional parameters, so the primary constructor alone is not enough
    // for reflection-based construction.
    public GoogleTtsProvider(GenConfig cfg) : this(cfg, null) { }

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
    // Standard (not WaveNet/Chirp/Studio) voices only - Standard is the tier with the
    // largest free allowance and the one whose SSML support is unrestricted, matching
    // what AzureTtsProvider already relies on (<prosody rate/pitch>).
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
                $"GoogleTtsProvider has no free Standard voice for language \"{language}\" - route it to " +
                "AzureTtsProvider instead (see TtsProviderFactory).");

        var escaped = System.Security.SecurityElement.Escape(text);

        // rate/pitch come from the caller's resolved ContentFormatDef, same as
        // AzureTtsProvider, so a format's pacing can't drift between providers. Google
        // Standard voices have no equivalent to Azure's express-as "cheerful" style tag,
        // so that dimension is simply absent here rather than approximated - styleOverride
        // (a scene's mood tag, see ITtsProvider) is intentionally unused.
        //
        // Rate handling (verified 2026-11-12 in gcp_tts.py): the API IGNORES
        // the SSML <prosody rate> on this path - the same text at +50% and
        // -50% came back byte-identical. audioConfig.speakingRate DOES work,
        // so the rate percentage is translated to a multiplier here (mirroring
        // the helper's _rate_to_speaking_rate) and left OUT of the SSML so the
        // two mechanisms can never double-apply. Pitch stays in the SSML.
        // Without this, a switch to this provider silently shipped slow
        // narration at the default rate.
        var ssml = $"<speak><prosody pitch='{pitch}'>{escaped}</prosody></speak>";

        // L42/L47: the scene label threads into the RetryHandler log
        // callback, so every retry line — and the handler's exhaustion
        // line — names the scene (job id via PrepContext.Prefix).
        // `innerHandler` is a test seam: tests inject a canned handler so
        // no live Google call is made.
        var sceneSuffix = string.IsNullOrWhiteSpace(sceneLabel) ? "" : $" scene {sceneLabel.Trim()}";
        var retryHandler = new RetryHandler(maxAttempts: MaxAttempts,
            log: m => Console.WriteLine($"  {PrepContext.Prefix}[google-tts]{sceneSuffix} {m}"));
        if (innerHandler is not null) retryHandler.InnerHandler = innerHandler;
        using var http = new HttpClient(retryHandler)
        {
            Timeout = TimeSpan.FromMinutes(2),
        };

        var body = JsonSerializer.Serialize(new
        {
            input = new { ssml },
            voice = new { languageCode = voice.LanguageCode, name = voice.VoiceName },
            audioConfig = new { audioEncoding = "MP3", speakingRate = RateToSpeakingRate(rate, sceneLabel) },
        });

        var url = $"https://texttospeech.googleapis.com/v1/text:synthesize?key={cfg.GoogleCloudTtsApiKey}";
        var resp = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
        {
            // L47: the handler already logged "gave up after 4 attempts";
            // the provider adds the scene-named exhaustion outcome before
            // throwing, so the give-up is visible at the provider level too.
            if (IsTransientStatus(resp.StatusCode))
                Console.WriteLine($"  {PrepContext.Prefix}[google-tts]{sceneSuffix}: retries exhausted after {MaxAttempts} attempts (HTTP {(int)resp.StatusCode}) — falling through to next TTS provider");
            throw new Exception($"Google TTS failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 400)}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var audioContent = doc.RootElement.GetProperty("audioContent").GetString()!;
        var bytes = Convert.FromBase64String(audioContent);
        if (bytes.Length == 0) throw new Exception("Google TTS returned an empty audio stream.");

        await File.WriteAllBytesAsync(outputPath, bytes);
    }

    // Translates an SSML-style rate percentage ("-10%", "+4%", "0%") or a
    // plain multiplier ("1.0") to an audioConfig speakingRate multiplier,
    // clamped to the API's 0.25-4.0 range. Mirrors gcp_tts.py's
    // _rate_to_speaking_rate exactly, so the direct provider and the helper
    // provider agree.
    private static double RateToSpeakingRate(string rate, string? sceneLabel = null)
    {
        double multiplier;
        try
        {
            var value = rate.Trim();
            multiplier = value.EndsWith('%')
                ? 1.0 + double.Parse(value[..^1], System.Globalization.CultureInfo.InvariantCulture) / 100.0
                : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            // L78: a malformed rate used to synthesize silently at 1.0x —
            // the wrong pace with no trace. Log the bad value + scene.
            Console.WriteLine($"  {PrepContext.Prefix}[google-tts] unparseable rate \"{rate}\" for {PrepContext.ScenePhrase(sceneLabel)} — using 1.0x");
            multiplier = 1.0;
        }
        return Math.Clamp(multiplier, 0.25, 4.0);
    }
}
