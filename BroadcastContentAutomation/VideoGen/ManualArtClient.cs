// Human-in-the-loop replacement for ImageClient's automated OpenAI/Stability calls (see
// tasks/todo.md, Deliverable 6): prints a Gemini-ready prompt - the same by-hand pattern
// already used for this project's avatar/banner art - then blocks until a person has
// generated it at gemini.google.com and dropped the file at the expected path. Used by
// both profiles for character art. Chronicle & Chaos additionally relies on
// StockFootageClient for context/b-roll scenes, which needs no human step at all.
static class ManualArtClient
{
    // Shared with CharacterSource's pool scan, which needs the same "what counts as an
    // image" list. Gemini's web UI saves .jpg/.jpeg or .webp depending on browser/export
    // choice, not whatever extension the expected outputPath happens to use below, so both
    // need to accept all of these rather than just the one the caller asked for.
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    // Blocks on Console.ReadLine until an image exists at outputPath, or at the same base
    // name under any of ImageExtensions, and is non-empty - there is no API response to
    // await, so this is deliberately synchronous work wrapped in a Task, not real async
    // I/O. referenceImagePath, when given, is a previously generated character portrait
    // the human should attach to the Gemini chat so the new image keeps the same
    // appearance - Gemini's own multi-turn/attach flow does the consistency work an API's
    // image-to-image endpoint (ImageClient.GenerateWithReferenceAsync) would otherwise do.
    public static Task<string> PromptAndWaitAsync(
        string prompt, string outputPath, ImageClient.Orientation orientation, string? referenceImagePath = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var basePath = Path.Combine(directory ?? "", Path.GetFileNameWithoutExtension(outputPath));

        var orientationLabel = orientation == ImageClient.Orientation.Portrait
            ? "9:16 vertical (portrait)"
            : "16:9 horizontal (landscape)";

        var step = 1;

        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine("  MANUAL ART NEEDED - go to https://gemini.google.com");
        if (referenceImagePath is not null)
            Console.WriteLine($"  {step++}. Attach this reference image to the chat first, for consistency: {referenceImagePath}");
        Console.WriteLine($"  {step++}. Paste this prompt (orientation: {orientationLabel}):");
        Console.WriteLine();
        Console.WriteLine($"    {prompt}");
        Console.WriteLine();
        Console.WriteLine($"  {step}. Save the generated image to {basePath}, any extension ({string.Join(", ", ImageExtensions)}):");
        Console.WriteLine($"    {outputPath}");
        Console.WriteLine("  Then press Enter here to continue.");
        Console.WriteLine("  ================================================================");

        while (true)
        {
            Console.ReadLine();

            var found = FindSaved(outputPath, basePath);
            if (found is not null)
            {
                Console.WriteLine($"  Found {Path.GetFileName(found)} ({new FileInfo(found).Length / 1024} KB), continuing.");
                return Task.FromResult(found);
            }

            Console.WriteLine($"  Nothing at \"{basePath}\" (checked {string.Join(", ", ImageExtensions)}) yet - save the image there, then press Enter again.");
        }
    }

    private static string? FindSaved(string outputPath, string basePath)
    {
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0) return outputPath;

        foreach (var ext in ImageExtensions)
        {
            var candidate = basePath + ext;
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 0) return candidate;
        }

        return null;
    }
}
