using System.Text;
using GiggleGarden.Shared;

// Azure Speech REST API — no SDK dependency needed for simple synthesis.
class TtsClient(GenConfig cfg)
{
    private static readonly Dictionary<string, (string Locale, string Voice)> Voices = new()
    {
        ["en"] = ("en-US", "en-US-JennyNeural"),
        ["hi"] = ("hi-IN", "hi-IN-SwaraNeural"),
        ["pa"] = ("pa-IN", "pa-IN-VaaniNeural"),
    };

    public async Task SynthesizeAsync(string text, string language, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Cannot synthesize empty narration.", nameof(text));

        var (locale, voice) = Voices.TryGetValue(language, out var v) ? v : Voices["en"];

        // Slightly slower rate + higher pitch reads better for young kids.
        var ssml = $"""
<speak version='1.0' xml:lang='{locale}'>
  <voice name='{voice}'>
    <prosody rate='-8%' pitch='+5%'>{System.Security.SecurityElement.Escape(text)}</prosody>
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
