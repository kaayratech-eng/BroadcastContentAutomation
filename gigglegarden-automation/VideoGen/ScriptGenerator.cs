using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

class VideoScript
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<Scene> Scenes { get; set; } = [];

    // TikTok rewards a casual, front-loaded hook and a short hashtag list;
    // Instagram/Facebook Reels feeds truncate the caption after ~1-2 lines, so
    // the hook needs to be the very first words, and tolerate more hashtags.
    // Kept separate rather than one shared caption because reusing the same
    // text across platforms reads as generic on both.
    public string TikTokCaption { get; set; } = "";
    public string ReelsCaption { get; set; } = "";

    // Drives the custom YouTube thumbnail: a single bold, exaggerated-expression
    // illustration concept distinct from any one scene (built for click-through,
    // not narrative accuracy), plus a 2-4 word overlay phrase burned onto it.
    public string ThumbnailPrompt { get; set; } = "";
    public string ThumbnailText { get; set; } = "";

    // Spoken + burned-in text for the branded intro bumper prepended to every video
    // (see VideoAssembler/Program.cs) — always "Welcome to Giggle World!" translated into
    // the target language, plus a one-line preview of this video's specific learning
    // objective, so every video opens the same recognizable way before the hook scene.
    public string IntroText { get; set; } = "";

    // Persisted so --assemble renders in the same language --prep was run with,
    // days later, without the caller having to repeat the flag.
    public string Language { get; set; } = "en";
}

class Scene
{
    public string Narration { get; set; } = "";
    public string ImagePrompt { get; set; } = "";
    public string? AudioPath { get; set; }
    public string? ImagePath { get; set; }
    public string? VerticalImagePath { get; set; }

    // Second image for scenes whose narration runs long (see GenConfig.SplitLongSceneAfterSeconds) —
    // shown via an internal crossfade partway through so a long line isn't one static photo start to finish.
    public string? ImagePath2 { get; set; }
    public string? VerticalImagePath2 { get; set; }
    public double DurationSeconds { get; set; }

    // Vidu image-to-video state. StartFramePath is the frame this scene animates from
    // (the canonical mascot reference for scene 1, the previous scene's last frame after
    // that); ViduTaskId is the async job submitted during --prep; ClipPath is the finished
    // clip once --assemble has collected it. Persisted in script.json so the two phases,
    // which may run days apart, share state across process restarts.
    public string? StartFramePath { get; set; }
    public string? ViduTaskId { get; set; }
    public string? ClipPath { get; set; }

    // What Vidu should animate for this scene. Distinct from ImagePrompt (which describes
    // a still) because a video prompt has to describe motion, not just composition.
    public string MotionPrompt { get; set; } = "";
}

