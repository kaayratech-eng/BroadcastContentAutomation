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
var script = await ScriptGenerator.GenerateAsync(cfg, topic, language);
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
for (var i = 0; i < script.Scenes.Count; i++)
{
    var scene = script.Scenes[i];

    var imgPath = Path.Combine(workDir, $"scene{i:D2}.png");
    await img.GenerateAsync(scene.ImagePrompt, cfg.CharacterStyle, imgPath, ImageClient.Orientation.Landscape);
    scene.ImagePath = imgPath;

    if (cfg.GenerateVerticalImages && !landscapeOnly)
    {
        var verticalPath = Path.Combine(workDir, $"scene{i:D2}-v.png");
        await img.GenerateAsync(scene.ImagePrompt, cfg.CharacterStyle, verticalPath, ImageClient.Orientation.Portrait);
        scene.VerticalImagePath = verticalPath;
    }

    Console.WriteLine($"Image scene {i + 1}/{script.Scenes.Count}");
}

// 4. Assemble
var slug = Text.Slug(script.Title);
Directory.CreateDirectory(cfg.OutputDirectory);
var rendered = new List<string>();

if (!verticalOnly)
{
    var landscape = Path.Combine(cfg.OutputDirectory, $"{slug}-{language}.mp4");
    await VideoAssembler.AssembleAsync(cfg, script.Scenes, language, workDir, landscape, 1920, 1080, preferVerticalImages: false);
    Console.WriteLine($"Rendered: {landscape}");

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
    var shortScenes = new List<Scene>();
    var running = 0.0;
    foreach (var scene in script.Scenes)
    {
        var next = running + scene.DurationSeconds + 0.5;
        if (shortScenes.Count > 0 && next > cfg.VerticalMaxSeconds) break;
        shortScenes.Add(scene);
        running = next;
    }

    if (shortScenes.Count < script.Scenes.Count)
        Console.WriteLine($"Short: trimmed to {shortScenes.Count}/{script.Scenes.Count} scenes ({running:0}s) for the {cfg.VerticalMaxSeconds}s cap.");

    var vertical = Path.Combine(cfg.OutputDirectory, $"{slug}-{language}-short.mp4");
    await VideoAssembler.AssembleAsync(cfg, shortScenes, language, workDir, vertical, 1080, 1920, preferVerticalImages: true);
    Console.WriteLine($"Rendered: {vertical}");

    // Distinct metadata from the landscape master. Identical title + description on two
    // uploads of the same content is what gets a channel flagged for duplicate uploads.
    await new Sidecar
    {
        Title = Text.Fit($"{script.Title} #Shorts", 100),
        Description = $"{script.ShortCaption}\n\n#Shorts",
        Tags = [.. script.Tags.Take(12), "shorts"],
        Approved = false,
        Language = language,
        Aspect = "vertical",
        Targets = [.. cfg.VerticalTargets],
        CaptionOverrides =
        {
            // YouTube keeps the #Shorts marker; the others must not carry it.
            [Platforms.Instagram] = script.ShortCaption,
            [Platforms.Facebook] = script.ShortCaption,
            [Platforms.TikTok] = script.ShortCaption,
        },
    }.SaveAsync(vertical);

    rendered.Add(vertical);
}

Console.WriteLine();
Console.WriteLine("Done. Review each video, set \"approved\": true in its sidecar JSON, then the publisher takes over:");
foreach (var path in rendered) Console.WriteLine($"  {Sidecar.PathFor(path)}");

return 0;
