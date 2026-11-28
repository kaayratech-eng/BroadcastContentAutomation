// Fix #3 (monetization audit, 2026-11-26): purpose-built thumbnails —
// real verified star face + 1-3 words, 3 variants per video, 320px
// mobile legibility check, and the TokenCapture scope follow-up.
//
// The sandbox runner invokes [Fact] methods without awaiting a returned
// Task, so these tests are synchronous and block on async calls with
// GetAwaiter().GetResult().
using System.Diagnostics;
using Xunit;

namespace BroadcastContentAutomation.Tests;

public class ThumbnailTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "production", "finish-chunks.py")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate repo root (production/finish-chunks.py).");
        }
    }

    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tn-thumb-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, $"{file} {args} failed ({proc.ExitCode}): {stderr}");
    }

    // A plain 1024x1024 still image, standing in for scene art.
    private static string MakeSquareImage(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        Run("ffmpeg", $"-y -v error -f lavfi -i color=c=0x336699:s=1024x1024 -frames:v 1 \"{path}\"");
        return path;
    }

    private static VideoScript ScriptWithArt(string title, params (string? ArtSource, string? Path)[] scenes)
    {
        var script = new VideoScript { Title = title, Description = "Test.", Tags = ["news"] };
        foreach (var (artSource, path) in scenes)
        {
            script.Scenes.Add(new Scene
            {
                Narration = "A test scene with enough narration to pass any length check that might run.",
                ArtSource = artSource,
                ImagePath = path,
                LandscapeImagePath = path,
            });
        }
        return script;
    }

    [Fact]
    public void RankedStarNeverUsesFluxFace()
    {
        var dir = TempDir("flux");
        var flux1 = MakeSquareImage(dir, "flux1.jpg");
        var flux2 = MakeSquareImage(dir, "flux2.jpg");
        var commons = MakeSquareImage(dir, "commons.jpg");
        var pexels = MakeSquareImage(dir, "pexels.jpg");

        var script = ScriptWithArt("Cristiano Ronaldo Hangs Up Portugal Boots - A Legendary Farewell",
            ("cloudflare-flux", flux1),   // scene 0 (title card)
            ("cloudflare-flux", flux2),   // scene 1 — a FLUX face of a real person
            ("wikimedia-commons", commons),
            ("pexels-stock", pexels));

        var source = ThumbnailBuilder.SelectThumbnailSource(script, isRankedStar: true);
        Assert.NotNull(source);
        Assert.Equal("wikimedia-commons", source!.ArtSource);
        Assert.Equal(2, source.SceneIndex);
        Assert.Equal(commons, source.Path);
    }

    [Fact]
    public void RankedStarWithNoRealPhotoReturnsNullInsteadOfAiFace()
    {
        var dir = TempDir("fluxonly");
        var flux1 = MakeSquareImage(dir, "flux1.jpg");
        var flux2 = MakeSquareImage(dir, "flux2.jpg");

        var script = ScriptWithArt("Cristiano Ronaldo Hangs Up Portugal Boots - A Legendary Farewell",
            ("cloudflare-flux", flux1),
            ("cloudflare-flux", flux2));

        Assert.Null(ThumbnailBuilder.SelectThumbnailSource(script, isRankedStar: true));
    }

    // L56 — with only FLUX art for a ranked star, the variant build
    // falls back to the legacy base: the log must name that base path
    // at the decision point, not just warn in general terms.
    [Fact]
    public async Task BuildVariants_NoRealPhotoLogsLegacyBase()
    {
        var dir = TempDir("fluxlegacy");
        var flux1 = MakeSquareImage(dir, "flux1.jpg");
        var flux2 = MakeSquareImage(dir, "flux2.jpg");
        var legacyBase = MakeSquareImage(dir, "legacy.jpg");

        var script = ScriptWithArt("Cristiano Ronaldo Hangs Up Portugal Boots - A Legendary Farewell",
            ("cloudflare-flux", flux1),
            ("cloudflare-flux", flux2));

        var sb = new System.Text.StringBuilder();
        var oldOut = Console.Out;
        ThumbnailBuilder.BuiltThumbnails built;
        try
        {
            Console.SetOut(new StringWriter(sb));
            built = await ThumbnailBuilder.BuildVariantsAsync(
                new GenConfig(), script, legacyBase: legacyBase, outputBase: Path.Combine(dir, "ronaldo.thumb"));
        }
        finally
        {
            Console.SetOut(oldOut);
        }

        Assert.Null(built.Source);
        Assert.Equal(legacyBase, built.BasePath);
        Assert.Equal(3, built.Variants.Count);
        var log = sb.ToString();
        Assert.Contains("no verified real photo for star", log);
        Assert.Contains("Cristiano Ronaldo", log);
        Assert.Contains(legacyBase, log);
    }

    // L57 — when the variant build AND the plain-thumbnail fallback
    // both fail, the log must name the job and say the fallback failed
    // (it used to propagate to the top-level FATAL with only the ffmpeg
    // error). RunAssembleAsync is a top-level local function that needs
    // a full job + render to run, so — like the other RunAssembleAsync
    // guards in this suite — this pins the fallback block by source:
    // the variant-failure line carries the job id, the fallback is
    // wrapped in its own try/catch that logs the job + rethrows.
    [Fact]
    public void RunAssemble_ThumbnailFallbackFailureLogsJob()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "VideoGen", "Program.cs"));
        var assemble = program.IndexOf("async Task<int> RunAssembleAsync()", StringComparison.Ordinal);
        Assert.True(assemble >= 0, "RunAssembleAsync not found in Program.cs");
        var body = program[assemble..];

        var variantFail = body.IndexOf("Thumbnail: variant build failed", StringComparison.Ordinal);
        Assert.True(variantFail >= 0, "variant-build failure line not found");
        var variantLine = body[variantFail..body.IndexOf('\n', variantFail)];
        Assert.Contains("job", variantLine);

        var fallbackFail = body.IndexOf("Thumbnail fallback also failed", StringComparison.Ordinal);
        Assert.True(fallbackFail >= 0, "fallback failure line not found");
        var fallbackLine = body[fallbackFail..body.IndexOf('\n', fallbackFail)];
        Assert.Contains("assemble aborting without thumbnail", fallbackLine);
        Assert.Contains("job", fallbackLine);

        // The fallback catch must rethrow (assemble aborts without a
        // thumbnail) instead of swallowing the failure.
        var afterFallback = body[(fallbackFail + "Thumbnail fallback also failed".Length)..];
        Assert.Contains("throw;", afterFallback[..afterFallback.IndexOf("Thumbnail generated (1280x720).", StringComparison.Ordinal)]);
    }

    [Fact]
    public void CommonsBeatsPexelsForRankedStar()
    {
        var dir = TempDir("order");
        var pexels = MakeSquareImage(dir, "pexels.jpg");
        var commons = MakeSquareImage(dir, "commons.jpg");

        var script = ScriptWithArt("Cristiano Ronaldo Hangs Up Portugal Boots - A Legendary Farewell",
            (null, null),
            ("pexels-stock", pexels),
            ("wikimedia-commons", commons));

        var source = ThumbnailBuilder.SelectThumbnailSource(script, isRankedStar: true);
        Assert.NotNull(source);
        Assert.Equal("wikimedia-commons", source!.ArtSource);
        Assert.Equal(2, source.SceneIndex);
    }

    [Fact]
    public void NonStarFallsBackToFirstStoryArt()
    {
        var dir = TempDir("nonstar");
        var art = MakeSquareImage(dir, "art.jpg");

        var script = ScriptWithArt("Trump's D.C. Memorial Arch Faces a Second Court Block",
            (null, null),
            ("cloudflare-flux", art));

        var source = ThumbnailBuilder.SelectThumbnailSource(script, isRankedStar: false);
        Assert.NotNull(source);
        Assert.Equal(1, source!.SceneIndex);
        Assert.Equal(art, source.Path);
    }

    [Fact]
    public void DeriveOverlayWordsIsAtMostThreeWordsAndNeverTheStarName()
    {
        var (v1, v2, v3) = ThumbnailBuilder.DeriveOverlayWords(
            "Cristiano Ronaldo Hangs Up Portugal Boots - A Legendary Farewell", "Cristiano Ronaldo");
        // Significant words: HANGS PORTUGAL BOOTS LEGENDARY FAREWELL.
        Assert.Equal("LEGENDARY FAREWELL", v1);
        Assert.Equal("HANGS PORTUGAL", v2);
        Assert.Equal("LEGENDARY", v3);
        foreach (var words in new[] { v1, v2, v3 })
        {
            Assert.True(words.Split(' ').Length <= 3, $"'{words}' is more than 3 words.");
            Assert.DoesNotContain("RONALDO", words);
            Assert.DoesNotContain("CRISTIANO", words);
        }

        var (m1, m2, m3) = ThumbnailBuilder.DeriveOverlayWords(
            "Lionel Messi's MLS Playoff Chase: The Big Change That Could Define His Final Year", "Lionel Messi");
        Assert.Equal("FINAL YEAR", m1);
        Assert.Equal("MLS PLAYOFF", m2);
        Assert.Equal("PLAYOFF", m3);
        foreach (var words in new[] { m1, m2, m3 })
        {
            Assert.True(words.Split(' ').Length <= 3);
            Assert.DoesNotContain("MESSI", words);
            Assert.DoesNotContain("LIONEL", words);
        }

        var (g1, g2, g3) = ThumbnailBuilder.DeriveOverlayWords(
            "GTA 6's Massive Open World Finally Revealed", null);
        Assert.Equal("FINALLY REVEALED", g1);
        Assert.Equal("GTA 6", g2);
        Assert.Equal("REVEALED", g3); // longest word: REVEALED (8) beats MASSIVE (7).
    }

    [Fact]
    public void FitFontSizeStaysInBounds()
    {
        // 18 chars -> floor(1180 / (0.70 * 18)) = 93.
        Assert.Equal(93, ThumbnailBuilder.FitFontSize("LEGENDARY FAREWELL"));
        // Short word would fit at 210 -> clamped to the 140 cap.
        Assert.Equal(140, ThumbnailBuilder.FitFontSize("FAREWELL"));
        // 30 chars would fit at 56 -> clamped to the 60 floor.
        Assert.Equal(60, ThumbnailBuilder.FitFontSize("ABCDEFGHIJKLMNOPQRSTUVWXYZABCD"));
    }

    [Fact]
    public void MobileLegibilityCheckBehaves()
    {
        // Cap height at 320px = fontSize * 0.73 * 320/1280 = fontSize * 0.1825.
        var (h140, pass140) = ThumbnailBuilder.CheckMobileLegibility(140);
        Assert.True(pass140);
        Assert.Equal(25.55, h140, precision: 2);

        var (h60, pass60) = ThumbnailBuilder.CheckMobileLegibility(60);
        Assert.True(pass60);
        Assert.Equal(10.95, h60, precision: 2);

        var (_, pass49) = ThumbnailBuilder.CheckMobileLegibility(49);
        Assert.False(pass49); // 8.94px < 9px — a smear on a phone.
    }

    [Fact]
    public async Task ThreeVariantsAreProduced()
    {
        var dir = TempDir("variants");
        var commons = MakeSquareImage(dir, "commons.jpg");
        var script = ScriptWithArt("Cristiano Ronaldo Hangs Up Portugal Boots - A Legendary Farewell",
            ("cloudflare-flux", commons),
            ("wikimedia-commons", commons));

        var built = await ThumbnailBuilder.BuildVariantsAsync(
            new GenConfig(), script, legacyBase: commons, outputBase: Path.Combine(dir, "ronaldo.thumb"));

        Assert.NotNull(built.Source);
        Assert.Equal("wikimedia-commons", built.Source!.ArtSource);
        Assert.Equal(3, built.Variants.Count);
        foreach (var variant in built.Variants)
        {
            Assert.True(File.Exists(variant.Path), $"Missing variant file {variant.Path}.");
            Assert.True(new FileInfo(variant.Path).Length > 0, $"Empty variant file {variant.Path}.");
            Assert.True(variant.Words.Split(' ').Length <= 3, $"'{variant.Words}' is more than 3 words.");
            Assert.True(variant.MobileCheckPass, $"Variant {variant.Path} failed the 320px check.");
            // The 320px preview is a real scaled render, 320 wide.
            Assert.True(File.Exists(variant.PreviewPath), $"Missing 320px preview {variant.PreviewPath}.");
            var psi = new ProcessStartInfo("ffprobe",
                $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{variant.PreviewPath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var dims = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            Assert.Equal("320,180", dims);
        }

        // The legacy direct path (<slug>.thumb.jpg) keeps resolving — to
        // the picked default variant (v1), not the old no-text crop.
        var legacy = Path.Combine(dir, "ronaldo.thumb.jpg");
        Assert.True(File.Exists(legacy));
        Assert.Equal(
            File.ReadAllBytes(built.Variants[0].Path),
            File.ReadAllBytes(legacy));
    }

    [Fact]
    public void NoNoTextGuidanceRemainsAndBuilderIsWired()
    {
        var profile = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "VideoGen", "TrendlineNowProfile.cs"));
        Assert.DoesNotContain("no text in the image", profile);
        Assert.Contains("1-3 words", profile);

        // The builder is actually called from the finish path, not dead code.
        var program = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "VideoGen", "Program.cs"));
        Assert.Contains("ThumbnailBuilder.BuildVariantsAsync", program);
        Assert.Contains("--make-thumbnails", program);
        Assert.Contains("RunMakeThumbnailsAsync", program);
    }

    [Fact]
    public void TokenCaptureScopeListHasAllFiveScopes()
    {
        var capture = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "TokenCapture", "Program.cs"));
        Assert.Contains("YouTubeService.Scope.YoutubeUpload", capture);
        Assert.Contains("YouTubeService.Scope.YoutubeReadonly", capture);
        Assert.Contains("https://www.googleapis.com/auth/yt-analytics.readonly", capture);
        Assert.Contains("https://www.googleapis.com/auth/youtube.force-ssl", capture);
        // The full scope, quoted — the closing quote distinguishes it
        // from youtube.force-ssl / youtube.readonly, which share the prefix.
        Assert.Contains("\"https://www.googleapis.com/auth/youtube\"", capture);
    }

    [Fact]
    public void UploaderSetterExists()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "Uploader", "Program.cs"));
        Assert.Contains("--set-thumbnail", program);
        Assert.Contains("SetThumbnailAsync", program);
        var publisher = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "Uploader", "Publishing", "YouTubePublisher.cs"));
        Assert.Contains("public async Task<(bool Ok, string? Error)> SetThumbnailAsync", publisher);
    }
}
