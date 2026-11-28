// Free image-generation pool client (added 2026-11-22, rebuilt 2026-11-24).
using GiggleGarden.Shared;
//
// The pool is the fallback after Cloudflare FLUX in the rebuilt chain
// (see ImagePool.ChainOrder + hidden_files/image-pool.md):
//   1. Cloudflare FLUX (primary, in Program.cs GenerateSquareArtAsync)
//   2. Pexels stock photo — this client with only: "pexels", a
//      topic/entity --query (built by ImagePool.BuildPexelsQuery from
//      the script title + tags + scene narration) and --seen-file
//      <job>/pexels-used.json so no photo repeats in one job
//   3. OVHcloud SDXL — this client with only: "ovh" (filler only)
//   4-6. Pollinations keyed / Z.ai CogView / Leonardo (dormant legs,
//      attempted by Program.cs directly, not through this client)
//
// art_pool.py itself now contains only OVHcloud + Pexels (Pollinations
// anonymous + AI Horde were discarded in the 2026-11-24 bake-off and
// deleted from the helper), governs its own ovh/pexels calls, and
// normalizes whatever it gets to exactly 1024x1024 JPEG, exiting 0
// only when a valid image is written and printing which provider
// succeeded. No key is handled here — every pool provider is anonymous
// except Pexels, which goes through its own vault-backed helper.
//
// The pool is tried AFTER FLUX in Program.cs GenerateSquareArtAsync;
// CharacterSource calls this client directly (its funnel never goes
// through that wrapper) with no --only, so the helper tries OVH then
// Pexels in its default order.
// Square-crop provenance (Drake 2026-11-27): the raw photo's
// dimensions plus the crop anchor the helper used, so the quality
// gate can flag a head-crop risk instead of shipping it.
// VerticalBias is the ffmpeg crop anchor: 0 = top, 0.5 = centre.
// Top level (not nested in ArtPoolClient) so Program.cs and the
// quality gate can name it without qualification.
internal sealed record ArtProvenance(int RawWidth, int RawHeight, double VerticalBias);

