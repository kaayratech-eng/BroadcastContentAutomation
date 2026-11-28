// QualityGateTests.cs — fail-first xUnit tests for the QualityGate
// (VideoGen/QualityGate.cs). One section per gate: script, image
// (prompt + pixels + file + batch), music, final mix, report.
//
// The video #4 batch test (BatchFlagsVideo4KnownBad) is the proof run:
// it reads job-20261123-150439 read-only. Before the 2026-11-24 art
// re-do it asserted the known-bad scenes (0/5/7 prompt-text, 20
// low-res leg, 31 blurry, 41 duplicate of 31); after the re-do it
// asserted the re-do cleared every image error. After the 2026-11-24
// bake-off it asserts the new reality: 39/57 scenes are from legs
// the bake-off discarded, so the batch check errors on them — the
// gate doing its job on old art, not a re-render signal.
//
// Run: cd ~/workspace/BroadcastContentAutomation/BroadcastContentAutomation
//      && dotnet run --project Tests.Runner -c Release
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace BroadcastContentAutomation.Tests;

public class QualityGateTests
{
    const string Job4 = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261123-150439";
    const string MusicDir = "/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/assets/music-trendline";
    const string Topic = "Ukraine drones strike Russian ships Libya";

    static string TrackPath(string file) => Path.Combine(MusicDir, file);

    // ------------------------------------------------------------------
    // Baseline: a script that passes every script rule cleanly.
    // ------------------------------------------------------------------

    static string NarrationFor(int i)
    {
        if (i == 10 || i == 20)
            return $"Drone raid {i}: reports suggest Ukrainian naval drones may have used a new guidance system in the Libyan strike, though officials have not confirmed the details yet.";
        return $"Drone raid {i}: Ukrainian naval drones crossed the Black Sea toward the Libyan coast and struck Russian vessels near Benghazi port in a coordinated overnight attack.";
    }

    static VideoScript BaselineScript()
    {
        var script = new VideoScript
        {
            Title = "Ukraine's Drones Strike Russian Ships in Libya – What It Means",
            Description = "Ukraine's drones struck Russian warships off Libya. We break down what happened, why it matters, and what happens next. Watch more at youtube.com/@spotlightnowofficial — new video daily.",
            Tags = ["ukraine", "russia", "drones", "libya", "news", "trending"],
            MusicMood = "tense atmospheric",
        };
        for (var i = 0; i < 50; i++)
        {
            var scene = new Scene
            {
                Narration = i == 49
                    ? "The raid changes the map for both sides, and the next moves will be watched closely around the world. The next big story is already brewing. Follow @spotlightnowofficial so you don't miss it."
                    : NarrationFor(i),
                ImagePrompt = "An illustration of a cargo ship at a port at dusk, navy and red tones, centered, no text",
                ArtSource = "ovhcloud",
                IsSpeculative = i == 10 || i == 20,
            };
            script.Scenes.Add(scene);
        }
        return script;
    }

    static bool HasFinding(List<QualityFinding> fs, string check, QualitySeverity sev) =>
        fs.Any(f => f.Check == check && f.Severity == sev);

    static bool HasError(List<QualityFinding> fs) =>
        fs.Any(f => f.Severity == QualitySeverity.Error);

    static VideoScript LoadJob4Script() =>
        JsonSerializer.Deserialize<VideoScript>(
            File.ReadAllText(Path.Combine(Job4, "script.json")))!;

    // ------------------------------------------------------------------
    // SCRIPT — hook
    // ------------------------------------------------------------------

    [Fact]
    public void BaselinePassesClean()
    {
        var findings = QualityGate.CheckScript(BaselineScript(), Topic);
        Assert.Empty(findings);
    }

    [Fact]
    public void IntroBumperSkipped()
    {
        // Scene 0 is the intro bumper (narration == IntroText): a title
        // card + hook line, short by design — it must not trip the
        // scene-length rule. The bumper text IS the hook now (no brief to
        // carry it), so it must still pass the hook check with a concrete
        // peak moment — only the length rule skips it.
        var s = BaselineScript();
        s.IntroText = "Ronaldo broke a 30-year record in front of 80,000 fans.";
        s.Scenes[0].Narration = "Ronaldo broke a 30-year record in front of 80,000 fans.";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Empty(f);
    }

