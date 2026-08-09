using System.Text;
using GiggleGarden.Shared;

// Azure Speech REST API — no SDK dependency needed for simple synthesis.
class TtsClient(GenConfig cfg)
{
    // Style is Azure's per-voice "cheerful"-type express-as tag - verified live against
    // /cognitiveservices/voices/list before picking these (not every voice supports one,
    // and Azure's docs lag behind what's actually deployed for a given locale). Currently
    // null for pa-IN: neither Punjabi neural voice offers a style at all yet.
    private static readonly Dictionary<string, (string Locale, string Voice, string? Style)> Voices = new()
    {
        ["en"] = ("en-US", "en-US-JennyNeural", "cheerful"),
        ["hi"] = ("hi-IN", "hi-IN-SwaraNeural", "cheerful"),
        ["pa"] = ("pa-IN", "pa-IN-VaaniNeural", null),
    };

    public async Task SynthesizeAsync(string text, string language, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Cannot synthesize empty narration.", nameof(text));

        var (locale, voice, style) = Voices.TryGetValue(language, out var v) ? v : Voices["en"];
        var escaped = System.Security.SecurityElement.Escape(text);

        // Slightly faster/brighter than a flat reading, plus the voice's "cheerful" style
        // where available - a plain rate/pitch nudge alone still reads as flat/robotic,
        // not the warm, delighted delivery a kids' show wants.
        var prosody = $"<prosody rate='-4%' pitch='+6%'>{escaped}</prosody>";
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
