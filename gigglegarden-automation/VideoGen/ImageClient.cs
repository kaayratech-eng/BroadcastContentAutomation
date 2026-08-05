using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

// Pluggable image generation. Default: OpenAI Images API. Provider switched
// via cfg.ImageProvider ("openai" | "stability").
class ImageClient(GenConfig cfg)
{
    public enum Orientation { Landscape, Portrait }

    public async Task GenerateAsync(string scenePrompt, string stylePrompt, string outputPath, Orientation orientation)
    {
        var framing = orientation == Orientation.Portrait
            ? "Vertical 9:16 portrait composition, subject centred with generous headroom and " +
              "empty space in the lower third for subtitles."
            : "Horizontal 16:9 composition, subject centred, empty space in the lower third for subtitles.";

        var fullPrompt =
            $"{stylePrompt}. {scenePrompt}. {framing} Children's book illustration, bright cheerful " +
            "colors, soft rounded shapes, no text, no words, no letters anywhere in the image.";

        switch (cfg.ImageProvider.ToLowerInvariant())
        {
            case "stability": await StabilityAsync(fullPrompt, outputPath, orientation); break;
            default: await OpenAiAsync(fullPrompt, outputPath, orientation); break;
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new Exception($"Image provider returned no usable image for {Path.GetFileName(outputPath)}.");
    }

    private async Task OpenAiAsync(string prompt, string outputPath, Orientation orientation)
    {
        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  [openai] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {cfg.OpenAiApiKey}");

        var body = JsonSerializer.Serialize(new
        {
            model = cfg.OpenAiImageModel,
            prompt,
            n = 1,
            size = orientation == Orientation.Portrait ? "1024x1536" : "1536x1024",
            quality = "medium",
        });

        var resp = await http.PostAsync("https://api.openai.com/v1/images/generations",
            new StringContent(body, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"OpenAI image failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 500)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var first = doc.RootElement.GetProperty("data")[0];

        if (first.TryGetProperty("b64_json", out var b64))
            await File.WriteAllBytesAsync(outputPath, Convert.FromBase64String(b64.GetString()!));
        else
            await File.WriteAllBytesAsync(outputPath, await http.GetByteArrayAsync(first.GetProperty("url").GetString()!));
    }

    private async Task StabilityAsync(string prompt, string outputPath, Orientation orientation)
    {
        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  [stability] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {cfg.StabilityApiKey}");
        http.DefaultRequestHeaders.Add("Accept", "image/*");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(prompt), "prompt" },
            { new StringContent(orientation == Orientation.Portrait ? "9:16" : "16:9"), "aspect_ratio" },
            { new StringContent("png"), "output_format" },
        };

        var resp = await http.PostAsync("https://api.stability.ai/v2beta/stable-image/generate/core", form);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Stability failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 500)}");

        await File.WriteAllBytesAsync(outputPath, await resp.Content.ReadAsByteArrayAsync());
    }
}
