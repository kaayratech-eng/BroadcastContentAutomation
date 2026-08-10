using GiggleGarden.Shared;

record GenConfig
{
    public string WorkDirectory { get; init; } = @"D:\Business\VideoGen\work";
    public string OutputDirectory { get; init; } = @"D:\Business\Videos";

    public string AnthropicApiKey { get; init; } = "";
    public string ClaudeModel { get; init; } = "claude-opus-5";

    public string AzureSpeechKey { get; init; } = "";
    public string AzureSpeechRegion { get; init; } = "eastus";

    // Vidu image-to-video (see ViduClient.cs) - the source of every scene's picture.
    // OffPeak halves the price in exchange for a delivery window of up to 48 hours,
    // which is why generation (--prep) and assembly (--assemble) are separate phases.
    public string ViduApiKey { get; init; } = "";
    public string ViduModel { get; init; } = "viduq2-turbo";
    public string ViduResolution { get; init; } = "540p";
    public bool ViduOffPeak { get; init; } = true;

    // The single canonical picture of the mascot, reused for every video forever.
    // Scene 1 starts from this frame and each later scene starts from the previous
    // clip's last frame, so the character stays on-model without per-scene art.
    // Must be 9:16 - Vidu's output aspect ratio follows its input image's.
    public string CharacterReferencePath { get; init; } = @"D:\Business\gigglegarden-automation\VideoGen\assets\gigi-reference.png";

    // Retained for the static-image fallback path only; the Vidu flow above needs
    // none of these. Kept until the video pipeline has a few weeks of real runs
    // behind it, then this and ImageClient.cs can go.
    public string ImageProvider { get; init; } = "openai";   // openai | stability
    public string OpenAiApiKey { get; init; } = "";
    public string OpenAiImageModel { get; init; } = "gpt-image-1";
    public string StabilityApiKey { get; init; } = "";

    // Public YouTube Data API key (no OAuth - Google Cloud Console -> Credentials ->
    // API key, with "YouTube Data API v3" enabled) used to pull real trending kids'
    // titles as topic inspiration when --topic is not given. Separate from the
    // Uploader's OAuth client secret, which is a heavier credential for a different job.
    public string YouTubeApiKey { get; init; } = "";
    public string[] TrendQueries { get; init; } =
    [
        "nursery rhymes for kids", "kids learning songs", "hindi rhymes for children",
        "punjabi kids songs", "learning with alphabets kids", "learning maths for kids",
    ];

    // Render a second, natively-vertical image set for the 9:16 cut. Without this the
    // vertical render centre-crops a 3:2 landscape frame and reliably decapitates the
    // character. Costs one extra image per scene; set false to trade quality for spend.
    public bool GenerateVerticalImages { get; init; } = true;

    // Generates one reference image of the character first, then uses image-to-image
    // for every scene so the character's appearance stays consistent instead of
    // drifting - each independent text-to-image call otherwise has no memory of prior
    // output. Costs one extra image per orientation per video; set false to trade
    // consistency for spend (CharacterStyle's text description is the only guardrail then).
    public bool UseCharacterReference { get; init; } = true;

    // The mascot's fixed identity - name and species must stay identical across every
    // video for a recognizable channel character. Referenced by both the image prompt
    // (CharacterStyle, below) and the script prompt (ScriptGenerator.cs), which used to
    // free-invent a different name/species per video (e.g. "Pip the Squirrel" in one,
    // "Pip the Penguin" in another) while the visuals stayed a duckling regardless -
    // title/narration and on-screen art were describing two different characters.
    public string CharacterName { get; init; } = "Gigi the Duckling";

    // Keeps the recurring character consistent across scenes & videos.
    public string CharacterStyle { get; init; } =
        "A cheerful little yellow duckling named Gigi with big friendly eyes and a tiny red scarf, " +
        "cute 2D children's cartoon style, flat colors, thick outlines";

    // A single track, or a folder of them to pick from per video (see
    // VideoAssembler.ResolveBackgroundMusic). Ships with the repo, so the default points
    // at the checked-in assets rather than at the output tree. Empty means render without
    // music on purpose; a path that isn't there throws.
    public string BackgroundMusicPath { get; init; } =
        @"D:\Business\gigglegarden-automation\VideoGen\assets\music";

    // Target loudness for the music bed, in LUFS. Every track is normalised to this
    // regardless of how it was mastered, so the bed sits at the same level from video to
    // video. Narration lands around -19 LUFS, so this is ~17 LU underneath it: something
    // you notice when it stops rather than while it plays, which is the whole job of a
    // bed under a narrator aimed at 2-6 year olds. Less negative = louder music.
    public double BackgroundMusicLufs { get; init; } = -36.0;
    public string SubtitleFontPath { get; init; } = @"C:\Windows\Fonts\NirmalaB.ttf"; // covers Devanagari + Gurmukhi
    public int SubtitleMaxLines { get; init; } = 3;
    public string FfmpegPath { get; init; } = "";   // empty = use PATH
    public string FfprobePath { get; init; } = "";

    // Which platforms each render is offered to. The Uploader still decides whether a
    // platform is enabled and configured; this only declares intent.
    //
    // Defaults deliberately empty, not pre-populated with the "real" values - .NET's
    // configuration array binding APPENDS config-file values onto a non-empty default
    // instead of replacing it, so a default here matching appsettings.json's real
    // values silently doubled every target (e.g. ["youtube","youtube"]), which meant
    // every publish ran twice per platform. appsettings.json always defines both keys
    // (loaded with optional: false), so an empty default here is never actually hit.
    public string[] LandscapeTargets { get; init; } = [];
    public string[] VerticalTargets { get; init; } = [];

    // Hard ceiling for the 9:16 cut. Reels reject >90s; Shorts allow more but the short
    // form is the point. Scenes past the limit are dropped from the vertical render only.
    public int VerticalMaxSeconds { get; init; } = 89;

    // Scenes whose narration runs longer than this get a second image generated and
    // shown via an internal crossfade partway through, instead of one static photo
    // sitting on screen for the whole line. Costs one extra image (per orientation)
    // for every scene that qualifies — raise this or set a high value to trade the
    // extra motion back for lower image spend.
    public double SplitLongSceneAfterSeconds { get; init; } = 8.0;

    public void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(AnthropicApiKey) || AnthropicApiKey.StartsWith("PUT-")) missing.Add(nameof(AnthropicApiKey));
        if (string.IsNullOrWhiteSpace(AzureSpeechKey) || AzureSpeechKey.StartsWith("PUT-")) missing.Add(nameof(AzureSpeechKey));
        if (string.IsNullOrWhiteSpace(ViduApiKey) || ViduApiKey.StartsWith("PUT-")) missing.Add(nameof(ViduApiKey));

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing credentials: {string.Join(", ", missing)}. Set them in appsettings.Local.json " +
                $"or as environment variables (GIGGLE_{missing[0]}=...).");

        if (!File.Exists(SubtitleFontPath))
            throw new FileNotFoundException($"Subtitle font not found: {SubtitleFontPath}", SubtitleFontPath);

        if (!File.Exists(CharacterReferencePath))
            throw new FileNotFoundException(
                $"Character reference image not found: {CharacterReferencePath}. This is the canonical " +
                "9:16 picture of the mascot that every video's first scene starts from.", CharacterReferencePath);
    }
}
