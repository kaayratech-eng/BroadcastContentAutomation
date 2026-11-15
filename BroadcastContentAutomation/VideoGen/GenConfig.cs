using GiggleGarden.Shared;

record GenConfig
{
    public string WorkDirectory { get; init; } = @"D:\Business\VideoGen\work";
    public string OutputDirectory { get; init; } = @"D:\Business\Videos";

    // Linux / Muse-system port (2026-10-25): when set, profile asset folders
    // (music, character-pool) are resolved under this directory instead of
    // their hardcoded Windows paths in the profile files, and topic history
    // is written here instead of D:\Business\... . Empty = keep the original
    // Windows behaviour, so the original machine needs no config change.
    public string AssetsDirectory { get; init; } = "";
    public string TopicHistoryDirectory { get; init; } = "";

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
    public string GroqModel { get; init; } = "openai/gpt-oss-120b"; // was llama-3.3-70b-versatile, decommissioned - verified 2026-11-11: the live models list returns model_not_found for it, and the key's catalog lists gpt-oss-120b/20b and qwen3.8-27b as the chat models; gpt-oss-120b is the strongest and the providers already handle gpt-oss reasoning (low effort, no reasoning in content)
    public string GeminiApiKey { get; init; } = "";
    public string GeminiModel { get; init; } = "gemini-3.6-flash";

    // Vault-backed helper paths (set up 2026-11-11, same pattern as the
    // scene-art helpers): the keys live in Muse's secure vault, and the
    // helpers perform the authd surrogate exchange at call time. No key
    // value ever appears in config, arguments or environment variables.
    // The "-helper" script providers (see ScriptProviderFactory) shell out
    // to these instead of calling the API directly with a config key.
    public string GeminiHelperPath { get; init; } = "/home/hatch/workspace/custom-connectors/custom.gemini/bin/gemini_image.py";
    public string GroqHelperPath { get; init; } = "/home/hatch/workspace/custom-connectors/custom.groq/bin/groq_chat.py";

    public string AzureSpeechKey { get; init; } = "";
    public string AzureSpeechRegion { get; init; } = "eastus";
    // Vault-backed helper path (set up 2026-11-14): the Azure Speech key lives
    // in the secure vault (custom.azure-speech); AzureHelperTtsProvider shells
    // out to this helper instead of calling the API with a config key.
    public string AzureTtsHelperPath { get; init; } = "/home/hatch/workspace/custom-connectors/custom.azure-speech/bin/azure_tts.py";

    // Google Cloud Text-to-Speech: Standard voices are free to 4M chars/mo, WaveNet to
    // 1M chars/mo (resets monthly, doesn't expire) - see TtsProviderFactory for the
    // per-language routing this key enables. Restricted API key from GCP Console,
    // scoped to "Cloud Text-to-Speech API" (same auth shape as YouTubeApiKey above).
    public string GoogleCloudTtsApiKey { get; init; } = "";
    // Vault-backed helper path (set up 2026-11-11): the GCP key lives in the
    // secure vault (custom.googlecloudtts); GoogleHelperTtsProvider shells
    // out to this helper instead of calling the API with a config key.
    public string GoogleTtsHelperPath { get; init; } = "/home/hatch/workspace/custom-connectors/custom.googlecloudtts/bin/gcp_tts.py";

    // auto (default) = route by language via TtsProviderFactory (en/hi -> Google,
    // pa -> Azure, since no free Punjabi voice exists on any provider). azure/google
    // force every language onto one provider - azure is the zero-risk safety switch,
    // google is for testing only and will hard-fail on pa rather than silently
    // degrading to a paid/unintended voice. "google-helper" is auto's routing with
    // the Google leg via the vault-backed helper instead of a config key.
    // "azure-helper" routes every language to Azure via the vault-backed helper
    // instead of a config key, restoring the original Harper/DavisNeural voices
    // with per-scene mood variation (see TtsProviderFactory).
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

