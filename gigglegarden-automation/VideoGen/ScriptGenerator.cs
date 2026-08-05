using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

class VideoScript
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<Scene> Scenes { get; set; } = [];

    // Short-form caption: tighter than the YouTube description, hashtag-forward.
    // Reels/TikTok cap at 2,200 characters and reward front-loaded hooks.
    public string ShortCaption { get; set; } = "";
}

class Scene
{
    public string Narration { get; set; } = "";
    public string ImagePrompt { get; set; } = "";
    public string? AudioPath { get; set; }
    public string? ImagePath { get; set; }
    public string? VerticalImagePath { get; set; }
    public double DurationSeconds { get; set; }
}

static class ScriptGenerator
{
    public static async Task<VideoScript> GenerateAsync(GenConfig cfg, string? topic, string language)
    {
        var langName = Text.LanguageName(language);

        var topicLine = topic is null
            ? "Choose ONE fresh topic suitable for ages 2-6 (counting, colors, animals, shapes, habits like brushing teeth, seasons)."
            : $"Topic: {topic}";

        var prompt = $$"""
You write original scripts for an animated kids' YouTube channel (ages 2-6).
{{topicLine}}
Language for ALL narration: {{langName}}.

Requirements:
- 6 to 8 scenes. Each scene: 1-2 short, rhythmic, sing-song sentences a
  narrator reads aloud (simple vocabulary, repetition kids love).
- Keep each scene's narration under 140 characters. It is burned into the
  video as an on-screen subtitle and long lines do not fit the frame.
- Each scene also gets an image prompt IN ENGLISH describing the illustration:
  same recurring main character across all scenes, bright colors, simple
  composition, no text in the image. Keep the subject centred and clear of the
  top and bottom thirds so the same art works cropped to both 16:9 and 9:16.
- Entirely original - do not copy existing rhymes' lyrics.
- Title <=90 chars in {{langName}}. Description: 2 short paragraphs in
  {{langName}} + 3-5 hashtags. 10-15 tags mixing {{langName}} and English.
- shortCaption: a punchy caption for Reels/TikTok in {{langName}}, under 250
  characters, hook first, ending with 3-5 hashtags. Do NOT include #Shorts.

Respond ONLY with JSON, no markdown fences:
{
  "title": "...",
  "description": "...",
  "shortCaption": "...",
  "tags": ["..."],
  "scenes": [ { "narration": "...", "imagePrompt": "..." } ]
}
""";

        var body = JsonSerializer.Serialize(new
        {
            model = cfg.ClaudeModel,
            max_tokens = 8000,
            messages = new[] { new { role = "user", content = prompt } },
        });

        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  [claude] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.Add("x-api-key", cfg.AnthropicApiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var resp = await http.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Claude script generation failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 600)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Safety classifiers return HTTP 200 with stop_reason "refusal" and empty
        // content - reading content[0] first would throw an unhelpful index error.
        if (root.TryGetProperty("stop_reason", out var stop) && stop.GetString() == "refusal")
            throw new Exception("Claude declined this script request. Try a different topic.");

        var text = ExtractJson(root.GetProperty("content"));

        var script = JsonSerializer.Deserialize<VideoScript>(text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new Exception("Claude returned no parseable script JSON.");

        if (script.Scenes.Count == 0) throw new Exception("Claude returned a script with no scenes.");
        if (string.IsNullOrWhiteSpace(script.Title)) throw new Exception("Claude returned a script with no title.");
        if (string.IsNullOrWhiteSpace(script.ShortCaption)) script.ShortCaption = script.Description;

        return script;
    }

    // Concatenate every text block, then strip fences. Thinking-capable models can
    // return a leading block before the answer, so content[0] is not reliably the JSON.
    private static string ExtractJson(JsonElement content)
    {
        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                sb.Append(block.GetProperty("text").GetString());

        var text = sb.ToString().Replace("```json", "").Replace("```", "").Trim();

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
