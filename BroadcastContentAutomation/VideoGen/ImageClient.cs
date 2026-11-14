// Prompt assembly shared by ManualArtClient's per-scene call sites.
class ImageClient
{
    public enum Orientation { Landscape, Portrait, Square }

    // Used by ManualArtClient's per-scene call sites (CharacterSource.DrawAsync,
    // Program.cs's no-Vidu prep branch) to build the style+framing+consistency
    // prompt handed to the manual-art generation step.
    public static string BuildPrompt(string scenePrompt, string stylePrompt, string portraitStyleSuffix, Orientation orientation, bool matchReference)
    {
        var framing = orientation == Orientation.Portrait
            ? "Vertical 9:16 portrait composition, subject centred with generous headroom and " +
              "empty space in the lower third for subtitles."
            : orientation == Orientation.Square
                ? "Square 1:1 composition, main subject centred in the frame with generous space " +
                  "around the subject — the subject's head must be fully visible with clear space " +
                  "above it, and the subject should sit in the middle third of the frame — so the " +
                  "image can be cropped to both vertical 9:16 and horizontal 16:9 without losing " +
                  "the subject. Empty space in the lower third for subtitles."
                : "Horizontal 16:9 composition, subject centred, empty space in the lower third for subtitles.";

        var consistency = matchReference
            ? " Match the reference image's face, identity, body build, robe design/colors and overall art style. " +
              "Do not copy the reference image's pose, camera angle or framing - use a new pose and camera angle that fits the scene description above."
            : "";

        return $"{stylePrompt}. {scenePrompt}. {framing} {portraitStyleSuffix}{consistency}";
    }
}
