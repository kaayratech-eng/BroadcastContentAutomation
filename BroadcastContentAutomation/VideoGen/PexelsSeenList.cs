// Per-job Pexels seen-list (Ronaldo 2026-11-25: scenes 26+31 got the
// SAME night-stadium clip because the stock-video path had no dedupe at
// all — the photo dedupe lived in the other funnel). pexels-video-used.json
// sits in the job dir next to commons-used.json and records every stock
// download: kind (video/photo), Pexels id, scene index, search query and
// page URL. It is the audit log (which clip came from which query) AND
// the enforcement input: the visual loop passes the ids used by the
// previous three scenes as exclusions, so the same clip can never come
// back within < 4 scenes (Rishi's reuse rule, 2026-11-25 23:49). A
// reuse 4+ scenes later is allowed, so only the last three scenes'
// ids are excluded — older entries stay in the file for the audit.
using System.Text.Json;
using GiggleGarden.Shared;

// One entry of the per-job Pexels seen-list (pexels-video-used.json).
internal sealed record PexelsUsedEntry(string Kind, long Id, int Scene, string Query, string? Url);

internal static class PexelsSeenList
{
    internal static List<PexelsUsedEntry> Load(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PexelsUsedEntry>>(
                File.ReadAllText(manifestPath), Sidecar.Options) ?? [];
        }
        catch
        {
            // A corrupt manifest must not kill a prep — the seen-list is a
            // best-effort guard; the quality gate's near-duplicate check is
            // the backstop. Start fresh rather than throw.
            return [];
        }
    }

    internal static void Append(string manifestPath, PexelsUsedEntry entry)
    {
        var entries = Load(manifestPath);
        entries.Add(entry);
        var temp = manifestPath + $".tmp{Environment.ProcessId}";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Sidecar.Options));
        File.Move(temp, manifestPath, overwrite: true);
    }

    // Video ids the loop must refuse for scene `sceneIndex`: the ids used
    // by the previous three scenes (distance < 4). Older reuse is allowed.
    internal static HashSet<long> ExcludedIds(IReadOnlyList<PexelsUsedEntry> used, int sceneIndex) =>
        used.Where(e => e.Kind == "video" && e.Scene > sceneIndex - 4 && e.Scene < sceneIndex)
            .Select(e => e.Id)
            .ToHashSet();
}
