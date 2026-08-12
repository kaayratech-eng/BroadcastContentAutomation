using GiggleGarden.Shared;

record GenConfig
{
    public string WorkDirectory { get; init; } = @"D:\Business\VideoGen\work";
    public string OutputDirectory { get; init; } = @"D:\Business\Videos";

    public string AnthropicApiKey { get; init; } = "";
    public string ClaudeModel { get; init; } = "claude-opus-5";

    // claude (default, zero risk) or groq (free tier - 30 RPM/6,000 TPM/14,400
    // req/day, no card required, and unlike NVIDIA NIM's free tier Groq's own terms
    // allow real low-volume production use, not just prototyping). See
    // GroqScriptProvider/ScriptProviderFactory.
    public string ScriptProvider { get; init; } = "claude";
    public string GroqApiKey { get; init; } = "";
    public string GroqModel { get; init; } = "llama-3.3-70b-versatile";

    public string AzureSpeechKey { get; init; } = "";
    public string AzureSpeechRegion { get; init; } = "eastus";

    // Google Cloud Text-to-Speech: Standard voices are free to 4M chars/mo, WaveNet to
    // 1M chars/mo (resets monthly, doesn't expire) - see TtsProviderFactory for the
    // per-language routing this key enables. Restricted API key from GCP Console,
    // scoped to "Cloud Text-to-Speech API" (same auth shape as YouTubeApiKey above).
    public string GoogleCloudTtsApiKey { get; init; } = "";

    // auto (default) = route by language via TtsProviderFactory (en/hi -> Google,
    // pa -> Azure, since no free Punjabi voice exists on any provider). azure/google
    // force every language onto one provider - azure is the zero-risk safety switch,
    // google is for testing only and will hard-fail on pa rather than silently
    // degrading to a paid/unintended voice.
    public string TtsProvider { get; init; } = "auto";

    // Vidu image-to-video (see ViduClient.cs) - the source of every scene's picture.
    // OffPeak halves the price in exchange for a delivery window of up to 48 hours,
    // which is why generation (--prep) and assembly (--assemble) are separate phases.
    public string ViduApiKey { get; init; } = "";
    public string ViduModel { get; init; } = "viduq2-turbo";
    public string ViduResolution { get; init; } = "540p";
    public bool ViduOffPeak { get; init; } = true;

    // Where this video's character comes from. Empty (the default) means the script
    // invents a brand-new one per video and it is drawn on the spot - see
    // CharacterSource.cs. Otherwise: a single image pins every video to that one
    // character, or a folder picks one per video the way BackgroundMusicPath picks a
    // track. Every image in a pool needs a same-named .json beside it giving the
    // character's name and appearance, because the script has to be written about
    // whatever is in the picture.
    //
    // Pool art must have a transparent background - the character alone, no scene or
    // flat backdrop baked in - so Vidu generates the scene behind it instead of
    // animating a flat backdrop. Run VideoGen/tools/remove-background.py on any
    // source image before adding it here (see tasks/free-character-tool-shortlist.md).
    // This does not apply to the invented-character path above: CharacterSource.DrawAsync
    // draws its own opaque backdrop because VideoAssembler.FitToPortraitAsync fills the
    // frame by blurring a copy of the source image itself.
    //
    // Nothing here has to be 9:16; images are fitted to 1080x1920 before submission.
    public string CharacterPoolPath { get; init; } = "";

    // The three below only matter when CharacterPoolPath is a folder - they drive
    // CharacterSource.Resolve's build-up/mix/reuse policy so the cast grows on its own
    // instead of staying a stranger every video.
    //
    // Below this many characters in the pool, --prep always invents+draws a new one and
    // adds it to the pool - same as today's behaviour while the pool is still empty.
    public int CharacterPoolBuildupSize { get; init; } = 15;

    // At or above this many, --prep never invents - the pool is "full" and every video
    // reuses one of the existing characters.
    public int CharacterPoolMaxSize { get; init; } = 40;

    // A pooled character can't be picked again until at least this many *other* pool
    // characters have been used since its last appearance.
    public int CharacterPoolCooldown { get; init; } = 10;

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

