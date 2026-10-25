// Human-in-the-loop replacement for ImageClient's automated OpenAI/Stability calls (see
// tasks/todo.md, Deliverable 6): prints a Gemini-ready prompt - the same by-hand pattern
// already used for this project's avatar/banner art - then blocks until a person has
// generated it and dropped the file at the expected path. Points at gemini.google.com -
// briefly switched to Google AI Studio (aistudio.google.com) for its higher free-tier
// quota, reverted per tasks/todo.md's 2026-08-26 entry: AI Studio didn't work for the
// user in practice. Used by both profiles for character art. Chronicle & Chaos
// additionally relies on StockFootageClient for context/b-roll scenes, which needs no
// human step at all.
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

    // Same blocking pattern as PromptAndWaitAsync, but for ImageClient.BuildDualOrientationPrompt:
    // one Gemini prompt, two images expected back (landscape + portrait), so the human does
    // one generation instead of two. Loops until both are found rather than returning as soon
    // as either one lands, since a caller needing only one of them would just use the
    // single-image method instead.
    public static Task<(string Landscape, string Portrait)> PromptAndWaitDualAsync(
        string prompt, string landscapeOutputPath, string portraitOutputPath, string? referenceImagePath = null)
    {
        var landscapeDir = Path.GetDirectoryName(landscapeOutputPath);
        if (!string.IsNullOrEmpty(landscapeDir)) Directory.CreateDirectory(landscapeDir);
        var portraitDir = Path.GetDirectoryName(portraitOutputPath);
        if (!string.IsNullOrEmpty(portraitDir)) Directory.CreateDirectory(portraitDir);

        var landscapeBase = Path.Combine(landscapeDir ?? "", Path.GetFileNameWithoutExtension(landscapeOutputPath));
        var portraitBase = Path.Combine(portraitDir ?? "", Path.GetFileNameWithoutExtension(portraitOutputPath));

        var step = 1;

        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine("  MANUAL ART NEEDED (2 images, 1 prompt) - go to https://gemini.google.com");
        if (referenceImagePath is not null)
            Console.WriteLine($"  {step++}. Attach this reference image to the chat first, for consistency: {referenceImagePath}");
        Console.WriteLine($"  {step++}. Paste this prompt:");
        Console.WriteLine();
        Console.WriteLine($"    {prompt}");
        Console.WriteLine();
        Console.WriteLine($"  {step++}. Save Image 1 (16:9 landscape) to {landscapeBase}, any extension ({string.Join(", ", ImageExtensions)}):");
        Console.WriteLine($"    {landscapeOutputPath}");
        Console.WriteLine($"  {step}. Save Image 2 (9:16 portrait) to {portraitBase}, any extension ({string.Join(", ", ImageExtensions)}):");
        Console.WriteLine($"    {portraitOutputPath}");
        Console.WriteLine("  Then press Enter here to continue.");
        Console.WriteLine("  ================================================================");

        while (true)
        {
            Console.ReadLine();

            var landscapeFound = FindSaved(landscapeOutputPath, landscapeBase);
            var portraitFound = FindSaved(portraitOutputPath, portraitBase);

            if (landscapeFound is not null && portraitFound is not null)
            {
                Console.WriteLine($"  Found {Path.GetFileName(landscapeFound)} and {Path.GetFileName(portraitFound)}, continuing.");
                return Task.FromResult((landscapeFound, portraitFound));
            }

            var missing = new List<string>();
            if (landscapeFound is null) missing.Add($"landscape at \"{landscapeBase}\"");
            if (portraitFound is null) missing.Add($"portrait at \"{portraitBase}\"");
            Console.WriteLine($"  Still missing: {string.Join(" and ", missing)} (checked {string.Join(", ", ImageExtensions)}) - save it, then press Enter again.");
        }
    }

    // Batch counterpart to PromptAndWaitDualAsync: one prompt (see
    // ImageClient.BuildBatchDualOrientationPrompt), many scenes' worth of landscape+portrait
    // pairs expected back. Loops until every pair is on disk, printing only what's still
    // missing on each re-check. Whichever extension the human actually saved with, the file is
    // moved to the exact requested outputPath - unlike PromptAndWaitDualAsync's single-scene
    // case, a batched scene's path may already have been read by a later scene reusing it
    // (RunVisualLoopAsync's visual-group reuse), so the path has to land exactly where planned.
    public static Task<IReadOnlyList<(string Landscape, string Portrait)>> PromptAndWaitBatchAsync(
        string prompt, IReadOnlyList<(string LandscapeOutputPath, string PortraitOutputPath)> pairs, string? referenceImagePath = null)
    {
        var bases = new List<(string LandscapeBase, string PortraitBase)>();
        foreach (var (landscapeOutputPath, portraitOutputPath) in pairs)
        {
            var landscapeDir = Path.GetDirectoryName(landscapeOutputPath);
            if (!string.IsNullOrEmpty(landscapeDir)) Directory.CreateDirectory(landscapeDir);
            var portraitDir = Path.GetDirectoryName(portraitOutputPath);
            if (!string.IsNullOrEmpty(portraitDir)) Directory.CreateDirectory(portraitDir);

            bases.Add((
                Path.Combine(landscapeDir ?? "", Path.GetFileNameWithoutExtension(landscapeOutputPath)),
                Path.Combine(portraitDir ?? "", Path.GetFileNameWithoutExtension(portraitOutputPath))));
        }

        var step = 1;
        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine($"  MANUAL ART NEEDED ({pairs.Count * 2} images, 1 prompt, {pairs.Count} scenes) - go to https://gemini.google.com");
        if (referenceImagePath is not null)
            Console.WriteLine($"  {step++}. Attach this reference image to the chat first, for consistency: {referenceImagePath}");
        Console.WriteLine($"  {step++}. Paste this prompt (this many images can take a few minutes to finish generating):");
        Console.WriteLine();
        Console.WriteLine($"    {prompt}");
        Console.WriteLine();
        Console.WriteLine($"  {step}. Save each pair, any extension ({string.Join(", ", ImageExtensions)}):");
        for (var i = 0; i < pairs.Count; i++)
            Console.WriteLine($"       Scene {i + 1}: landscape -> {pairs[i].LandscapeOutputPath}   portrait -> {pairs[i].PortraitOutputPath}");
        Console.WriteLine("  Then press Enter here to continue.");
        Console.WriteLine("  ================================================================");

        while (true)
        {
            Console.ReadLine();

            var results = new List<(string Landscape, string Portrait)>();
            var missing = new List<string>();

            for (var i = 0; i < pairs.Count; i++)
            {
                var landscapeFound = FindSaved(pairs[i].LandscapeOutputPath, bases[i].LandscapeBase);
                var portraitFound = FindSaved(pairs[i].PortraitOutputPath, bases[i].PortraitBase);

                if (landscapeFound is not null && portraitFound is not null)
                {
                    if (!string.Equals(landscapeFound, pairs[i].LandscapeOutputPath, StringComparison.OrdinalIgnoreCase))
                        File.Move(landscapeFound, pairs[i].LandscapeOutputPath, overwrite: true);
                    if (!string.Equals(portraitFound, pairs[i].PortraitOutputPath, StringComparison.OrdinalIgnoreCase))
                        File.Move(portraitFound, pairs[i].PortraitOutputPath, overwrite: true);

                    results.Add((pairs[i].LandscapeOutputPath, pairs[i].PortraitOutputPath));
                }
                else
                {
                    if (landscapeFound is null) missing.Add($"scene {i + 1} landscape at \"{bases[i].LandscapeBase}\"");
                    if (portraitFound is null) missing.Add($"scene {i + 1} portrait at \"{bases[i].PortraitBase}\"");
                }
            }

            if (missing.Count == 0)
            {
                Console.WriteLine($"  Found all {pairs.Count * 2} images, continuing.");
                return Task.FromResult<IReadOnlyList<(string, string)>>(results);
            }

            Console.WriteLine($"  Still missing {missing.Count}: {string.Join(", ", missing)} - save them, then press Enter again.");
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
