namespace GiggleGarden.Shared;

// The ≤60s Story cut of a vertical Short (Rishi 2026-11-26): Instagram
// Stories are capped at 60s and the Shorts this pipeline makes are 78-81s,
// so the instagram_story target used to be skipped outright. finish-chunks.py
// now cuts the first 60s (the hook lives at the start) into a sibling file
// next to the Short, and the Uploader uploads that cut for the Story target
// instead. One definition of the naming, shared by the finish step (Python
// mirrors it by string), the uploader scan/dispatch, and the Story gate —
// a cut is a derivative, never a video of its own: it has no sidecar and
// must never be picked up by the watch-dir scan.
public static class StoryCut
{
    public const string Suffix = "-story60";

    public static bool IsStoryCut(string videoPath) =>
        Path.GetExtension(videoPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileNameWithoutExtension(videoPath).EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

    public static string PathFor(string shortVideoPath) =>
        Path.Combine(Path.GetDirectoryName(shortVideoPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(shortVideoPath) + Suffix + Path.GetExtension(shortVideoPath));
}
