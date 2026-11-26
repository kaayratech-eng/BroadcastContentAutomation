// Automated free scene-art client (Option 1 + Option 3, Hybrid: Muse for
// character, free API for scenes).
//
// Why this exists: ManualArtClient blocks on a human going to
// gemini.google.com per image. At 2-3 videos/day/channel that is 32-48+
// images/day for shorts alone, which cannot be manual. This client calls
// Pollinations.ai's free, no-key image API instead:
//   GET https://image.pollinations.ai/prompt/{prompt}?width=&height=&model=&seed=&nologo=true
// Verified 2026-11-04: a 768x1024 flux request returned a JPEG in ~6s.
// Limits, from the API's own docs / community docs (not verified live beyond
// the test above): anonymous use is rate-limited (reported 1 request / 15s),
// images are public via the CDN URL, and FLUX model licences restrict
// commercial use in some readings - check before monetised use. No
// image-to-image / reference-image support in the basic endpoint, so
// character consistency here is by prompt text (the full character
// description from ImageClient.BuildPrompt), not by uploading the
// Muse-generated character portrait. The Muse portrait is still the
// consistency anchor for the video: it is the character frame, and scene
// prompts describe that same character in words.
//
// Count reduction (Option 3): callers generate ONE portrait image per
// visual beat, not two orientations. VideoAssembler already falls back
// to the portrait asset with a blurred fill for the landscape render
// (see VideoAssembler.cs line ~108), so a missing LandscapeImagePath
// is not a failure - it is the intended derived-landscape path.
// Long-form VisualGroup reuse is GONE (fix sequence #2 ITEM 3, 2026-11-22):
// every scene gets its own group and its own visual now - one image per
// scene, resolved in Program.cs; this client is only called for a scene
// that has no pre-placed art on disk, exactly like ManualArtClient was.
static class AutomatedArtClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static DateTime _lastCallUtc = DateTime.MinValue;

    // Throttle to respect the reported anonymous rate limit. Serialised
    // with a simple lock because RunVisualLoopAsync is serial anyway.
    private static async Task ThrottleAsync(int delaySeconds)
    {
        var elapsed = DateTime.UtcNow - _lastCallUtc;
        var wait = TimeSpan.FromSeconds(delaySeconds) - elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        _lastCallUtc = DateTime.UtcNow;
    }

    public static async Task<string> GenerateAsync(
        string prompt, string outputPath, string model, int delaySeconds = 15, int maxAttempts = 3)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Reuse on disk, same contract as ManualArtClient: a resumed job
        // never regenerates an image it already has.
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0) return outputPath;

        // Deterministic seed per output file, so a retry of the same beat
        // reproduces the same image instead of a new random one.
        var seed = Math.Abs(outputPath.GetHashCode(StringComparison.Ordinal));

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await ThrottleAsync(delaySeconds);
                var url = "https://image.pollinations.ai/prompt/" + Uri.EscapeDataString(prompt) +
                    $"?width=1080&height=1920&model={Uri.EscapeDataString(model)}&seed={seed}&nologo=true";
                using var resp = await Http.GetAsync(url);
                if ((int)resp.StatusCode == 429 && attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30 * attempt));
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 1024) throw new Exception($"Pollinations returned only {bytes.Length} bytes.");
                await File.WriteAllBytesAsync(outputPath, bytes);
                Console.WriteLine($"  Automated art: {Path.GetFileName(outputPath)} ({bytes.Length / 1024} KB, model {model}, seed {seed})");
                return outputPath;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Console.WriteLine($"  Automated art attempt {attempt} failed ({ex.Message}), retrying...");
            }
        }
        throw new Exception($"Pollinations art generation failed after {maxAttempts} attempts for {outputPath}.");
    }
}
