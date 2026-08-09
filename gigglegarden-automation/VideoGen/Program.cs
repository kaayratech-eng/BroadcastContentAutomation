// GiggleGarden Video Generator (.NET 8)
// Pipeline: Claude script -> Azure TTS -> image gen -> FFmpeg assembly.
// Outputs a 16:9 (1920x1080) master and a 9:16 (1080x1920) short into the
// publisher's watch folder, each with a sidecar .json carrying approved:false
// and the list of platforms it is destined for.
//
// Usage:
//   dotnet run -- --topic "counting ducks" --language en
//   dotnet run -- --language hi                  (Claude picks a topic)
//   dotnet run -- --topic "colors" --language pa --landscape-only
//
// Requires ffmpeg + ffprobe on PATH or at cfg.FfmpegPath / cfg.FfprobePath.

using GiggleGarden.Shared;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables("GIGGLE_")
    .Build();

var cfg = config.Get<GenConfig>() ?? throw new InvalidOperationException("appsettings.json invalid");

string? topic = null, language = "en";
bool landscapeOnly = false, verticalOnly = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--topic" when i + 1 < args.Length: topic = args[++i]; break;
        case "--language" when i + 1 < args.Length: language = args[++i]; break;
        case "--landscape-only": landscapeOnly = true; break;
        case "--vertical-only": verticalOnly = true; break;
        case "--help" or "-h":
            Console.WriteLine("Usage: VideoGen [--topic \"...\"] [--language en|hi|pa] [--landscape-only|--vertical-only]");
            return 0;
    }
}

if (language is not ("en" or "hi" or "pa"))
{
    Console.Error.WriteLine($"Unsupported --language '{language}'. Use en, hi, or pa.");
    return 1;
}

try
{
    cfg.Validate();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

var workDir = Directory.CreateDirectory(
    Path.Combine(cfg.WorkDirectory, $"job-{DateTime.Now:yyyyMMdd-HHmmss}")).FullName;
Console.WriteLine($"Workspace: {workDir}");

// 1. Script
var trendContext = topic is null ? await TrendResearch.FetchAsync(cfg) : null;
var script = await ScriptGenerator.GenerateAsync(cfg, topic, language, trendContext);
await File.WriteAllTextAsync(Path.Combine(workDir, "script.json"),
    System.Text.Json.JsonSerializer.Serialize(script, Sidecar.Options));
Console.WriteLine($"Script: \"{script.Title}\" — {script.Scenes.Count} scenes");

// 2. TTS per scene (duration is measured once here and reused by both renders)
var tts = new TtsClient(cfg);
for (var i = 0; i < script.Scenes.Count; i++)
{
    var audioPath = Path.Combine(workDir, $"scene{i:D2}.mp3");
    await tts.SynthesizeAsync(script.Scenes[i].Narration, language, audioPath);
    script.Scenes[i].AudioPath = audioPath;
    script.Scenes[i].DurationSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, audioPath);
    Console.WriteLine($"TTS scene {i + 1}/{script.Scenes.Count} ({script.Scenes[i].DurationSeconds:0.0}s)");
}

// 3. Images per scene
var img = new ImageClient(cfg);

// Generate one neutral reference image per orientation first, then feed it back into
// every scene via image-to-image - keeps the character's appearance consistent
// instead of drifting scene to scene (see GenConfig.UseCharacterReference).
const string referencePose =
    "Full-body reference pose, simple neutral standing pose, friendly smile, plain " +
    "soft-colored background, character reference sheet style.";

string? landscapeRef = null, verticalRef = null;
if (cfg.UseCharacterReference)
{
    landscapeRef = Path.Combine(workDir, "character-ref.png");
    await img.GenerateAsync(referencePose, cfg.CharacterStyle, landscapeRef, ImageClient.Orientation.Landscape);
    Console.WriteLine("Character reference generated (landscape).");

    if (cfg.GenerateVerticalImages && !landscapeOnly)
    {
        verticalRef = Path.Combine(workDir, "character-ref-v.png");
        await img.GenerateAsync(referencePose, cfg.CharacterStyle, verticalRef, ImageClient.Orientation.Portrait);
        Console.WriteLine("Character reference generated (portrait).");
    }
}

