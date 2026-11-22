using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    // Curiosity brief (Rishi 2026-11-21): a 10-20s teaser that plays BEFORE the
    // intro sting - it creates curiosity and makes viewers hold and watch the
    // full video (which tells the full story). 2 short teaser scenes whose
    // narration teases the most intriguing angle WITHOUT resolving it, over
    // the video's most dramatic images (reused from the main scenes, so no
    // new art is generated). Empty for scripts written before this feature
    // (video #1/#2) and for profiles that don't use a brief; the assembler
    // renders no brief when the list is empty. Applies from video #3 onward.
    public List<Scene> BriefScenes { get; set; } = [];

    // The brief's teaser narration as written by the model: exactly 2 short
    // punchy sentences (10-20s spoken) that tease the story's most intriguing
    // angle without resolving it. BriefScenes is built from this text (one
    // sentence per brief scene) during --prep, reusing the main scenes' most
    // dramatic images. Empty for scripts written before this feature.
    public string Brief { get; set; } = "";

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

    // The topic category this video was written as (TopicCategoryStyles) - e.g.
    // "finance", "gaming", "politics", "general". Drives the video's style (tone,
    // palette, pacing, visual emphasis) and is persisted so --assemble/--retts
    // render with the same category pacing --prep used, without re-classifying.
    // Only meaningful for profiles with CategoryStyles (Trendline Now today);
    // empty for every other profile.
    public string TopicCategory { get; set; } = "";

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

    // Free hook/SEO + retention fields (2026-10-25, priorities 2 and 8).
    // Scored after generation (see SeoScore) and stored so analytics can
    // correlate them with views. CommentPrompt is the end-of-video question
    // that drives comments; TitleVariants are kept for a later title swap
    // if CTR is low. Chapters are built at assemble time from VisualGroups
    // for long-form, not stored here.
    public int HookScore { get; set; }
    public int SeoScoreValue { get; set; }
    public List<string> TitleVariants { get; set; } = [];
    public string CommentPrompt { get; set; } = "";

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

// Resume state for a --prep that died mid-script-generation: the partial
// script (acts already generated, reused verbatim - never regenerated) plus
// how many acts are done. Passed into GenerateAsync so a re-run continues
// from the next act instead of restarting at act 1. The total act count is
// derived from the profile at the call site (news long-form = 3 acts, mascot
// long-form = 4), not stored here, because older checkpoints recorded the
// wrong total for the news path (SavePartialScriptAsync hardcoded 4).
sealed record ScriptResume(VideoScript Script, int CompletedActs);

class Scene
{
    public string Narration { get; set; } = "";
    public string ImagePrompt { get; set; } = "";
    public string? AudioPath { get; set; }
    public string? ImagePath { get; set; }
    public string? VerticalImagePath { get; set; }
    // Dual-orientation art (2026-10-25): ImagePath is the portrait 1080x1920 asset
    // for the vertical render. LandscapeImagePath / LandscapeClipPath are the
    // 1920x1080 counterparts for the YouTube landscape master, when present.
    // Renderers must prefer the matching orientation and only fall back to the
    // portrait asset with a blurred-background fill when no landscape asset
    // exists, so old jobs still render.
    public string? LandscapeImagePath { get; set; }
    public string? LandscapeClipPath { get; set; }
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

    // Speculation flag (Trendline Now only, legal/spice rules Rishi 2026-11-21):
    // true when this scene is a theory, rumor, or "what if" beat rather than a
    // reported fact. The model sets it (the news prompt requires it);
    // NormalizeNewsScenes also sets it as a safety net when the narration
    // carries an explicit speculation marker the model forgot to flag.
    // VideoAssembler passes it through to Remotion, which renders a visible
    // SPECULATION label on the scene; a speculative scene is never a "visual"
    // scene (see ValidateNewsScenes). Defaults false, so old scripts without
    // the field load as all-factual, exactly as they were written.
    public bool IsSpeculative { get; set; } = false;

    // Short English stock-footage/photo search phrase (2-6 words, e.g. "ancient greek ruins
    // fog") - only populated, and only meaningful, for SceneKind == "context". Consumed by
    // StockFootageClient.DownloadBestMatchAsync as the query; ImagePrompt/MotionPrompt are
    // not used for a context scene since nothing generates or animates art for it.
    public string? StockQuery { get; set; }