    [Fact]
    public void HookGreetingIsError()
    {
        var s = BaselineScript();
        s.Scenes[0].Narration = "Welcome to Trendline Now, today we have a huge story for you.";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "hook" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void HookVagueIsError()
    {
        var s = BaselineScript();
        s.Scenes[0].Narration = "Something incredible happened today that changes everything.";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "hook" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void HookTooLongWarns()
    {
        var s = BaselineScript();
        s.Scenes[0].Narration = "Ukraine's drones struck Russian warships 1,500 km from its borders in a daring overnight raid that nobody saw coming, and the response from Moscow was immediate and furious, reshaping the whole conflict in a single night.";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "hook" && x.Severity == QualitySeverity.Warning);
        Assert.False(HasError(f));
    }

    // ------------------------------------------------------------------
    // SCRIPT — questions, duplicates, length, placeholders
    // ------------------------------------------------------------------

    [Fact]
    public void QuestionsOverTwoIsError()
    {
        var s = BaselineScript();
        for (var i = 0; i < 3; i++)
            s.Scenes[i].Narration = s.Scenes[i].Narration[..^1] + "?";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "questions" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void DuplicateNarrationIsError()
    {
        var s = BaselineScript();
        s.Scenes[3].Narration = s.Scenes[2].Narration;
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "duplicate-scene" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void SceneTooShortIsError()
    {
        var s = BaselineScript();
        s.Scenes[0].Narration = "Short.";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "scene-length" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void SceneTooLongIsError()
    {
        var s = BaselineScript();
        s.Scenes[0].Narration = new string('x', 300);
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "scene-length" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void SceneMidRangeWarns()
    {
        // Scene 0 is the hook now, so length tests mutate scene 1 — a
        // mid-range scene 0 would trip the hook check instead.
        var s = BaselineScript();
        s.Scenes[1].Narration = new string('x', 110);
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "scene-length" && x.Severity == QualitySeverity.Warning);
        Assert.False(HasError(f));
    }

    [Fact]
    public void PlaceholderIsError()
    {
        var s = BaselineScript();
        s.Scenes[0].Narration = "Drone raid 0: TODO fill in the details of the Libyan strike here before recording the narration.";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "placeholder" && x.Severity == QualitySeverity.Error);
    }

    // ------------------------------------------------------------------
    // SCRIPT — speculation
    // ------------------------------------------------------------------

    [Fact]
    public void SpeculationFiveIsError()
    {
        var s = BaselineScript();
        foreach (var i in new[] { 30, 40, 41 })
        {
            s.Scenes[i].IsSpeculative = true;
            s.Scenes[i].Narration = $"Drone raid {i}: reports suggest the strike may have involved a second wave of drones launched from a nearby port, though this remains unconfirmed.";
        }
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "speculation" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void SpeculationThreeWarns()
    {
        var s = BaselineScript();
        s.Scenes[30].IsSpeculative = true;
        s.Scenes[30].Narration = "Drone raid 30: reports suggest the strike may have involved a second wave of drones launched from a nearby port, though this remains unconfirmed.";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "speculation" && x.Severity == QualitySeverity.Warning);
        Assert.False(HasError(f));
    }

    [Fact]
    public void SpeculativeWithoutAttributionIsError()
    {
        var s = BaselineScript();
        s.Scenes[10].Narration = "Drone raid 10: Ukrainian naval drones used a new guidance system in the Libyan strike, a secret weapon nobody knew about.";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "speculation-attribution" && x.Severity == QualitySeverity.Error);
    }

    // ------------------------------------------------------------------
    // SCRIPT — structure, pace, title, description
    // ------------------------------------------------------------------

    [Fact]
    public void NoBriefIsFine()
    {
        // No sting, no brief (removed 2026-11-25, Rishi): new scripts carry
        // no BriefScenes at all, and the gate must not ask for one. An old
        // script that still has brief scenes is judged on its scenes too —
        // the brief never errors the gate either way.
        var s = BaselineScript();
        Assert.Empty(s.BriefScenes);
        var f = QualityGate.CheckScript(s, Topic);
        Assert.DoesNotContain(f, x => x.Check == "brief");
        var withOldBrief = BaselineScript();
        withOldBrief.BriefScenes =
        [
            new Scene { Narration = "Ukraine's drones struck Russian warships 1,500 km from its borders, marking a new reach for the conflict." },
        ];
        var f2 = QualityGate.CheckScript(withOldBrief, Topic);
        Assert.DoesNotContain(f2, x => x.Check == "brief");
    }

    [Fact]
    public void OutroMissingIsError()
    {
        var s = BaselineScript();
        s.Scenes[49].Narration = "The raid changes the map for both sides, and the next moves will be watched closely around the world.";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "outro" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void OutroInCardPasses()
    {
        // The CTA lives on the profile's outro card, not in a scene's
        // narration (video #4's last scene has no CTA; the card does).
        // The gate must accept the card text, not false-positive.
        var s = BaselineScript();
        s.Scenes[49].Narration = "The raid changes the map for both sides, and the next moves will be watched closely around the world.";
        var f = QualityGate.CheckScript(s, Topic,
            outroLines: ["The next big story is already brewing.", "Follow @spotlightnowofficial so you don't miss it."]);
        Assert.DoesNotContain(f, x => x.Check == "outro");
    }

    [Fact]
    public void TitleTooLongIsError()
    {
        var s = BaselineScript();
        s.Title = new string('x', 120);
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "title" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void TagsTooFewIsError()
    {
        var s = BaselineScript();
        s.Tags = ["ukraine", "news", "trending"];
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "tags" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void DescriptionMissingIsError()
    {
        var s = BaselineScript();
        s.Description = "";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "description" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void PaceTooShortWarns()
    {
        var s = BaselineScript();
        s.Scenes = s.Scenes.Take(25).ToList();
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "pace" && x.Severity == QualitySeverity.Warning);
    }

    [Fact]
    public void PaceTooLongWarns()
    {
        var s = BaselineScript();
        for (var i = 50; i < 65; i++)
            s.Scenes.Add(new Scene { Narration = new string('y', 190) + $" {i}", ImagePrompt = "clean", ArtSource = "ovhcloud" });
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "pace" && x.Severity == QualitySeverity.Warning);
    }

    [Fact]
    public void TitleNoKeywordWarns()
    {
        var s = BaselineScript();
        s.Title = "Big News Today: What Happened";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "title-keywords" && x.Severity == QualitySeverity.Warning);
    }

    [Fact]
    public void TitleAllCapsWarns()
    {
        var s = BaselineScript();
        s.Title = "Ukraine's Drones Strike Russian Ships in Libya – BREAKING";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "title-caps" && x.Severity == QualitySeverity.Warning);
    }

    [Fact]
    public void TitleClickbaitWarns()
    {
        var s = BaselineScript();
        s.Title = "you won't believe what happened next";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "title-clickbait" && x.Severity == QualitySeverity.Warning);
    }

    [Fact]
    public void DescriptionLinksWarns()
    {
        var s = BaselineScript();
        s.Description = "Ukraine's drones struck Russian warships off Libya. We break down what happened, why it matters, and what happens next.";
        var f = QualityGate.CheckScript(s, Topic);
        Assert.Contains(f, x => x.Check == "description-links" && x.Severity == QualitySeverity.Warning);
    }

    // ------------------------------------------------------------------
    // IMAGE — prompt ban + sanitize
    // ------------------------------------------------------------------

    [Fact]
    public void PromptBanLettering()
    {
        Assert.True(QualityGate.ImagePromptHasTextRequest(
            "A title card with TRENDLINE NOW lettering on a navy background"));
    }

    [Fact]
    public void PromptBanTweet()
    {
        Assert.True(QualityGate.ImagePromptHasTextRequest(
            "A tweet from Zelenskiy in a quote bubble with the word exemplary"));
    }

    [Fact]
    public void PromptBanHeadline()
    {
        Assert.True(QualityGate.ImagePromptHasTextRequest(
            "A Russian newspaper headline with the word stealth"));
    }

    [Fact]
    public void PromptBanAllowsNoText()
    {
        Assert.False(QualityGate.ImagePromptHasTextRequest(
            "An illustration of a ship at sea, navy and red tones, no text"));
    }

    [Fact]
    public void PromptBanAllowsClean()
    {
        Assert.False(QualityGate.ImagePromptHasTextRequest(
            "An illustration of a cargo ship at a port at dusk, navy and red tones"));
    }

    [Fact]
    public void SanitizeStripsTextRequest()
    {
        var (clean, removed) = QualityGate.SanitizeImagePrompt(
            "A map of the Black Sea with the headline 'STEALTH' across it, navy tones");
        Assert.False(clean.Contains("headline", StringComparison.OrdinalIgnoreCase));
        Assert.False(clean.Contains("STEALTH", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith(QualityGate.NoTextSuffix, clean);
        Assert.NotEmpty(removed);
    }

    [Fact]
    public void SanitizeTitleCardFallsBack()
    {
        var (clean, _) = QualityGate.SanitizeImagePrompt(
            "A title card with TRENDLINE NOW lettering on a navy background");
        Assert.Equal(QualityGate.FallbackArtPrompt, clean);
    }

    [Fact]
    public void SanitizeKeepsCleanPrompt()
    {
        var (clean, removed) = QualityGate.SanitizeImagePrompt(
            "An illustration of a cargo ship at a port at dusk, navy and red tones");
        Assert.Contains("cargo ship", clean);
        Assert.EndsWith(QualityGate.NoTextSuffix, clean);
        Assert.Empty(removed);
    }

    [Fact]
    public void PhotoSubjectDetected()
    {
        Assert.True(QualityGate.ImagePromptIsPhotoSubject(
            "A photo of a cargo ship at a port with people on the dock"));
        Assert.False(QualityGate.ImagePromptIsPhotoSubject(
            "An abstract illustration of waves and light, navy tones"));
    }

    // ------------------------------------------------------------------
    // IMAGE — pixels
    // ------------------------------------------------------------------

    [Fact]
    public void AnalyzePixelsBlankUniform()
    {
        var grey = Enumerable.Repeat((byte)128, 256 * 256).ToArray();
        var (stdDev, varLap) = QualityGate.AnalyzePixels(grey, 256, 256);
        Assert.Equal(0, stdDev, precision: 3);
        Assert.Equal(0, varLap, precision: 3);
    }

    [Fact]
    public void AnalyzePixelsSharpVsBlurry()
    {
        // Checkerboard: maximum gradient energy.
        var sharp = new byte[256 * 256];
        for (var y = 0; y < 256; y++)
            for (var x = 0; x < 256; x++)
                sharp[y * 256 + x] = (byte)(((x + y) % 2 == 0) ? 255 : 0);
        // Smooth vertical ramp: near-zero Laplacian (no edges).
        var blurry = new byte[256 * 256];
        for (var y = 0; y < 256; y++)
            for (var x = 0; x < 256; x++)
                blurry[y * 256 + x] = (byte)y;
        var (_, sharpLap) = QualityGate.AnalyzePixels(sharp, 256, 256);
        var (_, blurryLap) = QualityGate.AnalyzePixels(blurry, 256, 256);
        Assert.True(sharpLap > 200);
        Assert.True(blurryLap < 200);
        Assert.True(sharpLap > blurryLap * 100);
    }

    [Fact]
    public void AnalyzePixelsBufferTooShortThrows()
    {
        Assert.Throws<ArgumentException>(() => QualityGate.AnalyzePixels(new byte[100], 256, 256));
    }

    // ------------------------------------------------------------------
    // IMAGE — file checks (video #4 art, read-only)
    // ------------------------------------------------------------------

    [Fact]
    public void CheckImageFileMissingIsError()
    {
        var f = QualityGate.CheckImageFile(
            Path.Combine(Job4, "scene99-art-square.jpg"), "ovhcloud", "clean", 99);
        Assert.Contains(f, x => x.Check == "image-exists" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void CheckImageFileVideo4Scene0PassesFileChecks()
    {
        // Scene 0's FILE is fine (1024x1024, sharp, OVHcloud) — its
        // failure is the PROMPT asking for lettering, caught by the
        // batch check, not by the file check.
        var script = LoadJob4Script();
        var f = QualityGate.CheckImageFile(
            Path.Combine(Job4, "scene00-art-square.jpg"),
            script.Scenes[0].ArtSource, script.Scenes[0].ImagePrompt, 0);
        Assert.Empty(f);
    }

    [Fact]
    public void CheckImageFileWrongDimsIsError()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-dimstest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var small = Path.Combine(tmp, "small.jpg");
            var psi = new ProcessStartInfo("ffmpeg", $"-y -v error -i \"{Path.Combine(Job4, "scene00-art-square.jpg")}\" -vf scale=512:512 \"{small}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(File.Exists(small), "ffmpeg scale failed");
            var f = QualityGate.CheckImageFile(small, "ovhcloud", "clean", 0);
            Assert.Contains(f, x => x.Check == "image-dimensions" && x.Severity == QualitySeverity.Error);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // ------------------------------------------------------------------
    // IMAGE — batch check against video #4 (the proof run)
    // ------------------------------------------------------------------

    [Fact]
    public void BatchAfterRedo_NoDiscardedLegs()
    {
        // Proof run on video #4's art AFTER the 2026-11-24 v3 re-do: all
        // 39 bad scenes were repaired with the rebuilt pool (25 FLUX, 19
        // OVH, 12 Pexels), so the batch check now finds ZERO discarded-leg
        // errors — the gate's job is done. This replaces the old
        // BatchFlagsVideo4KnownBad expectation (39 errors on the pre-redo
        // art: 22 Pollinations anonymous, 17 AI Horde), which the re-do
        // itself invalidated by replacing that art on disk. The re-do's
        // prompt fixes hold (no prompt-text errors) and the scene 41
        // duplicate is gone.
        var script = LoadJob4Script();
        var findings = QualityGate.CheckImagesBatch(Job4, script);

        var errors = findings.Where(f => f.Severity == QualitySeverity.Error).ToList();
        Assert.Empty(errors.Where(f => f.Check == "image-discarded-leg"));
        Assert.DoesNotContain(findings, f => f.Check == "image-prompt-text");
        Assert.DoesNotContain(findings, f => f.Scene == "scene41" && f.Check == "image-duplicate");
        // Provider mix: 37/57 scenes now come from the passing legs
        // (25 FLUX + 12 Pexels) — over half, so no provider-mix warning.
        Assert.DoesNotContain(findings, f => f.Check == "image-provider-mix");
    }

    // ------------------------------------------------------------------
    // IMAGE — bake-off 2026-11-24 (new pool order + prompt fix)
    // ------------------------------------------------------------------

    [Fact]
    public void DiscardedLegIsError()
    {
        var file = Path.Combine(Job4, "scene00-art-square.jpg");
        foreach (var source in new[] { "pollinations", "ai-horde" })
        {
            var f = QualityGate.CheckImageFile(file, source, "clean", 0);
            Assert.Contains(f, x => x.Check == "image-discarded-leg" && x.Severity == QualitySeverity.Error);
            Assert.Single(f); // one finding only — no softness pile-on
        }
    }

    [Fact]
    public void DiscardedLegSkippedForPassingLegs()
    {
        var file = Path.Combine(Job4, "scene00-art-square.jpg");
        // cloudflare-flux / cloudflare-sdxl / ovhcloud / pexels-stock /
        // wikimedia-commons are the passing legs; pollinations-keyed is
        // dormant, not discarded (same FLUX model, waits for Pollen).
        foreach (var source in new[] { "cloudflare-flux", "cloudflare-sdxl", "ovhcloud", "pexels-stock", "pollinations-keyed", "wikimedia-commons" })
        {
            var f = QualityGate.CheckImageFile(file, source, "clean", 0);
            Assert.DoesNotContain(f, x => x.Check == "image-discarded-leg");
        }
        Assert.Empty(QualityGate.CheckImageFile(file, "ovhcloud", "clean", 0));
    }

    [Fact]
    public void NoTextSuffixIsBakeoffWording()
    {
        Assert.Equal("dramatic cinematic lighting, high contrast, expressive faces, strong emotion, dynamic composition, vibrant colour, rule of thirds, clear focal subject, no dull flat frames, wide shot, subject with headroom, full scene visible, no close-up, no cropped faces, no text, no words, no letters, no signage", QualityGate.NoTextSuffix);
        Assert.EndsWith(QualityGate.NoTextSuffix, QualityGate.FallbackArtPrompt);
    }

    // ------------------------------------------------------------------
    // IMAGE — square head-crop (Drake 2026-11-27)
    // ------------------------------------------------------------------
    // Commons "Drake 2010.jpg" (3056x4592 portrait) was centre-cropped
    // to the 1024x1024 square and lost the top 16.7% — the subject's
    // head — so the Short opened on a headless Drake. CheckSquareCrop
    // judges the derivation facts (original dimensions + crop anchor);
    // CropScaleToSquareAsync top-anchors portrait originals so it
    // cannot happen again.

    [Fact]
    public void SquareCrop_DrakePortraitCentreCrop_IsError()
    {
        var f = QualityGate.CheckSquareCrop(3056, 4592, 0.5, 0, "wikimedia-commons", "scene00-art-square.jpg");
        Assert.Contains(f, x => x.Check == "square-head-crop" && x.Severity == QualitySeverity.Error);
        Assert.Contains("16.7%", f.Single(x => x.Check == "square-head-crop").Message);
        Assert.Equal("scene0", f.Single(x => x.Check == "square-head-crop").Scene);
    }

    [Fact]
    public void SquareCrop_DrakePortraitTopAnchored_NoFinding()
    {
        Assert.Empty(QualityGate.CheckSquareCrop(3056, 4592, 0.0, 0, "wikimedia-commons", "scene00-art-square.jpg"));
    }

    [Fact]
    public void SquareCrop_LandscapeCentreCrop_NoFinding()
    {
        // A landscape original has no vertical overflow, so the anchor
        // is unobservable — centre crop keeps the whole height.
        Assert.Empty(QualityGate.CheckSquareCrop(4592, 3056, 0.5, 0, "pexels-stock", "scene00-art-square.jpg"));
    }

    [Fact]
    public void SquareCrop_ThreeFourPortraitCentreCrop_IsError()
    {
        // A 3:4 portrait centre-cropped loses 12.5% from the top.
        var f = QualityGate.CheckSquareCrop(1200, 1600, 0.5, 3, "pexels-stock", "scene03-art-square.jpg");
        Assert.Contains(f, x => x.Check == "square-head-crop" && x.Severity == QualitySeverity.Error);
        Assert.Contains("12.5%", f.Single(x => x.Check == "square-head-crop").Message);
    }

    [Fact]
    public void SquareCrop_MildPortraitCentreCrop_IsWarningOnly()
    {
        // 1024x1150 loses 5.5% from the top: suspect framing, not a
        // lost head — warning, never an error.
        var f = QualityGate.CheckSquareCrop(1024, 1150, 0.5, 5, "pexels-stock", "scene05-art-square.jpg");
        Assert.Contains(f, x => x.Check == "square-head-crop" && x.Severity == QualitySeverity.Warning);
        Assert.DoesNotContain(f, x => x.Check == "square-head-crop" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void SquareCrop_InvalidDims_NoFinding()
    {
        Assert.Empty(QualityGate.CheckSquareCrop(0, 0, 0.5, 0, "wikimedia-commons"));
        Assert.Empty(QualityGate.CheckSquareCrop(-1, 4592, 0.5, 0, "wikimedia-commons"));
    }

    [Fact]
    public void SquareCrop_BatchGateFlagsRecordedCentreCrop()
    {
        // The batch gate re-checks a scene that records a
        // centre-cropped portrait original in script.json.
        var script = LoadJob4Script();
        script.Scenes[0].ArtSourceWidth = 3056;
        script.Scenes[0].ArtSourceHeight = 4592;
        script.Scenes[0].ArtCropAnchor = "centre";
        var findings = QualityGate.CheckImagesBatch(Job4, script);
        Assert.Contains(findings, f => f.Check == "square-head-crop"
            && f.Severity == QualitySeverity.Error && f.Scene == "scene0");
    }

    [Fact]
    public void SquareCrop_BatchGatePassesRecordedTopAnchor()
    {
        var script = LoadJob4Script();
        script.Scenes[0].ArtSourceWidth = 3056;
        script.Scenes[0].ArtSourceHeight = 4592;
        script.Scenes[0].ArtCropAnchor = "top";
        var findings = QualityGate.CheckImagesBatch(Job4, script);
        Assert.DoesNotContain(findings, f => f.Check == "square-head-crop");
    }

    [Fact]
    public void SquareCropBias_PortraitIsTop_LandscapeAndSquareAreCentre()
    {
        Assert.Equal(0.0, VideoAssembler.SquareCropBias(3056, 4592));
        Assert.Equal(0.0, VideoAssembler.SquareCropBias(1200, 1600));
        Assert.Equal(0.5, VideoAssembler.SquareCropBias(4592, 3056));
        Assert.Equal(0.5, VideoAssembler.SquareCropBias(1024, 1024));
    }

    [Fact]
    public async Task SquareCrop_CropScaleToSquare_KeepsPortraitHead()
    {
        // End to end with real ffmpeg: a 600x900 portrait with a
        // white top band (the "head") must keep the band after the
        // square derivation; the old centre crop kept rows 150-750
        // and the band was gone.
        var tmp = Path.Combine(Path.GetTempPath(), "qg-squarecrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var raw = Path.Combine(tmp, "portrait.jpg");
            var psi = new ProcessStartInfo("ffmpeg",
                $"-y -v error -f lavfi -i \"color=c=black:s=600x900\" -vf \"drawbox=x=0:y=0:w=600:h=90:color=white:t=fill\" -frames:v 1 \"{raw}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
            var square = Path.Combine(tmp, "square.jpg");
            await VideoAssembler.CropScaleToSquareAsync(new GenConfig(), raw, square);
            var dims = await VideoAssembler.GetImageDimensionsAsync(new GenConfig(), square);
            Assert.Equal((1024, 1024), dims);
            var (gray, reason) = QualityGate.LoadGray256(square);
            Assert.NotNull(gray);
            // LoadGray256 downsamples to 256x256: the band spans rows
            // 0-38 there, so row 5, centre column must be near-white.
            Assert.True(gray![5 * 256 + 128] > 200,
                $"top-centre grey is {gray[5 * 256 + 128]}, expected near-white (head kept)");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void NoTextSuffixCarriesEyeCatchingDirection()
    {
        // Eye-catching direction (Rishi 2026-11-25): every image must
        // stop the scroll — dramatic, high-contrast, expressive.
        foreach (var clause in new[]
        {
            "dramatic cinematic lighting", "high contrast",
            "expressive faces", "strong emotion", "dynamic composition",
            "vibrant colour", "rule of thirds", "clear focal subject",
            "no dull flat frames",
        })
            Assert.Contains(clause, QualityGate.NoTextSuffix, StringComparison.OrdinalIgnoreCase);
        // ...layered in FRONT of the wide-shot / no-text guards, which
        // fixed the over-zoom problem and must survive verbatim.
        foreach (var guard in new[]
        {
            "wide shot", "subject with headroom", "full scene visible",
            "no close-up", "no cropped faces",
            "no text, no words, no letters, no signage",
        })
            Assert.Contains(guard, QualityGate.NoTextSuffix, StringComparison.OrdinalIgnoreCase);
        // The whole direction rides on every sanitized prompt.
        var (clean, _) = QualityGate.SanitizeImagePrompt("A footballer celebrates a goal in a packed stadium");
        Assert.Contains("dramatic cinematic lighting", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expressive faces", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wide shot", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeAppendsWideShotEvenWhenNoTextPresent()
    {
        // Bake-off fix: EVERY prompt carries the wide-shot wording,
        // not just prompts that lacked "no text".
        var (clean, _) = QualityGate.SanitizeImagePrompt("Clean illustration of a port, no text");
        Assert.Contains("wide shot", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("subject with headroom", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignageIsTextRequest()
    {
        Assert.True(QualityGate.ImagePromptHasTextRequest("A photo of a shop with signage reading OPEN"));
        // ...but the "no signage" clause in the suffix must not trip it.
        Assert.False(QualityGate.ImagePromptHasTextRequest("A photo of a port at dusk, no signage"));
        Assert.False(QualityGate.ImagePromptHasTextRequest(QualityGate.NoTextSuffix));
    }

    static VideoScript MixScript(params string[] sources)
    {
        var script = new VideoScript { Title = "Mix Test", Description = "Test.", Tags = ["news"] };
        for (var i = 0; i < sources.Length; i++)
        {
            script.Scenes.Add(new Scene
            {
                Narration = $"Scene {i} tells a distinct part of the story with its own facts and details.",
                ImagePrompt = "clean",
                ArtSource = sources[i],
                LandscapeImagePath = Path.Combine(Job4, $"scene{i:00}-art-square.jpg"),
            });
        }
        return script;
    }

    [Fact]
    public void ProviderMixPassesWhenPassingLegsDominate()
    {
        // 3/4 from passing legs (FLUX + Pexels) — no warning, even
        // though one scene is OVH filler.
        var f = QualityGate.CheckImagesBatch(Job4, MixScript("cloudflare-flux", "cloudflare-flux", "pexels-stock", "ovhcloud"));
        Assert.DoesNotContain(f, x => x.Check == "image-provider-mix");
        // pollinations-keyed counts as passing (same FLUX model): 2/4
        // is exactly half, not under half — no warning.
        var f2 = QualityGate.CheckImagesBatch(Job4, MixScript("pollinations-keyed", "pollinations-keyed", "ovhcloud", "ovhcloud"));
        Assert.DoesNotContain(f2, x => x.Check == "image-provider-mix");
        // wikimedia-commons counts as passing too (real freely-licensed
        // star photo, Rishi 2026-11-24): 2/4 is exactly half — no warning.
        var f3 = QualityGate.CheckImagesBatch(Job4, MixScript("wikimedia-commons", "wikimedia-commons", "ovhcloud", "ovhcloud"));
        Assert.DoesNotContain(f3, x => x.Check == "image-provider-mix");
    }

    [Fact]
    public void ProviderMixWarnsWhenPassingLegsAreMinority()
    {
        // 1/4 from passing legs — the primary leg under-delivered
        // (usually the FLUX daily quota running out mid-prep).
        var f = QualityGate.CheckImagesBatch(Job4, MixScript("cloudflare-flux", "ovhcloud", "ovhcloud", "ovhcloud"));
        Assert.Contains(f, x => x.Check == "image-provider-mix" && x.Severity == QualitySeverity.Warning);
    }

    // ------------------------------------------------------------------
    // MUSIC
    // ------------------------------------------------------------------

    [Fact]
    public void BlockedTrackIsError()
    {
        var f = QualityGate.CheckMusicTrack(
            "/x/alex-morgan-tense-suspense-rising-dread-573934.mp3",
            "tense atmospheric", videoSeconds: 0, description: null);
        Assert.Contains(f, x => x.Check == "music-blocked" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void UnknownTrackIsError()
    {
        var f = QualityGate.CheckMusicTrack(
            "/x/random-track.mp3", "tense atmospheric", videoSeconds: 0, description: null);
        Assert.Contains(f, x => x.Check == "music-pool" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void MissingTrackFileIsError()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-music-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var f = QualityGate.CheckMusicTrack(
                Path.Combine(tmp, "tense-newsroom-01.mp3"),
                "tense atmospheric", videoSeconds: 0, description: null);
            Assert.Contains(f, x => x.Check == "music-file" && x.Severity == QualitySeverity.Error);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ValidTrackPasses()
    {
        var f = QualityGate.CheckMusicTrack(
            TrackPath("tense-newsroom-01.mp3"), "tense atmospheric", videoSeconds: 0, description: null);
        Assert.False(HasError(f));
    }

    [Fact]
    public void MoodMismatchWarns()
    {
        var f = QualityGate.CheckMusicTrack(
            TrackPath("tense-newsroom-01.mp3"), "driving epic", videoSeconds: 0, description: null);
        Assert.Contains(f, x => x.Check == "music-mood" && x.Severity == QualitySeverity.Warning);
        Assert.False(HasError(f));
    }

    [Fact]
    public void CcByWithoutCreditIsError()
    {
        var f = QualityGate.CheckMusicTrack(
            TrackPath("uplifting-tech-01.mp3"), "driving epic", videoSeconds: 0,
            description: "A news video about Ukraine and global events.");
        Assert.Contains(f, x => x.Check == "music-credit" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void CcByWithCreditPasses()
    {
        var credit = QualityGate.ApprovedTracks["uplifting-tech-01.mp3"].Credit!;
        var f = QualityGate.CheckMusicTrack(
            TrackPath("uplifting-tech-01.mp3"), "driving epic", videoSeconds: 0,
            description: "A news video about Ukraine. " + credit);
        Assert.DoesNotContain(f, x => x.Check == "music-credit" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void ComputePeakTrimDbSanity()
    {
        // Peak after the gain is already below the -3 dBFS ceiling: no trim.
        Assert.Equal(0, QualityGate.ComputePeakTrimDb(-0.0, -15), precision: 3);
        Assert.Equal(0, QualityGate.ComputePeakTrimDb(-4.5, -2), precision: 3);
        // Peak after the gain would exceed the ceiling: trim exactly to it.
        Assert.Equal(-1.0, QualityGate.ComputePeakTrimDb(-0.0, -2), precision: 3);
        Assert.Equal(-1.8, QualityGate.ComputePeakTrimDb(-0.2, -1), precision: 3);
    }

    [Fact]
    public void MusicClippingWarningNamesTrim()
    {
        // The warning must say what the mix does about the hot master
        // (re-gain + peak-headroom trim), not claim the mix distorts —
        // the mix pulls the bed ~15-18 dB down, so it does not clip.
        var f = QualityGate.CheckMusicTrack(
            TrackPath("tense-newsroom-01.mp3"), "tense atmospheric", videoSeconds: 0, description: null);
        Assert.Contains(f, x => x.Check == "music-clipping" && x.Severity == QualitySeverity.Warning);
        Assert.Contains(f, x => x.Check == "music-clipping" && x.Message.Contains("peak-headroom trim"));
    }

    [Fact]
    public void FindCleanSameMoodTrackNoneInRealPool()
    {
        // Measured 2026-11-24: both tense beds clip and both epic
        // beds clip, so no clean same-mood alternative exists in
        // today's pool — the trim, not the fallback, does the work.
        Assert.Null(QualityGate.FindCleanSameMoodTrack(MusicDir, "tense atmospheric", "tense-newsroom-01.mp3"));
        Assert.Null(QualityGate.FindCleanSameMoodTrack(MusicDir, "driving epic", "uplifting-tech-01.mp3"));
        Assert.Null(QualityGate.FindCleanSameMoodTrack(MusicDir, "curious ambient", "calm-neutral-01.mp3"));
    }

    [Fact]
    public void FindCleanSameMoodTrackFindsCleanOne()
    {
        // Synthetic pool: same approved filenames, different file
        // content — tense-newsroom-02 carries the clean calm track,
        // so it is the fallback for a clipping tense-newsroom-01. A
        // non-pool file (tense-extra.mp3, also clean) must NOT be picked.
        var tmp = Path.Combine(Path.GetTempPath(), "qg-music-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.Copy(TrackPath("tense-newsroom-01.mp3"), Path.Combine(tmp, "tense-newsroom-01.mp3"));
            File.Copy(TrackPath("calm-neutral-01.mp3"), Path.Combine(tmp, "tense-newsroom-02.mp3"));
            File.Copy(TrackPath("calm-neutral-01.mp3"), Path.Combine(tmp, "tense-extra.mp3"));
            var found = QualityGate.FindCleanSameMoodTrack(tmp, "tense atmospheric", "tense-newsroom-01.mp3");
            Assert.Equal(Path.Combine(tmp, "tense-newsroom-02.mp3"), found);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void PostGainPeakBelowCeilingForAllPoolTracks()
    {
        // Music-subagent verification 2026-11-24: with the -34 LUFS
        // re-gain, every pool track's peak lands at -19..-22 dBFS in
        // the mix — well below the -3 dBFS ceiling — so the
        // peak-headroom trim is a no-op for all five tracks today
        // (belt-and-braces). The test recomputes the gain from each
        // track's measured integrated loudness, so a future hot master
        // or bed-target change fails here first.
        var bedLufs = ContentProfileRegistry.Get("trendlinenow").BackgroundMusicLufs;
        foreach (var file in QualityGate.ApprovedTracks.Keys)
        {
            var path = TrackPath(file);
            var psi = new ProcessStartInfo("ffmpeg", $"-i \"{path}\" -af ebur128=framelog=quiet -f null -")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var integrated = QualityGate.ParseEbur128Integrated(stderr);
            Assert.True(integrated.HasValue, $"ebur128 parse failed for {file}");
            var (_, max) = QualityGate.MeasureTrackLevel(path);
            Assert.True(max.HasValue, $"volumedetect failed for {file}");
            var gainDb = bedLufs - integrated!.Value;
            Assert.Equal(0, QualityGate.ComputePeakTrimDb(max!.Value, gainDb), precision: 3);
            Assert.True(max.Value + gainDb <= QualityGate.MusicPeakCeilingDb,
                $"{file}: post-gain peak {max.Value + gainDb:0.0} dBFS exceeds the ceiling");
        }
    }

    [Fact]
    public void ProbeAudioValid()
    {
        var (ok, error) = QualityGate.ProbeAudio(TrackPath("tense-newsroom-01.mp3"));
        Assert.True(ok, error ?? "probe failed");
        Assert.True(QualityGate.ProbeAudioDuration(TrackPath("tense-newsroom-01.mp3")) > 0);
    }

    [Fact]
    public void ProbeAudioInvalid()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-notaudio-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tmp, "not audio at all");
        try
        {
            var (ok, _) = QualityGate.ProbeAudio(tmp);
            Assert.False(ok);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ParseEbur128()
    {
        var sample = """
            [Parsed_ebur128_0 @ 0x7f123] Summary:

              Integrated loudness:
                I:         -14.2 LUFS
                Threshold:   -34.0 LUFS

              Loudness range:
                LRA:         5.1 LU
            """;
        Assert.Equal(-14.2, QualityGate.ParseEbur128Integrated(sample)!.Value, precision: 1);
        Assert.Null(QualityGate.ParseEbur128Integrated("no summary here"));
    }

    [Fact]
    public void ParseSilence()
    {
        var sample = """
            [silencedetect @ 0x123] silence_start: 0
            [silencedetect @ 0x123] silence_end: 2.34 | silence_duration: 2.34
            [silencedetect @ 0x123] silence_start: 30.5
            [silencedetect @ 0x123] silence_end: 31.0 | silence_duration: 0.5
            """;
        var gaps = QualityGate.ParseSilence(sample);
        Assert.Equal(2, gaps.Count);
        Assert.Equal(0, gaps[0].Start, precision: 1);
        Assert.Equal(2.34, gaps[0].Duration, precision: 2);
    }

    [Fact]
    public void CheckFinalMixShortPasses()
    {
        // Video #4's finished Short: loudnorm'd to -14 LUFS, narration
        // from 0:00. Proves the final-mix path end to end. The file now
        // lives under done/ (moved there by the uploader after the
        // 2026-11-24 upload), so the search covers subdirectories.
        var shortPath = Directory.GetFiles(
                "/home/hatch/workspace/BroadcastContentAutomation/videos/trendlinenow",
                "ukraine-*-short.mp4", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        var f = QualityGate.CheckFinalMix(shortPath, expectedSeconds: 72.8);
        Assert.False(HasError(f));
    }

    // ------------------------------------------------------------------
    // SCRIPT — craft (correctness / scene fit / retention + SEO,
    // Rishi 2026-11-23 ~23:45). Severity policy: warn by default;
    // only clear-cut defects error. Calibrated 2026-11-24 on the
    // approved COP31 + Bisleri scripts (see the design note):
    // COP31 gains 0 new findings, Bisleri gains 5 near-duplicate
    // errors — all verified genuine restatements.
    // ------------------------------------------------------------------

    // A 12-scene script that passes every craft check cleanly: varied
    // lengths (stdev ~22 chars), consecutive scenes sharing a content
    // word, no dead stretch, backstory markers in the middle third,
    // forward markers in the last third, title words stemmed in the
    // body, topic keywords in the description's first lines and tags.
    static VideoScript CraftBaselineScript()
    {
        var narrations = new[]
        {
            "Ukrainian naval drones crossed the Black Sea toward the Libyan coast and struck Russian warships near Benghazi port overnight.",
            "The strike hit two Russian vessels anchored off Libya, and satellite images later showed smoke rising from the harbour as crews fought the fires and rescue boats circled the burning ships.",
            "Libya's coast guard reported the attack began after midnight, when the drones slipped past harbour defences built to stop exactly this kind of raid.",
            "Moscow denied any warships were damaged, but independent analysts matched the burn marks in the satellite photos to the vessels photographed in port days earlier.",
            "The operation marks a sharp expansion of Ukraine's reach, taking a drone campaign built over years of Black Sea operations more than 1,500 kilometres from its own shores into the Mediterranean.",
            "Naval experts say the raid relied on long-range guidance first tested in the Black Sea, where similar drones sank a Russian landing ship last year.",
            "For Libya, the attack lands in the middle of a fragile ceasefire, and officials in Tripoli warned that foreign strikes risk dragging the country back into war.",
            "Oil traders reacted within hours, and the price of Brent crude climbed as markets priced in the risk to shipping through the Mediterranean corridor.",
            "Ukraine has not claimed the strike directly, though officials in Kyiv hinted the navy can now reach targets far beyond the Black Sea.",
            "The raid changes the map for both sides, and the next moves will be watched closely in Moscow, in Kyiv, and across the Mediterranean.",
            "What happens next depends on whether Russia reinforces its Mediterranean squadron or pulls its remaining warships back to safer waters.",
            "Analysts expect the Mediterranean to stay tense, and the next drone wave will test whether Russia can protect its ships so far from home.",
        };
        var script = new VideoScript
        {
            Title = "Ukraine's Drones Strike Russian Ships in Libya – What It Means",
            Description = "Ukraine's drones struck Russian warships off Libya. We break down what happened, why it matters, and what happens next. Watch more at youtube.com/@spotlightnowofficial — new video daily.",
            Tags = ["ukraine", "russia", "drones", "libya", "news", "trending"],
            BriefScenes =
            [
                new Scene { Narration = "Ukraine's drones struck Russian warships 1,500 km from its borders, marking a new reach for the conflict." },
                new Scene { Narration = "The full story is more surprising than the headline suggests." },
            ],
        };
        foreach (var n in narrations)
            script.Scenes.Add(new Scene { Narration = n, ImagePrompt = "Clean illustration of a cargo ship at a port at dusk, navy and red tones, no text" });
        return script;
    }

    static List<QualityFinding> Craft(VideoScript s, string? topic = Topic) =>
        QualityGate.CheckScriptCraft(s, topic);

    [Fact]
    public void CraftBaselinePassesClean()
    {
        var f = Craft(CraftBaselineScript());
        Assert.Empty(f);
    }

    [Fact]
    public void SentenceIncompleteIsError()
    {
        var s = CraftBaselineScript();
        s.Scenes[3].Narration = s.Scenes[3].Narration.TrimEnd('.');
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "sentence-complete" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void SentenceCompleteAllowsTrailingQuote()
    {
        // COP31 scene 35 ends "…technology.”" — the closer must be
        // stripped before judging the final character.
        var s = CraftBaselineScript();
        s.Scenes[3].Narration = "Moscow denied any warships were damaged, but analysts matched the burn marks to the vessels in port days earlier, calling it “a clear hit.”";
        var f = Craft(s);
        Assert.DoesNotContain(f, x => x.Check == "sentence-complete");
    }

    [Fact]
    public void ImmediateRepeatIsError()
    {
        var s = CraftBaselineScript();
        s.Scenes[2].Narration = "Libya's coast guard reported the the attack began after midnight, when the drones slipped past harbour defences.";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "repeated-word" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void ContentWordStuffingWarns_StopwordDoesNot()
    {
        var s = CraftBaselineScript();
        s.Scenes[2].Narration = "The water crisis deepened as water supplies failed, water trucks arrived late, and water became the story of the day in Kerala.";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "word-stuffing" && x.Severity == QualitySeverity.Warning);

        // "the" 4x in one scene is fine (COP31 scene 14 does exactly
        // this) — the stuffing rule only counts content words.
        var s2 = CraftBaselineScript();
        s2.Scenes[2].Narration = "The coast guard reported the attack began after midnight, and the drones slipped past the harbour defences in the dark.";
        var f2 = Craft(s2);
        Assert.DoesNotContain(f2, x => x.Check == "word-stuffing");
    }

    [Fact]
    public void NameSpellingIsError()
    {
        var s = CraftBaselineScript();
        s.Scenes[1].Narration = "The plan was announced by Zelenskiy after weeks of talks in Kyiv, and the navy confirmed the details the same day.";
        s.Scenes[4].Narration = "Officials confirmed Zelensky later denied the report, and the ministry in Kyiv declined to comment on the story.";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "name-spelling" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void NameSpellingIgnoresNounAdjectivePairs()
    {
        // "Africa" / "African" is a noun/adjective pair, not a spelling
        // error — the prefix rule must not flag it (both scripts do this).
        var s = CraftBaselineScript();
        s.Scenes[1].Narration = "Leaders from across Africa gathered in Nairobi, and African officials led the talks on the climate deal.";
        var f = Craft(s);
        Assert.DoesNotContain(f, x => x.Check == "name-spelling");
    }

    [Fact]
    public void MeasurementInconsistentWarns_BareUnitsDoNot()
    {
        var s = CraftBaselineScript();
        s.Scenes[1].Narration = "The 250-foot arch towered over the National Mall in Washington, drawing crowds every single day.";
        s.Scenes[4].Narration = "The 200-foot arch drew crowds downtown, and the city said the monument would stay open late.";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "measurement-consistency" && x.Severity == QualitySeverity.Warning);

        // Bare units are NOT compared: COP31 correctly says both 1.5°C
        // (target) and 2°C (goal).
        var s2 = CraftBaselineScript();
        s2.Scenes[1].Narration = "Scientists warn of 1.5°C of warming, though the goal is to stay below 2°C if the world acts fast enough.";
        var f2 = Craft(s2);
        Assert.DoesNotContain(f2, x => x.Check == "measurement-consistency");
    }

    [Fact]
    public void NearDuplicateError()
    {
        // Bisleri scenes 13–27 score 0.611 on content-word Jaccard:
        // near-verbatim restatement of the same founding story.
        var s = CraftBaselineScript();
        s.Scenes[1].Narration = "Bisleri was founded in 1969 by Ramesh Chauhan and grew into India's biggest bottled water and soft drinks company.";
        s.Scenes[4].Narration = "Founded in 1969 by Ramesh Chauhan, Bisleri grew into India's biggest bottled water and soft drinks company.";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "near-duplicate-scene" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void NearDuplicateWarningBand()
    {
        // Jaccard 0.44: heavy overlap, distinct scenes — warns, does
        // not error. (Computed: 7 shared of 16 union.)
        var s = CraftBaselineScript();
        s.Scenes[1].Narration = "The arch is 250 feet tall and dominates the National Mall in Washington, drawing crowds every day.";
        s.Scenes[4].Narration = "The 250-foot arch dominates the National Mall in Washington, where crowds gather in the morning light.";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "near-duplicate-scene" && x.Severity == QualitySeverity.Warning);
        Assert.DoesNotContain(f, x => x.Check == "near-duplicate-scene" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void JaccardSimilaritySanity()
    {
        Assert.Equal(1.0, QualityGate.JaccardSimilarity(
            new HashSet<string> { "a", "b" }, new HashSet<string> { "a", "b" }), precision: 3);
        Assert.Equal(0.0, QualityGate.JaccardSimilarity(
            new HashSet<string> { "a" }, new HashSet<string> { "b" }), precision: 3);
        Assert.Equal(0.5, QualityGate.JaccardSimilarity(
            new HashSet<string> { "a", "b" }, new HashSet<string> { "a", "b", "c", "d" }), precision: 3);
    }

    [Fact]
    public void StemWordSanity()
    {
        Assert.Equal(QualityGate.StemWord("collapses"), QualityGate.StemWord("collapsed"));
        Assert.Equal(QualityGate.StemWord("bottle"), QualityGate.StemWord("bottled"));
        Assert.Equal(QualityGate.StemWord("checks"), QualityGate.StemWord("check"));
        Assert.Equal(QualityGate.StemWord("expanding"), QualityGate.StemWord("expand"));
        Assert.Equal("glass", QualityGate.StemWord("glass")); // never strips "ss"
    }

    [Fact]
    public void DisconnectTripwireOnlyAtScriptLevel()
    {
        // Six scenes sharing zero content words: 5/5 consecutive pairs
        // disconnected (>80%) — the script-level tripwire fires.
        var s = new VideoScript { Title = "Six Unrelated Stories", Description = "Six stories.", Tags = ["news"] };
        var narrations = new[]
        {
            "The penguin colony gathered on the Antarctic ice as winter storms approached the frozen coast.",
            "Bakers in Paris kneaded sourdough through the night before the morning market opened its stalls.",
            "Astronauts aboard the station repaired a solar panel while Earth turned slowly beneath them.",
            "The violinist practised scales in an empty hall as rain tapped against the tall windows.",
            "Farmers in the valley harvested wheat before the harvest festival brought crowds to the village square.",
            "The lighthouse keeper polished the great lens while gulls circled the rocky shore at dawn.",
        };
        foreach (var n in narrations)
            s.Scenes.Add(new Scene { Narration = n });
        var f = Craft(s, topic: null);
        Assert.Contains(f, x => x.Check == "scene-disconnect" && x.Severity == QualitySeverity.Warning);
        // ...but the craft baseline (27% disconnected) does not trip it.
        Assert.DoesNotContain(Craft(CraftBaselineScript()), x => x.Check == "scene-disconnect");
    }

    [Fact]
    public void RhythmWarnsWhenUniform()
    {
        var s = new VideoScript { Title = "Uniform Story", Description = "A story.", Tags = ["news"] };
        for (var i = 0; i < 8; i++)
            s.Scenes.Add(new Scene { Narration = $"Scene number {i} fills the screen with a steady story beat that keeps the viewer watching the channel." });
        var f = Craft(s, topic: null);
        Assert.Contains(f, x => x.Check == "rhythm" && x.Severity == QualitySeverity.Warning);
        Assert.DoesNotContain(Craft(CraftBaselineScript()), x => x.Check == "rhythm");
    }

    [Fact]
    public void DeadStretchWarns()
    {
        var s = CraftBaselineScript();
        s.Scenes[2].Narration = "The plan moved forward quietly as officials kept the details close and the public waited for news.";
        s.Scenes[3].Narration = "The mood in the room shifted as the talks dragged on and the delegates waited for a signal.";
        s.Scenes[4].Narration = "The meeting ended without a statement, and the reporters outside waited for someone to speak.";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "dead-stretch" && x.Severity == QualitySeverity.Warning);
    }

    [Fact]
    public void ActRolesWarnWhenFlat()
    {
        // 9 identical flat scenes: middle third has no backstory
        // markers, last third has no forward-looking language.
        var s = new VideoScript { Title = "Flat Story", Description = "A story.", Tags = ["news"] };
        for (var i = 0; i < 9; i++)
            s.Scenes.Add(new Scene { Narration = "The committee met in the hall and discussed the proposal at length as the session continued." });
        var f = Craft(s, topic: null);
        Assert.Contains(f, x => x.Check == "act2-backstory" && x.Severity == QualitySeverity.Warning);
        Assert.Contains(f, x => x.Check == "act3-forward" && x.Severity == QualitySeverity.Warning);
        Assert.DoesNotContain(Craft(CraftBaselineScript()), x => x.Check == "act2-backstory");
        Assert.DoesNotContain(Craft(CraftBaselineScript()), x => x.Check == "act3-forward");
    }

    [Fact]
    public void TitleStoryUndeliveredIsError()
    {
        var s = CraftBaselineScript();
        s.Title = "The Secret Plan Nobody Saw Coming";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "title-story" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void TitleStoryPartialIsWarning()
    {
        var s = CraftBaselineScript();
        s.Title = "Ukraine and the Secret Plan";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "title-story" && x.Severity == QualitySeverity.Warning);
        Assert.DoesNotContain(f, x => x.Check == "title-story" && x.Severity == QualitySeverity.Error);
    }

    [Fact]
    public void DescriptionKeywordsWarns()
    {
        var s = CraftBaselineScript();
        s.Description = "A big story broke today. We break down what happened and why it matters, with new videos every single day.";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "description-keywords" && x.Severity == QualitySeverity.Warning);
    }

    [Fact]
    public void TagCoverageWarns()
    {
        var s = CraftBaselineScript();
        s.Tags = ["news", "trending", "daily", "world"];
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "tag-coverage" && x.Severity == QualitySeverity.Warning);
    }

    [Fact]
    public void KeywordStuffingWarns()
    {
        var s = CraftBaselineScript();
        s.Title = "Ukraine Ukraine Ukraine Ukraine Ukraine Ukraine";
        s.Description = "Ukraine news about Ukraine and Ukraine.";
        var f = Craft(s);
        Assert.Contains(f, x => x.Check == "keyword-stuffing" && x.Severity == QualitySeverity.Warning);
    }

    // ------------------------------------------------------------------
    // SCRIPT — craft on the two approved real scripts (calibration)
    // ------------------------------------------------------------------

    const string JobCop31 = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261123-083645";
    const string JobBisleri = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261124-122414";

    static VideoScript LoadJobScript(string job) =>
        JsonSerializer.Deserialize<VideoScript>(File.ReadAllText(Path.Combine(job, "script.json")))!;

    [Fact]
    public void Cop31CraftHasNoErrors()
    {
        // Calibration 2026-11-24: COP31 gains 0 new findings from the
        // craft gate — it is the clean approved script.
        var f = Craft(LoadJobScript(JobCop31), "COP31 climate deal collapse");
        Assert.False(HasError(f), string.Join("; ", f.Where(x => x.Severity == QualitySeverity.Error).Select(x => x.ToString())));
    }

    [Fact]
    public void BisleriCraftFlagsNearDuplicates()
    {
        // Calibration 2026-11-24: Bisleri's approved script contains
        // 5 genuine near-duplicate pairs (Jaccard ≥0.50) — the craft
        // gate's highest-value catch. All 5 are the same founding-story
        // restatement; the gate must flag them and nothing else as an
        // error.
        var f = Craft(LoadJobScript(JobBisleri), "Bisleri water unsafe Kerala bottle checks");
        var errors = f.Where(x => x.Severity == QualitySeverity.Error).ToList();
        Assert.Equal(5, errors.Count(x => x.Check == "near-duplicate-scene"));
        Assert.DoesNotContain(errors, x => x.Check != "near-duplicate-scene");
        Assert.Contains(errors, x => x.Message.Contains("Scenes 13 and 27"));
    }

    // ------------------------------------------------------------------
    // IMAGE — prompt improver (selective repair)
    // ------------------------------------------------------------------

    [Fact]
    public void ImprovePromptRebuildsTitleCardFromNarration()
    {
        var (improved, changes) = QualityGate.ImproveImagePrompt(
            "A title card with bold lettering 'UKRAINE DRONES STRIKE' over a warship",
            "Ukrainian naval drones struck Russian warships near the Libyan port of Benghazi.");
        Assert.DoesNotContain("lettering", improved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UKRAINE DRONES STRIKE", improved);
        Assert.Contains("Benghazi", improved);
        Assert.Contains("no text", improved, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(changes);
    }

    [Fact]
    public void ImprovePromptAddsComposition()
    {
        var (person, _) = QualityGate.ImproveImagePrompt(
            "A photo of a naval officer on the deck of a warship",
            "The officer stood on the deck of the warship as the crew prepared for departure.");
        Assert.Contains("medium shot", person, StringComparison.OrdinalIgnoreCase);
        // Eye-catching direction (2026-11-25): person scenes carry an
        // expressive face, not just a medium shot.
        Assert.Contains("expressive face", person, StringComparison.OrdinalIgnoreCase);

        var (place, _) = QualityGate.ImproveImagePrompt(
            "A photo of a harbour at dawn",
            "The harbour at dawn was quiet as the ships waited in the bay.");
        Assert.Contains("wide establishing shot", place, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImprovePromptNeverReturnsSamePrompt()
    {
        var original = "Clean illustration of a port, no text";
        // Narration with no person/place terms, so no composition
        // directive is added — the variation clause is the only change.
        var (improved, _) = QualityGate.ImproveImagePrompt(original, "The day was quiet and slow.");
        Assert.NotEqual(original, improved);
        Assert.Contains("different angle", improved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImprovePromptDuplicateAddsVariation()
    {
        var (improved, _) = QualityGate.ImproveImagePrompt(
            "A photo of a cargo ship at a port",
            "The cargo ship waited at the port as dawn broke.",
            ["image-duplicate"]);
        Assert.Contains("different angle", improved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConcreteSubjectsSkipsSentenceInitial()
    {
        var subjects = QualityGate.ConcreteSubjects(
            "The harbour at Benghazi was quiet as Russian ships waited offshore.");
        Assert.Contains(subjects, s => s.Equals("Benghazi", StringComparison.Ordinal));
        Assert.Contains(subjects, s => s.Equals("Russian", StringComparison.Ordinal));
        Assert.DoesNotContain(subjects, s => s.Equals("The", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // IMAGE — perceptual near-duplicates (Ronaldo 2026-11-25)
    //
    // The checker missed near-identical statue photos (scenes 5+6) and a
    // byte-identical clip (scenes 26+31) because it never looked at image
    // content. These tests prove the aHash + grey-histogram check catches
    // the original defects from the backups in the Ronaldo job dir
    // (scene04-stock.mp4.bad = hockey clip, scene06-art.png.bad = the
    // duplicate statue photo) — read-only, never the fixed live files.
    // ------------------------------------------------------------------

    const string JobRonaldo = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261125-234227";

    static byte[] GrayBufferWith(byte topHalf, byte bottomHalf)
    {
        var buf = new byte[256 * 256];
        for (var y = 0; y < 256; y++)
            for (var x = 0; x < 256; x++)
                buf[y * 256 + x] = y < 128 ? topHalf : bottomHalf;
        return buf;
    }

    [Fact]
    public void AverageHashIdenticalBuffersAreNearDuplicates()
    {
        var buf = GrayBufferWith(200, 40);
        var hash = QualityGate.ComputeAverageHash(buf);
        Assert.Equal(0, QualityGate.HammingDistance(hash, hash));
        Assert.Equal(1.0, QualityGate.GrayHistogramCosine(buf, buf), precision: 6);
        Assert.True(QualityGate.AreNearDuplicates(hash, hash, 1.0));
    }

    [Fact]
    public void AverageHashNegativeIsNotNearDuplicate()
    {
        // Photographic negative: every cell flips across the mean, so the
        // hash is the bitwise complement (distance 64) and the histogram
        // shares no bins — the opposite of a near-duplicate.
        var buf = GrayBufferWith(200, 40);
        var neg = buf.Select(v => (byte)(255 - v)).ToArray();
        var hash = QualityGate.ComputeAverageHash(buf);
        var negHash = QualityGate.ComputeAverageHash(neg);
        Assert.Equal(64, QualityGate.HammingDistance(hash, negHash));
        Assert.Equal(0.0, QualityGate.GrayHistogramCosine(buf, neg), precision: 6);
        Assert.False(QualityGate.AreNearDuplicates(hash, negHash,
            QualityGate.GrayHistogramCosine(buf, neg)));
    }

    [Fact]
    public void ReuseDistanceBandsFollowRishisRule()
    {
        // Rishi 2026-11-25 23:49: reuse only 4-7+ scenes later, never
        // consecutive. Distance < 4 errors, 4-7 warns, 8+ is allowed.
        Assert.Equal(QualitySeverity.Error, QualityGate.ReuseDistanceSeverity(0));
        Assert.Equal(QualitySeverity.Error, QualityGate.ReuseDistanceSeverity(3));
        Assert.Equal(QualitySeverity.Warning, QualityGate.ReuseDistanceSeverity(4));
        Assert.Equal(QualitySeverity.Warning, QualityGate.ReuseDistanceSeverity(7));
        Assert.Null(QualityGate.ReuseDistanceSeverity(8));
    }

    [Fact]
    public void StockSubjectMismatchFlagsHockeyInFootballStory()
    {
        var hockey = "https://www.pexels.com/video/exciting-sweden-vs-norway-ice-hockey-match-highlights-32159115/";
        var football = "https://www.pexels.com/video/vibrant-football-stadium-during-evening-match-34699783/";
        // Mirrors the batch check's scene text (query + narration +
        // title + tags): the tags carry the subject words the narration
        // never spells out — without them a correct football clip would
        // flag on a Ronaldo story that never says "football".
        var sceneText = "cristiano ronaldo celebrating goal portugal national team — He carried Portugal to glory for years. Cristiano Ronaldo Hangs Up Portugal Boots — cristiano ronaldo, pep guardiola, portugal football, football legends, sports news";
        Assert.Equal("ice hockey", QualityGate.StockSubjectMismatch(hockey, sceneText));
        Assert.Null(QualityGate.StockSubjectMismatch(football, sceneText));
        // "soccer" in the slug is the same sport as "football" in the scene.
        Assert.Null(QualityGate.StockSubjectMismatch(
            "https://www.pexels.com/video/aerial-view-of-a-soccer-field-at-night-2249402/",
            "football stadium lights — the crowd roared under the floodlights"));
        // American football is not excused by the shared word "football".
        Assert.Equal("american football", QualityGate.StockSubjectMismatch(
            "https://www.pexels.com/video/nfl-game-highlights-123/",
            "soccer match under the lights — the crowd roared"));
        // No listed subject in the metadata: no signal, no finding.
        Assert.Null(QualityGate.StockSubjectMismatch(
            "https://www.pexels.com/video/sevilla-20537082/", sceneText));
        Assert.Null(QualityGate.StockSubjectMismatch(null, sceneText));
    }

    [Fact]
    public void PickBestVideoFileExcludesVideoIds()
    {
        // Two clips: the best (1080p 30fps) and a lesser one (720p 30fps).
        // Excluding the best must pick the other, not the same footage.
        using var doc = JsonDocument.Parse("""
            [
              {"id": 111, "url": "https://www.pexels.com/video/best-111/",
               "video_files": [{"file_type": "video/mp4", "width": 1920, "height": 1080, "fps": 30, "link": "https://cdn/best.mp4"}]},
              {"id": 222, "url": "https://www.pexels.com/video/other-222/",
               "video_files": [{"file_type": "video/mp4", "width": 1280, "height": 720, "fps": 30, "link": "https://cdn/other.mp4"}]}
            ]
            """);
        var videos = doc.RootElement;
        var (link, id, page) = StockFootageClient.PickBestVideoFile(videos, ImageClient.Orientation.Landscape);
        Assert.Equal("https://cdn/best.mp4", link);
        Assert.Equal(111, id);
        Assert.Equal("https://www.pexels.com/video/best-111/", page);

        var (link2, id2, _) = StockFootageClient.PickBestVideoFile(
            videos, ImageClient.Orientation.Landscape, new HashSet<long> { 111 });
        Assert.Equal("https://cdn/other.mp4", link2);
        Assert.Equal(222, id2);

        var (link3, id3, _) = StockFootageClient.PickBestVideoFile(
            videos, ImageClient.Orientation.Landscape, new HashSet<long> { 111, 222 });
        Assert.Null(link3);
        Assert.Null(id3);
    }

    static string RonaldoSquare(string sceneFile) => Path.Combine(JobRonaldo, sceneFile);

    // Frozen fixtures (Tests/Fixtures/QualityGate, copied to the test
    // output dir): the live Ronaldo job dir is NOT a fixture source —
    // the scene-6 repair replaced its square with the celebration photo,
    // which silently broke NearDuplicateStatuePairIsError (it pins the
    // DEFECTIVE pair). Frozen files pin the state the test asserts on,
    // so a later repair of the live job can never break it again.
    // Stored base64 (.jpg.b64): the GitHub push tool carries UTF-8 text
    // only, so binary JPEGs cannot be pushed; the test decodes them back
    // to temp files (cached per name under %TEMP%/qg-fixtures).
    static string FixtureSquare(string name)
    {
        var b64Path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "QualityGate", name + ".b64");
        var decoded = Path.Combine(Path.GetTempPath(), "qg-fixtures", name);
        if (!File.Exists(decoded))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(decoded)!);
            File.WriteAllBytes(decoded, Convert.FromBase64String(File.ReadAllText(b64Path)));
        }
        return decoded;
    }

    // Builds a scratch job dir with square art files copied from the
    // Ronaldo job (read-only source) plus a minimal script. `squares`
    // maps scene index -> square filename, resolved with `resolveSquare`
    // (the live job dir by default, frozen fixtures when given);
    // `squareFrom` maps scene index -> a source file to scale to a
    // 1024x1024 square (the fixed scene-6 portrait has no square on
    // disk — the repair replaced the portrait only, which is itself
    // the incomplete-repair finding the live-job check surfaces).
    static (string Dir, VideoScript Script) ScratchNearDupJob(
        Dictionary<int, string> squares, Dictionary<int, string>? squareFrom = null,
        Func<string, string>? resolveSquare = null)
    {
        var resolve = resolveSquare ?? RonaldoSquare;
        var tmp = Path.Combine(Path.GetTempPath(), "qg-neardup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var script = new VideoScript
        {
            Title = "Cristiano Ronaldo Hangs Up Portugal Boots - A Legendary Farewell",
            Description = "Ronaldo retires from Portugal. We break down what happened, why it matters, and what happens next.",
            Tags = ["ronaldo", "portugal", "football", "news"],
            MusicMood = "tense atmospheric",
        };
        var keys = squares.Keys.AsEnumerable();
        if (squareFrom is not null) keys = keys.Concat(squareFrom.Keys);
        var maxScene = keys.DefaultIfEmpty(-1).Max();
        for (var i = 0; i <= maxScene; i++)
        {
            var scene = new Scene
            {
                Narration = $"Ronaldo scene {i}: Portugal's captain led the team through a long international career with 234 appearances and 146 goals for his country.",
                ImagePrompt = "A football stadium at night under floodlights",
                ArtSource = "wikimedia-commons",
            };
            var dest = Path.Combine(tmp, $"scene{i:D2}-art-square.jpg");
            if (squares.TryGetValue(i, out var square))
            {
                File.Copy(resolve(square), dest);
            }
            else if (squareFrom is not null && squareFrom.TryGetValue(i, out var source))
            {
                var psi = new ProcessStartInfo("ffmpeg",
                    $"-y -v error -i \"{RonaldoSquare(source)}\" -vf scale=1024:1024 \"{dest}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                Assert.True(File.Exists(dest), "ffmpeg square failed for " + source);
            }
            else continue;
            scene.LandscapeImagePath = dest;
            scene.ImagePath = dest;
            script.Scenes.Add(scene);
        }
        return (tmp, script);
    }

    [Fact]
    public void NearDuplicateStatuePairIsError()
    {
        // The ORIGINAL defect: scenes 5+6, near-identical photos of the
        // same CR7 statue (Commons -23 and -24), back to back. Distance
        // 1 < 4, so the reuse rule makes it an error on scene 6. The
        // squares are FROZEN fixtures (Tests/Fixtures/QualityGate): the
        // live job's scene-6 square was replaced by the repair with the
        // celebration photo, so reading the live job dir made this test
        // fail — the pair is no longer near-duplicates there.
        var (dir, script) = ScratchNearDupJob(
            new Dictionary<int, string>
            {
                [0] = "scene08-art-square.jpg",
                [1] = "scene09-art-square.jpg",
                [2] = "scene10-art-square.jpg",
                [3] = "scene12-art-square.jpg",
                [4] = "scene13-art-square.jpg",
                [5] = "statue-scene05-square.jpg",
                [6] = "statue-scene06-square-defective.jpg",
            },
            resolveSquare: FixtureSquare);
        try
        {
            var findings = QualityGate.CheckImagesBatch(dir, script);
            Assert.Contains(findings, f => f.Check == "image-near-duplicate"
                && f.Severity == QualitySeverity.Error && f.Scene == "scene6");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FixedStatuePairPasses()
    {
        // The REPAIRED scene 6 (Ronaldo celebrating, Portugal No. 7) is
        // a different photo from the statue in scene 5 — no finding.
        // The fixed portrait is scaled to a square here, which is what
        // the repair should have done to the square source as well.
        var (dir, script) = ScratchNearDupJob(
            new Dictionary<int, string>
            {
                [0] = "scene08-art-square.jpg",
                [1] = "scene09-art-square.jpg",
                [2] = "scene10-art-square.jpg",
                [3] = "scene12-art-square.jpg",
                [4] = "scene13-art-square.jpg",
                [5] = "scene05-art-square.jpg",
            },
            new Dictionary<int, string> { [6] = "scene06-art.png" });
        try
        {
            var findings = QualityGate.CheckImagesBatch(dir, script);
            Assert.DoesNotContain(findings, f => f.Check == "image-near-duplicate");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void DifferentImagesNotFlagged()
    {
        // Scenes 17+18 (dark silhouette portrait vs boy in red Adidas
        // shirt): an earlier aHash-only scan flagged them at distance 6
        // — a false positive. The histogram half of the rule clears them.
        var (dir, script) = ScratchNearDupJob(
            new Dictionary<int, string>
            {
                [0] = "scene17-art-square.jpg",
                [1] = "scene18-art-square.jpg",
            });
        try
        {
            var findings = QualityGate.CheckImagesBatch(dir, script);
            Assert.DoesNotContain(findings, f => f.Check == "image-near-duplicate");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SameSubjectDifferentPoseNotFlagged()
    {
        // Scenes 10+16 (two FLUX Ronaldo portraits in red on dark
        // backgrounds): the same aHash distance (12) as the statue
        // portraits, but a different picture — close-up face with bokeh
        // vs medium shot with a football. The histogram half of the rule
        // (0.83 < 0.90) clears them, even back to back. A warning here
        // would be noise on every star video, where half the art is the
        // same person in the same kit.
        var (dir, script) = ScratchNearDupJob(
            new Dictionary<int, string>
            {
                [0] = "scene10-art-square.jpg",
                [1] = "scene16-art-square.jpg",
            });
        try
        {
            var findings = QualityGate.CheckImagesBatch(dir, script);
            Assert.DoesNotContain(findings, f => f.Check == "image-near-duplicate");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IdenticalClipAtDistanceFiveIsWarning()
    {
        // The Ronaldo 26/31 clip, byte-identical, 5 scenes apart: allowed
        // by Rishi's rule (reuse only 4-7+ scenes later), so it warns
        // instead of erroring — and the mid-frame hash catches it even
        // though the two files have different names.
        var tmp = Path.Combine(Path.GetTempPath(), "qg-clipdup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var script = new VideoScript
            {
                Title = "Cristiano Ronaldo Hangs Up Portugal Boots - A Legendary Farewell",
                Description = "Ronaldo retires from Portugal.",
                Tags = ["ronaldo", "portugal", "football", "news"],
                MusicMood = "tense atmospheric",
            };
            var clips = new Dictionary<int, string>
            {
                [0] = "scene26-stock.mp4",
                [1] = "scene07-stock.mp4",
                [2] = "scene14-stock.mp4",
                [3] = "scene21-stock.mp4",
                [4] = "scene29-stock.mp4",
                [5] = "scene31-stock.mp4",
            };
            for (var i = 0; i < 6; i++)
            {
                var dest = Path.Combine(tmp, $"scene{i:D2}-stock.mp4");
                File.Copy(Path.Combine(JobRonaldo, clips[i]), dest);
                script.Scenes.Add(new Scene
                {
                    Narration = $"Ronaldo scene {i}: the stadium lights shone over the pitch as Portugal played on.",
                    SceneKind = "context",
                    StockQuery = "football stadium lights",
                    ClipPath = dest,
                    ArtSource = "pexels-stock",
                });
            }
            var findings = QualityGate.CheckImagesBatch(tmp, script);
            Assert.Contains(findings, f => f.Check == "clip-near-duplicate"
                && f.Severity == QualitySeverity.Warning && f.Scene == "scene5");
            Assert.DoesNotContain(findings, f => f.Check == "clip-near-duplicate"
                && f.Severity == QualitySeverity.Error);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // L52 — a clip the near-duplicate check cannot mid-frame (here:
    // ffprobe cannot probe any duration from garbage bytes) must warn
    // with the scene and the reason, not silently exempt the clip.
    [Fact]
    public void CheckVisualNearDuplicates_MidFrameFailureWarns()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-midfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var dest = Path.Combine(tmp, "scene00-stock.mp4");
            File.WriteAllBytes(dest, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 });
            var script = new VideoScript
            {
                Title = "Cristiano Ronaldo Hangs Up Portugal Boots - A Legendary Farewell",
                Description = "Ronaldo retires from Portugal.",
                Tags = ["ronaldo", "portugal", "football", "news"],
                MusicMood = "tense atmospheric",
            };
            script.Scenes.Add(new Scene
            {
                Narration = "Ronaldo scene 0: the stadium lights shone over the pitch as Portugal played on.",
                SceneKind = "context",
                StockQuery = "football stadium lights",
                ClipPath = dest,
                ArtSource = "pexels-stock",
            });
            var findings = QualityGate.CheckVisualNearDuplicates(tmp, script);
            Assert.Contains(findings, f => f.Check == "near-duplicate-check-skipped"
                && f.Severity == QualitySeverity.Warning && f.Scene == "scene0"
                && f.Message.Contains("scene 0") && f.Message.Contains("duration"));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void HockeyClipSubjectMismatchWarns()
    {
        // The ORIGINAL defect: scene 4's Pexels clip is ice hockey, for
        // a football story. The slug is the only subject signal the
        // video API returns — with it recorded, the check warns.
        var tmp = Path.Combine(Path.GetTempPath(), "qg-subject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var dest = Path.Combine(tmp, "scene04-stock.mp4");
            File.Copy(Path.Combine(JobRonaldo, "scene04-stock.mp4.bad"), dest);
            var script = new VideoScript
            {
                Title = "Cristiano Ronaldo Hangs Up Portugal Boots - A Legendary Farewell",
                Description = "Ronaldo retires from Portugal.",
                Tags = ["ronaldo", "portugal", "football", "news"],
                MusicMood = "tense atmospheric",
            };
            script.Scenes.Add(new Scene
            {
                Narration = "He carried Portugal to glory for years, scoring goals that stunned the world.",
                SceneKind = "context",
                StockQuery = "cristiano ronaldo celebrating goal portugal national team",
                ClipPath = dest,
                ArtSource = "pexels-stock",
                PexelsUrl = "https://www.pexels.com/video/exciting-sweden-vs-norway-ice-hockey-match-highlights-32159115/",
            });
            var findings = QualityGate.CheckImagesBatch(tmp, script);
            Assert.Contains(findings, f => f.Check == "stock-subject-mismatch"
                && f.Severity == QualitySeverity.Warning && f.Scene == "scene0");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ImprovePromptNearDuplicateAddsVariation()
    {
        var (improved, _) = QualityGate.ImproveImagePrompt(
            "A photo of a statue of Cristiano Ronaldo in Funchal",
            "The statue of Ronaldo stands outside his museum in Funchal.",
            ["image-near-duplicate"]);
        Assert.Contains("different angle", improved, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // REPORT
    // ------------------------------------------------------------------

    [Fact]
    public void AppendReportRoundTrips()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var reportPath = Path.Combine(tmp, "quality-report.json");
            QualityGate.AppendReport(reportPath, "job-test", "script",
                [new QualityFinding("script", "hook", QualitySeverity.Error, "vague hook", "scene0")]);
            QualityGate.AppendReport(reportPath, "job-test", "images",
                [new QualityFinding("images", "image-sharpness", QualitySeverity.Warning, "blurry", "scene31")]);
            var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal("FAIL", doc.RootElement.GetProperty("Status").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("Findings").GetArrayLength());
            Assert.Equal(1, doc.RootElement.GetProperty("ErrorCount").GetInt32());
            Assert.StartsWith("quality[script]: FAIL",
                QualityGate.OneLineSummary("script",
                    [new QualityFinding("script", "x", QualitySeverity.Error, "m", null)]));
            Assert.StartsWith("quality[images]: WARN",
                QualityGate.OneLineSummary("images",
                    [new QualityFinding("images", "x", QualitySeverity.Warning, "m", null)]));
            Assert.StartsWith("quality[music]: PASS",
                QualityGate.OneLineSummary("music", []));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // L79 — a corrupt quality-report.json must not be rebuilt silently:
    // the warning names the path and the reason, and the report restarts
    // empty (the old findings are gone, and the log says so).
    [Fact]
    public void AppendReport_CorruptReportLogsWarning()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qg-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var oldOut = Console.Out;
        var writer = new StringWriter();
        try
        {
            var reportPath = Path.Combine(tmp, "quality-report.json");
            File.WriteAllText(reportPath, "{ this is not json");
            Console.SetOut(writer);
            var report = QualityGate.AppendReport(reportPath, "job-test", "script",
                [new QualityFinding("script", "hook", QualitySeverity.Error, "vague hook", "scene0")]);
            Console.SetOut(oldOut);
            var log = writer.ToString();
            Assert.Contains("WARNING: quality-report.json", log);
            Assert.Contains(reportPath, log);
            Assert.Contains("rebuilding empty", log);
            // The corrupt history is gone; only the new finding survives.
            Assert.Single(report.Findings);
            Assert.Equal("hook", report.Findings[0].Check);
            var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal(1, doc.RootElement.GetProperty("Findings").GetArrayLength());
        }
        finally
        {
            Console.SetOut(oldOut);
            Directory.Delete(tmp, recursive: true);
        }
    }
}
