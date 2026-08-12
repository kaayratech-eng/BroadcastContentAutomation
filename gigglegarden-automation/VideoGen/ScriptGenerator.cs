using System.Text.Json;
using GiggleGarden.Shared;

// The video's content style. Educational is today's original (and still default-weighted)
// behaviour; the rest are genuinely different writing/pacing rules, not the same script with
// different words - see ScriptGenerator.FormatInstructions. Extend by adding a member here plus
// a case in FormatInstructions; PickFormat and the weighting in GenConfig pick it up automatically.
enum ContentFormat
{
    Educational,
    Rhyme,
    Poem,
    Bedtime,
    SingAlong,
    CountingSong,
}

// Everything about a ContentFormat the script-generation prompt needs, supplied by the
// active ContentProfile (see ContentProfile.FormatInstructions) - one enum member plus
// one case in the profile's switch is all a new format needs; GenerateAsync and its
// callers stay untouched.
record FormatInstructionSet(
    string ContentRules, string ColourGuidance, string ThumbnailGuidance,
    string IntroThird, string MusicMenu, string HashtagExamples);

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
    //
    // NOTE: nothing currently renders a separate image from this text - the real
    // delivered YouTube thumbnail is a cropped frame extracted from Scenes[0] (the
    // intro bumper clip, see Program.cs). This field is kept format-aware and
    // colourful purely so script.json stays internally coherent/documentary; the
    // actual visual lever for the thumbnail's mood is the intro bumper's prompts.
    public string ThumbnailPrompt { get; set; } = "";

    // Spoken + burned-in text for the branded intro bumper prepended to every video
    // (see VideoAssembler/Program.cs) — the channel greeting translated into the target
    // language, this video's character introducing itself by name, and a one-line
    // preview appropriate to this video's ContentFormat. The greeting is what stays
    // constant across the channel now that the character does not.
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

    // Persisted so --assemble/--retts render under the same content profile --prep ran
    // with, without the caller having to repeat --profile.
    public string Profile { get; set; } = "";

    // Which content style this video was written as - persisted so --assemble (which may
    // run days later, in a separate process) knows what it's rendering without re-deriving
    // it, and so --retts can re-voice with the same format-appropriate TTS pacing.
    public ContentFormat Format { get; set; } = ContentFormat.Educational;

    // One mood tag chosen by the model from a small closed menu (see
    // ScriptGenerator.FormatInstructions) appropriate to this format's energy - never free
    // text, so VideoAssembler can map it straight to a background-music folder without any
    // fuzzy matching. Falls back to a format-appropriate default if the model omits it.
    public string MusicMood { get; set; } = "";

    // Human-readable description of this video's narration pacing (e.g. "calm, slow, soft"
    // for Bedtime). Set by us from ScriptGenerator.FormatVoiceProfile, not the model - the
    // actual rate/pitch TtsClient applies come from that same table, so this field can never
    // drift from what the narration really sounds like. Kept on the script primarily so
    // script.json documents what was used, and so --retts logging can show it.
    public string NarrationStyle { get; set; } = "";
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
    // Rate/pitch are plain SSML prosody - safe for every voice/language. TtsClient reads this
    // table directly instead of a hardcoded literal; ScriptGenerator reads only Label, so the
    // "how each format sounds" definition lives in exactly one place. Deliberately does NOT
    // touch Azure's per-voice `style` (express-as) attribute - only "cheerful" is confirmed
    // live for the en/hi voices this channel uses, and pa has no style support at all, so an
    // arbitrary calm-format style value risks a failed Azure call for an unverified pairing.
    public static readonly Dictionary<ContentFormat, (string Rate, string Pitch, string Label)> FormatVoiceProfile = new()
    {
        [ContentFormat.Educational] = ("-4%", "+6%", "bright, lively"),
        [ContentFormat.Rhyme] = ("-4%", "+6%", "bright, playful"),
        [ContentFormat.SingAlong] = ("-4%", "+6%", "bright, singable"),
        [ContentFormat.CountingSong] = ("-4%", "+6%", "bright, playful"),
        [ContentFormat.Poem] = ("-12%", "+2%", "gentle, measured"),
        [ContentFormat.Bedtime] = ("-22%", "-6%", "calm, slow, soft"),
    };

    private static bool IsCalm(ContentFormat format) => format is ContentFormat.Poem or ContentFormat.Bedtime;

    // Weighted random pick, or the caller's override verbatim (--format / deterministic
    // testing). ContentProfile.ContentFormatWeights is data, not code, specifically so the
    // mix can be biased later by editing the profile rather than shipping a new build.
    public static ContentFormat PickFormat(ContentProfile profile, ContentFormat? overrideFormat)
    {
        if (overrideFormat is { } chosen) return chosen;

        var weights = profile.ContentFormatWeights;
        var total = weights.Values.Sum();
        if (total <= 0) return ContentFormat.Educational;

        var roll = Random.Shared.Next(total);
        var cumulative = 0;
        foreach (var (name, weight) in weights)
        {
            cumulative += weight;
            if (roll < cumulative && Enum.TryParse<ContentFormat>(name, ignoreCase: true, out var format))
                return format;
        }
        return ContentFormat.Educational;
    }

    // character is the one the video must be written about, when it came from a pool.
    // Null means invent one - the normal path - and the invented description is then
    // what the reference frame gets drawn from.
    //
    // avoidNames only matters on the invent path: each generation is otherwise
    // independent and has no way to know what earlier videos were called, so without
    // this two runs can pick the same short, obvious name for two different animals.
    //
    // formatOverride forces a specific ContentFormat instead of the weighted random pick -
    // for deterministic testing, or a future --format CLI flag.
    public static async Task<VideoScript> GenerateAsync(
        ContentProfile profile, string? topic, string language, IScriptProvider provider,
        string? trendContext = null, CharacterBrief? character = null,
        IReadOnlyList<string>? avoidNames = null, ContentFormat? formatOverride = null)
    {
        if (!profile.UsesCharacterMascot)
            throw new NotSupportedException(
                $"Profile \"{profile.Id}\" has UsesCharacterMascot = false, but ScriptGenerator " +
                "only implements the character-invention/character-anchored prompt today. A " +
                "faceless-visual script flow is future work, not built in this pass. Set " +
                $"UsesCharacterMascot = true on \"{profile.Id}\", or implement the faceless path first.");

        var format = PickFormat(profile, formatOverride);
        var (contentRules, colourGuidance, thumbnailGuidance, introThird, musicMenu, hashtagExamples) =
            profile.FormatInstructions(format);

        var langName = Text.LanguageName(language);

        var topicGuidance = profile.TopicGuidance;

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
            : profile.BuildCharacterInventionInstructions(langName, avoidLine);

        var prompt = $$"""
{{profile.AudiencePersona}}
{{topicLine}}
Language for ALL narration: {{langName}}.
{{profile.SafetyFraming}}

This video's content format is {{format}}. Its requirements are given below and
DIVERGE from other formats on purpose - follow THIS format's rules, not a
generic "kids video" template.

{{characterLine}}

The character must be recognisably the SAME one in every scene of this video.
Every imagePrompt and every motionPrompt must name it and restate its key
visual features from characterDescription - the picture generator has no memory
between scenes, so "the character" on its own is not enough.

Universal requirements (apply to every format):
- Exactly 8 scenes. Each scene: 1-2 short sentences a narrator reads aloud
  (simple vocabulary). 8 scenes plus the intro is sized to land the finished
  video just inside the 90-second limit Reels enforces - see the per-format
  line-length guidance below, since narration pace differs by format. More
  scenes than this and the ending gets cut off.
- Keep each scene's narration under 115 characters (a hard ceiling - some
  formats below ask for meaningfully shorter). It is burned into the video as
  an on-screen subtitle, and long lines do not fit the frame. The harder limit
  is that each scene becomes one animated clip capped at 10 seconds: a line
  much past 115 characters takes longer than that to speak, and the picture
  then freezes on its final frame while the narrator finishes.
- Write narration in ordinary sentence case. Never put a word in ALL CAPITALS
  for emphasis - the speech synthesizer reads a fully capitalised word as an
  initialism and spells it out letter by letter ("AH-CHOO" becomes "A-H-C-H-O-O").
  Convey excitement through word choice and punctuation instead.
- Entirely original - do not copy existing rhymes' lyrics.
- Each scene also gets an image prompt IN ENGLISH describing the illustration.
  Open it by naming the character and its appearance, then the setting and
  action. Keep the subject centred and clear of the top and bottom thirds so
  the same art works cropped to both 16:9 and 9:16, no text in the image.
  ALWAYS give it an explicit COLOURFUL background/setting - name a colour or
  palette in the sentence itself (e.g. "colourful garden background, bright
  cheerful palette") - never plain white, black, grey or empty, and never a
  single-colour flat void. {{colourGuidance}}
- Each scene ALSO gets a motionPrompt IN ENGLISH describing what actually MOVES
  in that scene, because every scene is generated as a short animated clip that
  starts from a picture of this video's character. Name the character and its
  appearance first, then describe the action, not the composition: what it does,
  and any simple scene motion (leaves drifting, bubbles rising). Match the
  actions to the body the character actually has - do not have it clap if it
  has wings. Keep it to one or two sentences, keep the movement small and
  loopable. Like the image prompt, ALWAYS name an explicit colourful
  background/setting - the whole clip stays colourful start to finish, never a
  plain or empty backdrop - and always end with: "2D cartoon animation, flat
  colors, thick outlines, character design stays consistent." Never describe a
  camera cut or a change of character.
- musicMood: ONE mood tag for this video's background music, chosen EXACTLY
  from this list (do not invent your own wording): {{musicMenu}}. Pick whichever
  one best matches this format's energy.

This format's specific requirements ({{format}}):
{{contentRules}}

- Title <=90 chars in {{langName}}, written for search. Shape it as
  "[familiar rhyme/concept or concrete topic] [one emoji] | [the twist setting]" -
  the first segment is what gets searched for, the second is what makes it
  look new. Include the concrete topic, never just the character's name
  (nobody is searching for a character they have never seen). A single
  ALL-CAPS word for emphasis is fine HERE - titles are not spoken - but
  nowhere else.
- Description in {{langName}}, in this order:
  line 1: a hook addressed straight to the child - "Can you ...?", "Let's ...!",
  "It's time to ...!", "Oh no, ...!" - optionally with an emoji. (For calm
  formats like Bedtime/Poem, keep this line's energy soft and inviting rather
  than exclamatory - e.g. "It's time to drift off to sleep..." not "Oh no!")
  line 2: 7-12 hashtags on ONE line, near the TOP, not at the bottom. First the
  channel, then 3-4 specific to this video's format and topic - favour tags
  like {{hashtagExamples}} where they fit - then broad audience ones.
  then: a short paragraph naming what this video actually is (the skill it
  teaches, the rhyme, the poem's theme, or the bedtime wind-down) and who it
  is for (toddlers, preschoolers).
- tags: 5-10 topical tags for THIS video only, mixing {{langName}} and English -
  the format, the rhyme/skill/theme, the setting, the character's species. Do
  not include generic channel or audience tags; those are added automatically.
- tiktokCaption: casual, front-loaded hook in {{langName}}, under 150
  characters, matching this format's energy (soft for Bedtime/Poem, upbeat
  otherwise), ending with 3-5 hashtags. Do NOT include #Shorts.
- reelsCaption: for Instagram/Facebook Reels in {{langName}}, under 300
  characters - the FIRST LINE must work standalone since feeds truncate
  after 1-2 lines - ending with 5-10 hashtags (mix broad + specific). Do NOT
  include #Shorts.
- thumbnailPrompt IN ENGLISH: one bold illustration concept for a YouTube
  thumbnail (documentary text only - nothing currently renders a separate
  image from it, but keep it consistent with the rest of this script.json).
  {{thumbnailGuidance}} It does not need to depict a specific scene; it needs to
  be readable by a child who cannot read.
- introText: exactly three very short sentences in {{langName}}, spoken over the
  channel's branded opening bumper (plays before scene 1). First: a natural
  translation of "Welcome to {{profile.ChannelName}}!" - an energetic greeting, not a
  stiff word-for-word translation, and keep the channel name recognisable (for
  Bedtime, keep this greeting warm rather than loud). Second: the character
  introducing ITSELF by name ("I'm Milo the fox!") - viewers have never met it
  before, so this is the only place they learn who they are watching. Third:
  {{introThird}}. Under 105 characters TOTAL across all three - this is a ~10
  second spoken intro on top of 8 scenes, and going over pushes the video past
  the length Reels allows.

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
  "musicMood": "...",
  "tags": ["..."],
  "scenes": [ { "narration": "...", "imagePrompt": "...", "motionPrompt": "..." } ]
}
""";

        var text = await provider.CompleteAsync(prompt);

        var script = JsonSerializer.Deserialize<VideoScript>(text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new Exception("The script provider returned no parseable script JSON.");

        if (script.Scenes.Count == 0) throw new Exception("Claude returned a script with no scenes.");
        if (string.IsNullOrWhiteSpace(script.Title)) throw new Exception("Claude returned a script with no title.");
        if (string.IsNullOrWhiteSpace(script.TikTokCaption)) script.TikTokCaption = script.Description;
        if (string.IsNullOrWhiteSpace(script.ReelsCaption)) script.ReelsCaption = script.Description;
        if (string.IsNullOrWhiteSpace(script.ThumbnailPrompt)) script.ThumbnailPrompt = script.Scenes[0].ImagePrompt;
        if (string.IsNullOrWhiteSpace(script.IntroText)) script.IntroText = $"Welcome to {profile.ChannelName}!";
        if (string.IsNullOrWhiteSpace(script.MusicMood)) script.MusicMood = IsCalm(format) ? "soft piano lullaby" : "upbeat playful";

        script.Format = format;
        script.NarrationStyle = FormatVoiceProfile[format].Label;

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
        script.Tags = profile.BaseTags.Concat(script.Tags)
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
}
