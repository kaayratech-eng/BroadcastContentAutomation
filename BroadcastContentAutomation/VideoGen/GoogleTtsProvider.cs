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
class GoogleTtsProvider(GenConfig cfg) : ITtsProvider
{
    // Standard (not WaveNet/Chirp/Studio) voices only - Standard is the tier with the
    // largest free allowance and the one whose SSML support is unrestricted, matching
    // what AzureTtsProvider already relies on (<prosody rate/pitch>).
    private static readonly Dictionary<string, (string LanguageCode, string VoiceName)> Voices = new()
    {
        ["en"] = ("en-US", "en-US-Standard-C"),
        ["hi"] = ("hi-IN", "hi-IN-Standard-A"),
    };

    public async Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch)
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
        // so that dimension is simply absent here rather than approximated.
        var ssml = $"<speak><prosody rate='{rate}' pitch='{pitch}'>{escaped}</prosody></speak>";

        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  [google-tts] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(2),
        };

        var body = JsonSerializer.Serialize(new
        {
            input = new { ssml },
            voice = new { languageCode = voice.LanguageCode, name = voice.VoiceName },
            audioConfig = new { audioEncoding = "MP3" },
        });

        var url = $"https://texttospeech.googleapis.com/v1/text:synthesize?key={cfg.GoogleCloudTtsApiKey}";
        var resp = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Google TTS failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 400)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var audioContent = doc.RootElement.GetProperty("audioContent").GetString()!;
        var bytes = Convert.FromBase64String(audioContent);
        if (bytes.Length == 0) throw new Exception("Google TTS returned an empty audio stream.");

        await File.WriteAllBytesAsync(outputPath, bytes);
    }
}