// Branded intro bumper, prepended to both renders below. Reuses the character
// reference image directly (same plain pose every video) rather than generating
// scene-specific art, so the opening is deliberately identical/recognizable video
// to video instead of varying like the content scenes do. Falls back to a fresh
// generation only if character-reference images are turned off in config.
var introImagePath = landscapeRef ?? Path.Combine(workDir, "intro.png");
if (landscapeRef is null)
    await img.GenerateAsync(referencePose, cfg.CharacterStyle, introImagePath, ImageClient.Orientation.Landscape);

string? introVerticalImagePath = null;
if (cfg.GenerateVerticalImages && !landscapeOnly)
{
    introVerticalImagePath = verticalRef ?? Path.Combine(workDir, "intro-v.png");
    if (verticalRef is null)
        await img.GenerateAsync(referencePose, cfg.CharacterStyle, introVerticalImagePath, ImageClient.Orientation.Portrait);
}

var introScene = new Scene
{
    Narration = script.IntroText,
    ImagePath = introImagePath,
    VerticalImagePath = introVerticalImagePath,
};
var introAudioPath = Path.Combine(workDir, "intro.mp3");
await tts.SynthesizeAsync(introScene.Narration, language, introAudioPath);
introScene.AudioPath = introAudioPath;
introScene.DurationSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, introAudioPath);
Console.WriteLine($"Intro: \"{introScene.Narration}\" ({introScene.DurationSeconds:0.0}s)");

for (var i = 0; i < script.Scenes.Count; i++)
{
    var scene = script.Scenes[i];

    var imgPath = Path.Combine(workDir, $"scene{i:D2}.png");
    if (landscapeRef is not null)
        await img.GenerateWithReferenceAsync(scene.ImagePrompt, cfg.CharacterStyle, landscapeRef, imgPath, ImageClient.Orientation.Landscape);
    else
        await img.GenerateAsync(scene.ImagePrompt, cfg.CharacterStyle, imgPath, ImageClient.Orientation.Landscape);
    scene.ImagePath = imgPath;

    if (cfg.GenerateVerticalImages && !landscapeOnly)
    {
        var verticalPath = Path.Combine(workDir, $"scene{i:D2}-v.png");
        if (verticalRef is not null)
            await img.GenerateWithReferenceAsync(scene.ImagePrompt, cfg.CharacterStyle, verticalRef, verticalPath, ImageClient.Orientation.Portrait);
        else
            await img.GenerateAsync(scene.ImagePrompt, cfg.CharacterStyle, verticalPath, ImageClient.Orientation.Portrait);
        scene.VerticalImagePath = verticalPath;
    }

    // Long-narration scenes get a second image so the video isn't one static photo
    // sitting on screen for the whole line — VideoAssembler crossfades between the two
    // partway through. Same reference image, a nudged prompt so it isn't a near-duplicate.
    if (scene.DurationSeconds > cfg.SplitLongSceneAfterSeconds)
    {
        var variantPrompt = $"{scene.ImagePrompt}, a different pose or moment within the same scene";

        var imgPath2 = Path.Combine(workDir, $"scene{i:D2}b.png");
        if (landscapeRef is not null)
            await img.GenerateWithReferenceAsync(variantPrompt, cfg.CharacterStyle, landscapeRef, imgPath2, ImageClient.Orientation.Landscape);
        else
            await img.GenerateAsync(variantPrompt, cfg.CharacterStyle, imgPath2, ImageClient.Orientation.Landscape);
        scene.ImagePath2 = imgPath2;

        if (cfg.GenerateVerticalImages && !landscapeOnly)
        {
            var verticalPath2 = Path.Combine(workDir, $"scene{i:D2}b-v.png");
            if (verticalRef is not null)
                await img.GenerateWithReferenceAsync(variantPrompt, cfg.CharacterStyle, verticalRef, verticalPath2, ImageClient.Orientation.Portrait);
            else
                await img.GenerateAsync(variantPrompt, cfg.CharacterStyle, verticalPath2, ImageClient.Orientation.Portrait);
            scene.VerticalImagePath2 = verticalPath2;
        }
    }

    Console.WriteLine($"Image scene {i + 1}/{script.Scenes.Count}");
}

// 4. Assemble
var slug = Text.Slug(script.Title);
Directory.CreateDirectory(cfg.OutputDirectory);
var rendered = new List<string>();

List<Scene> landscapeScenes = [introScene, .. script.Scenes];