    // The channel itself, not a character. Every video opens "Welcome to Giggle
    // Garden!" (translated), which is what stays constant across the channel now that
    // the cast does not.
    //
    // This replaced CharacterName, which pinned every video to one mascot. The reason
    // that setting existed was a real bug - the script used to free-invent a name and
    // species per video ("Pip the Squirrel", then "Pip the Penguin") while the art
    // stayed a duckling regardless, so narration and picture described two different
    // characters. Inventing a character is fine; inventing one the art never saw is
    // not. That is why the invented description now drives the picture too.
    public string ChannelName { get; init; } = "Giggle Garden";

    // Art style only - deliberately says nothing about who the character is, since
    // that changes every video. Applied when drawing the character reference.
    public string CharacterStyle { get; init; } =
        "Cute 2D children's cartoon style, flat colors, thick outlines, bright cheerful palette";

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

    // Weighted random draw for ScriptGenerator.PickFormat - keys are ContentFormat member
    // names (case-insensitive). Editing this (or overriding it from appsettings.json, the
    // same way every other tunable here works) is how the content mix gets biased later
    // without a rebuild. A --format flag/override always wins over this draw.
    public Dictionary<string, int> ContentFormatWeights { get; init; } = new()
    {
        ["Educational"] = 30,
        ["Rhyme"] = 20,
        ["Poem"] = 10,
        ["Bedtime"] = 15,
        ["SingAlong"] = 15,
        ["CountingSong"] = 10,
    };

    public void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(AnthropicApiKey) || AnthropicApiKey.StartsWith("PUT-")) missing.Add(nameof(AnthropicApiKey));
        if (string.IsNullOrWhiteSpace(AzureSpeechKey) || AzureSpeechKey.StartsWith("PUT-")) missing.Add(nameof(AzureSpeechKey));
        if (string.IsNullOrWhiteSpace(ViduApiKey) || ViduApiKey.StartsWith("PUT-")) missing.Add(nameof(ViduApiKey));

        // "azure" forces every language onto Azure, so Google's key is genuinely unused
        // in that mode - only require it when routing can actually reach Google.
        var googleReachable = !TtsProvider.Equals("azure", StringComparison.OrdinalIgnoreCase);
        if (googleReachable && (string.IsNullOrWhiteSpace(GoogleCloudTtsApiKey) || GoogleCloudTtsApiKey.StartsWith("PUT-")))
            missing.Add(nameof(GoogleCloudTtsApiKey));

        // "claude" (the default) never touches Groq, so only require the key when
        // ScriptProvider can actually route there.
        var groqReachable = ScriptProvider.Equals("groq", StringComparison.OrdinalIgnoreCase);
        if (groqReachable && (string.IsNullOrWhiteSpace(GroqApiKey) || GroqApiKey.StartsWith("PUT-")))
            missing.Add(nameof(GroqApiKey));

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing credentials: {string.Join(", ", missing)}. Set them in appsettings.Local.json " +
                $"or as environment variables (GIGGLE_{missing[0]}=...).");

        if (!File.Exists(SubtitleFontPath))
            throw new FileNotFoundException($"Subtitle font not found: {SubtitleFontPath}", SubtitleFontPath);

        // Empty is the normal case (draw a new character per video), so only a path
        // that was set and then pointed nowhere is an error.
        if (!string.IsNullOrWhiteSpace(CharacterPoolPath) &&
            !File.Exists(CharacterPoolPath) && !Directory.Exists(CharacterPoolPath))
            throw new FileNotFoundException(
                $"CharacterPoolPath points at \"{CharacterPoolPath}\", which does not exist. Point it at a " +
                "character image, or at a folder of them, or clear it to draw a new character each video.",
                CharacterPoolPath);

        if (CharacterPoolBuildupSize > CharacterPoolMaxSize)
            throw new InvalidOperationException(
                $"CharacterPoolBuildupSize ({CharacterPoolBuildupSize}) is greater than CharacterPoolMaxSize " +
                $"({CharacterPoolMaxSize}) - the pool would never reach the mixing zone. Lower the first or " +
                "raise the second.");
    }
}
