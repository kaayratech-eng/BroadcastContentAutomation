// GiggleGarden Video Generator (.NET 8)
// Pipeline: Claude script -> Azure TTS -> Vidu image-to-video -> FFmpeg assembly.
// Outputs a 9:16 (1080x1920) short and a 16:9 (1920x1080) master into the
// publisher's watch folder, each with a sidecar .json carrying approved:false
// and the list of platforms it is destined for.
//
// Vidu generates asynchronously and its cheap off-peak tier only promises delivery
// within 48 hours, so the run is split into two phases:
//
//   --prep       write the script, synthesize narration, submit every clip, save state
//   --assemble   collect finished clips, render, write sidecars  (re-run until done)
//
// Both phases share state through script.json in the job folder, so --assemble can
// be run repeatedly - days later, across restarts - until every clip has landed.
//
// Usage:
//   dotnet run -- --prep --topic "counting ducks" --language en
//   dotnet run -- --prep --language hi              (Claude picks the topic)
//   dotnet run -- --assemble                        (newest job folder)
//   dotnet run -- --assemble --job job-20260809-1200
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

string? topic = null, language = "en", job = null, profileId = null;
var mode = "";
ContentFormat? formatOverride = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--prep": mode = "prep"; break;
        case "--assemble": mode = "assemble"; break;
        case "--retts": mode = "retts"; break;
        case "--test-tts": mode = "test-tts"; break;
        case "--dry-script": mode = "dry-script"; break;
        case "--job" when i + 1 < args.Length: job = args[++i]; break;
        case "--topic" when i + 1 < args.Length: topic = args[++i]; break;
        case "--language" when i + 1 < args.Length: language = args[++i]; break;
        case "--profile" when i + 1 < args.Length: profileId = args[++i]; break;
        case "--format" when i + 1 < args.Length:
            if (!Enum.TryParse<ContentFormat>(args[++i], ignoreCase: true, out var parsedFormat))
            {
                Console.Error.WriteLine($"Unknown --format '{args[i]}'. Use one of: {string.Join(", ", Enum.GetNames<ContentFormat>())}.");
                return 1;
            }
            formatOverride = parsedFormat;
            break;
        case "--help" or "-h":
            Console.WriteLine("Usage: VideoGen --prep [--topic \"...\"] [--language en|hi|pa] [--profile gigglegarden] [--format Educational|Rhyme|Poem|Bedtime|SingAlong|CountingSong]");
            Console.WriteLine("       VideoGen --assemble [--job job-YYYYMMDD-HHMMSS]");
            Console.WriteLine("       VideoGen --retts    [--job job-YYYYMMDD-HHMMSS]   re-voice an existing job");
            Console.WriteLine("       VideoGen --test-tts [--language en|hi|pa]   synthesize a sample line on every reachable provider, no Vidu/Claude spend");
            Console.WriteLine("       VideoGen --dry-script [--topic \"...\"] [--language en|hi|pa] [--format ...]   one script-generation call only, no TTS/character-draw/Vidu spend");
            return 0;
    }
}

if (mode.Length == 0)
{
    Console.Error.WriteLine("Specify --prep or --assemble. See --help.");
    return 1;
}

if (language is not ("en" or "hi" or "pa"))
{
    Console.Error.WriteLine($"Unsupported --language '{language}'. Use en, hi, or pa.");
    return 1;
}

