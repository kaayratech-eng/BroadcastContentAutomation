using System.Text.Json;
using GiggleGarden.Shared;

// Everything about a content format the script-generation prompt needs, supplied by the
// active ContentProfile as part of a ContentFormatDef (see ContentProfile.cs) - a profile
// adds a format by adding one ContentFormatDef to its Formats list; GenerateAsync and its
// callers never change.
// LongFormContentRules is null for every profile that never generates long-form (e.g.
// GiggleGarden, which stays on the Vidu path) - GenerateAsync falls back to ContentRules
// whenever it is null, so short-form profiles need no changes at all.
record FormatInstructionSet(
    string ContentRules, string ColourGuidance, string ThumbnailGuidance,
    string IntroThird, string MusicMenu, string HashtagExamples,
    string? LongFormContentRules = null);

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
    // preview appropriate to this video's ContentFormat.
    public string IntroText { get; set; } = "";

    // This video's character. CharacterDescription is the appearance every scene's
    // prompts are anchored to, and the description the reference frame was drawn from,
    // so the words and the picture cannot drift apart within a video.
    //
    // Across videos, this is usually one of a small recurring cast (see
    // CharacterSource.Resolve/CharacterPoolMaxSize in GiggleGardenProfile.cs) rather than
    // a fresh invention each time — a recognisable returning character is what the
    // research says drives loyalty in this genre, so the channel deliberately builds a
    // cast instead of a new stranger every video. CharacterCatchphrase is that cast
    // member's signature line, carried in the pool sidecar so it repeats across
    // appearances; empty for a character that hasn't been given one, or an older pool
    // entry from before this field existed.
    //
    // All three stay in the Latin alphabet whatever the narration language, because they
    // are only ever read by machines that expect English: the image prompt the reference
    // frame is drawn from, and the motion prompt Vidu animates it with. Hindi and
    // Punjabi scripts spell the name in their own script inside the narration.
    public string CharacterName { get; set; } = "";
    public string CharacterDescription { get; set; } = "";
    public string? CharacterCatchphrase { get; set; }

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

    // True when this script was generated with --long (a 12-15 minute YouTube documentary
    // cut, 70-90 scenes) instead of the usual ~8-scene Reels/Shorts short. Persisted so
    // --assemble knows to trust Scene.VisualGroup for art reuse and so a script.json is
    // self-documenting about which shape it is.
    public bool IsLongForm { get; set; } = false;

    // Which content format this video was written as - the Id of a ContentFormatDef on
    // whichever ContentProfile generated it (see Profile below). Persisted so --assemble
    // (which may run days later, in a separate process) knows what it's rendering without
    // re-deriving it, and so --retts can re-voice with the same format-appropriate TTS
    // pacing - resolve it back to a ContentFormatDef via ScriptGenerator.PickFormat(profile,
    // script.Format), which does an exact Id match when given a non-null override.
    public string Format { get; set; } = "";

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

// Response shape for a long-form continuation act (GenerateContinuationActAsync) - just
// more scenes, no metadata, since Act 1 already supplied title/description/etc.
class ScenesOnly
{
    public List<Scene> Scenes { get; set; } = [];
}

class Scene
{
    public string Narration { get; set; } = "";
    public string ImagePrompt { get; set; } = "";
    public string? AudioPath { get; set; }
    public string? ImagePath { get; set; }
    public string? VerticalImagePath { get; set; }
    public double DurationSeconds { get; set; }

    // Vidu image-to-video state. StartFramePath is the frame this scene animates from —
    // this video's character reference, the same frame for every scene, which is what
    // keeps one character across the whole video while leaving all clips submittable at
    // once. (Chaining each scene off the previous clip's last frame would be serial, and
    // off-peak Vidu takes up to 48 hours per clip.) ViduTaskId is the async job submitted
    // during --prep; ClipPath is the finished clip once --assemble has collected it.
    // Persisted in script.json so the two phases, which may run days apart, share state
    // across process restarts.
    //
    // ClipPath is no longer exclusively Vidu's: for a profile with AllowsContextScenes
    // (Deliverable 6), a "context" scene's ClipPath is instead a Pexels video download
    // (StockFootageClient), resolved synchronously during --prep, and StartFramePath/
    // ViduTaskId stay null since nothing is ever submitted to Vidu for that scene.
    public string? StartFramePath { get; set; }
    public string? ViduTaskId { get; set; }
    public string? ClipPath { get; set; }

