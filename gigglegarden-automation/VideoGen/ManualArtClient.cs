// Human-in-the-loop replacement for ImageClient's automated OpenAI/Stability calls (see
// tasks/todo.md, Deliverable 6): prints a Gemini-ready prompt - the same by-hand pattern
// already used for this project's avatar/banner art - then blocks until a person has
// generated it at gemini.google.com and dropped the file at the expected path. Used by
// both profiles for character art. Chronicle & Chaos additionally relies on
// StockFootageClient for context/b-roll scenes, which needs no human step at all.
static class ManualArtClient
{
    // Blocks on Console.ReadLine until outputPath exists and is non-empty - there is no API
    // response to await, so this is deliberately synchronous work wrapped in a Task, not
    // real async I/O. referenceImagePath, when given, is a previously generated character
    // portrait the human should attach to the Gemini chat so the new image keeps the same
    // appearance - Gemini's own multi-turn/attach flow does the consistency work an API's
    // image-to-image endpoint (ImageClient.GenerateWithReferenceAsync) would otherwise do.
    public static Task<string> PromptAndWaitAsync(
        string prompt, string outputPath, ImageClient.Orientation orientation, string? referenceImagePath = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

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
        Console.WriteLine($"  {step}. Save the generated image to exactly:");
        Console.WriteLine($"    {outputPath}");
        Console.WriteLine("  Then press Enter here to continue.");
        Console.WriteLine("  ================================================================");

        while (true)
        {
            Console.ReadLine();

            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            {
                Console.WriteLine($"  Found {Path.GetFileName(outputPath)} ({new FileInfo(outputPath).Length / 1024} KB), continuing.");
                return Task.FromResult(outputPath);
            }

            Console.WriteLine($"  Nothing at \"{outputPath}\" yet - save the image there, then press Enter again.");
        }
    }
}
