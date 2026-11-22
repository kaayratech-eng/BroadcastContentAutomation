// Free image-generation pool client (first step, added 2026-11-22).
using GiggleGarden.Shared;
//
// The three keyed scene-art providers all failed at once on 2026-11-21
// (Cloudflare FLUX daily quota exhausted, Pollinations $0 Quest balance,
// Cloudflare SDXL model deprecated 2026-11-20), blocking video #2's prep.
// This client shells out to the art_pool.py helper, which tries free
// providers in order until one returns a real image:
//   1. OVHcloud AI Endpoints SDXL (anonymous, 1024x1024 PNG, no logo)
//   2. Pollinations anonymous (768x768 Sana JPEG, logo cropped, upscaled)
//   3. AI Horde (anonymous, 512x512 WEBP, upscaled)
//   4. Pexels stock photo (real photo, not AI-generated; last resort)
// The helper normalizes whatever it gets to exactly 1024x1024 JPEG and
// exits 0 only when a valid image is written, printing which provider
// succeeded. No key is handled here — every pool provider is anonymous
// except Pexels, which goes through its own vault-backed helper.
//
// The pool is tried FIRST in Program.cs GenerateSquareArtAsync; the keyed
// providers (Pollinations keyed, Cloudflare FLUX/SDXL) remain as backup
// for when the pool is exhausted (e.g. OVHcloud + Pollinations both
// rate-limited in the same minute).
static class ArtPoolClient
{
    // Generates one 1024x1024 square source image via the pool helper.
    // The helper writes the normalized JPEG directly to outputPath.
    public static async Task<string> GenerateSquareAsync(
        GenConfig cfg, string prompt, string outputPath, int maxAttempts = 2)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (ProcessHelper.AlreadyOnDisk(outputPath)) return outputPath;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await ProcessHelper.RunAsync("python3",
                [
                    cfg.ArtPoolHelperPath,
                    "--prompt", prompt,
                    "--out", outputPath,
                    "--width", "1024",
                    "--height", "1024",
                ], timeoutMinutes: 15);
                if (result.ExitCode != 0)
                    throw new Exception(
                        $"art pool helper exit {result.ExitCode}: " +
                        Text.Tail(result.Stderr + result.Stdout, 500));
                ProcessHelper.RequireFile(outputPath, "Art pool call");
                var (w, h) = await VideoAssembler.GetImageDimensionsAsync(cfg, outputPath);
                if (w != 1024 || h != 1024)
                    throw new Exception(
                        $"art pool returned {w}x{h}, expected 1024x1024");
                var bytes = new FileInfo(outputPath).Length;
                Console.WriteLine($"  Art pool square art: {Path.GetFileName(outputPath)} ({bytes / 1024} KB, {Text.Tail(result.Stdout, 120).Trim()})");
                return outputPath;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Console.WriteLine($"  Art pool square art attempt {attempt} failed ({ex.Message}), retrying...");
            }
        }
        throw new Exception($"Art pool square art generation failed after {maxAttempts} attempts for {outputPath}.");
    }
}