ContentProfile profile;
try
{
    profile = ContentProfileRegistry.Get(profileId ?? cfg.Profile);
    cfg.Validate();
    profile.Validate();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

var jsonOptions = Sidecar.Options;

try
{
    return mode switch
    {
        "prep" => await RunPrepAsync(),
        "retts" => await RunRettsAsync(),
        "test-tts" => await RunTestTtsAsync(),
        "dry-script" => await RunDryScriptAsync(),
        _ => await RunAssembleAsync(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------- prep ------

async Task<int> RunPrepAsync()
{
    var workDir = Directory.CreateDirectory(
        Path.Combine(cfg.WorkDirectory, $"job-{DateTime.Now:yyyyMMdd-HHmmss}")).FullName;
    Console.WriteLine($"Workspace: {workDir}");

    // 1. Character, then script. A pooled character is resolved first so the script can
    //    be written about the picture that already exists; an invented one is drawn
    //    afterwards, from the description the script came up with. Either order ends
    //    with the words and the artwork describing the same creature, which is the
    //    whole point - they used to disagree.
    var (pooled, addToPool) = profile.UsesCharacterMascot
        ? CharacterSource.Resolve(profile, cfg, workDir)
        : (null, false);
    var recentNames = profile.UsesCharacterMascot && pooled is null
        ? CharacterSource.RecentNames(cfg, workDir) : [];

    var trendContext = topic is null ? await TrendResearch.FetchAsync(profile, cfg) : null;
    var scriptProvider = ScriptProviderFactory.Create(cfg);
    var script = await ScriptGenerator.GenerateAsync(profile, topic, language!, scriptProvider, trendContext, pooled, recentNames, formatOverride);

    var characterImage = pooled?.ImagePath ?? await CharacterSource.DrawAsync(profile, cfg, script, workDir);
    script.CharacterImagePath = characterImage;
    if (addToPool) CharacterSource.SaveToPool(profile, script, characterImage);

    Console.WriteLine($"Script: \"{script.Title}\" — {script.Format} — {script.Scenes.Count} scenes, starring {script.CharacterName}");

    // 2. The branded intro bumper is scene zero: the same channel greeting every video,
    //    this video's character introducing itself, and a one-line preview of what it
    //    teaches. The greeting is the constant now that the cast is not. This clip is also
    //    what the YouTube thumbnail is cropped from (see ExtractThumbnailFrameAsync below),
    //    so its wording/motion is what actually delivers a format-appropriate thumbnail -
    //    calm formats get a gentler wave and a soft-pastel-but-colourful setting instead of
    //    the energetic bounce every format used to get.
    var calmIntro = script.Format is ContentFormat.Poem or ContentFormat.Bedtime;
    var intro = new Scene
    {
        Narration = script.IntroText,
        ImagePrompt = calmIntro
            ? $"{script.CharacterName} greeting the viewer softly, colourful soft pastel background, warm cosy palette."
            : $"{script.CharacterName} greeting the viewer, colourful bright background, cheerful palette.",
        MotionPrompt = calmIntro
            ? $"{script.CharacterName} ({script.CharacterDescription}) waves hello softly to the viewer and sways " +
              "gently on the spot, eyes soft and calm. Colourful soft pastel background, warm cosy palette. " +
              "2D cartoon animation, flat colors, thick outlines, character design stays consistent."
            : $"{script.CharacterName} ({script.CharacterDescription}) waves hello to the viewer and bounces " +
              "happily on the spot, eyes bright and smiling. Colourful bright background, cheerful palette. " +
              "2D cartoon animation, flat colors, thick outlines, character design stays consistent.",
    };
    script.Scenes.Insert(0, intro);

    // 3. Narration first: every clip is generated to the length of the line it has to
    //    carry, so the audio has to exist before anything is submitted.
    var tts = TtsProviderFactory.Create(cfg, language!);
    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];
        var audioPath = Path.Combine(workDir, $"scene{i:D2}.mp3");
        await tts.SynthesizeAsync(scene.Narration, language!, audioPath, script.Format);
        scene.AudioPath = audioPath;
        scene.DurationSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, audioPath);
        Console.WriteLine($"TTS {i + 1}/{script.Scenes.Count} ({scene.DurationSeconds:0.0}s)");
    }

    var total = script.Scenes.Sum(s => s.DurationSeconds + 0.5);
    Console.WriteLine($"Narration total: {total:0}s");

    // 4. Submit every clip. All scenes seed from this video's one character frame rather
    //    than chaining each clip off the previous one's last frame: chaining is serial,
    //    which cannot work against an off-peak queue that may take 48 hours per clip,
    //    and it compounds drift over a dozen generations. Seeding every scene from a
    //    single frame is fully parallel, has no drift at all, and still gives one
    //    consistent character for the length of the video.
    IVideoProvider vidu = new ViduClient(cfg);
    var credits = 0;

    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];
        scene.StartFramePath = characterImage;

        var submission = await vidu.SubmitAsync(characterImage, scene.MotionPrompt, scene.DurationSeconds);
        scene.ViduTaskId = submission.TaskId;
        credits += submission.Credits;

        Console.WriteLine($"Submitted {i + 1}/{script.Scenes.Count} — task {submission.TaskId} ({submission.Credits} credits)");
    }

    script.Language = language!;
    script.Profile = profile.Id;
    await SaveScriptAsync(workDir, script);

    Console.WriteLine();
    Console.WriteLine($"Submitted {script.Scenes.Count} clips, {credits} credits (~${credits * 0.005:0.00}).");
    Console.WriteLine(cfg.ViduOffPeak
        ? "Off-peak: Vidu delivers within 48 hours. Run --assemble later to collect and render."
        : "Peak rate: clips usually land within minutes. Run --assemble to collect and render.");
    Console.WriteLine($"  dotnet run -- --assemble --job {Path.GetFileName(workDir)}");
    return 0;
}

