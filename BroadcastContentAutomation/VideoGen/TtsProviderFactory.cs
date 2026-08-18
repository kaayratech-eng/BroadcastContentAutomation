// Picks which ITtsProvider narrates a run. Routing is per-language, not a single
// global switch, because the free-tier gap is per-language: Google Cloud TTS has no
// free Standard/WaveNet Punjabi voice, so pa must always reach Azure regardless of
// GenConfig.TtsProvider. See tasks/todo.md Part 2 for the sourced free-tier numbers
// behind this choice.
static class TtsProviderFactory
{
    public static ITtsProvider Create(GenConfig cfg, ContentProfile profile, string language)
    {
        var mode = cfg.TtsProvider.ToLowerInvariant();

        if (mode == "azure") return new AzureTtsProvider(cfg, profile.VoiceOverride);

        if (mode == "google")
        {
            if (language == "pa")
                throw new NotSupportedException(
                    "TtsProvider is forced to \"google\" but there is no free Google Cloud TTS voice for Punjabi " +
                    "(pa) - set TtsProvider to \"auto\" or \"azure\" instead of silently falling back.");
            return new GoogleTtsProvider(cfg);
        }

        // auto: free provider where one exists, Azure where it doesn't.
        return language switch
        {
            "en" or "hi" => new GoogleTtsProvider(cfg),
            _ => new AzureTtsProvider(cfg, profile.VoiceOverride),
        };
    }
}