    // Scene art provider (decided 2026-11-04, refined 2026-11-10/11):
    // "manual" = ManualArtClient prompt-and-wait on gemini.google.com.
    // "pollinations" (default) = PollinationsKeyedClient, the keyed
    // Pollinations.ai FLUX endpoint, ONE square (1024x1024) image per
    // visual beat, from which both orientations are centre cover-cropped
    // so portrait and landscape are identical (rule changed 2026-11-11
    // after the user found the old two-separate-generations rule produced
    // different images per orientation). "cloudflare" = CloudflareArtClient
    // (FLUX square, same model) with the same one-square-per-beat shape.
    // Whichever provider is primary, the other is the automatic fallback
    // on failure, and Cloudflare SDXL square is the last resort. No
    // blur-pad anywhere in the scene-art flow.
    // before --prep in every mode, and are picked up without prompting.
    // The API keys live in Muse's secure vault, not in config: the clients
    // shell out to the Python helpers below, which perform the authd
    // surrogate exchange at call time. The Cloudflare account id is a
    // non-secret identifier, so it lives here like the Page/IG ids in the
    // Uploader config. Helper paths are Linux/Muse-system paths; override
    // them in appsettings.Local.json when running elsewhere.
    public string SceneArtProvider { get; init; } = "pollinations";
    public string PollinationsModel { get; init; } = "flux";
    public string PollinationsHelperPath { get; init; } = "/home/hatch/workspace/custom-connectors/custom.pollinations/bin/pollinations_image.py";
    public string CloudflareAccountId { get; init; } = "f15282dd52108829bfcadc311ef96ec8";
    public string CloudflareHelperPath { get; init; } = "/home/hatch/workspace/custom-connectors/custom.cloudflare/bin/cf_workers_ai.py";
    public string CloudflareImageModel { get; init; } = "@cf/stabilityai/stable-diffusion-xl-base-1.0";
    public int CloudflareSdxlSteps { get; init; } = 20;
    public double CloudflareSdxlGuidance { get; init; } = 7.5;
    public int CloudflareFluxSteps { get; init; } = 4;

    // Pexels stock photo/video search (see StockFootageClient) - the auto-fetched
    // "context/b-roll moment" visual source for the stock-footage/Remotion pipeline.
    // Free API, no card required; license permits commercial/monetized use with no
    // attribution needed (checked live before adopting). Only required by Validate()
    // below for profiles with AllowsContextScenes = true (chronicleandchaos) - the
    // Vidu-only gigglegarden profile never calls StockFootageClient.
    public string PexelsApiKey { get; init; } = "";
    // Vault-backed helper path (set up 2026-11-11): the Pexels key lives in
    // the secure vault (custom.pexels). When no real key is in config,
    // Program.cs uses StockFootageHelperClient, which shells out to this
    // helper for the authenticated searches and downloads the (public)
    // media links directly.
    public string PexelsHelperPath { get; init; } = "/home/hatch/workspace/custom-connectors/custom.pexels/bin/pexels_search.py";

    // Absolute path to the remotion-assembler sibling project (Deliverable 6,
    // tasks/todo.md) - VideoAssembler.AssembleWithRemotionAsync shells out to
    // `npx remotion render` there, the same way RunFfmpegAsync shells out to ffmpeg.
    // Only read when the active profile has UsesRemotionStills = true; a Vidu-only
    // profile never reaches it, so an unset value doesn't block that channel.
    public string RemotionProjectPath { get; init; } = "";

    // Ceiling on a single `npx remotion render` call - headless-Chromium frame rendering
    // has no other progress signal once it's running, so without a bound a genuine hang
    // (Chromium wedged, npx stuck resolving) would wait forever. A long-form landscape
    // render (many scenes, full narration length rather than the ~90s vertical cap) is
    // legitimately slow, not stuck - keep this generous rather than tune it tight.
    public int RemotionRenderTimeoutMinutes { get; init; } = 180;

    // Public YouTube Data API key (no OAuth - Google Cloud Console -> Credentials ->
    // API key, with "YouTube Data API v3" enabled) used to pull real trending kids'
    // titles as topic inspiration when --topic is not given. Separate from the
    // Uploader's OAuth client secret, which is a heavier credential for a different job.
    public string YouTubeApiKey { get; init; } = "";

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