// --------------------------------------------------------------- retts ------

// Re-synthesizes narration for an existing job and re-measures every scene, without
// touching the clips already paid for. Use after a change to how narration is spoken,
// or after editing script.json by hand - the generated clips stay valid because they
// are only ever trimmed or frame-padded to whatever the new durations turn out to be.
async Task<int> RunRettsAsync()
{
    var workDir = ResolveJobDirectory();
    Console.WriteLine($"Workspace: {workDir}");

    var script = await LoadScriptAsync(workDir);
    language = string.IsNullOrWhiteSpace(script.Language) ? language : script.Language;
    profile = string.IsNullOrWhiteSpace(script.Profile) ? profile : ContentProfileRegistry.Get(script.Profile);

    var tts = TtsProviderFactory.Create(cfg, language!);
    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];
        var audioPath = Path.Combine(workDir, $"scene{i:D2}.mp3");
        await tts.SynthesizeAsync(scene.Narration, language!, audioPath, script.Format);
        scene.AudioPath = audioPath;

        var before = scene.DurationSeconds;
        scene.DurationSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, audioPath);
        Console.WriteLine($"TTS {i + 1}/{script.Scenes.Count} ({before:0.0}s -> {scene.DurationSeconds:0.0}s)");
    }

    await SaveScriptAsync(workDir, script);

    var total = script.Scenes.Sum(s => s.DurationSeconds + 0.5);
    Console.WriteLine($"Narration total: {total:0}s. Run --assemble to re-render.");
    return 0;
}

// ------------------------------------------------------------ test-tts ------

// Synthesizes one fixed sample line on every TTS provider reachable for --language,
// so Google's free output can be listened to side by side with Azure's before
// deciding TtsProvider's default. Deliberately skips Claude/Vidu entirely - no
// script generation, no clip submission, zero risk to Vidu spend.
async Task<int> RunTestTtsAsync()
{
    var samples = new Dictionary<string, string>
    {
        ["en"] = "Hello little duckling! Let's count to five together: one, two, three, four, five!",
        ["hi"] = "नमस्ते छोटी बत्तख! चलो साथ में एक से पांच तक गिनती सीखते हैं।",
        ["pa"] = "ਸਤ ਸ੍ਰੀ ਅਕਾਲ ਛੋਟੀ ਬੱਤਖ! ਆਓ ਇਕੱਠੇ ਇੱਕ ਤੋਂ ਪੰਜ ਤੱਕ ਗਿਣਤੀ ਸਿੱਖੀਏ।",
    };
    var text = samples[language!];

    var outDir = Directory.CreateDirectory(Path.Combine(cfg.WorkDirectory, "tts-test")).FullName;
    Console.WriteLine($"Comparing TTS providers for \"{language}\" -> {outDir}");

    var azurePath = Path.Combine(outDir, $"azure-{language}.mp3");
    await new AzureTtsProvider(cfg).SynthesizeAsync(text, language!, azurePath, ContentFormat.Educational);
    Console.WriteLine($"  Azure  -> {azurePath}");

    try
    {
        var googlePath = Path.Combine(outDir, $"google-{language}.mp3");
        await new GoogleTtsProvider(cfg).SynthesizeAsync(text, language!, googlePath, ContentFormat.Educational);
        Console.WriteLine($"  Google -> {googlePath}");
    }
    catch (NotSupportedException ex)
    {
        Console.WriteLine($"  Google -> skipped ({ex.Message})");
    }

    return 0;
}

// ---------------------------------------------------------- dry-script ------

