// Pollinations keyed-endpoint scene-art client (primary, decided 2026-11-11).
using GiggleGarden.Shared;
//
// The no-key endpoint (AutomatedArtClient) turned out to serve Sana at
// 576x1024 with a logo despite model=flux, so the pipeline uses the keyed
// endpoint instead: GET https://gen.pollinations.ai/image/{prompt} with
// model=flux and an Authorization: Bearer key. Verified 2026-11-10: HTTP
// 200 in 25.5s, real FLUX.1 Schnell (EXIF Make tag), 768x1280, no logo.
//
// The key lives in Muse's secure vault (custom.pollinations), so this
// client shells out to the pollinations_image.py helper, which performs
// the authd surrogate exchange. No key value is handled here.
//
// DORMANT until Pollen exists (balance 0, 2026-11-24): a $0 balance
// fails fast with HTTP 402, and a missing connection with a surrogate
// error. Either is a key-blocked failure, not a transient one —
// retrying it burns a real HTTP round-trip for nothing, so a
// key-blocked failure throws immediately (no second attempt) and the
// chain logs the leg dormant for the rest of the run (see ImagePool).
//
// The endpoint does not return the exact requested size (1080x1920 came
// back 768x1280), but a 1024x1024 request IS honoured (verified live
// 2026-11-11: 1024x1024 JPEG in 10.5s). The pipeline therefore generates
// ONE square image per scene and derives both orientations from it with
// VideoAssembler.CropScaleToSquareAsync — a cover crop that
// top-anchors portrait downloads so the subject's head survives
// (Drake 2026-11-27: a centre square crop of a tall portrait cut
// the head off), not a blur-pad — so portrait and landscape show
// the identical scene. This
// replaced the 2026-11-10 separate-orientation rule (two diffusion runs
// per scene), which the user found produced different images per
// orientation in the first test videos.
static class PollinationsKeyedClient
{
    // Generates one 1024x1024 square source image — the single source both
    // orientations are derived from (see Program.cs's art branch). The
    // downloaded file is kept at 1024x1024: if the endpoint ever returns a
    // different size, it is centre-cropped to square instead.
    public static async Task<string> GenerateSquareAsync(
        GenConfig cfg, string prompt, string outputPath, int maxAttempts = 2)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (ProcessHelper.AlreadyOnDisk(outputPath)) return outputPath;

        // L50: the final attempt is caught too (see ArtPoolClient) — the
        // old summary throw was dead code. A key-blocked failure still
        // propagates untouched (neither catch matches it) so the chain
        // logs the leg dormant instead of retrying a dead key.
        Exception? lastError = null;
        var seed = ProcessHelper.SeedFor(outputPath);
        var temp = outputPath + ".raw";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await ProcessHelper.RunAsync("python3",
                [
                    cfg.PollinationsHelperPath, "image",
                    "--prompt", prompt,
                    "--out", temp,
                    "--width", "1024",
                    "--height", "1024",
                    "--model", cfg.PollinationsModel,
                    "--seed", seed.ToString(),
                ], timeoutMinutes: 10);
                if (result.ExitCode != 0)
                    throw new Exception(
                        $"pollinations helper exit {result.ExitCode}: " +
                        Text.Tail(result.Stderr + result.Stdout, 500));
                ProcessHelper.RequireFile(temp, "Pollinations keyed call");
                var (w, h) = await VideoAssembler.GetImageDimensionsAsync(cfg, temp);
                if (w == 1024 && h == 1024)
                {
                    File.Move(temp, outputPath, overwrite: true);
                }
                else
                {
                    await VideoAssembler.CropScaleToSquareAsync(cfg, temp, outputPath);
                    try { File.Delete(temp); } catch { }
                }
                var bytes = new FileInfo(outputPath).Length;
                Console.WriteLine($"  Keyed Pollinations square art: {Path.GetFileName(outputPath)} ({bytes / 1024} KB, flux, seed {seed})");
                return outputPath;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ImagePool.IsKeyBlockedError(ex.Message))
            {
                Console.WriteLine($"  Keyed Pollinations square art attempt {attempt} failed ({ex.Message}), retrying...");
            }
            catch (Exception ex) when (!ImagePool.IsKeyBlockedError(ex.Message))
            {
                lastError = ex;
            }
        }
        Console.WriteLine($"  art leg pollinations-keyed scene {Path.GetFileName(outputPath)} failed after {maxAttempts} attempts, last error: {lastError?.Message}");
        throw new Exception($"Pollinations keyed square art generation failed after {maxAttempts} attempts for {outputPath} (leg pollinations-keyed, scene {Path.GetFileName(outputPath)}): last error: {lastError?.Message}");
    }
}