    // What Vidu should animate for this scene. Distinct from ImagePrompt (which describes
    // a still) because a video prompt has to describe motion, not just composition.
    public string MotionPrompt { get; set; } = "";

    // "character" (default) or "context" - only meaningful when the active profile has
    // AllowsContextScenes = true (see ContentProfile.cs); ScriptGenerator never asks the
    // model for this field otherwise, so every scene stays "character" for profiles like
    // GiggleGarden. Program.cs's call-site rewiring (Deliverable 6) reads this to route a
    // "context" scene to StockFootageClient instead of the manual Gemini character-art loop.
    public string SceneKind { get; set; } = "character";

    // Short English stock-footage/photo search phrase (2-6 words, e.g. "ancient greek ruins
    // fog") - only populated, and only meaningful, for SceneKind == "context". Consumed by
    // StockFootageClient.DownloadBestMatchAsync as the query; ImagePrompt/MotionPrompt are
    // not used for a context scene since nothing generates or animates art for it.
    public string? StockQuery { get; set; }

    // Only meaningful when VideoScript.IsLongForm is true. Consecutive scenes sharing the
    // same VisualGroup number are the same "visual beat" - the art-resolution loop in
    // Program.cs resolves one image/clip for the group and reuses it across every scene in
    // it (Remotion gives each scene its own pan/zoom regardless, so a held image still
    // reads as motion - see Assembly.tsx). A short-form script never asks the model for
    // this, so it stays at its default 0 for every scene, which the art-resolution loop
    // never treats as a group since it only reuses when IsLongForm is true.
    public int VisualGroup { get; set; } = 0;

    // One express-as style tag for THIS scene's narration, chosen by the model from
    // ContentProfile.NarrationMoodStyles (only asked for when that list is non-empty - see
    // ScriptGenerator.GenerateAsync). Null/empty means a neutral, unstyled read - the same
    // behavior every scene had before this field existed. Any value not in the profile's
    // menu is nulled out during postprocessing rather than sent to Azure, since an
    // unverified style string would fail the whole scene's TTS call.
    public string? Mood { get; set; }
}