// One ScriptGenerator.GenerateAsync call (one Claude call) and nothing else - no
// character draw, no TTS, no Vidu submission. For checking a specific --format
// (or the weighted random pick) produces the right shape before spending anything
// on the rest of the pipeline. Writes the script next to tts-test's sibling folder
// so it never collides with a real job- folder --assemble would look for.
async Task<int> RunDryScriptAsync()
{
    var trendContext = topic is null ? await TrendResearch.FetchAsync(profile, cfg) : null;
    var scriptProvider = ScriptProviderFactory.Create(cfg);
    var script = await ScriptGenerator.GenerateAsync(profile, topic, language!, scriptProvider, trendContext, null, null, formatOverride);

    Console.WriteLine($"Format: {script.Format}   Title: \"{script.Title}\"");
    Console.WriteLine($"Character: {script.CharacterName} - {script.CharacterDescription}");
    Console.WriteLine($"MusicMood: {script.MusicMood}   NarrationStyle: {script.NarrationStyle}");
    Console.WriteLine($"Intro: {script.IntroText}");
    for (var i = 0; i < script.Scenes.Count; i++)
        Console.WriteLine($"  Scene {i + 1} ({script.Scenes[i].Narration.Length} chars): {script.Scenes[i].Narration}");
    Console.WriteLine($"Hashtags: {string.Join(" ", script.Tags.Where(t => t.StartsWith('#')))}");

    var outDir = Directory.CreateDirectory(Path.Combine(cfg.WorkDirectory, "dry-script")).FullName;
    var outPath = Path.Combine(outDir, $"{script.Format}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(outPath, System.Text.Json.JsonSerializer.Serialize(script, jsonOptions));
    Console.WriteLine($"Saved -> {outPath}");

    return 0;
}

// ------------------------------------------------------------ assemble ------

async Task<int> RunAssembleAsync()
{
    var workDir = ResolveJobDirectory();
    Console.WriteLine($"Workspace: {workDir}");

    var script = await LoadScriptAsync(workDir);
    language = string.IsNullOrWhiteSpace(script.Language) ? language : script.Language;
    profile = string.IsNullOrWhiteSpace(script.Profile) ? profile : ContentProfileRegistry.Get(script.Profile);

    IVideoProvider vidu = new ViduClient(cfg);
    var pending = 0;

    // 5. Collect. Each scene is independent, so a clip that is still queued only holds
    //    up this run - already-downloaded clips are recorded in script.json and skipped
    //    next time, which makes repeated --assemble runs cheap and resumable.
    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];
        var label = $"{i + 1}/{script.Scenes.Count}";

        if (scene.ClipPath is { } existing && File.Exists(existing)) continue;

        if (string.IsNullOrEmpty(scene.ViduTaskId))
            throw new InvalidOperationException($"Scene {label} was never submitted. Re-run --prep.");

        var result = await vidu.PollAsync(scene.ViduTaskId);

        if (result.Succeeded)
        {
            var clipPath = Path.Combine(workDir, $"clip{i:D2}-source.mp4");
            await vidu.DownloadAsync(result.Url!, clipPath);
            scene.ClipPath = clipPath;
            Console.WriteLine($"  {label} downloaded");
        }
        else if (result.IsTerminal)
        {
            // Vidu auto-cancels off-peak work it could not place inside the window and
            // refunds the credits, so the fix is to resubmit this one scene, not the job.
            Console.WriteLine($"  {label} {result.State} — resubmitting ({result.Error ?? "no reason given"})");

            // Has to be this job's own character frame, not a channel-wide one: a scene
            // resubmitted from anything else comes back with a different creature in it.
            var startFrame = scene.StartFramePath ?? script.CharacterImagePath
                ?? throw new InvalidOperationException(
                    $"Scene {label} needs resubmitting but the job records no character image. Re-run --prep.");

            var resubmit = await vidu.SubmitAsync(startFrame, scene.MotionPrompt, scene.DurationSeconds);
            scene.ViduTaskId = resubmit.TaskId;
            pending++;
        }
        else
        {
            Console.WriteLine($"  {label} {result.State}");
            pending++;
        }
    }

    await SaveScriptAsync(workDir, script);

    if (pending > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"{pending} clip(s) still generating. Re-run --assemble to continue:");
        Console.WriteLine($"  dotnet run -- --assemble --job {Path.GetFileName(workDir)}");
        return 0;
    }

    // 6. Render
    var slug = Text.Slug(script.Title);
    Directory.CreateDirectory(cfg.OutputDirectory);
    var rendered = new List<string>();

    // Reels reject anything over 90s, so the vertical cut is trimmed to whole scenes
    // that fit the budget rather than shipping a render the platform will refuse.
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
    await VideoAssembler.AssembleFromClipsAsync(profile, cfg, shortScenes, language!, workDir, vertical, 1080, 1920, script.MusicMood);
    await VideoAssembler.WriteSrtAsync(shortScenes, Path.ChangeExtension(vertical, ".srt"));
    Console.WriteLine($"Rendered: {vertical}");

    await new Sidecar
    {
        Title = Text.Fit($"{script.Title} #Shorts", 100),
        Description = $"{script.ReelsCaption}\n\n#Shorts",
        Tags = [.. script.Tags.Take(12), "shorts"],
        Approved = false,
        Language = language!,
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

    // The 16:9 master reuses the same clips on a blurred blow-up of themselves rather
    // than generating a second orientation, which would double the per-video spend.
    var landscape = Path.Combine(cfg.OutputDirectory, $"{slug}-{language}.mp4");
    await VideoAssembler.AssembleFromClipsAsync(profile, cfg, script.Scenes, language!, workDir, landscape, 1920, 1080, script.MusicMood);
    await VideoAssembler.WriteSrtAsync(script.Scenes, Path.ChangeExtension(landscape, ".srt"));
    Console.WriteLine($"Rendered: {landscape}");

    // Thumbnail from a frame of the finished master - the art is already on-model,
    // and a still pulled from the render costs nothing to produce.
    var thumbFrame = Path.Combine(workDir, "thumbnail-frame.jpg");
    var grabAt = Math.Min(3.0, Math.Max(0.5, script.Scenes[0].DurationSeconds * 0.6));
    await VideoAssembler.ExtractThumbnailFrameAsync(cfg, landscape, grabAt, thumbFrame);
    await VideoAssembler.BuildThumbnailAsync(cfg, thumbFrame, Path.ChangeExtension(landscape, ".thumb.jpg"));
    Console.WriteLine("Thumbnail generated.");

    await new Sidecar
    {
        Title = script.Title,
        Description = script.Description,
        Tags = script.Tags,
        Approved = false,
        Language = language!,
        Aspect = "landscape",
        Targets = [.. cfg.LandscapeTargets],
    }.SaveAsync(landscape);
    rendered.Add(landscape);

    Console.WriteLine();
    Console.WriteLine("Done. Review each video, set \"approved\": true in its sidecar JSON, then the publisher takes over:");
    foreach (var path in rendered) Console.WriteLine($"  {Sidecar.PathFor(path)}");
    return 0;
}

