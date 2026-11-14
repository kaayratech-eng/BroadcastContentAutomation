// Cloudflare Workers AI scene-art client (backup, decided 2026-11-11).
using GiggleGarden.Shared;
//
// Two models, two shapes:
// - SDXL base (@cf/stabilityai/stable-diffusion-xl-base-1.0) honours
//   width/height, so it can generate a 1024x1024 square for the
//   single-source art flow (last step in the square chain). Verified
//   2026-11-10: exact 1080x1920 PNG in 24.3s, no logo. Prompt adherence
//   is looser than FLUX (the test scene came back as two robed women
//   instead of Medusa before Athena's statue), and the neuron cost at
//   this size is unmeasured.
// - FLUX.1 Schnell is faster (2.2s) and better composed, and the REST
//   endpoint only accepts prompt + steps and always returns 1024x1024
//   square (width/height rejected with HTTP 400, verified 2026-11-10).
//   It is the second step in the square-source chain (same FLUX.1
//   Schnell model as Pollinations), returning the raw square for the
//   caller to cover-crop — no blur-pad anywhere in the scene-art flow
//   since the identical-image rule of 2026-11-11.
//
// The API token lives in Muse's secure vault (custom.cloudflare), so this
// client shells out to the cf_workers_ai.py helper, which performs the
// authd surrogate exchange. No key value is handled here.
static class CloudflareArtClient
{
    // SDXL: called with 1024x1024 for the square-source chain (last step),
    // where it honours the size exactly; any other size is cover-cropped.
    public static async Task<string> GenerateAsync(
        GenConfig cfg, string prompt, string outputPath, int width, int height,
        int maxAttempts = 2)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (ProcessHelper.AlreadyOnDisk(outputPath)) return outputPath;

        var seed = ProcessHelper.SeedFor(outputPath);
        var temp = outputPath + ".raw";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await ProcessHelper.RunAsync("python3",
                [
                    cfg.CloudflareHelperPath, "sdxl",
                    "--account-id", cfg.CloudflareAccountId,
                    "--prompt", prompt,
                    "--width", width.ToString(),
                    "--height", height.ToString(),
                    "--steps", cfg.CloudflareSdxlSteps.ToString(),
                    "--guidance", cfg.CloudflareSdxlGuidance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--seed", seed.ToString(),
                    "--model", cfg.CloudflareImageModel,
                    "--out", temp,
                ], timeoutMinutes: 10);
                if (result.ExitCode != 0)
                    throw new Exception(
                        $"cloudflare sdxl helper exit {result.ExitCode}: " +
                        Text.Tail(result.Stderr + result.Stdout, 500));
                ProcessHelper.RequireFile(temp, "Cloudflare SDXL call");
                var (w, h) = await VideoAssembler.GetImageDimensionsAsync(cfg, temp);
                if (w == width && h == height)
                {
                    File.Move(temp, outputPath, overwrite: true);
                }
                else
                {
                    await VideoAssembler.CropScaleToExactAsync(cfg, temp, outputPath, width, height);
                    try { File.Delete(temp); } catch { }
                }
                var bytes = new FileInfo(outputPath).Length;
                Console.WriteLine($"  Cloudflare SDXL art: {Path.GetFileName(outputPath)} ({bytes / 1024} KB, sdxl, seed {seed})");
                return outputPath;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Console.WriteLine($"  Cloudflare SDXL art attempt {attempt} failed ({ex.Message}), retrying...");
            }
        }
        throw new Exception($"Cloudflare SDXL art generation failed after {maxAttempts} attempts for {outputPath}.");
    }

    // FLUX square: second step in the square-source chain. Returns the raw
    // 1024x1024 square (cover-cropped to square if the endpoint ever
    // returns another size) — no blur-pad fit; the caller derives both
    // orientations from it with a centre cover crop.
    public static async Task<string> GenerateFluxSquareAsync(
        GenConfig cfg, string prompt, string outputPath, int maxAttempts = 2)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (ProcessHelper.AlreadyOnDisk(outputPath)) return outputPath;

        var temp = outputPath + ".square.jpg";
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await ProcessHelper.RunAsync("python3",
                [
                    cfg.CloudflareHelperPath, "flux",
                    "--account-id", cfg.CloudflareAccountId,
                    "--prompt", prompt,
                    "--steps", cfg.CloudflareFluxSteps.ToString(),
                    "--out", temp,
                ], timeoutMinutes: 10);
                if (result.ExitCode != 0)
                    throw new Exception(
                        $"cloudflare flux helper exit {result.ExitCode}: " +
                        Text.Tail(result.Stderr + result.Stdout, 500));
                ProcessHelper.RequireFile(temp, "Cloudflare FLUX call");
                var (w, h) = await VideoAssembler.GetImageDimensionsAsync(cfg, temp);
                if (w == 1024 && h == 1024)
                {
                    File.Move(temp, outputPath, overwrite: true);
                }
                else
                {
                    await VideoAssembler.CropScaleToExactAsync(cfg, temp, outputPath, 1024, 1024);
                    try { File.Delete(temp); } catch { }
                }
                var bytes = new FileInfo(outputPath).Length;
                Console.WriteLine($"  Cloudflare FLUX square art: {Path.GetFileName(outputPath)} ({bytes / 1024} KB, flux)");
                return outputPath;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Console.WriteLine($"  Cloudflare FLUX square art attempt {attempt} failed ({ex.Message}), retrying...");
            }
        }
        throw new Exception($"Cloudflare FLUX square art generation failed after {maxAttempts} attempts for {outputPath}.");
    }
}
