// Purpose-built thumbnails (fix #3, monetization audit 2026-11-26).
//
// The old pipeline used the first story scene's art, straight-cropped to
// 1280x720 with NO overlay text (the researched channels carried none).
// The audit reverses both halves of that rule for the thumbnail that
// actually gets the click:
//   * SOURCE: a verified REAL photo of the star (Wikimedia Commons first,
//     Pexels second) — never a FLUX/AI-generated face of a real person and
//     never a stock photo of a different person. For a ranked-star video
//     the selection returns null rather than an AI face; the finish path
//     then falls back to the old first-story-art thumb with a loud warning
//     (the pipeline never breaks on thumbnails — Rishi's variant pick is
//     the identity check anyway).
//   * WORDS: 1-3 large bold words burned in by ffmpeg afterwards. The
//     generated art itself stays text-free (FLUX bakes gibberish text
//     into signage), so the words are an overlay, never part of the image
//     prompt — see TrendlineNowProfile.ThumbnailGuidance.
//   * VARIANTS: 3 per video (different crop + wording); the sidecar's
//     ThumbnailPath points at the picked variant, default v1, so the
//     uploader keeps working unattended for new videos.
//
// FACE-CROP HONESTY: there is no face detector in this pipeline (no
// OpenCV, no ML model on either the VM or the laptop), so the crop cannot
// guarantee the face lands at 60-70% of the frame. The three variants
// differ in zoom + vertical anchor, which brackets the usual face
// positions in a square portrait photo (face in the upper half); Rishi
// picking a variant IS the face check. The live-video run (--make-
// thumbnails --source-scene N) exists precisely because viewing the
// photos beat the blind rule for Ronaldo (scene 1 is the Madeira statue;
// scene 8 is the close-up face).
using System.Globalization;
using System.Text;

static class ThumbnailBuilder
{
    public sealed record ThumbnailSource(string Path, string ArtSource, int SceneIndex);

    public sealed record ThumbnailVariant(
        string Path,
        string Words,
        int FontSizePx,
        double Zoom,
        double AnchorX,
        double AnchorY,
        double TextHeightAt320Px,
        bool MobileCheckPass,
        string PreviewPath);

    public sealed record BuiltThumbnails(
        ThumbnailSource? Source,
        string BasePath,
        IReadOnlyList<ThumbnailVariant> Variants);

    public sealed record ThumbnailBuildOptions(
        int? SourceSceneOverride = null,
        double AnchorXOverride = -1,
        double AnchorYOverride = -1);

