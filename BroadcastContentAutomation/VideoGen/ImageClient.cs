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
}