if (!verticalOnly)
{
    var landscape = Path.Combine(cfg.OutputDirectory, $"{slug}-{language}.mp4");
    await VideoAssembler.AssembleAsync(cfg, landscapeScenes, language, workDir, landscape, 1920, 1080, preferVerticalImages: false);
    await VideoAssembler.WriteSrtAsync(landscapeScenes, Path.ChangeExtension(landscape, ".srt"));
    Console.WriteLine($"Rendered: {landscape}");

    var thumbnailBase = Path.Combine(workDir, "thumbnail-base.png");
    if (landscapeRef is not null)
        await img.GenerateWithReferenceAsync(script.ThumbnailPrompt, cfg.CharacterStyle, landscapeRef, thumbnailBase, ImageClient.Orientation.Landscape);
    else
        await img.GenerateAsync(script.ThumbnailPrompt, cfg.CharacterStyle, thumbnailBase, ImageClient.Orientation.Landscape);
    await VideoAssembler.BuildThumbnailAsync(cfg, thumbnailBase, script.ThumbnailText, language, Path.ChangeExtension(landscape, ".thumb.jpg"));
    Console.WriteLine("Thumbnail generated.");

    await new Sidecar
    {
        Title = script.Title,
        Description = script.Description,
        Tags = script.Tags,
        Approved = false,
        Language = language,
        Aspect = "landscape",
        Targets = [.. cfg.LandscapeTargets],
    }.SaveAsync(landscape);

    rendered.Add(landscape);
}

if (!landscapeOnly)
{
    // Reels reject anything over 90s, so the short cut is trimmed to whole scenes that
    // fit the budget rather than shipping a render the platform will refuse outright.
    // The intro bumper eats into that same budget rather than sitting on top of it,
    // so intro + content together still land safely under the platform cap.
    var introBudget = introScene.DurationSeconds + 0.5;
    var contentBudget = cfg.VerticalMaxSeconds - introBudget;

    var shortScenes = new List<Scene>();
    var running = 0.0;
    foreach (var scene in script.Scenes)
    {
        var next = running + scene.DurationSeconds + 0.5;
        if (shortScenes.Count > 0 && next > contentBudget) break;
        shortScenes.Add(scene);
        running = next;
    }

    if (shortScenes.Count < script.Scenes.Count)
        Console.WriteLine($"Short: trimmed to {shortScenes.Count}/{script.Scenes.Count} scenes ({running:0}s content + {introBudget:0.0}s intro) for the {cfg.VerticalMaxSeconds}s cap.");

    List<Scene> verticalScenes = [introScene, .. shortScenes];

    var vertical = Path.Combine(cfg.OutputDirectory, $"{slug}-{language}-short.mp4");
    await VideoAssembler.AssembleAsync(cfg, verticalScenes, language, workDir, vertical, 1080, 1920, preferVerticalImages: true);
    await VideoAssembler.WriteSrtAsync(verticalScenes, Path.ChangeExtension(vertical, ".srt"));
    Console.WriteLine($"Rendered: {vertical}");

    // Distinct metadata from the landscape master. Identical title + description on two
    // uploads of the same content is what gets a channel flagged for duplicate uploads.
    // Instagram/Facebook and TikTok also get their own captions rather than sharing one -
    // each platform's feed truncates and formats differently, and reusing one caption
    // verbatim across all three reads as generic on every one of them.
    await new Sidecar
    {
        Title = Text.Fit($"{script.Title} #Shorts", 100),
        Description = $"{script.ReelsCaption}\n\n#Shorts",
        Tags = [.. script.Tags.Take(12), "shorts"],
        Approved = false,
        Language = language,
        Aspect = "vertical",
        Targets = [.. cfg.VerticalTargets],
        CaptionOverrides =
        {
            // YouTube keeps the #Shorts marker; the others must not carry it.
            [Platforms.Instagram] = script.ReelsCaption,
            [Platforms.Facebook] = script.ReelsCaption,
            [Platforms.TikTok] = script.TikTokCaption,
        },
    }.SaveAsync(vertical);

    rendered.Add(vertical);
}

Console.WriteLine();
Console.WriteLine("Done. Review each video, set \"approved\": true in its sidecar JSON, then the publisher takes over:");
foreach (var path in rendered) Console.WriteLine($"  {Sidecar.PathFor(path)}");

return 0;