    // Data-visual spec for a SceneKind == "visual" scene (Trendline Now only):
    // a graph, a table, or a big on-screen text callout, drawn by the Remotion
    // VisualScene component from this data - never generated as an image, so the
    // numbers stay sharp and exactly as the script states them. Null for every
    // other scene kind. Validated by ScriptGenerator.ValidateVisualSpec: a "line"
    // or "bar" chart needs labels + values of equal length; a "table" needs
    // headers + rows; a "callout" needs text. The numbers must come from the
    // news/trend facts supplied to the script prompt - never invented (see the
    // data-honesty rule in TrendlineNowProfile).
    public VisualSpec? VisualSpec { get; set; }

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

// One data visual for a "visual" scene (Trendline Now only) - a graph, a table,
// or a big on-screen text callout. The model emits this as a JSON object inside
// the scene; VideoAssembler passes it through to the Remotion VisualScene
// component, which draws it natively (SVG/divs), so the numbers render sharp.
// Kind decides which fields matter (see ScriptGenerator.ValidateVisualSpec):
//   "line"/"bar" - Labels + Values (same length), optional Unit, Title, Source.
//   "table"      - Headers + Rows (every row as many cells as Headers), Title, Source.
//   "callout"    - Text (the big line), optional Subtext (attribution/context).
// Title/Source are display strings (the chart's heading and its "Source: ..."
// footnote). Accent is an optional hex colour for the chart/table accent, filled
// in from the video's topic-category style when the model leaves it out.
sealed record VisualSpec(
    string Kind,
    string? Title = null,
    string[]? Labels = null,
    double[]? Values = null,
    string? Unit = null,
    string[]? Headers = null,
    string[][]? Rows = null,
    string? Text = null,
    string? Subtext = null,
    string? Source = null,
    string? Accent = null);

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
        bool longForm = false, IReadOnlyList<string>? avoidTopics = null,
        Func<VideoScript, int, Task>? onActComplete = null,
        string? topicCategory = null, IReadOnlyList<TrendResearch.NewsItem>? newsItems = null,
        ScriptResume? resumeFrom = null)
    {
        // Faceless profiles (Trendline Now) never invent a character - the news
        // explainer path builds its own prompt (statement-driven beats,
        // topic-category style, hook-first writing, art/context/visual scenes).
        // The character-anchored flow below is only for profiles with a mascot.
        if (!profile.UsesCharacterMascot)
            return await GenerateNewsAsync(profile, topic, language, provider, trendContext,
                topicCategory, newsItems, formatOverride, longForm, avoidTopics, onActComplete, resumeFrom);

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
- Retention (free, no extra tool): scene 1 must be a hook, not a setup — a
  question, a surprising claim, or action already underway. Never open with
  "Long ago", "Welcome to", or "In this video". Every 2-3 scenes, change the
  beat: a new visual, a question, a twist, or a direct address to the viewer.
  End with a comment question that invites a reply, and for short-form end
  the final line so it echoes the opening line (a loop replays better).
  For long-form, the opening scenes must also work as a self-contained teaser.
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

        // Resume: a partial checkpoint's acts are reused verbatim - Act 1 (and
        // any continuation acts already saved) are never regenerated, so a
        // re-run continues from the next act after the last checkpoint.
        VideoScript script;
        if (resumeFrom is not null)
        {
            script = resumeFrom.Script;
            Console.WriteLine(
                $"  Resuming script from act {resumeFrom.CompletedActs} checkpoint " +
                $"({script.Scenes.Count} scenes kept) - acts 1-{resumeFrom.CompletedActs} reused verbatim.");
        }
        else
        {
            Console.WriteLine($"  [{DateTime.UtcNow:HH:mm:ss}Z] Generating act 1/4 ...");
            script = await CompleteAndParseAsync<VideoScript>(provider, prompt, "script",
                validate: s => ValidateScenes(s.Scenes, profile));

            if (script.Scenes.Count == 0) throw new Exception("Claude returned a script with no scenes.");
            if (string.IsNullOrWhiteSpace(script.Title)) throw new Exception("Claude returned a script with no title.");
        }

        // Run-level fields, set here (not only at the end) so the per-act
        // checkpoint carries them and a resume can re-derive format, language
        // and long-form shape from the partial alone.
        script.Format = formatDef.Id;
        script.IsLongForm = longForm;
        script.Language = language;
        script.Profile = profile.Id;

        // Checkpoint: a long-form script is built act by act, and a failure in a
        // later act (e.g. Gemini 402 at Act 3 on 2026-11-12) used to lose the acts
        // already paid for, because nothing was saved until all four acts finished.
        // Save after each act so the completed acts survive on disk. Short-form
        // scripts have no acts, so the callback never fires for them. A resume
        // never re-saves an act it did not generate.
        if (longForm && onActComplete is not null && resumeFrom is null)
            await onActComplete(script, 1);

        // Acts 2-4: smaller follow-up requests that continue the same story (see
        // sceneCountLine above for why Act 1 alone only asked for ~18-22 scenes). Runs
        // before every check/normalization below so those apply once, generically, over
        // the whole merged scene list exactly as they already do for a short-form script.
        if (longForm)
        {
            var startAct = resumeFrom is null ? 2 : resumeFrom.CompletedActs + 1;
            string[] actRoles = ["the rising complication", "the turning point", "the climax and resolution"];
            for (var act = startAct; act <= 4; act++)
            {
                Console.WriteLine($"  [{DateTime.UtcNow:HH:mm:ss}Z] Generating act {act}/4 ({actRoles[act - 2]}) ...");
                var scenes = await GenerateContinuationActAsync(
                    profile, formatDef, effectiveContentRules, sceneKindGuidance, colourGuidance,
                    langName, provider, script, act, actRoles[act - 2], moodStyles);
                script.Scenes.AddRange(scenes);
                if (onActComplete is not null)
                    await onActComplete(script, act);
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

        // Free hook/SEO scoring (priority 2) + retention defaults (priority 8).
        // Scores are logged by the caller; a low hook score is a warning, not
        // a failure, because a heuristic cannot judge a story — but a video
        // whose opening line starts "Long ago..." should not ship silently.
        script.SeoScoreValue = GiggleGarden.Shared.SeoScore.ScoreTitle(script.Title);
        script.HookScore = script.Scenes.Count > 0
            ? GiggleGarden.Shared.SeoScore.ScoreHook(script.Scenes[0].Narration) : 0;
        script.TitleVariants = GiggleGarden.Shared.SeoScore.TitleVariants(script.Title);
        if (string.IsNullOrWhiteSpace(script.CommentPrompt) && script.Scenes.Count > 0)
            script.CommentPrompt = $"Which part of {script.CharacterName}'s story surprised you most? Tell us in the comments.";

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

        // Feed the FULL script so far - every prior scene's narration, numbered, in
        // order - so the model has the whole story for continuity, not just the tail
        // of the previous act. Narration is the story; image/motion prompts are left
        // out to keep the prompt lean.
        var recap = string.Join("\n", scriptSoFar.Scenes.Select((s, i) => $"{i + 1}. {s.Narration}"));

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

The full story so far (every scene, in order) - continue naturally from the last
scene; do not repeat any beat already covered:
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
            validate: r => ValidateScenes(r.Scenes, profile));

        if (result.Scenes.Count == 0)
            throw new Exception($"The script provider returned an act {actNumber} with no scenes.");

        return result.Scenes;
    }

    // The faceless news path (Trendline Now): no character invention, no reference
    // portrait, no mascot greeting. The video is a statement-driven news explainer
    // built from the supplied facts (news headlines + Wikipedia background when
    // available), styled by the resolved topic category, and written hook-first so
    // it reads like something you'd stop scrolling for. Scenes are one of three kinds:
    //   "art"     - an AI-generated editorial illustration (imagePrompt).
    //   "context" - real stock photo/video of the actual thing (stockQuery).
    //   "visual"  - a data visual drawn by Remotion from VisualSpec (graph/table/
    //               callout). Numbers only from the supplied facts, never invented.
    // Long-form is 3 acts (hook + what happened / backstory / now + why it
    // matters + what's next) for a 36-48 scene, 8-12 minute video; short-form is
    // a tight 8-scene statement-driven arc. Per-scene mood tags are never asked for (the profile's
    // NarrationMoodStyles is empty on purpose) - the energy comes from the script
    // text, not express-as styles.
    static async Task<VideoScript> GenerateNewsAsync(
        ContentProfile profile, string? topic, string language, IScriptProvider provider,
        string? trendContext, string? topicCategory,
        IReadOnlyList<TrendResearch.NewsItem>? newsItems,
        string? formatOverride, bool longForm, IReadOnlyList<string>? avoidTopics,
        Func<VideoScript, int, Task>? onActComplete, ScriptResume? resumeFrom = null)
    {
        var formatDef = PickFormat(profile, formatOverride);
        var (contentRules, colourGuidance, thumbnailGuidance, introThird, musicMenu, hashtagExamples, longFormContentRules) =
            formatDef.Instructions;
        var effectiveContentRules = longForm ? (longFormContentRules ?? contentRules) : contentRules;

        var langName = Text.LanguageName(language);
        var category = string.IsNullOrWhiteSpace(topicCategory) ? "general" : topicCategory;
        var style = TopicCategoryStyles.Get(category);

        var avoidTopicsLine = topic is null && avoidTopics is { Count: > 0 }
            ? $" Already covered by earlier videos - do not repeat any of these topics/titles, pick something distinct: {string.Join("; ", avoidTopics)}."
            : "";

        // Facts block: structured news items when available (headline + outlet +
        // link + time), else the trend-context text. This is the ONLY source of
        // facts - the SafetyFraming forbids inventing beyond it. Descriptions are
        // truncated hard (2026-11-21): Google News RSS descriptions run ~2,400
        // chars each, and 8 of them made the facts block ~5,200 tokens - enough
        // with the completion floor to blow Groq's 8,000 TPM budget on long-form
        // Act 1 (HTTP 413). 300 chars keeps the useful snippet and bounds the
        // block to ~750 tokens. Headlines carry the facts; descriptions are
        // supporting detail, and the provenance check only trusts what is here.
        var factsBlock = newsItems is { Count: > 0 }
            ? "Today's top global news stories (headline, outlet, link, publish time - use ONLY these facts, plus the Wikipedia background below when present):\n" +
              string.Join("\n", newsItems.Take(8).Select(n =>
                  $"- \"{n.Title}\" ({n.Source}, {n.PublishedAt:yyyy-MM-dd HH:mm} UTC) {n.Link}" +
                  (string.IsNullOrWhiteSpace(n.Description) ? "" : $" - {TruncateFacts(n.Description, 300)}")))
              + WikipediaBackgroundBlock(trendContext)
            : (trendContext ?? "");

        var topicLine = topic is not null
            ? $"Topic: {topic}\nFacts for this topic:\n{factsBlock}"
            : $"Cover the top story in the list below - the single biggest trending story in the world today.{avoidTopicsLine}\n{factsBlock}";

        var categoryBlock = $"""
            This video's topic category is {style.Label} ({style.Id}). Its style rules - follow
            them for the script tone, the image prompts, and which visual scenes you request.
            A {style.Label} video must not read, sound, or look like a video from another category:
            - Tone: {style.ToneGuidance}
            - Palette: {style.ColourPalette}
            - Art direction: {style.ArtStyleGuidance}
            - Visual emphasis: {style.VisualEmphasis}
            """;

        var sceneCountLine = longForm
            ? """
              - Roughly 10 to 14 scenes for THIS ACT ONLY (Act 1 of 3 - the hook and what
                happened). This act also carries the full title/description response below,
                so it is kept shorter than the acts that follow - those ask for more scenes
                each (see GenerateNewsContinuationActAsync) to reach a full 36-48 scene,
                8-12 minute video narrated at a natural pace (~140 words per minute). Do
                not try to tell the whole story here - hook the viewer, establish what
                happened and the stakes, and leave the backstory and the resolution for
                the acts that follow.
              """
            : """
              - Exactly 8 scenes. Each scene: 1-2 short sentences a narrator reads aloud.
                8 scenes plus the intro is sized to land the finished video just inside
                the 90-second limit Reels enforces.
              """;

        var sceneLengthLine = longForm
            ? """
              - Keep each scene's narration under 180 characters, and aim for 130-170
                characters per scene - fuller scenes, not one-liners. A long-form video
                needs roughly 6,500+ characters of narration to reach 8 minutes at a
                natural pace (~140 words per minute); short scenes make the video land
                under the 8-minute mid-roll bar. It is burned into the video as an
                on-screen subtitle, and long lines do not fit the frame - break a longer
                beat into two consecutive scenes (in the same visualGroup if they share
                the same shot) rather than writing one long line.
              """
            : """
              - Keep each scene's narration under 115 characters (a hard ceiling). It is
                burned into the video as an on-screen subtitle, and long lines do not fit
                the frame.
              """;

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
              number by 1 (groups do not need to be contiguous per sceneKind - a "visual"
              and an "art" scene can share a group if they're the same beat). Cluster
              THIS ACT into roughly 3 to 5 visual groups, starting at 1, each typically
              spanning 20-40 seconds of narration (a handful of consecutive scenes) - do not
              give every scene its own group, and do not put the whole act in one group.
              """
            : "";

        var prompt = $$"""
{{profile.AudiencePersona}}
{{topicLine}}
Language for ALL narration: {{langName}}.
{{profile.SafetyFraming}}

{{categoryBlock}}

This video's content format is {{formatDef.Id}}. Its requirements are given below and
DIVERGE from other formats on purpose - follow THIS format's rules, not a generic
"news video" template.
{{hookGuidance}}{{visualGroupGuidance}}

Universal requirements (apply to every format):
{{sceneCountLine}}
{{sceneLengthLine}}
- Retention (free, no extra tool): scene 1 must be the hook - the most surprising
  true angle, an exclamation ("What happened!"), a bold claim, or action already
  underway. Never open with "Long ago", "Welcome to", "Today in the news", or
  "In this video". Every 2-3 scenes, change the beat: a new visual, a twist, an
  exclamation, or a direct address to the viewer. End each act on a cliffhanger line
  that pulls the viewer into the next act. End the video with a comment question that
  invites a reply (that is one of the at most 1-2 questions in the whole video), and
  for short-form end the final line so it echoes the opening line (a loop replays
  better). For long-form, the opening scenes must also work as a self-contained teaser.
- Write narration in ordinary sentence case. Never put a word in ALL CAPITALS for
  emphasis - the speech synthesizer reads a fully capitalised word as an initialism and
  spells it out letter by letter ("AH-CHOO" becomes "A-H-C-H-O-O"). Convey excitement
  through word choice and punctuation instead.
- Entirely original - do not copy existing text.
- Each "art" scene gets an image prompt IN ENGLISH describing the illustration.
  Open it with the setting and action, keep the subject centred and clear of the top
  and bottom thirds so the same art works cropped to both 16:9 and 9:16, no text in
  the image. {{profile.ColourRule}} {{colourGuidance}}
- musicMood: ONE mood tag for this video's background music, chosen EXACTLY from this
  list (do not invent your own wording): {{musicMenu}}. Pick whichever one best matches
  this video's energy.

This format's specific requirements ({{formatDef.Id}}):
{{effectiveContentRules}}

- Title <=90 chars in {{langName}}, written for search. {{profile.TitleGuidance}}
  A single ALL-CAPS word for emphasis is fine HERE - titles are not spoken -
  but nowhere else.
- Description in {{langName}}, in this order:
  line 1: {{profile.BuildDescriptionHookGuidance(formatDef.IsCalm)}}
  line 2: 7-12 hashtags on ONE line, near the TOP, not at the bottom. First the
  channel, then 3-4 specific to this video's topic - favour tags like
  {{hashtagExamples}} where they fit - then broad audience ones.
  then: a short paragraph naming what this video actually is and who it is
  for ({{profile.AudienceDescriptorLine}}).
- tags: 5-10 topical tags for THIS video only, mixing {{langName}} and English -
  the story, the people/places involved, the category. Do not include generic channel
  or audience tags; those are added automatically.
- tiktokCaption: casual, front-loaded hook in {{langName}}, under 150
  characters, matching this video's energy, ending with 3-5 hashtags. Do NOT
  include #Shorts.
- reelsCaption: for Instagram/Facebook Reels in {{langName}}, under 300
  characters - the FIRST LINE must work standalone since feeds truncate
  after 1-2 lines - ending with 5-10 hashtags (mix broad + specific). Do NOT
  include #Shorts.
- thumbnailPrompt IN ENGLISH: one bold illustration concept for a YouTube
  thumbnail (documentary text only - nothing currently renders a separate
  image from it, but keep it consistent with the rest of this script.json).
  {{thumbnailGuidance}} It does not need to depict a specific scene; it needs to
  read instantly at a glance.
- introText: the video's opening hook, 1-2 very short sentences in {{langName}},
  spoken over the channel's branded title card (plays before scene 1). First:
  {{profile.BuildIntroGreetingInstruction(profile.ChannelName)}}. Under 180 characters
  TOTAL across the hook - this is a ~10 second spoken intro.
- brief: the 10-20s curiosity teaser that plays BEFORE the intro sting (Rishi
  2026-11-21) - exactly 2 short punchy sentences in {{langName}} that tease the
  story's single most intriguing angle and make the viewer NEED to watch the full
  video. Tease WITHOUT resolving: name the shocking fact or impossible claim and
  the stakes, but do NOT give the answer, the explanation, or the outcome - the
  full story comes after the intro. 30-55 words total (10-20s spoken at a brisk
  pace). It is spoken over the video's most dramatic images, so it must read as
  a teaser, not a summary. For a long-form video this is Act 1's brief; the
  continuation acts do not repeat it.

Respond ONLY with JSON, no markdown fences:
{
  "title": "...",
  "description": "...",
  "tiktokCaption": "...",
  "reelsCaption": "...",
  "thumbnailPrompt": "...",
  "introText": "...",
  "brief": "...",
  "topicCategory": "{{category}}",
  "musicMood": "...",
  "tags": ["..."],
  "scenes": [ { "narration": "...", "imagePrompt": "...", "sceneKind": "art|context|visual", "stockQuery": "...", "visualSpec": {"kind": "line|bar|table|callout", "title": "...", "labels": ["..."], "values": [0], "unit": "...", "headers": ["..."], "rows": [["..."]], "text": "...", "subtext": "...", "source": "..."} , "isSpeculative": false }{{(longForm ? ", \"visualGroup\": 0" : "")}} ]
}
""";

        // Resume: a partial checkpoint's acts are reused verbatim - Act 1 (and
        // any continuation act already saved) is never regenerated, so a
        // re-run continues from the next act after the last checkpoint.
        VideoScript script;
        if (resumeFrom is not null)
        {
            script = resumeFrom.Script;
            Console.WriteLine(
                $"  Resuming news script from act {resumeFrom.CompletedActs} checkpoint " +
                $"({script.Scenes.Count} scenes kept) - acts 1-{resumeFrom.CompletedActs} reused verbatim.");
        }
        else
        {
            Console.WriteLine($"  [{DateTime.UtcNow:HH:mm:ss}Z] Generating news act 1/3 ...");
            script = await CompleteAndParseAsync<VideoScript>(provider, prompt, "news script",
                validate: s => ValidateNewsScenes(s.Scenes, factsBlock));

            if (script.Scenes.Count == 0) throw new Exception("The script provider returned a news script with no scenes.");
            if (string.IsNullOrWhiteSpace(script.Title)) throw new Exception("The script provider returned a news script with no title.");
        }

        // Run-level fields, set here (not only at the end) so the per-act
        // checkpoint carries them and a resume can re-derive format, language
        // and long-form shape from the partial alone.
        script.Format = formatDef.Id;
        script.IsLongForm = longForm;
        script.TopicCategory = category;
        script.Language = language;
        script.Profile = profile.Id;

        if (longForm && onActComplete is not null && resumeFrom is null)
            await onActComplete(script, 1);

        // Acts 2-3: smaller follow-up requests that continue the same story (see
        // sceneCountLine above for why Act 1 alone only asked for ~10-14 scenes).
        // 3 acts total for an 8-12 minute video, not 4 - the news arc is tighter
        // than a mythology documentary's.
        if (longForm)
        {
            var startAct = resumeFrom is null ? 2 : resumeFrom.CompletedActs + 1;
            string[] actRoles =
            [
                "the backstory and the context that explains how we got here",
                "what's happening now, why it matters, and what happens next",
            ];
            for (var act = startAct; act <= 3; act++)
            {
                Console.WriteLine($"  [{DateTime.UtcNow:HH:mm:ss}Z] Generating news act {act}/3 ({actRoles[act - 2]}) ...");
                var scenes = await GenerateNewsContinuationActAsync(
                    profile, formatDef, effectiveContentRules, style,
                    langName, provider, script, act, actRoles[act - 2], factsBlock);
                script.Scenes.AddRange(scenes);
                if (onActComplete is not null)
                    await onActComplete(script, act);
            }
        }

        NormalizeNewsScenes(script.Scenes, style);

        if (string.IsNullOrWhiteSpace(script.TikTokCaption)) script.TikTokCaption = script.Description;
        if (string.IsNullOrWhiteSpace(script.ReelsCaption)) script.ReelsCaption = script.Description;
        if (string.IsNullOrWhiteSpace(script.ThumbnailPrompt))
            script.ThumbnailPrompt = script.Scenes.FirstOrDefault(s => s.SceneKind == "art" && !string.IsNullOrWhiteSpace(s.ImagePrompt))?.ImagePrompt
                ?? script.Title;
        if (string.IsNullOrWhiteSpace(script.IntroText)) script.IntroText = profile.BuildFallbackIntroText(profile.ChannelName);
        if (string.IsNullOrWhiteSpace(script.MusicMood)) script.MusicMood = formatDef.DefaultMusicMood;

        script.Format = formatDef.Id;
        script.NarrationStyle = formatDef.NarrationStyleLabel;
        script.IsLongForm = longForm;
        script.TopicCategory = category;

        // Every scene sharing (or omitting) visualGroup would mean the art-resolution loop
        // reuses ONE image for the entire video - catch that here rather than silently
        // shipping a video that is one static image with a soundtrack. Checked before
        // Program.cs inserts the intro scene (which is not model-generated and has no
        // visualGroup of its own), so this only judges the model's own output.
        if (longForm && script.Scenes.Select(s => s.VisualGroup).Distinct().Count() <= 1)
            throw new Exception(
                "The script provider returned a long-form news script where every scene shares " +
                "the same visualGroup (or omitted it) - that would reuse a single image for the " +
                "whole video. Expected roughly 9-15 distinct groups.");

        // Fixed brand/audience block first, then whatever is specific to this video,
        // de-duplicated in case the model volunteered one of the fixed ones anyway.
        script.Tags = profile.BaseTags.Concat(script.Tags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        script.SeoScoreValue = GiggleGarden.Shared.SeoScore.ScoreTitle(script.Title);
        script.HookScore = script.Scenes.Count > 0
            ? GiggleGarden.Shared.SeoScore.ScoreHook(script.Scenes[0].Narration) : 0;
        script.TitleVariants = GiggleGarden.Shared.SeoScore.TitleVariants(script.Title);
        if (string.IsNullOrWhiteSpace(script.CommentPrompt))
            script.CommentPrompt = "What do you think happens next? Tell us in the comments.";

        return script;
    }

    // A continuation act (2-3) for a long-form news video - mirrors
    // GenerateContinuationActAsync but faceless: no character, no moods, and the
    // category style block + visual-scene rules carry over so the tone and the
    // data visuals do not drift between acts. Ends each act on a cliffhanger.
    static async Task<List<Scene>> GenerateNewsContinuationActAsync(
        ContentProfile profile, ContentFormatDef formatDef, string effectiveContentRules,
        TopicStyleProfile style, string langName, IScriptProvider provider,
        VideoScript scriptSoFar, int actNumber, string actRole, string? sourceFacts = null)
    {
        var nextVisualGroup = scriptSoFar.Scenes.Max(s => s.VisualGroup) + 1;

        // Feed the FULL script so far - every prior scene's narration, numbered, in
        // order - so the model has the whole story for continuity, not just the tail
        // of the previous act. Narration is the story; image prompts are left out to
        // keep the prompt lean.
        var recap = string.Join("\n", scriptSoFar.Scenes.Select((s, i) => $"{i + 1}. {s.Narration}"));

        // A later act has no facts block of its own in its prompt - only the recap
        // of what was already narrated - so its visuals are checked against the Act 1
        // facts PLUS the recap: a number already spoken in an earlier act may be
        // shown on screen, but no NEW number may appear in a chart or callout.
        var actSourceFacts = string.IsNullOrWhiteSpace(sourceFacts)
            ? null : sourceFacts + "\n" + recap;

        var sceneCountLine = actNumber == 2
            ? "Roughly 14 to 18 scenes for THIS ACT ONLY."
            : "Roughly 12 to 16 scenes for THIS ACT ONLY.";

        var prompt = $$"""
{{profile.AudiencePersona}}
{{profile.SafetyFraming}}

This is Act {{actNumber}} of 3 of a long-form (36-48 scene, 8-12 minute) news video
already in progress. Continue the SAME story - do not restart, re-introduce the
topic, or repeat any beat already covered. This act should cover: {{actRole}}.

This video's topic category is {{style.Label}} ({{style.Id}}). Keep its style:
- Tone: {{style.ToneGuidance}}
- Palette: {{style.ColourPalette}}
- Art direction: {{style.ArtStyleGuidance}}
- Visual emphasis: {{style.VisualEmphasis}}

The video's content format is {{formatDef.Id}}. Its requirements:
{{effectiveContentRules}}

The full story so far (every scene, in order) - continue naturally from the last
scene; do not repeat any beat already covered:
{{recap}}

Each scene also gets a visualGroup: a small integer naming which "visual beat" it belongs
to, continuing the numbering from the previous act - START THIS ACT'S FIRST GROUP AT
{{nextVisualGroup}} and increase by 1 for each new location, subject, or dramatic turn.
Cluster THIS ACT into roughly 3 to 5 visual groups, each typically spanning 20-40 seconds
of narration - do not give every scene its own group, and do not put the whole act in one
group.

Universal requirements (apply to every scene):
- {{sceneCountLine}} (Act 1 asked for fewer since it also carried the full
  title/description response - this act and the one after it make up the
  difference to reach the full 36-48 scene target).
- Keep each scene's narration under 180 characters. It is burned into the video as an
  on-screen subtitle, and long lines do not fit the frame - break a longer beat into two
  consecutive scenes (in the same visualGroup if they share the same shot) rather than
  writing one long line.
- Write narration in {{langName}}, ordinary sentence case. Never put a word in ALL CAPITALS
  for emphasis - the speech synthesizer reads a fully capitalised word as an initialism and
  spells it out letter by letter. Convey excitement through word choice and punctuation
  instead.
- Entirely original - do not copy existing text.
- Each "art" scene gets an image prompt IN ENGLISH describing the illustration. Open it
  with the setting and action, keep the subject centred and clear of the top and bottom
  thirds so the same art works cropped to both 16:9 and 9:16, no text in the image.
  {{profile.ColourRule}}
- End this act on a cliffhanger line that pulls the viewer into the next act (or, for
  the final act, on the story's actual consequence and what happens next).

Respond ONLY with JSON, no markdown fences:
{
  "scenes": [ { "narration": "...", "imagePrompt": "...", "sceneKind": "art|context|visual", "stockQuery": "...", "visualSpec": {"kind": "line|bar|table|callout", "title": "...", "labels": ["..."], "values": [0], "unit": "...", "headers": ["..."], "rows": [["..."]], "text": "...", "subtext": "...", "source": "..."} , "isSpeculative": false }, "visualGroup": 0 } ]
}
""";

        var result = await CompleteAndParseAsync<ScenesOnly>(provider, prompt, $"news act {actNumber}",
            validate: r => ValidateNewsScenes(r.Scenes, actSourceFacts));

        if (result.Scenes.Count == 0)
            throw new Exception($"The script provider returned a news act {actNumber} with no scenes.");

        return result.Scenes;
    }

    // The Wikipedia background lives in TrendReport.Text (TrendResearch appends
    // it after the news list); the newsItems list alone carries no background,
    // so pull that block out and append it to the facts. Without it the model
    // only ever sees headlines - and with nothing but a headline to draw on, it
    // fills gaps with invented numbers (seen 2026-11-21: a "30% of capacity moved
    // overseas" callout for a story whose RSS items contain no number at all).
    static string WikipediaBackgroundBlock(string? trendContext)
    {
        if (string.IsNullOrWhiteSpace(trendContext)) return "";
        const string marker = "Background facts (Wikipedia)";
        var idx = trendContext.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return "";
        // Capped at ~1,200 chars: a broad article's extract can run long, and
        // the facts block already has a token budget to respect (see the
        // truncation note on factsBlock above).
        return "\n" + TruncateFacts(trendContext[idx..], 1200);
    }

    static string TruncateFacts(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";

    // Fail fast at script time, not mid-TTS/render: a model slip that leaves a scene
    // without the visual its kind needs only surfaces later as a missing image, a
    // failed stock search, or an unrenderable chart. Thrown as InvalidDataException so
    // CompleteAndParseAsync treats it exactly like a parse failure and retries it.
    // A missing or unknown sceneKind is read as "art" (the default) - so a scene
    // that forgot both its kind and its image prompt still fails here, on the
    // imagePrompt check, instead of rendering as a blank frame.
    // sourceFacts is the exact facts text the model was given (the Act 1 facts
    // block, plus the recap for continuation acts). When it is null - a
    // hand-written manual script - the provenance check below is skipped.
    static void ValidateNewsScenes(IReadOnlyList<Scene> scenes, string? sourceFacts = null)
    {
        for (var i = 0; i < scenes.Count; i++)
        {
            var scene = scenes[i];
            var label = $"scene {i + 1}";
            if (string.IsNullOrWhiteSpace(scene.Narration))
                throw new InvalidDataException(
                    $"{label} has no narration - every scene needs something for TTS to read.");
            var kind = NewsSceneKind(scene);
            if (kind == "context")
            {
                if (string.IsNullOrWhiteSpace(scene.StockQuery))
                    throw new InvalidDataException(
                        $"{label} was tagged sceneKind: \"context\" but returned no stockQuery");
            }
            else if (kind == "visual")
            {
                ValidateVisualSpec(scene.VisualSpec, label);
                // Data provenance (2026-11-21): the prompt's honesty rule alone did
                // not stop the model inventing a statistic - a "30%" callout for a
                // story with no numbers in its RSS items. Every number in a visual
                // must appear in the facts the model was actually given; a spec that
                // fails is demoted to an art scene instead of throwing, so the video
                // keeps its shape and just loses a chart it could not support.
                if (!string.IsNullOrWhiteSpace(sourceFacts) &&
                    scene.VisualSpec is { } spec &&
                    !VisualNumbersComeFromSource(spec, sourceFacts))
                {
                    Console.WriteLine(
                        $"  [script] {label}: visual demoted to art - its numbers do not appear in the source facts.");
                    scene.SceneKind = "art";
                    scene.VisualSpec = null;
                    if (string.IsNullOrWhiteSpace(scene.ImagePrompt))
                        scene.ImagePrompt =
                            $"Documentary news illustration for this beat: {scene.Narration}";
                }
                // Speculation is narration-only (legal/spice rules Rishi 2026-11-21):
                // a theory/rumor/"what if" beat never gets a chart, a table, or a
                // callout number. Same demotion as the provenance failure above.
                if (scene.IsSpeculative && scene.SceneKind == "visual")
                {
                    Console.WriteLine(
                        $"  [script] {label}: visual demoted to art - a speculative scene never gets a chart or callout.");
                    scene.SceneKind = "art";
                    scene.VisualSpec = null;
                    if (string.IsNullOrWhiteSpace(scene.ImagePrompt))
                        scene.ImagePrompt =
                            $"Documentary news illustration for this beat: {scene.Narration}";
                }
            }
            else if (string.IsNullOrWhiteSpace(scene.ImagePrompt))
            {
                throw new InvalidDataException(
                    $"{label} has no imagePrompt - an \"art\" scene needs one for its art.");
            }
        }
    }

    static readonly Regex NumberTokenRegex = new(@"\d[\d.]*%?", RegexOptions.Compiled);

    // Every number a visual shows must be traceable to the source facts text:
    // chart values (formatted back to plain numbers), and any number token in
    // labels, titles, table cells, or callout text. A token counts when it
    // appears in the source with a non-digit on both sides, so "30" does not
    // match "300" and "2024" does not match "20240". Commas are stripped on
    // both sides ("1,000" matches "1000"); a "%" is ignored on both sides, so
    // a "30%" callout matches a source that says "30" and vice versa.
    static bool VisualNumbersComeFromSource(VisualSpec spec, string sourceFacts)
    {
        var source = sourceFacts.Replace(",", "");
        var numbers = new List<string>();
        if (spec.Values is not null)
            numbers.AddRange(spec.Values.Select(v =>
                v.ToString("G", CultureInfo.InvariantCulture).Replace(",", "")));
        var texts = new[] { spec.Title, spec.Text, spec.Subtext, spec.Unit }
            .Concat(spec.Labels ?? [])
            .Concat(spec.Headers ?? [])
            .Concat((spec.Rows ?? []).SelectMany(r => r ?? []));
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            numbers.AddRange(NumberTokenRegex.Matches(text.Replace(",", ""))
                .Select(m => m.Value.TrimEnd('%')));
        }
        return numbers.All(n =>
            Regex.IsMatch(source, $@"(?<![\d.]){Regex.Escape(n)}(?![\d.])"));
    }

    // "context" and "visual" are the only kinds that change what a scene needs;
    // anything else - "art", "character", a typo, or a missing value - is an art
    // scene, which is also what NormalizeNewsScenes writes back.
    static string NewsSceneKind(Scene scene) =>
        scene.SceneKind.Equals("context", StringComparison.OrdinalIgnoreCase) ? "context"
        : scene.SceneKind.Equals("visual", StringComparison.OrdinalIgnoreCase) ? "visual"
        : "art";

    // Validates one VisualSpec the way a chart library would: the shape has to be
    // drawable, not just present. Caps keep a runaway table/chart from producing
    // an unreadable frame (12 labels, 10 rows). Throws InvalidDataException so a
    // bad spec retries exactly like malformed JSON.
    static void ValidateVisualSpec(VisualSpec? spec, string label)
    {
        if (spec is null)
            throw new InvalidDataException(
                $"{label} was tagged sceneKind: \"visual\" but returned no visualSpec");
        switch (spec.Kind)
        {
            case "line":
            case "bar":
                if (spec.Labels is not { Length: >= 2 } || spec.Labels.Length > 12)
                    throw new InvalidDataException(
                        $"{label}: a \"{spec.Kind}\" chart needs 2-12 labels, got {spec.Labels?.Length ?? 0}");
                if (spec.Values is not { Length: >= 2 } || spec.Values.Length != spec.Labels.Length)
                    throw new InvalidDataException(
                        $"{label}: a \"{spec.Kind}\" chart needs the same number of values as labels " +
                        $"({spec.Labels.Length} labels, {spec.Values?.Length ?? 0} values)");
                if (spec.Values.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                    throw new InvalidDataException(
                        $"{label}: a \"{spec.Kind}\" chart has a non-numeric value");
                break;
            case "table":
                if (spec.Headers is not { Length: >= 2 })
                    throw new InvalidDataException(
                        $"{label}: a \"table\" needs at least 2 headers");
                if (spec.Rows is not { Length: >= 1 } || spec.Rows.Length > 10)
                    throw new InvalidDataException(
                        $"{label}: a \"table\" needs 1-10 rows, got {spec.Rows?.Length ?? 0}");
                if (spec.Rows.Any(r => r is null || r.Length != spec.Headers.Length))
                    throw new InvalidDataException(
                        $"{label}: every \"table\" row needs exactly {spec.Headers.Length} cells");
                break;
            case "callout":
                if (string.IsNullOrWhiteSpace(spec.Text))
                    throw new InvalidDataException(
                        $"{label}: a \"callout\" needs text - the big on-screen line");
                break;
            default:
                throw new InvalidDataException(
                    $"{label}: unknown visualSpec kind \"{spec.Kind}\" - use line, bar, table, or callout");
        }
    }

    // Writes the canonical scene kinds back ("art"/"context"/"visual") and fills
    // the chart/table accent from the video's category style when the model left
    // it out, so the Remotion visual always has a colour to draw with. Runs after
    // validation, before the script is returned - the model's raw output is never
    // persisted un-normalized.
    static void NormalizeNewsScenes(IReadOnlyList<Scene> scenes, TopicStyleProfile style)
    {
        var accent = style.Id switch
        {
            "gaming" => "#22d3ee",
            "finance" => "#34d399",
            "politics" => "#ef4444",
            "sports" => "#f97316",
            "tech" => "#3b82f6",
            "entertainment" => "#e879f9",
            _ => "#ef4444",
        };
        foreach (var scene in scenes)
        {
            scene.SceneKind = NewsSceneKind(scene);
            if (scene.SceneKind == "visual" && scene.VisualSpec is { Accent: null } spec)
                scene.VisualSpec = spec with { Accent = accent };
            // Speculation safety net (legal/spice rules Rishi 2026-11-21): the
            // model is instructed to set IsSpeculative on theory/rumor/"what if"
            // scenes, but a missed flag would ship an unlabelled speculation.
            // Flag any scene whose narration carries an explicit speculation
            // marker. Conservative by design - these phrases are the ones the
            // prompt itself prescribes for theory beats, so flagging them cannot
            // mislabel a factual beat.
            if (!scene.IsSpeculative && NarrationMarksSpeculation(scene.Narration))
                scene.IsSpeculative = true;
        }
    }

    // The prompt's own prescribed phrasings for a theory beat - "the theory
    // going around is...", "people are saying...", "what if..." - plus the
    // plain words for rumor and for an imagined alternative. A factual beat
    // never uses these to state what happened, so matching them is safe.
    static bool NarrationMarksSpeculation(string narration)
    {
        var text = narration.ToLowerInvariant();
        return text.Contains("what if")
            || text.Contains("the theory")
            || text.Contains("people are saying")
            || text.Contains("rumor")
            || text.Contains("rumour")
            || text.Contains("imagine if")
            || text.Contains("the wildest take");
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
        // Faceless profiles (Trendline Now): a hand-written script needs no
        // character at all - validate it the news way (art/context/visual scenes)
        // and skip every character-anchored step below.
        if (!profile.UsesCharacterMascot)
        {
            if (script.Scenes.Count == 0) throw new Exception("Manual script has no scenes.");
            if (string.IsNullOrWhiteSpace(script.Title)) throw new Exception("Manual script has no title.");

            var newsFormat = PickFormat(profile, formatId);

            if (string.IsNullOrWhiteSpace(script.TikTokCaption)) script.TikTokCaption = script.Description;
            if (string.IsNullOrWhiteSpace(script.ReelsCaption)) script.ReelsCaption = script.Description;
            if (string.IsNullOrWhiteSpace(script.ThumbnailPrompt))
                script.ThumbnailPrompt = script.Scenes.FirstOrDefault(s => NewsSceneKind(s) == "art" && !string.IsNullOrWhiteSpace(s.ImagePrompt))?.ImagePrompt
                    ?? script.Title;
            if (string.IsNullOrWhiteSpace(script.IntroText)) script.IntroText = profile.BuildFallbackIntroText(profile.ChannelName);
            if (string.IsNullOrWhiteSpace(script.MusicMood)) script.MusicMood = newsFormat.DefaultMusicMood;

            script.Format = newsFormat.Id;
            script.NarrationStyle = newsFormat.NarrationStyleLabel;
            script.IsLongForm = false;
            if (string.IsNullOrWhiteSpace(script.TopicCategory)) script.TopicCategory = "general";

            script.Tags = profile.BaseTags.Concat(script.Tags)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();

            NormalizeNewsScenes(script.Scenes, TopicCategoryStyles.Get(script.TopicCategory));
            ValidateNewsScenes(script.Scenes);

            return script;
        }

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

        // Same fail-fast check as a generated script (see ValidateScenes) - a
        // hand-written scene with no narration fails at TTS time otherwise.
        ValidateScenes(script.Scenes, profile);

        NormalizeSceneMoods(script.Scenes, profile.NarrationMoodStyles.GetValueOrDefault(language, []));

        foreach (var scene in script.Scenes)
            if (scene.SceneKind == "character" && string.IsNullOrWhiteSpace(scene.MotionPrompt))
                scene.MotionPrompt =
                    $"{script.CharacterName} ({script.CharacterDescription}). {scene.ImagePrompt}. " +
                    "Gentle idle motion, the character breathes and blinks. " +
                    profile.ArtStyleSuffix;

        return script;
    }

    // Best-effort repair for a truncated provider response: the model was cut
    // off mid-scenes-array (the usual cause of "Expected depth to be zero at
    // the end of the JSON payload"), and every scene object that closed
    // cleanly is still good. Keeps the response head (all metadata fields,
    // which precede "scenes" in every prompt) plus the complete scene objects,
    // then closes the array and the root object. Returns null when nothing
    // salvageable is found (no "scenes" array, or fewer than 8 complete
    // scenes - too thin for an act). A salvaged act is shorter than asked
    // for, but a complete video with a short act beats a dead prep.
    internal static string? SalvageTruncatedJson(string text)
    {
        var t = text.Replace("```json", "", StringComparison.Ordinal)
            .Replace("```", "", StringComparison.Ordinal).Trim();
        var start = t.IndexOf('{');
        if (start < 0) return null;
        t = t[start..];
        var scenesIdx = t.IndexOf("\"scenes\"", StringComparison.Ordinal);
        if (scenesIdx < 0) return null;
        var arrStart = t.IndexOf('[', scenesIdx);
        if (arrStart < 0) return null;

        // Scan scene objects with brace matching, respecting strings and
        // escapes, and keep each object that closes cleanly.
        var completeEnds = new List<int>();
        var i = arrStart + 1;
        while (i < t.Length)
        {
            while (i < t.Length && (char.IsWhiteSpace(t[i]) || t[i] == ',')) i++;
            if (i >= t.Length || t[i] != '{') break;
            var depth = 0;
            var inStr = false;
            var esc = false;
            var j = i;
            var closed = false;
            for (; j < t.Length; j++)
            {
                var c = t[j];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                }
                else if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { j++; closed = true; break; }
                }
            }
            if (!closed) break; // incomplete object - the cut-off tail; stop here
            completeEnds.Add(j);
            i = j;
        }
        if (completeEnds.Count < 8) return null;
        return t[..completeEnds[^1]] + "]}";
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
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Console.WriteLine($"  [{label}] requesting completion (attempt {attempt}/{maxAttempts}) ...");
            var started = DateTime.UtcNow;
            var text = await provider.CompleteAsync(prompt);
            Console.WriteLine(
                $"  [{label}] provider returned {text.Length} chars in " +
                $"{(DateTime.UtcNow - started).TotalSeconds:0}s (attempt {attempt}/{maxAttempts})");
            try
            {
                var result = JsonSerializer.Deserialize<T>(text, jsonOpts)
                    ?? throw new JsonException("response body was \"null\"");
                validate?.Invoke(result);
                return result;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // A truncated response is salvageable: the model was cut off
                // mid-scenes-array, and every scene object that closed cleanly
                // is still good. Try the repair on every failed attempt, not
                // just the last - it saves a retry when the payload is
                // recoverable.
                var salvaged = SalvageTruncatedJson(text);
                if (salvaged is not null)
                {
                    try
                    {
                        var repaired = JsonSerializer.Deserialize<T>(salvaged, jsonOpts)
                            ?? throw new JsonException("salvaged body was \"null\"");
                        validate?.Invoke(repaired);
                        var scenes = repaired switch
                        {
                            VideoScript vs => vs.Scenes.Count,
                            ScenesOnly so => so.Scenes.Count,
                            _ => -1,
                        };
                        Console.WriteLine(
                            $"  [{label}] WARNING: salvaged a truncated provider response - " +
                            $"kept {scenes} complete scenes, dropped the cut-off tail (attempt {attempt}/{maxAttempts})");
                        return repaired;
                    }
                    catch (Exception rex) when (rex is JsonException or InvalidDataException)
                    {
                        Console.WriteLine($"  [{label}] salvage failed ({rex.Message}) - retrying");
                    }
                }
                if (attempt == maxAttempts)
                {
                    // Last resort: one extra call with a stricter instruction
                    // and a shorter ask, so the response is small enough to
                    // complete inside the provider's token budget.
                    var strictPrompt = prompt +
                        "\n\nSTRICT: Your previous reply was not valid JSON. Reply with ONLY " +
                        "a valid JSON object - no markdown fences, no commentary, no other " +
                        "text. Keep the scenes list short (at most 10 scenes) so the " +
                        "response completes.";
                    Console.WriteLine(
                        $"  [{label}] final attempt failed ({ex.Message}) - one strict retry with a shorter ask");
                    try
                    {
                        var strictText = await provider.CompleteAsync(strictPrompt);
                        var strictResult = JsonSerializer.Deserialize<T>(strictText, jsonOpts)
                            ?? throw new JsonException("response body was \"null\"");
                        validate?.Invoke(strictResult);
                        Console.WriteLine($"  [{label}] strict retry returned usable JSON");
                        return strictResult;
                    }
                    catch (Exception sex)
                    {
                        throw new Exception(
                            $"The script provider returned no usable {label} JSON after " +
                            $"{maxAttempts} attempts plus a strict retry: {ex.Message} (strict retry: {sex.Message})");
                    }
                }
                Console.WriteLine($"  [{label}] {ex.Message} (attempt {attempt}/{maxAttempts}) - retrying");
            }
        }

        throw new Exception("unreachable");
    }

    // Fail fast at script time, not mid-TTS: a model slip that leaves a scene's
    // narration empty only surfaced in the 2026-11-11 test run when TTS threw
    // "Cannot synthesize empty narration" on scene 3 of 9, after minutes of
    // script and art work. Thrown as InvalidDataException, not Exception, so
    // CompleteAndParseAsync's catch can tell this apart from an unrelated
    // downstream error and retry it the same way it retries malformed JSON - a
    // persistent slip still fails here, with the scene number, instead of
    // deep in --prep. Character scenes need an imagePrompt (art generation has
    // nothing to draw from without it); MotionPrompt is deliberately not
    // required here because GenerateAsync fills it in from the image prompt
    // when the model omits it. Context scenes need a stockQuery instead, and
    // leave imagePrompt/motionPrompt empty by design.
    static void ValidateScenes(IReadOnlyList<Scene> scenes, ContentProfile profile)
    {
        for (var i = 0; i < scenes.Count; i++)
        {
            var scene = scenes[i];
            var label = $"scene {i + 1}";
            if (string.IsNullOrWhiteSpace(scene.Narration))
                throw new InvalidDataException(
                    $"{label} has no narration - every scene needs something for TTS to read.");
            var isContext = profile.AllowsContextScenes &&
                scene.SceneKind.Equals("context", StringComparison.OrdinalIgnoreCase);
            if (isContext)
            {
                if (string.IsNullOrWhiteSpace(scene.StockQuery))
                    throw new InvalidDataException(
                        $"{label} was tagged sceneKind: \"context\" but returned no stockQuery");
            }
            else if (string.IsNullOrWhiteSpace(scene.ImagePrompt))
            {
                throw new InvalidDataException(
                    $"{label} has no imagePrompt - a character scene needs one for its art.");
            }
        }
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