    // Variant framings: (zoom, anchorX, anchorY). Anchors are fractions
    // (0..1) of the spare room after the 1280x720 crop window is cut from
    // the zoomed square. v1 tight + upper (face-forward), v2 medium +
    // upper-centre, v3 widest + top band. Zoom is relative to the square
    // source (1.25 = just covers 1280 wide); the ffmpeg filter clamps the
    // scale so the crop never exceeds the image.
    internal static readonly (double Zoom, double AnchorX, double AnchorY)[] VariantSpecs =
    [
        (1.55, 0.5, 0.08),
        (1.35, 0.5, 0.30),
        (1.25, 0.5, 0.00),
    ];

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "A", "AN", "THE", "OF", "TO", "IN", "ON", "FOR", "AND", "OR",
        "HIS", "HER", "ITS", "WITH", "AFTER", "BEFORE", "IS", "ARE",
        "WAS", "WERE", "BE", "BY", "AT", "FROM", "AS", "IT", "S",
        "THAT", "COULD", "WOULD", "WILL", "CAN", "HE", "SHE", "THEY",
        "THIS", "WHAT", "WHY", "HOW", "WHEN", "UP", "DOWN", "OUT",
        "OFF", "NEW", "HERE", "MEANS",
    };

    // A real photo of the subject, in preference order. Ranked-star
    // videos NEVER fall through to AI art here: when no real photo
    // exists the result is null and the caller falls back to the legacy
    // thumb with a warning (see BuildVariantsAsync / RunAssembleAsync).
    // Non-star videos keep the old behaviour as the last resort (first
    // story art of any source — those videos have no real face to get
    // wrong, and their art is usually AI illustration by design).
    public static ThumbnailSource? SelectThumbnailSource(VideoScript script, bool isRankedStar)
    {
        string? ArtPath(Scene scene)
        {
            if (!string.IsNullOrWhiteSpace(scene.LandscapeImagePath) && File.Exists(scene.LandscapeImagePath))
                return scene.LandscapeImagePath;
            if (!string.IsNullOrWhiteSpace(scene.ImagePath) && File.Exists(scene.ImagePath))
                return scene.ImagePath;
            return null;
        }

        // Scenes in story order (index >= 1 first — scene 0 is the title
        // card), then scene 0 as a last resort.
        IEnumerable<(int Index, Scene Scene)> OrderedScenes()
        {
            for (var i = 1; i < script.Scenes.Count; i++) yield return (i, script.Scenes[i]);
            if (script.Scenes.Count > 0) yield return (0, script.Scenes[0]);
        }

        foreach (var (index, scene) in OrderedScenes())
        {
            if (string.Equals(scene.ArtSource, "wikimedia-commons", StringComparison.OrdinalIgnoreCase)
                && ArtPath(scene) is { } path)
                return new ThumbnailSource(path, scene.ArtSource!, index);
        }

        foreach (var (index, scene) in OrderedScenes())
        {
            if (string.Equals(scene.ArtSource, "pexels-stock", StringComparison.OrdinalIgnoreCase)
                && ArtPath(scene) is { } path)
                return new ThumbnailSource(path, scene.ArtSource!, index);
        }

        if (!isRankedStar)
        {
            foreach (var (index, scene) in OrderedScenes())
            {
                if (ArtPath(scene) is { } path)
                    return new ThumbnailSource(path, scene.ArtSource ?? "unknown", index);
            }
        }

        return null;
    }

    // 1-3 words per variant, derived from the title with a mechanical
    // rule (no model call, no new spend): significant words = title
    // tokens minus stopwords, minus the star's name, minus single
    // letters. v1 = last two (the payoff/stakes, usually after the dash),
    // v2 = first two (the topic), v3 = longest single word (ties: the
    // earliest). Title and thumbnail must say different things, so the
    // star's name never appears in the words.
    public static (string V1, string V2, string V3) DeriveOverlayWords(string title, string? starName)
    {
        var starTokens = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(starName))
        {
            foreach (var token in Tokenize(starName))
                starTokens.Add(token);
        }

        var words = Tokenize(title)
            .Where(t => !Stopwords.Contains(t) && !starTokens.Contains(t))
            .ToList();

        if (words.Count == 0)
            return ("SPOTLIGHT", "SPOTLIGHT", "SPOTLIGHT");
        if (words.Count == 1)
            return (words[0], words[0], words[0]);

        var v1 = string.Join(" ", words.TakeLast(2));
        var v2 = string.Join(" ", words.Take(2));
        var longest = words.MaxBy(w => w.Length)!;
        return (v1, v2, longest);
    }

    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        // Single letters are initials ("D.C." -> D, C) or possessive
        // fragments ("Messi's" -> MESSI, S); digits ("GTA 6") stay.
        return tokens.Where(t => t.Length > 1 || char.IsDigit(t[0])).ToList();
    }

    // Bold-cap font size that fits the words in the 1280px thumbnail
    // with 50px margins each side. DejaVu Sans Bold / Arial Bold caps
    // average ~0.70em advance; the 0.70 factor keeps the text inside
    // the frame instead of trusting the estimate. Clamped to [60, 140].
    public static int FitFontSize(string words, int maxWidthPx = 1180)
    {
        if (string.IsNullOrWhiteSpace(words)) return 140;
        var fitted = (int)Math.Floor(maxWidthPx / (0.70 * words.Length));
        return Math.Clamp(fitted, 60, 140);
    }

    // 320px mobile legibility check (a geometric check, not OCR: there
    // is no text recogniser in this pipeline). The overlay is drawn at
    // 1280px wide and the phone renders it at 320px, so the cap height
    // shrinks by 4x; DejaVu/Arial caps are ~0.73em. PASS needs >= 9px
    // cap height at 320px wide — below that the words are a smear on a
    // phone. The 320px preview file is rendered alongside every variant
    // so the check is verified against a real scaled render, not just
    // the arithmetic.
    public static (double TextHeightAt320Px, bool Pass) CheckMobileLegibility(int fontSizePx)
    {
        var height = fontSizePx * 0.73 * 320.0 / 1280.0;
        return (height, height >= 9.0 && fontSizePx >= 60);
    }

    // Bold sans font, resolved per-OS: the finish path runs on the
    // laptop (Windows), the live-variant run on the VM (Linux). First
    // existing candidate wins; the error names every path searched.
    public static string ResolveThumbnailFontPath()
    {
        string[] candidates =
        [
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
            @"C:\Windows\Fonts\arialbd.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        ];
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException(
            "No bold sans font found for the thumbnail overlay (searched: " +
            string.Join(", ", candidates) + ").");
    }

    internal static string Invariant(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    // Builds the 3 variants for a video. legacyBase is the old
    // first-story-art thumb base (clip-frame / scene-0 / render-frame
    // fallbacks included); the real-photo selection above replaces it
    // whenever it finds one. outputBase is the file stem, e.g.
    // "<dir>/<slug>.thumb" -> "<slug>.thumb-v1.jpg" (+ "-v2", "-v3"),
    // plus a "<slug>.thumb.jpg" copy of v1 so the uploader's legacy
    // direct path keeps resolving to the picked variant. Throws when
    // there is no base image at all; the finish path catches that and
    // falls back to the old single thumb.
    public static async Task<BuiltThumbnails> BuildVariantsAsync(
        GenConfig cfg,
        VideoScript script,
        string legacyBase,
        string outputBase,
        ThumbnailBuildOptions? options = null)
    {
        options ??= new ThumbnailBuildOptions();
        var starMatch = StarRanking.TryMatchStar(script.Title);
        var isRankedStar = starMatch is not null;

        ThumbnailSource? source = null;
        string basePath;
        string? forcedPath = null;
        if (options.SourceSceneOverride is { } sceneIndex
            && sceneIndex >= 0 && sceneIndex < script.Scenes.Count)
        {
            // --source-scene N: a human viewed the photos and picked one
            // (live-video run). LandscapeImagePath wins; ImagePath is the
            // portrait asset, used only when no landscape art exists. An
            // override naming a scene with no art on disk falls through
            // to the normal selection instead of failing.
            var scene = script.Scenes[sceneIndex];
            if (!string.IsNullOrWhiteSpace(scene.LandscapeImagePath) && File.Exists(scene.LandscapeImagePath))
                forcedPath = scene.LandscapeImagePath;
            else if (!string.IsNullOrWhiteSpace(scene.ImagePath) && File.Exists(scene.ImagePath))
                forcedPath = scene.ImagePath;
        }
        // L56: an override with no art on disk used to fall through
        // silently — say so, naming the requested scene.
        if (options.SourceSceneOverride is { } requestedScene && forcedPath is null)
            Console.WriteLine($"Thumbnail: --source-scene {requestedScene} has no art on disk — falling through to normal selection.");
        if (forcedPath is not null && options.SourceSceneOverride is { } forcedIndex)
        {
            source = new ThumbnailSource(forcedPath, script.Scenes[forcedIndex].ArtSource ?? "unknown", forcedIndex);
            basePath = forcedPath;
        }
        else
        {
            source = SelectThumbnailSource(script, isRankedStar);
            basePath = source?.Path ?? legacyBase;
        }
        // L56: the null-source decision used to be silent here — the
        // caller warning never named the base actually used. Log it at
        // the decision point, with the legacy base path.
        if (source is null && starMatch is not null)
            Console.WriteLine($"Thumbnail: no verified real photo for star '{starMatch.Value.Star.Name}' — using legacy base {basePath}");

        if (!File.Exists(basePath))
            throw new FileNotFoundException($"Thumbnail base image not found: {basePath}", basePath);

        var (v1Words, v2Words, v3Words) = DeriveOverlayWords(script.Title, starMatch?.Star.Name);
        var words = new[] { v1Words, v2Words, v3Words };
        var fontPath = ResolveThumbnailFontPath();
        var outDir = Path.GetDirectoryName(outputBase);
        if (!string.IsNullOrWhiteSpace(outDir)) Directory.CreateDirectory(outDir);

        var variants = new List<ThumbnailVariant>();
        for (var i = 0; i < VariantSpecs.Length; i++)
        {
            var (zoom, anchorX, anchorY) = VariantSpecs[i];
            if (options.AnchorXOverride >= 0) anchorX = options.AnchorXOverride;
            if (options.AnchorYOverride >= 0) anchorY = options.AnchorYOverride;
            var fontSize = FitFontSize(words[i]);
            var outPath = $"{outputBase}-v{i + 1}.jpg";
            await VideoAssembler.BuildThumbnailVariantAsync(
                cfg, basePath, words[i], fontSize, fontPath, zoom, anchorX, anchorY, outPath);
            var previewPath = $"{outputBase}-v{i + 1}-320.jpg";
            await VideoAssembler.RenderMobilePreviewAsync(cfg, outPath, previewPath);
            var (heightAt320, pass) = CheckMobileLegibility(fontSize);
            variants.Add(new ThumbnailVariant(
                outPath, words[i], fontSize, zoom, anchorX, anchorY, heightAt320, pass, previewPath));
        }

        // Legacy direct path (<slug>.thumb.jpg) keeps resolving — to the
        // picked default variant, not the old no-text crop.
        File.Copy(variants[0].Path, outputBase + ".jpg", overwrite: true);
        return new BuiltThumbnails(source, basePath, variants);
    }
}