static class ScriptGenerator
{
    // Weighted random pick over profile.Formats, or an exact case-insensitive Id match on
    // the caller's override (--format / deterministic testing / re-resolving script.Format
    // back to its ContentFormatDef in Program.cs). Throws on an unknown Id rather than
    // silently falling back - same "throw, don't guess" pattern ContentProfileRegistry.Get
    // already established for --profile.
    public static ContentFormatDef PickFormat(ContentProfile profile, string? overrideId)
    {
        if (overrideId is not null)
        {
            var match = profile.Formats.FirstOrDefault(
                f => f.Id.Equals(overrideId, StringComparison.OrdinalIgnoreCase));
            return match ?? throw new ArgumentException(
                $"Unknown format \"{overrideId}\" for profile \"{profile.Id}\". Use one of: " +
                string.Join(", ", profile.Formats.Select(f => f.Id)));
        }

        var total = profile.Formats.Sum(f => f.Weight);
        if (total <= 0) return profile.Formats[0];

        var roll = Random.Shared.Next(total);
        var cumulative = 0;
        foreach (var f in profile.Formats)
        {
            cumulative += f.Weight;
            if (roll < cumulative) return f;
        }
        return profile.Formats[^1];
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
        IReadOnlyList<string>? avoidNames = null, string? formatOverride = null,
        bool longForm = false, IReadOnlyList<string>? avoidTopics = null)
    {
        if (!profile.UsesCharacterMascot)
            throw new NotSupportedException(
                $"Profile \"{profile.Id}\" has UsesCharacterMascot = false, but ScriptGenerator " +
                "only implements the character-invention/character-anchored prompt today. A " +
                "faceless-visual script flow is future work, not built in this pass. Set " +
                $"UsesCharacterMascot = true on \"{profile.Id}\", or implement the faceless path first.");

        var formatDef = PickFormat(profile, formatOverride);
        var (contentRules, colourGuidance, thumbnailGuidance, introThird, musicMenu, hashtagExamples, longFormContentRules) =
            formatDef.Instructions;
        var effectiveContentRules = longForm ? (longFormContentRules ?? contentRules) : contentRules;

        var langName = Text.LanguageName(language);

        var topicGuidance = profile.TopicGuidance;

        var avoidTopicsLine = topic is null && avoidTopics is { Count: > 0 }
            ? $" Already covered by earlier videos - do not repeat any of these topics/titles, pick something distinct: {string.Join("; ", avoidTopics)}."
            : "";

        var topicLine = topic is not null
            ? $"Topic: {topic}"
            : trendContext is not null
                ? $"Choose ONE fresh topic. {topicGuidance}{avoidTopicsLine} For inspiration " +
                  "only - do NOT copy any of these titles or their wording - here is what's " +
                  $"currently trending on YouTube:\n{trendContext}"
                : $"Choose ONE fresh topic. {topicGuidance}{avoidTopicsLine}";

        var avoidLine = avoidNames is { Count: > 0 }
            ? $"Recent videos already used these names - pick a different one: {string.Join(", ", avoidNames)}."
            : "";

        var catchphraseLine = character?.Catchphrase is { Length: > 0 }
            ? $" {character.Name}'s signature phrase is \"{character.Catchphrase}\" - work it in " +
              "naturally somewhere in this video (a greeting, a repeated beat), translated " +
              $"into {langName} the way it would actually be said, so returning viewers " +
              "recognise it."
            : "";

        var characterLine = character is not null
            ? $"""
               This video stars {character.Name}, a returning character the audience already
               knows from earlier videos, who looks like this: {character.Description}
               That description is of an actual picture that already exists and that every scene
               will be animated from, so do not change the species, colours or accessories - write
               the video around this character as described.{catchphraseLine} Echo the name and
               description back unchanged in characterName and characterDescription - both stay in
               the Latin alphabet even when the narration is not, because they are only ever read
               by the image and animation prompts, which are written in English. Spell the name
               however it sounds best inside the {langName} narration itself.
               """
            : profile.BuildCharacterInventionInstructions(langName, avoidLine);

        var sceneKindGuidance = profile.AllowsContextScenes
            ? """
              Each scene also gets a sceneKind, either "character" or "context":
              - "character": this video's figure is doing something on screen. Gets an
                imagePrompt and motionPrompt as described below; stockQuery stays empty.
              - "context": a real place, object, era-appropriate setting, or abstract idea
                with NO character in frame - the shot a documentary would cut to while the
                narration keeps talking (ruins, a landscape, a storm, an artifact, a crowd).
                Leave imagePrompt and motionPrompt empty and instead give a stockQuery: 2-6
                plain English words fit for a stock-footage/photo search (e.g. "ancient greek
                ruins fog", "stormy ocean waves night") - concrete and visual, not a summary
                of the narration.
              Mix both freely across the scenes - context scenes are how a real setting
              comes through without the character on screen every single beat - but do not
              use "context" for more than half the scenes, and scene 1 should be "character"
              so the video opens on its subject.
              """
            : "";

        var moodStyles = profile.NarrationMoodStyles.GetValueOrDefault(language, []);
        var moodGuidance = moodStyles.Count > 0
            ? $"""

              Each scene also gets a mood: ONE tag chosen EXACTLY from this list (do not invent
              your own wording), matching what is actually happening in THAT scene's narration -
              {string.Join(", ", moodStyles)}. A calm setup beat and a sudden
              disaster should NOT sound the same; vary mood scene to scene as the story's emotion
              actually shifts. Omit mood entirely (leave it out of the JSON) for a scene that is
              genuinely neutral and needs no particular emotional read.
              """
            : "";

        var hookGuidance = longForm
            ? """


              This is a long-form video, but its first 8-10 scenes will ALSO be cut out on
              their own and reused as a short teaser for Instagram/Facebook/TikTok/Shorts.
              Write those opening scenes so they work as a strong, self-contained hook that
              lands even if the video stopped right there - the same hook discipline a
              90-second short needs, just at the front of a longer piece.
              """
            : "";

        var visualGroupGuidance = longForm
            ? """

              Each scene also gets a visualGroup: a small integer naming which "visual beat"
              it belongs to. Consecutive scenes that would work fine sharing the exact same
              still image (the camera holding on one moment while the narration keeps
              talking, or a few scenes that are really one beat) get the SAME visualGroup
              number; moving to a new location, subject, or dramatic turn increases the
              number by 1 (groups do not need to be contiguous per sceneKind - a "context"
              and a "character" scene can share a group if they're the same beat). Cluster
              THIS ACT into roughly 3 to 5 visual groups, starting at 1, each typically
              spanning 20-40 seconds of narration (a handful of consecutive scenes) - do not
              give every scene its own group, and do not put the whole act in one group.
              """
            : "";

        // Long-form is generated as several smaller requests ("acts") rather than one huge
        // one - a single 70-90 scene completion is bigger than the free-tier token-per-minute
        // budget most script-provider accounts have (Groq's included), and it also crowds
        // Claude's own max_tokens ceiling (ClaudeScriptProvider.cs). This first call only
        // needs to cover Act 1's own scenes; GenerateContinuationActAsync below asks for the
        // rest in smaller follow-up requests that get merged into the same script.
        var sceneCountLine = longForm
            ? """
              - Roughly 10 to 14 scenes for THIS ACT ONLY (Act 1 of 4 - the opening and
                setup). This act also carries the full title/description/character-invention
                response below, so it is kept shorter than the acts that follow - those ask
                for more scenes each (see GenerateContinuationActAsync) to reach a full 70-90
                scene, 12-15 minute video narrated at a natural pace (~140 words per minute).
                Do not try to tell the whole story here - establish the premise, the stakes,
                and this video's hook, and leave the complication, turning point, and
                resolution for the acts that follow.
              """
            : """
              - Exactly 8 scenes. Each scene: 1-2 short sentences a narrator reads aloud
                (simple vocabulary). 8 scenes plus the intro is sized to land the finished
                video just inside the 90-second limit Reels enforces - see the per-format
                line-length guidance below, since narration pace differs by format. More
                scenes than this and the ending gets cut off.
              """;

        var sceneLengthLine = longForm
            ? """
              - Keep each scene's narration under 180 characters. It is burned into the
                video as an on-screen subtitle, and long lines do not fit the frame - break
                a longer beat into two consecutive scenes (in the same visualGroup if they
                share the same shot) rather than writing one long line.
              """
            : """
              - Keep each scene's narration under 115 characters (a hard ceiling - some
                formats below ask for meaningfully shorter). It is burned into the video as
                an on-screen subtitle, and long lines do not fit the frame. The harder limit
                is that each scene becomes one animated clip capped at 10 seconds: a line
                much past 115 characters takes longer than that to speak, and the picture
                then freezes on its final frame while the narrator finishes.
              """;

        var prompt = $$"""
{{profile.AudiencePersona}}
{{topicLine}}
Language for ALL narration: {{langName}}.
{{profile.SafetyFraming}}

This video's content format is {{formatDef.Id}}. Its requirements are given below and
DIVERGE from other formats on purpose - follow THIS format's rules, not a
generic "kids video" template.

{{characterLine}}

The character must be recognisably the SAME one in every{{(profile.AllowsContextScenes ? " character" : "")}} scene of this video.
Every imagePrompt and every motionPrompt{{(profile.AllowsContextScenes ? " on a \"character\" scene" : "")}} must name it and restate its key
visual features from characterDescription - the picture generator has no memory
between scenes, so "the character" on its own is not enough.
{{sceneKindGuidance}}{{moodGuidance}}{{hookGuidance}}{{visualGroupGuidance}}

Universal requirements (apply to every format):
{{sceneCountLine}}
{{sceneLengthLine}}
- Write narration in ordinary sentence case. Never put a word in ALL CAPITALS
  for emphasis - the speech synthesizer reads a fully capitalised word as an
  initialism and spells it out letter by letter ("AH-CHOO" becomes "A-H-C-H-O-O").
  Convey excitement through word choice and punctuation instead.
- Entirely original - do not copy existing rhymes' lyrics.
- Each scene also gets an image prompt IN ENGLISH describing the illustration.
  Open it by naming the character and its appearance, then the setting and
  action. Keep the subject centred and clear of the top and bottom thirds so
  the same art works cropped to both 16:9 and 9:16, no text in the image.
  {{profile.ColourRule}} {{colourGuidance}}
- Each scene ALSO gets a motionPrompt IN ENGLISH describing what actually MOVES
  in that scene, because every scene is generated as a short animated clip that
  starts from a picture of this video's character. Name the character and its
  appearance first, then describe the action, not the composition: what it does,
  and any simple scene motion (leaves drifting, bubbles rising). Match the
  actions to the body the character actually has - do not have it clap if it
  has wings. Keep it to one or two sentences, keep the movement small and
  loopable. Like the image prompt, ALWAYS name an explicit colourful
  background/setting - the whole clip stays colourful start to finish, never a
  plain or empty backdrop - and always end with: "{{profile.ArtStyleSuffix}}"
  Never describe a camera cut or a change of character.
- musicMood: ONE mood tag for this video's background music, chosen EXACTLY
  from this list (do not invent your own wording): {{musicMenu}}. Pick whichever
  one best matches this format's energy.

This format's specific requirements ({{formatDef.Id}}):
{{effectiveContentRules}}

- Title <=90 chars in {{langName}}, written for search. {{profile.TitleGuidance}}
  A single ALL-CAPS word for emphasis is fine HERE - titles are not spoken -
  but nowhere else.
- Description in {{langName}}, in this order:
  line 1: {{profile.BuildDescriptionHookGuidance(formatDef.IsCalm)}}
  line 2: 7-12 hashtags on ONE line, near the TOP, not at the bottom. First the
  channel, then 3-4 specific to this video's format and topic - favour tags
  like {{hashtagExamples}} where they fit - then broad audience ones.
  then: a short paragraph naming what this video actually is and who it is
  for ({{profile.AudienceDescriptorLine}}).
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
  read instantly at a glance.
- introText: exactly three very short sentences in {{langName}}, spoken over the
  channel's branded opening bumper (plays before scene 1). First:
  {{profile.BuildIntroGreetingInstruction(profile.ChannelName)}}. Second: the character
  introducing ITSELF by name ("I'm Milo the fox!") - viewers have never met it
  before, so this is the only place they learn who they are watching. Third:
  {{introThird}}. Under 105 characters TOTAL across all three - this is a ~10
  second spoken intro and stays that short regardless of how long the rest of
  the video runs.

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
  "characterCatchphrase": "...",
  "musicMood": "...",
  "tags": ["..."],
  "scenes": [ { "narration": "...", "imagePrompt": "...", "motionPrompt": "..."{{(profile.AllowsContextScenes ? ", \"sceneKind\": \"character|context\", \"stockQuery\": \"...\"" : "")}}{{(longForm ? ", \"visualGroup\": 0" : "")}}{{(moodStyles.Count > 0 ? ", \"mood\": \"...\"" : "")}} } ]
}
""";

        var script = await CompleteAndParseAsync<VideoScript>(provider, prompt, "script",
            validate: s => ValidateContextScenes(s.Scenes, profile));

        if (script.Scenes.Count == 0) throw new Exception("Claude returned a script with no scenes.");
        if (string.IsNullOrWhiteSpace(script.Title)) throw new Exception("Claude returned a script with no title.");

        // Acts 2-4: smaller follow-up requests that continue the same story (see
        // sceneCountLine above for why Act 1 alone only asked for ~18-22 scenes). Runs
        // before every check/normalization below so those apply once, generically, over
        // the whole merged scene list exactly as they already do for a short-form script.
        if (longForm)
        {
            string[] actRoles = ["the rising complication", "the turning point", "the climax and resolution"];
            for (var act = 2; act <= 4; act++)
            {
                var scenes = await GenerateContinuationActAsync(
                    profile, formatDef, effectiveContentRules, sceneKindGuidance, colourGuidance,
                    langName, provider, script, act, actRoles[act - 2], moodStyles);
                script.Scenes.AddRange(scenes);
            }
        }

        if (string.IsNullOrWhiteSpace(script.TikTokCaption)) script.TikTokCaption = script.Description;
        if (string.IsNullOrWhiteSpace(script.ReelsCaption)) script.ReelsCaption = script.Description;
        if (string.IsNullOrWhiteSpace(script.ThumbnailPrompt)) script.ThumbnailPrompt = script.Scenes[0].ImagePrompt;
        if (string.IsNullOrWhiteSpace(script.IntroText)) script.IntroText = profile.BuildFallbackIntroText(profile.ChannelName);
        if (string.IsNullOrWhiteSpace(script.MusicMood)) script.MusicMood = formatDef.DefaultMusicMood;

        script.Format = formatDef.Id;
        script.NarrationStyle = formatDef.NarrationStyleLabel;
        script.IsLongForm = longForm;

        // Every scene sharing (or omitting) visualGroup would mean the art-resolution loop
        // reuses ONE image for the entire 70-90 scene video - catch that here rather than
        // silently shipping a video that is one static image with a soundtrack. Checked
        // before Program.cs inserts the intro scene (which is not model-generated and has
        // no visualGroup of its own), so this only judges the model's own output.
        if (longForm && script.Scenes.Select(s => s.VisualGroup).Distinct().Count() <= 1)
            throw new Exception(
                "Claude returned a long-form script where every scene shares the same " +
                "visualGroup (or omitted it) - that would reuse a single image for the whole " +
                "video. Expected roughly 12-18 distinct groups.");

        // A pooled character is not the model's to change - it describes a picture that
        // already exists - so the sidecar wins over whatever came back.
        if (character is not null)
        {
            script.CharacterName = character.Name;
            script.CharacterDescription = character.Description;
            script.CharacterCatchphrase = character.Catchphrase;
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

        // A profile that never asked for sceneKind must not end up with scenes routed to
        // StockFootageClient anyway - normalize to "character" rather than trust whatever
        // the JSON default/deserialization produced.
        foreach (var scene in script.Scenes)
            scene.SceneKind = profile.AllowsContextScenes &&
                scene.SceneKind.Equals("context", StringComparison.OrdinalIgnoreCase)
                ? "context" : "character";

        if (profile.AllowsContextScenes)
        {
            var badContextScene = script.Scenes.FirstOrDefault(
                s => s.SceneKind == "context" && string.IsNullOrWhiteSpace(s.StockQuery));
            if (badContextScene is not null)
                throw new Exception(
                    "Claude tagged a scene sceneKind: \"context\" but returned no stockQuery - " +
                    "that scene has nothing for StockFootageClient to search for.");
        }

        NormalizeSceneMoods(script.Scenes, moodStyles);

        // A scene with no motion still has to animate - falling back to the still's
        // description plus idle movement beats submitting an empty prompt to Vidu. The
        // character is named explicitly because the fallback may be all Vidu gets. Context
        // scenes are skipped: they have no ImagePrompt to fall back on and are never routed
        // through Vidu, so a filled-in MotionPrompt would just be unused data.
        foreach (var scene in script.Scenes)
            if (scene.SceneKind == "character" && string.IsNullOrWhiteSpace(scene.MotionPrompt))
                scene.MotionPrompt =
                    $"{script.CharacterName} ({script.CharacterDescription}). {scene.ImagePrompt}. " +
                    "Gentle idle motion, the character breathes and blinks. " +
                    profile.ArtStyleSuffix;

        return script;
    }

    // A continuation act (2-4) for long-form generation - a smaller follow-up request that
    // picks the story up where scriptSoFar left off, asking for only that act's scenes
    // rather than the whole 70-90 (see sceneCountLine's comment in GenerateAsync for why).
    // Reuses the same character/format/colour rules as Act 1 so quality does not drift
    // between acts, but re-asks none of the metadata (title, description, captions, etc.) -
    // Act 1 already supplied those.
    static async Task<List<Scene>> GenerateContinuationActAsync(
        ContentProfile profile, ContentFormatDef formatDef, string effectiveContentRules,
        string sceneKindGuidance, string colourGuidance, string langName, IScriptProvider provider,
        VideoScript scriptSoFar, int actNumber, string actRole, IReadOnlyList<string> moodStyles)
    {
        var nextVisualGroup = scriptSoFar.Scenes.Max(s => s.VisualGroup) + 1;

        var recap = string.Join(" ", scriptSoFar.Scenes.TakeLast(4).Select(s => s.Narration));

        var moodGuidance = moodStyles.Count > 0
            ? $"""

              Each scene also gets a mood: ONE tag chosen EXACTLY from this list (do not invent
              your own wording), matching what is actually happening in THAT scene's narration -
              {string.Join(", ", moodStyles)}. Vary it scene to scene as the
              story's emotion actually shifts; omit it entirely for a genuinely neutral scene.
              """
            : "";

        var prompt = $$"""
{{profile.AudiencePersona}}
{{profile.SafetyFraming}}

This is Act {{actNumber}} of 4 of a long-form (70-90 scene, 12-15 minute) video already in
progress. Continue the SAME story - do not restart, re-introduce the character, or repeat
any beat already covered. This act should cover: {{actRole}}.

The video's content format is {{formatDef.Id}}. Its requirements:
{{effectiveContentRules}}

This video's character: {{scriptSoFar.CharacterName}}, who looks like this:
{{scriptSoFar.CharacterDescription}}
Every imagePrompt and every motionPrompt{{(profile.AllowsContextScenes ? " on a \"character\" scene" : "")}} must name it and restate its key
visual features - the picture generator has no memory between scenes, so "the character" on
its own is not enough. Do not change the species, colours, or accessories.

What happened so far, so you can continue naturally from here (do not repeat any of this):
{{recap}}
{{sceneKindGuidance}}{{moodGuidance}}

Each scene also gets a visualGroup: a small integer naming which "visual beat" it belongs
to, continuing the numbering from the previous act - START THIS ACT'S FIRST GROUP AT
{{nextVisualGroup}} and increase by 1 for each new location, subject, or dramatic turn.
Cluster THIS ACT into roughly 3 to 5 visual groups, each typically spanning 20-40 seconds
of narration - do not give every scene its own group, and do not put the whole act in one
group.

Universal requirements (apply to every scene):
- Roughly 20 to 25 scenes for THIS ACT ONLY (Act 1 asked for fewer since it also carried
  the full title/description/character-invention response - this act and the two after it
  make up the difference to reach the full 70-90 scene target).
- Keep each scene's narration under 180 characters. It is burned into the video as an
  on-screen subtitle, and long lines do not fit the frame - break a longer beat into two
  consecutive scenes (in the same visualGroup if they share the same shot) rather than
  writing one long line.
- Write narration in {{langName}}, ordinary sentence case. Never put a word in ALL CAPITALS
  for emphasis - the speech synthesizer reads a fully capitalised word as an initialism and
  spells it out letter by letter. Convey excitement through word choice and punctuation
  instead.
- Entirely original - do not copy existing rhymes' lyrics.
- Each scene also gets an image prompt IN ENGLISH describing the illustration. Open it by
  naming the character and its appearance, then the setting and action. Keep the subject
  centred and clear of the top and bottom thirds so the same art works cropped to both
  16:9 and 9:16, no text in the image. {{profile.ColourRule}} {{colourGuidance}}
- Each scene ALSO gets a motionPrompt IN ENGLISH describing what actually MOVES in that
  scene, because every scene is generated as a short animated clip that starts from a
  picture of this video's character. Name the character and its appearance first, then
  describe the action, not the composition. Match the actions to the body the character
  actually has. Keep it to one or two sentences, keep the movement small and loopable.
  Always name an explicit colourful background/setting, and always end with:
  "{{profile.ArtStyleSuffix}}" Never describe a camera cut or a change of character.

Respond ONLY with JSON, no markdown fences:
{
  "scenes": [ { "narration": "...", "imagePrompt": "...", "motionPrompt": "..."{{(profile.AllowsContextScenes ? ", \"sceneKind\": \"character|context\", \"stockQuery\": \"...\"" : "")}}, "visualGroup": 0{{(moodStyles.Count > 0 ? ", \"mood\": \"...\"" : "")}} } ]
}
""";

        var result = await CompleteAndParseAsync<ScenesOnly>(provider, prompt, $"act {actNumber}",
            validate: r => ValidateContextScenes(r.Scenes, profile));

        if (result.Scenes.Count == 0)
            throw new Exception($"The script provider returned an act {actNumber} with no scenes.");

        return result.Scenes;
    }

    // Normalizes and validates a hand-written script (see Program.cs's --manual mode) the
    // same way GenerateAsync's own postprocessing does for a model-generated one, so a
    // manually authored script.json is indistinguishable from a generated one to every
    // later step (TTS pacing, SceneKind routing, tag list, MotionPrompt fallback, etc.).
    // formatId is required rather than weighted-random picked, since a human choosing to
    // hand-write a script has already chosen its tone. Does not touch the character pool -
    // a manual script already names a specific figure, so the caller always draws fresh.
    public static VideoScript FinalizeManualScript(ContentProfile profile, VideoScript script, string formatId, string language)
    {
        if (script.Scenes.Count == 0) throw new Exception("Manual script has no scenes.");
        if (string.IsNullOrWhiteSpace(script.Title)) throw new Exception("Manual script has no title.");
        if (string.IsNullOrWhiteSpace(script.CharacterName) || string.IsNullOrWhiteSpace(script.CharacterDescription))
            throw new Exception(
                "Manual script needs characterName and characterDescription - the description " +
                "is what the reference frame gets drawn from.");

        var formatDef = PickFormat(profile, formatId);

        if (string.IsNullOrWhiteSpace(script.TikTokCaption)) script.TikTokCaption = script.Description;
        if (string.IsNullOrWhiteSpace(script.ReelsCaption)) script.ReelsCaption = script.Description;
        if (string.IsNullOrWhiteSpace(script.ThumbnailPrompt)) script.ThumbnailPrompt = script.Scenes[0].ImagePrompt;
        if (string.IsNullOrWhiteSpace(script.IntroText)) script.IntroText = profile.BuildFallbackIntroText(profile.ChannelName);
        if (string.IsNullOrWhiteSpace(script.MusicMood)) script.MusicMood = formatDef.DefaultMusicMood;

        script.Format = formatDef.Id;
        script.NarrationStyle = formatDef.NarrationStyleLabel;
        script.IsLongForm = false;

        script.Tags = profile.BaseTags.Concat(script.Tags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        foreach (var scene in script.Scenes)
            scene.SceneKind = profile.AllowsContextScenes &&
                scene.SceneKind.Equals("context", StringComparison.OrdinalIgnoreCase)
                ? "context" : "character";

        if (profile.AllowsContextScenes)
        {
            var badContextScene = script.Scenes.FirstOrDefault(
                s => s.SceneKind == "context" && string.IsNullOrWhiteSpace(s.StockQuery));
            if (badContextScene is not null)
                throw new Exception(
                    $"Scene \"{badContextScene.Narration}\" is sceneKind \"context\" but has no stockQuery.");
        }

        NormalizeSceneMoods(script.Scenes, profile.NarrationMoodStyles.GetValueOrDefault(language, []));

        foreach (var scene in script.Scenes)
            if (scene.SceneKind == "character" && string.IsNullOrWhiteSpace(scene.MotionPrompt))
                scene.MotionPrompt =
                    $"{script.CharacterName} ({script.CharacterDescription}). {scene.ImagePrompt}. " +
                    "Gentle idle motion, the character breathes and blinks. " +
                    profile.ArtStyleSuffix;

        return script;
    }

    // Open-weight models (Groq's free-tier catalog especially - see GroqScriptProvider.cs)
    // don't honor "respond only with valid JSON" as reliably as Claude, and long-form
    // chunking means up to 4 of these calls happen per script instead of 1, proportionally
    // raising the odds any single script hits a malformed response. Re-asking the exact
    // same prompt is a legitimate fix here (not a band-aid): the failure is a one-off
    // sampling glitch, not a deterministic problem with the prompt, so a fresh completion
    // is genuinely likely to come back clean.
    // validate is optional and covers the same class of problem as a JSON parse failure -
    // the model omitting a field its instructions required (e.g. a "context" scene with no
    // stockQuery, see ValidateContextScenes below). It should throw to signal a bad
    // response; that throw is treated exactly like a parse failure and triggers a retry.
    static async Task<T> CompleteAndParseAsync<T>(
        IScriptProvider provider, string prompt, string label, Action<T>? validate = null, int maxAttempts = 3)
        where T : class
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var text = await provider.CompleteAsync(prompt);
            try
            {
                var result = JsonSerializer.Deserialize<T>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new JsonException("response body was \"null\"");
                validate?.Invoke(result);
                return result;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                if (attempt == maxAttempts)
                    throw new Exception(
                        $"The script provider returned no usable {label} JSON after " +
                        $"{maxAttempts} attempts: {ex.Message}");
                Console.WriteLine($"  [{label}] {ex.Message} (attempt {attempt}/{maxAttempts}) - retrying");
            }
        }

        throw new Exception("unreachable");
    }

