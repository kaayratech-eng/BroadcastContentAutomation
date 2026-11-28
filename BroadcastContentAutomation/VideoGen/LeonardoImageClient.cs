// Leonardo image client — DORMANT leg in the rebuilt image pool.
// See ImagePool.ChainOrder + hidden_files/image-pool.md.
using GiggleGarden.Shared;
//
// Leonardo.Ai via the vault-backed leonardo_image.py helper: POST
// https://cloud.leonardo.ai/api/rest/v1/generations (async — returns a
// generationId), then GET /generations/{id} until COMPLETE, then
// download the image URL. The key lives in Muse's secure vault
// (custom.leonardo) and the helper performs the authd surrogate
// exchange; no key value is handled here.
//
// DORMANT: no custom.leonardo connection exists yet, AND no model id
// is configured (GenConfig.LeonardoModelId is empty — model ids change,
// so none is hard-coded; pick a current one from GET /platformModels
// when activating). With no connection the helper fails fast with a
// surrogate error and the chain logs the leg dormant for the rest of
// the run (see ImagePool) instead of retrying it — a key-blocked
// failure throws immediately, no second attempt. The leg activates
// only after a key + model id exist AND it passes the visual bar (same
// bake-off prompt, 2 images, looked at). NOTE: this client has never
// run against the live API (no key, no model) — the helper is written
// from Leonardo's published API docs, not a live call. Leonardo API
// generations spend API credits: confirm the free daily allowance
// still applies before activating, or the leg stays dormant (the
// pipeline uses free tools only).
static class LeonardoImageClient
{
    // Generates one 1024x1024 square source image — the single source
    // both orientations are derived from. The downloaded file is kept
    // at 1024x1024: if the endpoint ever returns a different size, it
    // is centre-cropped to square instead.
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
        var temp = outputPath + ".raw";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await ProcessHelper.RunAsync("python3",
                [
                    cfg.LeonardoHelperPath, "image",
                    "--prompt", prompt,
                    "--out", temp,
                    "--width", "1024",
                    "--height", "1024",
                    "--model-id", cfg.LeonardoModelId,
                ], timeoutMinutes: 10);
                if (result.ExitCode != 0)
                    throw new Exception(
                        $"leonardo image helper exit {result.ExitCode}: " +
                        Text.Tail(result.Stderr + result.Stdout, 500));
                ProcessHelper.RequireFile(temp, "Leonardo image call");
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
                Console.WriteLine($"  Leonardo square art: {Path.GetFileName(outputPath)} ({bytes / 1024} KB, leonardo)");
                return outputPath;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ImagePool.IsKeyBlockedError(ex.Message))
            {
                Console.WriteLine($"  Leonardo square art attempt {attempt} failed ({ex.Message}), retrying...");
            }
            catch (Exception ex) when (!ImagePool.IsKeyBlockedError(ex.Message))
            {
                lastError = ex;
            }
        }
        Console.WriteLine($"  art leg leonardo-image scene {Path.GetFileName(outputPath)} failed after {maxAttempts} attempts, last error: {lastError?.Message}");
        throw new Exception($"Leonardo square art generation failed after {maxAttempts} attempts for {outputPath} (leg leonardo-image, scene {Path.GetFileName(outputPath)}): last error: {lastError?.Message}");
    }
}