// ----------------------------------------------------------- plumbing ------

string ResolveJobDirectory()
{
    if (job is not null)
    {
        var explicitPath = Path.IsPathRooted(job) ? job : Path.Combine(cfg.WorkDirectory, job);
        if (!Directory.Exists(explicitPath))
            throw new DirectoryNotFoundException($"No such job folder: {explicitPath}");
        return explicitPath;
    }

    // Default to the newest job that still has work outstanding, so the common case
    // (prep today, assemble tomorrow) needs no arguments.
    var newest = Directory.Exists(cfg.WorkDirectory)
        ? Directory.GetDirectories(cfg.WorkDirectory, "job-*")
            .Where(d => File.Exists(Path.Combine(d, "script.json")))
            .OrderByDescending(d => d)
            .FirstOrDefault()
        : null;

    return newest ?? throw new InvalidOperationException(
        $"No job folders with a script.json under {cfg.WorkDirectory}. Run --prep first.");
}

async Task SaveScriptAsync(string workDir, VideoScript script)
{
    var path = Path.Combine(workDir, "script.json");
    var temp = path + ".tmp";
    await File.WriteAllTextAsync(temp, System.Text.Json.JsonSerializer.Serialize(script, jsonOptions));
    File.Move(temp, path, overwrite: true);
}

async Task<VideoScript> LoadScriptAsync(string workDir)
{
    var path = Path.Combine(workDir, "script.json");
    if (!File.Exists(path))
        throw new FileNotFoundException($"No script.json in {workDir}. Run --prep first.", path);

    return System.Text.Json.JsonSerializer.Deserialize<VideoScript>(
        await File.ReadAllTextAsync(path), jsonOptions)
        ?? throw new InvalidDataException($"{path} is not valid script JSON.");
}
