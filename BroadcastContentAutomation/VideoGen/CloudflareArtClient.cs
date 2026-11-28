// Cloudflare Workers AI scene-art client (FLUX only, since 2026-11-24).
using GiggleGarden.Shared;
//
// FLUX.1 Schnell is fast (2.2s) and well composed, and the REST
// endpoint only accepts prompt + steps and always returns 1024x1024
// square (width/height rejected with HTTP 400, verified 2026-11-10).
// It is the FIRST step in the rebuilt square-source chain (see
// ImagePool.ChainOrder), returning the raw square for the caller to
// cover-crop — no blur-pad anywhere in the scene-art flow since the
// identical-image rule of 2026-11-11.
//
// Cloudflare SDXL is GONE (deprecated 2026-11-20, removed from the
// chain 2026-11-24 with its GenConfig values): the old GenerateAsync
// was its only caller-side user besides CharacterSource's twin chain,
// which lost the step at the same time.
//
// Quota: the free tier allows 10,000 Workers AI neurons/day, reset at
// 00:00 UTC. When the quota is out, the helper prints
// "HTTP {status} ... {\"code\": 4006 ...}" and exits 1 — a refused call
// spends nothing, so retrying it is pure waste: a quota failure throws
// immediately (no second attempt) so the chain can park the leg until
// the reset and fall through to Pexels + OVH.
//
// The API token lives in Muse's secure vault (custom.cloudflare), so this
// client shells out to the cf_workers_ai.py helper, which performs the
// authd surrogate exchange. No key value is handled here.
static class CloudflareArtClient
{
    // FLUX square: first step in the square-source chain. Returns the raw
    // 1024x1024 square (cover-cropped to square if the endpoint ever
    // returns another size) — no blur-pad fit; the caller derives both
    // orientations from it with a centre cover crop.
    public static async Task<string> GenerateFluxSquareAsync(
        GenConfig cfg, string prompt, string outputPath, int maxAttempts = 2)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (ProcessHelper.AlreadyOnDisk(outputPath)) return outputPath;

        // L50: the final attempt is caught too (see ArtPoolClient) — the
        // old summary throw was dead code. A quota refusal still
        // propagates untouched (neither catch matches it) so the chain
        // can park the leg until 00:00 UTC.
        Exception? lastError = null;
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
                    await VideoAssembler.CropScaleToSquareAsync(cfg, temp, outputPath);
                    try { File.Delete(temp); } catch { }
                }
                var bytes = new FileInfo(outputPath).Length;
                Console.WriteLine($"  Cloudflare FLUX square art: {Path.GetFileName(outputPath)} ({bytes / 1024} KB, flux)");
                return outputPath;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ImagePool.IsFluxQuotaError(ex.Message))
            {
                Console.WriteLine($"  Cloudflare FLUX square art attempt {attempt} failed ({ex.Message}), retrying...");
            }
            catch (Exception ex) when (!ImagePool.IsFluxQuotaError(ex.Message))
            {
                lastError = ex;
            }
        }
        Console.WriteLine($"  art leg cloudflare-flux scene {Path.GetFileName(outputPath)} failed after {maxAttempts} attempts, last error: {lastError?.Message}");
        throw new Exception($"Cloudflare FLUX square art generation failed after {maxAttempts} attempts for {outputPath} (leg cloudflare-flux, scene {Path.GetFileName(outputPath)}): last error: {lastError?.Message}");
    }
}
