namespace GiggleGarden.Shared;

// The two Short files of one video (fix #5, Rishi 2026-11-27 — cadence:
// 1 long-form/day, the freed slot is a second Short, not a second
// long-form; 2nd-Short rule revised Rishi 2026-11-27 17:59 EST, publish
// hold corrected to 12h 18:07 EST).
//
//   <slug>-en-short.mp4   — the main Short: the first ~11 scenes, hook first.
//   <slug>-en-short2.mp4  — the second Short, built the NEW way: a payoff
//                            stretch cut from the finished landscape final
//                            and verticalized (finish-chunks.py step 3c),
//                            starting strictly after the main Short's last
//                            scene — ZERO overlap with it, never a tail cut
//                            of the main Short. Its own title (never the
//                            long-form / main-Short title) and its own
//                            thumbnail (never the auto-picked frame that
//                            duplicates the main Short), and it is held for
//                            publication until ~12h after the main Short
//                            goes live (Sidecar.PublishAfterUtc, anchored
//                            on the main Short's publication time, not the
//                            finish) — never posted the same day. No new images, no new TTS — it reuses
//                            the long-form's scenes, so it adds no review
//                            burden beyond its own title/thumbnail pick.
//
// One definition of the naming, shared by the finish step (Python mirrors
// it by string), the uploader scan/dispatch, the sibling lookup, and the
// quota guard — a second Short is a video of its own (it has a sidecar),
// never a derivative like the story60 cut.
public static class ShortVideo
{
    public const string Suffix = "-short";
    public const string SecondSuffix = "-short2";

    public static bool IsShort(string videoPath) =>
        Path.GetExtension(videoPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
        (Path.GetFileNameWithoutExtension(videoPath).EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileNameWithoutExtension(videoPath).EndsWith(SecondSuffix, StringComparison.OrdinalIgnoreCase));

    public static bool IsSecondShort(string videoPath) =>
        Path.GetExtension(videoPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileNameWithoutExtension(videoPath).EndsWith(SecondSuffix, StringComparison.OrdinalIgnoreCase);

    // The long-form slug for a Short file ("<slug>-en-short" or
    // "<slug>-en-short2" -> "<slug>-en"), or null when the file is not a
    // Short. The long-form's description links only the main Short — the
    // second Short gets its own "Full story here" link, never a second
    // line on the long-form.
    public static string? LongFormSlug(string fileNameWithoutExtension)
    {
        if (fileNameWithoutExtension.EndsWith(SecondSuffix, StringComparison.OrdinalIgnoreCase))
            return fileNameWithoutExtension[..^SecondSuffix.Length];
        if (fileNameWithoutExtension.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            return fileNameWithoutExtension[..^Suffix.Length];
        return null;
    }
}
