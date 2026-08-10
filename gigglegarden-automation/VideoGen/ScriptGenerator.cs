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
    // not narrative accuracy). No overlay text - every thumbnail sampled from the
    // researched channels carried none, just the face and the object.
    public string ThumbnailPrompt { get; set; } = "";

    // Spoken + burned-in text for the branded intro bumper prepended to every video
    // (see VideoAssembler/Program.cs) — the channel greeting translated into the target
    // language, this video's character introducing itself by name, and a one-line
    // preview of the learning objective. The greeting is what stays constant across the
    // channel now that the character does not.
    public string IntroText { get; set; } = "";

    // This video's character, and only this video's. Consistency is WITHIN a video:
    // CharacterDescription is the appearance every scene's prompts are anchored to, and
    // the description the reference frame was drawn from, so the words and the picture
    // cannot drift apart. A different character each video is the point — the channel,
    // not a mascot, is what viewers are meant to recognise.
    //
    // Both stay in the Latin alphabet whatever the narration language, because both are
    // only ever read by machines that expect English: the image prompt the reference
    // frame is drawn from, and the motion prompt Vidu animates it with. Hindi and
    // Punjabi scripts spell the name in their own script inside the narration.
    public string CharacterName { get; set; } = "";
    public string CharacterDescription { get; set; } = "";

    // The frame every scene animates from, resolved during --prep (drawn from the
    // description above, or picked from a pool). Persisted because --assemble may run
    // days later and needs it to resubmit a scene Vidu dropped.
    public string? CharacterImagePath { get; set; }

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

    // Vidu image-to-video state. StartFramePath is the frame this scene animates from —
    // this video's character reference, the same frame for every scene, which is what
    // keeps one character across the whole video while leaving all clips submittable at
    // once. (Chaining each scene off the previous clip's last frame would be serial, and
    // off-peak Vidu takes up to 48 hours per clip.) ViduTaskId is the async job submitted
    // during --prep; ClipPath is the finished clip once --assemble has collected it.
    // Persisted in script.json so the two phases, which may run days apart, share state
    // across process restarts.
    public string? StartFramePath { get; set; }
    public string? ViduTaskId { get; set; }
    public string? ClipPath { get; set; }

    // What Vidu should animate for this scene. Distinct from ImagePrompt (which describes
    // a still) because a video prompt has to describe motion, not just composition.
    public string MotionPrompt { get; set; } = "";
}

static class ScriptGenerator
{
    // Fixed tag block, appended to every video. Both CoComelon and Vlad and Niki run
    // exactly this architecture - an unchanging ~12-tag brand/audience block plus a
    // handful of topical tags - and it is the one piece of their metadata discipline
    // that costs nothing to copy (see tasks/research-kids-channels.md). Done here
    // rather than in the prompt because a constant does not need a model to produce it.
    private static readonly string[] BaseTags =
    [
        "giggle garden", "kids songs", "nursery rhymes", "kids learning", "preschool",
        "toddler", "kids animation", "kids education", "children songs", "learning for kids",
        "kids videos", "sing-along",
    ];