    // Linux font fallback (2026-10-25): Nirmala (Windows) covers Latin +
    // Devanagari + Gurmukhi in one file, but no single Linux font does.
    // If the configured font file is missing, pick a per-language fallback
    // that exists on this machine, so a Windows-tracked path never blocks a
    // Linux run. English uses DejaVu Sans; Hindi uses Noto Sans Devanagari;
    // Punjabi uses Noto Sans Gurmukhi.
    public string ResolveSubtitleFontPath(string language) =>
        File.Exists(SubtitleFontPath) ? SubtitleFontPath :
        language switch
        {
            "hi" => "/usr/share/fonts/truetype/noto/NotoSansDevanagari-Regular.ttf",
            "pa" => "/usr/share/fonts/truetype/noto/NotoSansGurmukhi-Regular.ttf",
            _ => "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        };

    // A config value counts as a real key only when it is set and is not one
    // of the "PUT-..." placeholders shipped in appsettings.json. Used by
    // Validate() and by the helper-vs-direct selection in Program.cs: a
    // missing key is fine when the vault-backed helper exists instead.
    public static bool HasRealKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith("PUT-");

    public void Validate(ContentProfile profile, string language)
    {
        var missing = new List<string>();
        // Anthropic is only needed when the script provider is actually Claude;
        // hybrid/groq/gemini runs previously failed validation for a key they
        // never used.
        if (ScriptProvider.Equals("claude", StringComparison.OrdinalIgnoreCase)
            && !HasRealKey(AnthropicApiKey)) missing.Add(nameof(AnthropicApiKey));

        // Azure is only reachable when TtsProvider forces it, or when the
        // run's language is Punjabi (auto routes pa to Azure - see
        // TtsProviderFactory). An en/hi run never constructs an Azure
        // provider, so demanding the key there blocked test runs that
        // needed nothing from Azure.
        var azureReachable = TtsProvider.Equals("azure", StringComparison.OrdinalIgnoreCase)
            || (language.Equals("pa", StringComparison.OrdinalIgnoreCase)
                && !TtsProvider.Equals("azure-helper", StringComparison.OrdinalIgnoreCase));
        if (azureReachable && !HasRealKey(AzureSpeechKey)) missing.Add(nameof(AzureSpeechKey));

        // "azure-helper" reaches Azure through the vault-backed helper instead
        // of a config key - only fail when the helper itself is missing.
        if (TtsProvider.Equals("azure-helper", StringComparison.OrdinalIgnoreCase)
            && !File.Exists(AzureTtsHelperPath)) missing.Add(nameof(AzureTtsHelperPath));

        // Only a profile still on the Vidu image-to-video path ever
        // constructs a ViduClient (see Program.cs's `!profile.UsesRemotionStills`
        // gates) - the no-Vidu stills pipeline never touches it.
        if (!profile.UsesRemotionStills && !HasRealKey(ViduApiKey))
            missing.Add(nameof(ViduApiKey));

        // Only the context-scene pipeline (chronicleandchaos) calls stock
        // footage. A real config key is one route; the vault-backed helper
        // is the other - only fail when neither exists.
        if (profile.AllowsContextScenes && !HasRealKey(PexelsApiKey) && !File.Exists(PexelsHelperPath))
            missing.Add(nameof(PexelsApiKey));

        // "azure" forces every language onto Azure, so Google's key is genuinely unused
        // in that mode - only require it when routing can actually reach Google
        // with a config key ("google-helper" uses the vault instead).
        var googleReachable = TtsProvider.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || TtsProvider.Equals("google", StringComparison.OrdinalIgnoreCase);
        if (googleReachable && !HasRealKey(GoogleCloudTtsApiKey))
            missing.Add(nameof(GoogleCloudTtsApiKey));

        // Only require a config key when ScriptProvider can actually route
        // there with a config key - the "-helper" variants use the vault.
        var groqReachable = ScriptProvider.Equals("groq", StringComparison.OrdinalIgnoreCase)
            || ScriptProvider.Equals("hybrid", StringComparison.OrdinalIgnoreCase);
        if (groqReachable && !HasRealKey(GroqApiKey))
            missing.Add(nameof(GroqApiKey));

        // "hybrid" needs both keys - it routes different acts of the same script
        // to Groq and Gemini (see HybridScriptProvider/ScriptProviderFactory).
        var geminiReachable = ScriptProvider.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            || ScriptProvider.Equals("hybrid", StringComparison.OrdinalIgnoreCase);
        if (geminiReachable && !HasRealKey(GeminiApiKey))
            missing.Add(nameof(GeminiApiKey));

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing credentials: {string.Join(", ", missing)}. Set them in appsettings.Local.json " +
                $"or as environment variables (GIGGLE_{missing[0]}=...).");

        // A missing configured font is only fatal if no Linux fallback exists
        // either (checked per-language at render time via ResolveSubtitleFontPath).
        if (!File.Exists(SubtitleFontPath)
            && !File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
            && !File.Exists("/usr/share/fonts/truetype/noto/NotoSansDevanagari-Regular.ttf")
            && !File.Exists("/usr/share/fonts/truetype/noto/NotoSansGurmukhi-Regular.ttf"))
            throw new FileNotFoundException($"Subtitle font not found: {SubtitleFontPath}", SubtitleFontPath);
    }
}
