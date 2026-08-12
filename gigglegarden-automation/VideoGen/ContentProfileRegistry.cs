// Picks which ContentProfile drives a run's content identity. Mirrors
// ScriptProviderFactory's shape, but unlike that factory's silent fallback for an
// unrecognised string, an unknown --profile throws: silently rendering as GiggleGarden
// on a typo'd flag is a much worse footgun here, since profile identity affects every
// tag, prompt and framing choice in the script.
static class ContentProfileRegistry
{
    private static readonly Dictionary<string, ContentProfile> All =
        new(StringComparer.OrdinalIgnoreCase) { ["gigglegarden"] = GiggleGardenProfile.Value };

    public static ContentProfile Get(string id) =>
        All.TryGetValue(id, out var profile) ? profile
            : throw new InvalidOperationException(
                $"Unknown --profile \"{id}\". Available profiles: {string.Join(", ", All.Keys)}.");
}