static class ScriptGenerator
{
    public static async Task<VideoScript> GenerateAsync(GenConfig cfg, string? topic, string language, string? trendContext = null)
    {
        var langName = Text.LanguageName(language);

        var topicLine = topic is not null
            ? $"Topic: {topic}"
            : trendContext is not null
                ? "Choose ONE fresh topic suitable for ages 2-6 (counting, colors, animals, shapes, " +
                  "habits like brushing teeth, seasons). For inspiration only - do NOT copy any of " +
                  "these titles or their wording - here is what's currently trending in kids' " +
                  $"content on YouTube:\n{trendContext}"
                : "Choose ONE fresh topic suitable for ages 2-6 (counting, colors, animals, shapes, habits like brushing teeth, seasons).";

        var prompt = $$"""
You write original scripts for an animated kids' YouTube channel (ages 2-6).
{{topicLine}}
Language for ALL narration: {{langName}}.
Everything you write must stay age-appropriate for 2-6 year olds regardless of
how the topic above is phrased - simple vocabulary, no scary or violent
content, no innuendo.

The channel's main character is always {{cfg.CharacterName}} - use this exact
name every time (translated/transliterated into {{langName}} where natural),
never a different name or a different animal/species. This is a recurring
mascot across every video on the channel, not a one-off character for this
topic alone.

Requirements:
- Exactly 8 or 9 scenes. Each scene: 1-2 short, rhythmic, sing-song sentences a
  narrator reads aloud (simple vocabulary, repetition kids love). Narration runs
  about 9 seconds per scene once spoken, so 8-9 scenes plus the intro lands the
  finished video around 85 seconds - just inside the 90-second limit Reels
  enforces. More scenes than this and the ending gets cut off.
- Keep each scene's narration under 115 characters. It is burned into the video
  as an on-screen subtitle, and long lines do not fit the frame. The harder
  limit is that each scene becomes one animated clip and those cap out at 10
  seconds: a line much past 115 characters takes longer than that to speak, and
  the picture then freezes on its final frame while the narrator finishes.
- Write narration in ordinary sentence case. Never put a word in ALL CAPITALS
  for emphasis - the speech synthesizer reads a fully capitalised word as an
  initialism and spells it out letter by letter ("AH-CHOO" becomes "A-H-C-H-O-O").
  Convey excitement through word choice and punctuation instead.
- Scene 1 is the hook: the very first line must grab attention in the first
  couple of seconds (an exciting question, a silly sound, a surprise) - not a
  slow "once upon a time" style opener.
- Include at least one genuinely funny/silly beat somewhere in the middle
  (a joke, a funny sound word, the character being goofy) - this is meant to
  be funny as well as educational, not just calm and pretty.
- Include exactly one interactive call-and-response beat: one scene ends with
  a direct question inviting the viewer to answer or join in (e.g. "Can you
  count with me?", "What color is this?"), and the very next scene opens with
  an enthusiastic payoff line that answers it (e.g. "Yes! 1, 2, 3!"). Kids
  watching alone should feel invited to participate, not lectured at.
- Pick ONE concrete skill or fact this video teaches (e.g. "counting to 5",
  "the color red", "brushing teeth before bed") and make sure the scenes
  actually teach it, not just mention it once.
- Each scene also gets an image prompt IN ENGLISH describing the illustration:
  same recurring main character across all scenes, bright colors, simple
  composition, no text in the image. Keep the subject centred and clear of the
  top and bottom thirds so the same art works cropped to both 16:9 and 9:16.
- Each scene ALSO gets a motionPrompt IN ENGLISH describing what actually MOVES
  in that scene, because every scene is generated as a short animated clip that
  starts from the previous scene's final frame. Describe the action, not the
  composition: what the character does (waves, hops, points, counts on a wing,
  peeks, spins), and any simple scene motion (leaves drifting, bubbles rising).
  Keep it to one or two sentences, keep the movement small and loopable, and
  always end with: "2D cartoon animation, flat colors, thick outlines, character
  design stays consistent." Never describe a camera cut or a change of character.
- Entirely original - do not copy existing rhymes' lyrics.
- Title <=90 chars in {{langName}}, written for search (include the concrete
  skill/topic, not just the character name).
- Description: 2 short paragraphs in {{langName}}. The second paragraph must
  name the specific skill this video teaches (parents search for that). End
  with 3-5 hashtags. 10-15 tags mixing {{langName}} and English.
- tiktokCaption: casual, front-loaded hook in {{langName}}, under 150
  characters, ending with 3-5 hashtags. Do NOT include #Shorts.
- reelsCaption: for Instagram/Facebook Reels in {{langName}}, under 300
  characters - the FIRST LINE must work standalone since feeds truncate
  after 1-2 lines - ending with 5-10 hashtags (mix broad + specific). Do NOT
  include #Shorts.
- thumbnailPrompt IN ENGLISH: one bold illustration concept for a YouTube
  thumbnail - the main character with an exaggerated, delighted/surprised
  expression, high contrast, simple background, no text in the image. It does
  not need to depict a specific scene; it needs to make a scrolling parent
  stop and click.
- thumbnailText: a 2-4 word punchy phrase in {{langName}} to overlay on the
  thumbnail (e.g. "COUNT WITH ME!"). Short enough to read at a glance.
- introText: exactly two short sentences in {{langName}}, spoken over the
  channel's branded opening bumper (plays before scene 1, same character,
  every video). First sentence: a natural translation of "Welcome to Giggle
  World!" - keep it an energetic greeting, not a literal word-for-word
  translation if that would sound stiff. Second sentence: a short, exciting
  preview of the ONE skill this specific video teaches (e.g. "Today we're
  learning to count to five!"). Together under 110 characters total - this is
  a ~4-5 second spoken intro, not another scene.

Respond ONLY with JSON, no markdown fences:
{
  "title": "...",
  "description": "...",
  "tiktokCaption": "...",
  "reelsCaption": "...",
  "thumbnailPrompt": "...",
  "thumbnailText": "...",
  "introText": "...",
  "tags": ["..."],
  "scenes": [ { "narration": "...", "imagePrompt": "...", "motionPrompt": "..." } ]
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
        if (string.IsNullOrWhiteSpace(script.TikTokCaption)) script.TikTokCaption = script.Description;
        if (string.IsNullOrWhiteSpace(script.ReelsCaption)) script.ReelsCaption = script.Description;
        if (string.IsNullOrWhiteSpace(script.ThumbnailPrompt)) script.ThumbnailPrompt = script.Scenes[0].ImagePrompt;
        if (string.IsNullOrWhiteSpace(script.ThumbnailText)) script.ThumbnailText = script.Title;
        if (string.IsNullOrWhiteSpace(script.IntroText)) script.IntroText = "Welcome to Giggle World!";

        // A scene with no motion still has to animate - falling back to the still's
        // description plus idle movement beats submitting an empty prompt to Vidu.
        foreach (var scene in script.Scenes)
            if (string.IsNullOrWhiteSpace(scene.MotionPrompt))
                scene.MotionPrompt = $"{scene.ImagePrompt}. Gentle idle motion, the character breathes and blinks. " +
                                     "2D cartoon animation, flat colors, thick outlines, character design stays consistent.";

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