    // A "context" scene with no stockQuery has nothing for StockFootageClient to search
    // for (see Scene.StockQuery) - thrown as InvalidDataException, not Exception, so
    // CompleteAndParseAsync's catch can tell this apart from an unrelated downstream error
    // and retry it the same way it retries malformed JSON.
    static void ValidateContextScenes(IEnumerable<Scene> scenes, ContentProfile profile)
    {
        if (!profile.AllowsContextScenes) return;
        var bad = scenes.FirstOrDefault(
            s => s.SceneKind.Equals("context", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(s.StockQuery));
        if (bad is not null)
            throw new InvalidDataException(
                "a scene was tagged sceneKind: \"context\" but returned no stockQuery");
    }

    // A mood tag outside the profile's own menu (a hallucination, a typo, capitalization
    // drift) must never reach AzureTtsProvider - an unverified express-as style value fails
    // that scene's whole TTS call. Nulled rather than thrown/retried: an occasional off-menu
    // tag is not worth burning a script-generation attempt over, it just falls back to the
    // same neutral read every scene had before this feature existed.
    static void NormalizeSceneMoods(IEnumerable<Scene> scenes, IReadOnlyList<string> moodStyles)
    {
        foreach (var scene in scenes)
            if (scene.Mood is not null &&
                !moodStyles.Any(m => m.Equals(scene.Mood, StringComparison.OrdinalIgnoreCase)))
                scene.Mood = null;
    }
}