    // character is the one the video must be written about, when it came from a pool.
    // Null means invent one - the normal path - and the invented description is then
    // what the reference frame gets drawn from.
    //
    // avoidNames only matters on the invent path: each generation is otherwise
    // independent and has no way to know what earlier videos were called, so without
    // this two runs can pick the same short, obvious name for two different animals.
    public static async Task<VideoScript> GenerateAsync(
        GenConfig cfg, string? topic, string language, string? trendContext = null,
        CharacterBrief? character = null, IReadOnlyList<string>? avoidNames = null)
    {
        var langName = Text.LanguageName(language);

        // Public-domain rhyme plus a novel setting is the highest-scoring formula in the
        // researched sample - CoComelon's top five in the retrieved window are all
        // public-domain rhymes with a twist (Humpty Dumpty + a balloon chase, Twinkle
        // Twinkle + shiny shoes), while original concepts cluster several times lower.
        // It costs nothing: the rhyme carries the search volume, the twist is prompt work.
        const string topicGuidance =
            "Prefer a well-known PUBLIC DOMAIN nursery rhyme or counting song given a fresh " +
            "twist setting (e.g. Twinkle Twinkle at the beach, Five Little Ducks at a birthday " +
            "party) - the familiar rhyme is what parents search for and the twist is what makes " +
            "it new. Otherwise choose a plain preschool concept (counting, colors, animals, " +
            "shapes, habits like brushing teeth, seasons). Never reproduce a copyrighted song's " +
            "lyrics - public-domain rhymes only, and write your own verses around them.";

        var topicLine = topic is not null
            ? $"Topic: {topic}"
            : trendContext is not null
                ? $"Choose ONE fresh topic suitable for ages 2-6. {topicGuidance} For inspiration " +
                  "only - do NOT copy any of these titles or their wording - here is what's " +
                  $"currently trending in kids' content on YouTube:\n{trendContext}"
                : $"Choose ONE fresh topic suitable for ages 2-6. {topicGuidance}";

        var avoidLine = avoidNames is { Count: > 0 }
            ? $"Recent videos already used these names - pick a different one: {string.Join(", ", avoidNames)}."
            : "";

        var characterLine = character is not null
            ? $"""
               This video stars {character.Name}, who looks like this: {character.Description}
               That description is of an actual picture that already exists and that every scene
               will be animated from, so do not change the species, colours or accessories - write
               the video around this character as described. Echo the name and description back
               unchanged in characterName and characterDescription - both stay in the Latin
               alphabet even when the narration is not, because they are only ever read by the
               image and animation prompts, which are written in English. Spell the name however
               it sounds best inside the {langName} narration itself.
               """
            : $"""
               Invent ONE brand-new character for THIS VIDEO ONLY - a fresh name and a fresh
               animal/species, different from anything obvious or generic. This is not a returning
               mascot; the next video gets a different character entirely, so do not write it as
               though the viewer already knows who it is.
               {avoidLine}
               Return it as:
               - characterName: the character's name, written in the Latin alphabet even when the
                 narration is not - it goes into the English image and animation prompts, and a
                 name in another script there gets ignored or garbled. Pick one that a {langName}
                 toddler finds short, warm and easy to say, and spell it in {langName} inside the
                 narration itself.
               - characterDescription: ONE English sentence describing exactly how it LOOKS -
                 species, main colour, body shape, eyes, and one distinguishing accessory
                 (a scarf, a hat, spots, a flower). This exact sentence is used to draw the
                 picture that every scene animates from, so it must be concrete and visual.
                 No personality, no backstory, no actions - appearance only.
               """;

        var prompt = $$"""
You write original scripts for an animated kids' YouTube channel (ages 2-6).
{{topicLine}}
Language for ALL narration: {{langName}}.
Everything you write must stay age-appropriate for 2-6 year olds regardless of
how the topic above is phrased - simple vocabulary, no scary or violent
content, no innuendo.

{{characterLine}}

The character must be recognisably the SAME one in every scene of this video.
Every imagePrompt and every motionPrompt must name it and restate its key
visual features from characterDescription - the picture generator has no memory
between scenes, so "the character" on its own is not enough.

Requirements:
- Exactly 8 scenes. Each scene: 1-2 short, rhythmic, sing-song sentences a
  narrator reads aloud (simple vocabulary, repetition kids love). Narration runs
  about 9 seconds per scene once spoken, so 8 scenes plus the intro lands the
  finished video around 83 seconds - just inside the 90-second limit Reels
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
  slow "once upon a time" style opener. Best of all is a small PROBLEM the
  character has, stated immediately - something lost, something wanted,
  something going wrong ("Oh no, where did my hat go?"). Possession, prohibition
  and peril are the three conflicts a three-year-old understands instantly.
- Use deliberate three-fold repetition of short words for the emotional beats -
  "No, no, no!", "Help, help, help!", "Come on, come on!". Repeat whole phrases
  between scenes too. Reuse the same small set of words throughout instead of
  reaching for variety: a narrow vocabulary is what makes this age group able to
  follow and join in, and it is a measured property of the channels that work.
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
- Each scene also gets an image prompt IN ENGLISH describing the illustration.
  Open it by naming the character and its appearance, then the setting and
  action. Bright colors, simple composition, no text in the image. Keep the
  subject centred and clear of the top and bottom thirds so the same art works
  cropped to both 16:9 and 9:16.
- Each scene ALSO gets a motionPrompt IN ENGLISH describing what actually MOVES
  in that scene, because every scene is generated as a short animated clip that
  starts from a picture of this video's character. Name the character and its
  appearance first, then describe the action, not the composition: what it does
  (waves, hops, points, counts on its fingers or paws, peeks, spins), and any
  simple scene motion (leaves drifting, bubbles rising). Match the actions to
  the body the character actually has - do not have it clap if it has wings.
  Keep it to one or two sentences, keep the movement small and loopable, and
  always end with: "2D cartoon animation, flat colors, thick outlines, character
  design stays consistent." Never describe a camera cut or a change of character.
- Entirely original - do not copy existing rhymes' lyrics.
- Title <=90 chars in {{langName}}, written for search. Shape it as
  "[familiar rhyme or concrete skill] [one emoji] | [the twist setting]" - the
  first segment is what gets searched for, the second is what makes it look new.
  Include the concrete skill/topic, never just the character's name (nobody is
  searching for a character they have never seen). A single ALL-CAPS word for
  emphasis is fine HERE - titles are not spoken - but nowhere else.
- Description in {{langName}}, in this order:
  line 1: a hook addressed straight to the child - "Can you ...?", "Let's ...!",
  "It's time to ...!", "Oh no, ...!" - optionally with an emoji.
  line 2: 7-12 hashtags on ONE line, near the TOP, not at the bottom. First the
  channel, then 3-4 specific to this topic, then broad audience ones.
  then: a short paragraph naming the specific skill this video teaches, because
  that is what a parent searches for, and who it is for (toddlers, preschoolers).
- tags: 5-10 topical tags for THIS video only, mixing {{langName}} and English -
  the rhyme or skill, the setting, the character's species. Do not include
  generic channel or audience tags; those are added automatically.
- tiktokCaption: casual, front-loaded hook in {{langName}}, under 150
  characters, ending with 3-5 hashtags. Do NOT include #Shorts.
- reelsCaption: for Instagram/Facebook Reels in {{langName}}, under 300
  characters - the FIRST LINE must work standalone since feeds truncate
  after 1-2 lines - ending with 5-10 hashtags (mix broad + specific). Do NOT
  include #Shorts.
- thumbnailPrompt IN ENGLISH: one bold illustration concept for a YouTube
  thumbnail. Name the character and its appearance, give it ONE enormous face
  filling much of the frame with an exaggerated delighted/surprised expression,
  mouth open, looking straight down the lens, plus one oversized instantly
  recognisable object beside it. Saturated primary colours, simple background,
  no text in the image. It does not need to depict a specific scene; it needs to
  make a scrolling parent stop, and to be readable by a child who cannot read.
- introText: exactly three very short sentences in {{langName}}, spoken over the
  channel's branded opening bumper (plays before scene 1). First: a natural
  translation of "Welcome to {{cfg.ChannelName}}!" - an energetic greeting, not a
  stiff word-for-word translation, and keep the channel name recognisable.
  Second: the character introducing ITSELF by name ("I'm Milo the fox!") -
  viewers have never met it before, so this is the only place they learn who
  they are watching. Third: a short, exciting preview of the ONE skill this
  video teaches ("Today we're learning to count to five!"). Under 105
  characters TOTAL across all three - this is a ~10 second spoken intro on top
  of 8 scenes, and going over pushes the video past the length Reels allows.

Respond ONLY with JSON, no markdown fences:
{
  "title": "...",
  "description": "...",
  "tiktokCaption": "...",
  "reelsCaption": "...",
  "thumbnailPrompt": "...",
  "introText": "...",
  "characterName": "...",
  "characterDescription": "...",
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
        if (string.IsNullOrWhiteSpace(script.IntroText)) script.IntroText = $"Welcome to {cfg.ChannelName}!";

        // A pooled character is not the model's to change - it describes a picture that
        // already exists - so the sidecar wins over whatever came back.
        if (character is not null)
        {
            script.CharacterName = character.Name;
            script.CharacterDescription = character.Description;
        }

        // The description is what the reference frame gets drawn from and what anchors
        // every scene prompt, so an empty one is not something to paper over quietly.
        if (string.IsNullOrWhiteSpace(script.CharacterName) || string.IsNullOrWhiteSpace(script.CharacterDescription))
            throw new Exception(
                "Claude returned a script without a character name and appearance description. " +
                "Both are required - the description is what this video's character is drawn from.");

        // Fixed brand/audience block first, then whatever is specific to this video,
        // de-duplicated in case the model volunteered one of the fixed ones anyway.
        script.Tags = BaseTags.Concat(script.Tags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        // A scene with no motion still has to animate - falling back to the still's
        // description plus idle movement beats submitting an empty prompt to Vidu. The
        // character is named explicitly because the fallback may be all Vidu gets.
        foreach (var scene in script.Scenes)
            if (string.IsNullOrWhiteSpace(scene.MotionPrompt))
                scene.MotionPrompt =
                    $"{script.CharacterName} ({script.CharacterDescription}). {scene.ImagePrompt}. " +
                    "Gentle idle motion, the character breathes and blinks. " +
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
