using GiggleGarden.Shared;

record GenConfig
{
    public string WorkDirectory { get; init; } = @"D:\Business\VideoGen\work";
    public string OutputDirectory { get; init; } = @"D:\Business\Videos";

    public string AnthropicApiKey { get; init; } = "";
    public string ClaudeModel { get; init; } = "claude-opus-5";

    public string AzureSpeechKey { get; init; } = "";
    public string AzureSpeechRegion { get; init; } = "eastus";

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

    // Keeps the recurring character consistent across scenes & videos.
    public string CharacterStyle { get; init; } =
        "A cheerful little yellow duckling named Gigi with big friendly eyes and a tiny red scarf, " +
        "cute 2D children's cartoon style, flat colors, thick outlines";

    public string BackgroundMusicPath { get; init; } = @"D:\Business\VideoGen\assets\music.mp3";
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
    public int VerticalMaxSeconds { get; init; } = 85;

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

        var needsOpenAi = ImageProvider.Equals("openai", StringComparison.OrdinalIgnoreCase);
        if (needsOpenAi && (string.IsNullOrWhiteSpace(OpenAiApiKey) || OpenAiApiKey.StartsWith("PUT-"))) missing.Add(nameof(OpenAiApiKey));
        if (!needsOpenAi && string.IsNullOrWhiteSpace(StabilityApiKey)) missing.Add(nameof(StabilityApiKey));

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing credentials: {string.Join(", ", missing)}. Set them in appsettings.json " +
                $"or as environment variables (GIGGLE_{missing[0]}=...).");

        if (!File.Exists(SubtitleFontPath))
            throw new FileNotFoundException($"Subtitle font not found: {SubtitleFontPath}", SubtitleFontPath);
    }
}
