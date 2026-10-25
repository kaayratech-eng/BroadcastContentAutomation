// Prompt assembly shared by ManualArtClient's per-scene call sites.
class ImageClient
{
    public enum Orientation { Landscape, Portrait }

    // Used by ManualArtClient's per-scene call sites (CharacterSource.DrawAsync,
    // Program.cs's no-Vidu prep branch) to build the style+framing+consistency
    // prompt handed to the manual-art generation step.
    public static string BuildPrompt(string scenePrompt, string stylePrompt, string portraitStyleSuffix, Orientation orientation, bool matchReference)
    {
        var framing = orientation == Orientation.Portrait
            ? "Vertical 9:16 portrait composition, subject centred with generous headroom and " +
              "empty space in the lower third for subtitles."
            : "Horizontal 16:9 composition, subject centred, empty space in the lower third for subtitles.";

        var consistency = matchReference
            ? " Match the reference image's face, identity, body build, robe design/colors and overall art style. " +
              "Do not copy the reference image's pose, camera angle or framing - use a new pose and camera angle that fits the scene description above."
            : "";

        return $"{stylePrompt}. {scenePrompt}. {framing} {portraitStyleSuffix}{consistency}";
    }

    // Used by ManualArtClient.PromptAndWaitDualAsync (Program.cs's no-Vidu prep branch,
    // "character" scenes) to get a landscape master and a vertical short out of the same
    // manual Gemini step, instead of running the loop twice per scene. Gemini/nanobanana
    // already returns multiple images from one prompt when asked explicitly for a
    // numbered list of shots - this leans on that rather than two separate chat turns.
    //
    // The count/identity guard wording below was tuned against two live tests batching
    // 3 test scenes (6 images) into one prompt: an "EXACTLY N" instruction alone did not
    // stop Gemini overshooting into extra unsolicited variants in either test, but pinning
    // "Image 1 defines the design, do not alter it" measurably held character identity
    // steady across all returned images in the test where it was used. At this method's
    // actual scale (2 images/prompt) overshoot was never observed, but the identity-pin
    // phrasing is cheap insurance and costs nothing to include regardless.
    public static string BuildDualOrientationPrompt(string scenePrompt, string stylePrompt, string portraitStyleSuffix, bool matchReference)
    {
        var consistency = matchReference
            ? " Match the reference image's face, identity, body build, robe design/colors and overall art style in both images. " +
              "Do not copy the reference image's pose, camera angle or framing - use a new pose and camera angle that fits the scene description above."
            : "";

        return $"{stylePrompt}. {scenePrompt}. Generate exactly 2 images total - no more, no fewer, and no extra alternate takes or bonus variations.\n\n" +
            "Two separate downloadable images:\n" +
            "Image 1: Widescreen Video Frame (16:9 Aspect Ratio) - subject positioned with clean space at the sides for a widescreen video frame.\n" +
            "Image 2: Subtitle-Ready Portrait (9:16 Aspect Ratio) - subject centred with generous headroom and empty space in the lower third for subtitles.\n\n" +
            "Image 1 defines this character's exact design - proportions, colors, face, outfit. Image 2 must depict the identical character from Image 1, " +
            "unchanged in design - only the framing/composition differs between the two images. " +
            $"{portraitStyleSuffix}{consistency}";
    }

    // Batches N scenes' worth of dual-orientation art (see BuildDualOrientationPrompt above)
    // into one Gemini prompt, so a --resume with many pending "character" scenes needs one
    // paste-and-wait instead of one per scene. Format validated live against gemini.google.com
    // at 8 images/4 scenes: a flat "generate each item, don't stop until all N" loop
    // instruction + one shared GLOBAL SETTINGS block (style/character design stated once, not
    // repeated per scene) + a flat numbered list, one line per image. An earlier attempt that
    // buried "two images per scene" inside a per-scene sentence instead of this flat numbered
    // list failed - see tasks/todo.md's 2026-08-26 entry.
    public static string BuildBatchDualOrientationPrompt(IReadOnlyList<string> scenePrompts, string stylePrompt, string portraitStyleSuffix, bool matchReference)
    {
        var imageCount = scenePrompts.Count * 2;

        var consistency = matchReference
            ? " Match the reference image's face, identity, body build, robe design/colors and overall art style in every image. " +
              "Do not copy the reference image's pose, camera angle or framing - use a new pose and camera angle that fits each scene description below."
            : "";

        var lines = new List<string>();
        var n = 1;
        foreach (var scenePrompt in scenePrompts)
        {
            lines.Add($"{n++}. Widescreen Video Frame (16:9 Aspect Ratio) - {scenePrompt}");
            lines.Add($"{n++}. Subtitle-Ready Portrait (9:16 Aspect Ratio) - {scenePrompt}");
        }

        return "Generate each item in the list below as a separate individual image. Call the image generation model once per item - " +
            $"{imageCount} items below means {imageCount} separate image-generation calls. Do not stop generating until all {imageCount} " +
            "requested images are generated. Each image must follow these exact specifications:\n\n" +
            $"GLOBAL SETTINGS: {stylePrompt}. {portraitStyleSuffix}{consistency} Image 1 defines this character's exact design - " +
            "proportions, colors, face, outfit - every subsequent image must depict the identical character, unchanged in design; " +
            "only pose, camera framing and background change per the descriptions below.\n\n" +
            string.Join("\n", lines) +
            $"\n\nGenerate all {imageCount} as separate downloadable images - no more, no fewer, no extra alternate takes or bonus variations.";
    }
}