static class ArtPoolClient
{
    // Maps the art_pool.py provider name printed on success
    // ("artpool: <name> -> <out>") to the ArtSource value recorded on the
    // scene (B11): the pool's Pexels leg is a real photo, not AI art,
    // and a reviewer must be able to tell. Returns null when the output
    // carries no provider line (older helper output). "ovh", "pexels"
    // and "wikimedia" are mapped now: the helper only ever returns
    // those three, and the discarded-leg mappings (pollinations/horde)
    // were dropped with the legs — an unexpected name passes through
    // raw, so the quality gate rejects it as an unknown source instead
    // of silently relabelling it.
    internal static string? ParseProviderName(string stdout)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            stdout ?? "", @"artpool:\s*(\w+)\s*->");
        if (!m.Success) return null;
        return m.Groups[1].Value switch
        {
            "ovh" => "ovhcloud",
            "pexels" => "pexels-stock",
            "wikimedia" => "wikimedia-commons",
            var other => other,
        };
    }

    // Parses the "(raw WxH, crop top|centre)" provenance art_pool.py
    // prints on its success line. Null when the helper predates the
    // provenance line or the line is absent (older helper, hand-placed
    // file) — the gate then has no crop to check and skips it.
    internal static ArtProvenance? ParseProvenance(string stdout)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            stdout ?? "", @"\(raw (\d+)x(\d+), crop (top|centre)\)");
        if (!m.Success) return null;
        return new ArtProvenance(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            m.Groups[3].Value == "top" ? 0.0 : 0.5);
    }

    // Generates one 1024x1024 square source image via the pool helper.
    // The helper writes the normalized JPEG directly to outputPath.
    public static async Task<string> GenerateSquareAsync(
        GenConfig cfg, string prompt, string outputPath, int maxAttempts = 2)
    {
        var (path, _) = await GenerateSquareWithSourceAsync(cfg, prompt, outputPath, maxAttempts);
        return path;
    }

    // Same as GenerateSquareAsync, but also returns which pool provider
    // produced the image (B11), parsed from the helper's stdout line.
    // Already-on-disk returns (path, null): the source is unknown from
    // the file alone, and the caller keeps the scene's saved ArtSource.
    // `only` forces a single pool leg ("ovh" or "pexels"); `query` is
    // passed to Pexels verbatim (omit it and the helper derives one
    // from the prompt); `seenFile` is the per-job JSON list of Pexels
    // photo ids already used, so no photo repeats in one job.
    public static async Task<(string Path, string? Source)> GenerateSquareWithSourceAsync(
        GenConfig cfg, string prompt, string outputPath, int maxAttempts = 2)
        => await GenerateSquareWithSourceAsync(cfg, prompt, outputPath, null, null, null, maxAttempts);

    public static async Task<(string Path, string? Source)> GenerateSquareWithSourceAsync(
        GenConfig cfg, string prompt, string outputPath,
        string? only, string? query, string? seenFile, int maxAttempts = 2)
    {
        var (path, source, _) = await GenerateSquareWithProvenanceAsync(
            cfg, prompt, outputPath, only, query, seenFile, maxAttempts);
        return (path, source);
    }

    // Same as GenerateSquareWithSourceAsync, but also returns the
    // square-crop provenance the helper printed (see ParseProvenance),
    // so the caller can run the head-crop quality check on it.
    public static async Task<(string Path, string? Source, ArtProvenance? Provenance)> GenerateSquareWithProvenanceAsync(
        GenConfig cfg, string prompt, string outputPath,
        string? only, string? query, string? seenFile, int maxAttempts = 2)
    {
        // Quality gate (video #4): never ask the model to draw text —
        // free models cannot spell. Sanitize here as well as in
        // Program.GenerateSquareArtAsync, because CharacterSource calls
        // this funnel directly (it never goes through that wrapper).
        // Sanitize is idempotent, so a prompt already cleaned upstream
        // passes through unchanged — and the suffix is appended exactly
        // once (the guard is the full NoTextSuffix, not just "wide
        // shot"; see QualityGate.SanitizeImagePrompt).
        prompt = QualityGate.SanitizeImagePrompt(prompt).Prompt;

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (ProcessHelper.AlreadyOnDisk(outputPath)) return (outputPath, null, null);

        // L50: the final attempt is caught too (the old loop's catch ran
        // only while attempt < maxAttempts, so the summary throw below
        // was dead code and the last error's reason was lost). The
        // terminal record now names the leg, the scene file, the attempt
        // count and the last error.
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var args = new List<string>
                {
                    cfg.ArtPoolHelperPath,
                    "--prompt", prompt,
                    "--out", outputPath,
                    "--width", "1024",
                    "--height", "1024",
                };
                if (!string.IsNullOrWhiteSpace(only)) { args.Add("--only"); args.Add(only); }
                if (!string.IsNullOrWhiteSpace(query)) { args.Add("--query"); args.Add(query); }
                if (!string.IsNullOrWhiteSpace(seenFile)) { args.Add("--seen-file"); args.Add(seenFile); }
                var result = await ProcessHelper.RunAsync("python3", args, timeoutMinutes: 15);
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
                var source = ParseProviderName(result.Stdout);
                Console.WriteLine($"  Art pool square art: {Path.GetFileName(outputPath)} ({bytes / 1024} KB, {Text.Tail(result.Stdout, 120).Trim()})");
                return (outputPath, source, ParseProvenance(result.Stdout));
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Console.WriteLine($"  Art pool square art attempt {attempt} failed ({ex.Message}), retrying...");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        Console.WriteLine($"  art leg art-pool scene {Path.GetFileName(outputPath)} failed after {maxAttempts} attempts, last error: {lastError?.Message}");
        throw new Exception($"Art pool square art generation failed after {maxAttempts} attempts for {outputPath} (leg art-pool, scene {Path.GetFileName(outputPath)}): last error: {lastError?.Message}");
    }
}
