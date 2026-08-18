using GiggleGarden.Shared;

record GenConfig
{
    public string WorkDirectory { get; init; } = @"D:\Business\VideoGen\work";
    public string OutputDirectory { get; init; } = @"D:\Business\Videos";

    public string AnthropicApiKey { get; init; } = "";
    public string ClaudeModel { get; init; } = "claude-opus-5";

    // hybrid (default - splits one script's acts across Groq for acts 1-2 and
    // Gemini for acts 3-4, so each provider's daily quota only has to cover half
    // a script), groq (free tier alone - confirmed 8,000 TPM/model + 200,000 TPD
    // total, no card required), gemini (free tier alone - confirmed 20
    // requests/day/model on gemini-3.6-flash, a separate quota bucket from
    // Groq's), or claude (zero free-tier-quota risk, but paid). See
    // GroqScriptProvider/GeminiScriptProvider/HybridScriptProvider/
    // ScriptProviderFactory.
    public string ScriptProvider { get; init; } = "hybrid";
    public string GroqApiKey { get; init; } = "";
    public string GroqModel { get; init; } = "llama-3.3-70b-versatile";
    public string GeminiApiKey { get; init; } = "";
    public string GeminiModel { get; init; } = "gemini-3.6-flash";

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

    // Retained for the static-image fallback path only; the Vidu flow above needs
    // none of these. Kept until the video pipeline has a few weeks of real runs
    // behind it, then this and ImageClient.cs can go.
    public string ImageProvider { get; init; } = "openai";   // openai | stability
    public string OpenAiApiKey { get; init; } = "";
    public string OpenAiImageModel { get; init; } = "gpt-image-1";
    public string StabilityApiKey { get; init; } = "";

    // Pexels stock photo/video search (see StockFootageClient) - the auto-fetched
    // "context/b-roll moment" visual source for the stock-footage/Remotion pipeline
    // (Deliverable 6, tasks/todo.md). Free API, no card required; license permits
    // commercial/monetized use with no attribution needed (checked live before
    // adopting). Not yet required in Validate() below - nothing calls this client
    // until the pipeline is wired up, so an unset key must not block the existing
    // Vidu-based runs from starting.
    public string PexelsApiKey { get; init; } = "";

    // Absolute path to the remotion-assembler sibling project (Deliverable 6,
    // tasks/todo.md) - VideoAssembler.AssembleWithRemotionAsync shells out to
    // `npx remotion render` there, the same way RunFfmpegAsync shells out to ffmpeg.
    // Only read when the active profile has AllowsContextScenes = true; GiggleGarden's
    // Vidu/ffmpeg path never reaches it, so an unset value doesn't block that channel.
    public string RemotionProjectPath { get; init; } = "";

    // Public YouTube Data API key (no OAuth - Google Cloud Console -> Credentials ->
    // API key, with "YouTube Data API v3" enabled) used to pull real trending kids'
    // titles as topic inspiration when --topic is not given. Separate from the
    // Uploader's OAuth client secret, which is a heavier credential for a different job.
    public string YouTubeApiKey { get; init; } = "";

    // Render a second, natively-vertical image set for the 9:16 cut. Without this the
    // vertical render centre-crops a 3:2 landscape frame and reliably decapitates the
    // character. Costs one extra image per scene; set false to trade quality for spend.
    public bool GenerateVerticalImages { get; init; } = true;

    // Which ContentProfile this run's content identity comes from. "gigglegarden" is
    // the only one that exists today; --profile overrides this per invocation.
    public string Profile { get; init; } = "gigglegarden";

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

        // "azure" forces every language onto Azure, so Google's key is genuinely unused
        // in that mode - only require it when routing can actually reach Google.
        var googleReachable = !TtsProvider.Equals("azure", StringComparison.OrdinalIgnoreCase);
        if (googleReachable && (string.IsNullOrWhiteSpace(GoogleCloudTtsApiKey) || GoogleCloudTtsApiKey.StartsWith("PUT-")))
            missing.Add(nameof(GoogleCloudTtsApiKey));

        // "claude" (the default) never touches Groq/Gemini, so only require a key
        // when ScriptProvider can actually route there.
        var groqReachable = ScriptProvider.Equals("groq", StringComparison.OrdinalIgnoreCase);
        if (groqReachable && (string.IsNullOrWhiteSpace(GroqApiKey) || GroqApiKey.StartsWith("PUT-")))
            missing.Add(nameof(GroqApiKey));

        // "hybrid" needs both keys - it routes different acts of the same script
        // to Groq and Gemini (see HybridScriptProvider/ScriptProviderFactory).
        var hybrid = ScriptProvider.Equals("hybrid", StringComparison.OrdinalIgnoreCase);
        var geminiReachable = hybrid || ScriptProvider.Equals("gemini", StringComparison.OrdinalIgnoreCase);
        if (geminiReachable && (string.IsNullOrWhiteSpace(GeminiApiKey) || GeminiApiKey.StartsWith("PUT-")))
            missing.Add(nameof(GeminiApiKey));
        if (hybrid && (string.IsNullOrWhiteSpace(GroqApiKey) || GroqApiKey.StartsWith("PUT-")))
            missing.Add(nameof(GroqApiKey));

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing credentials: {string.Join(", ", missing)}. Set them in appsettings.Local.json " +
                $"or as environment variables (GIGGLE_{missing[0]}=...).");

        if (!File.Exists(SubtitleFontPath))
            throw new FileNotFoundException($"Subtitle font not found: {SubtitleFontPath}", SubtitleFontPath);
    }
}
