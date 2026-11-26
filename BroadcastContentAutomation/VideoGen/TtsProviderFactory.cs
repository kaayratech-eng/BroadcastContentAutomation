// Picks which ITtsProvider narrates a run. Routing is per-language, not a single
// global switch, because the free-tier gap is per-language: Google Cloud TTS has no
// free Standard/WaveNet Punjabi voice, so pa must always reach Azure regardless of
// GenConfig.TtsProvider. See tasks/todo.md Part 2 for the sourced free-tier numbers
// behind this choice.
// "google-helper" is auto's routing with the Google leg via the vault-backed
// helper (custom.googlecloudtts) instead of a config key - the Muse-system
// default (see appsettings.Local.json).
// "azure-helper" routes every language to Azure via the vault-backed helper
// (custom.azure-speech) instead of a config key, keeping the profile's
// VoiceOverride picks (Harper/DavisNeural) and per-scene mood variation.
// "fallback-helper" (2026-11-22, Trendline Now - the working default) is a
// per-call fallback pool (see FallbackTtsProvider), mirroring the script
// pool's "pool-helper": Azure first, then the Google Cloud TTS helper for
// `en`/`hi` (free tier) when Azure fails. Punjabi has no free Google
// Standard voice, so `pa` stays Azure-only. The fallback only engages on
// failure (provider exception / non-zero helper exit / empty output);
// the voice (DavisNeural) and the +10% pace are unchanged on the Azure
// leg, which voices every scene while it is healthy.
static class TtsProviderFactory
{
    public static ITtsProvider Create(GenConfig cfg, ContentProfile profile, string language)
    {
        var mode = cfg.TtsProvider.ToLowerInvariant();

        if (mode == "azure") return new AzureTtsProvider(cfg, profile.VoiceOverride);

        if (mode == "azure-helper") return new AzureHelperTtsProvider(cfg, profile.VoiceOverride);

        if (mode == "fallback-helper")
        {
            if (language == "pa")
                return new AzureHelperTtsProvider(cfg, profile.VoiceOverride);
            return new FallbackTtsProvider([
                new AzureHelperTtsProvider(cfg, profile.VoiceOverride),
                new GoogleHelperTtsProvider(cfg),
            ], GovernorClient.FromConfig(cfg));
        }

        if (mode == "google")
        {
            if (language == "pa")
                throw new NotSupportedException(
                    "TtsProvider is forced to \"google\" but there is no free Google Cloud TTS voice for Punjabi " +
                    "(pa) - set TtsProvider to \"auto\" or \"azure\" instead of silently falling back.");
            return new GoogleTtsProvider(cfg);
        }

        if (mode == "google-helper")
        {
            if (language == "pa")
                return new AzureTtsProvider(cfg, profile.VoiceOverride);
            return new GoogleHelperTtsProvider(cfg);
        }

        // auto: free provider where one exists, Azure where it doesn't.
        return language switch
        {
            "en" or "hi" => new GoogleTtsProvider(cfg),
            _ => new AzureTtsProvider(cfg, profile.VoiceOverride),
        };
    }
}
