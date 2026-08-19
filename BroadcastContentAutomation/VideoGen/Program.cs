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

string? topic = null, language = "en", job = null, profileId = null, scriptFile = null;
var mode = "";
string? formatOverride = null;
var longForm = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--prep": mode = "prep"; break;
        case "--assemble": mode = "assemble"; break;
        case "--resume": mode = "resume"; break;
        case "--manual": mode = "manual"; break;
        case "--status": mode = "status"; break;
        case "--retts": mode = "retts"; break;
        case "--test-tts": mode = "test-tts"; break;
        case "--dry-script": mode = "dry-script"; break;
        case "--long": longForm = true; break;
        case "--job" when i + 1 < args.Length: job = args[++i]; break;
        case "--topic" when i + 1 < args.Length: topic = args[++i]; break;
        case "--language" when i + 1 < args.Length: language = args[++i]; break;
        case "--profile" when i + 1 < args.Length: profileId = args[++i]; break;
        case "--script-file" when i + 1 < args.Length: scriptFile = args[++i]; break;
        case "--format" when i + 1 < args.Length:
            formatOverride = args[++i];
            break;
        case "--help" or "-h":
            Console.WriteLine("Usage: VideoGen --prep [--topic \"...\"] [--language en|hi|pa] [--profile gigglegarden] [--format <id>] [--long]   (valid --format ids depend on --profile; see that profile's Formats catalog. --long generates a 12-15 minute YouTube documentary cut instead of the usual ~8-scene short; only supported by profiles with AllowsContextScenes.)");
            Console.WriteLine("       VideoGen --assemble [--job job-YYYYMMDD-HHMMSS]");
            Console.WriteLine("       VideoGen --resume   [--job job-YYYYMMDD-HHMMSS]   continue an interrupted --prep, skipping any scene whose audio/art is already on disk");
            Console.WriteLine("       VideoGen --manual --script-file <path> --format <id> [--language en|hi|pa] [--profile ...]   start a new job from a hand-written script.json-shaped file instead of calling the AI script provider");
            Console.WriteLine("       VideoGen --retts    [--job job-YYYYMMDD-HHMMSS]   re-voice an existing job");
            Console.WriteLine("       VideoGen --test-tts [--language en|hi|pa]   synthesize a sample line on every reachable provider, no Vidu/Claude spend");
            Console.WriteLine("       VideoGen --dry-script [--topic \"...\"] [--language en|hi|pa] [--format ...] [--long]   one script-generation call only, no TTS/character-draw/Vidu spend");
            Console.WriteLine("       VideoGen --status   lists every job with a decision still pending: never rendered, rendered but not approved, or approved and processed but never actually published anywhere");
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
    cfg.Validate(profile);
    profile.Validate();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

if (longForm && !profile.AllowsContextScenes)
{
    Console.Error.WriteLine(
        $"--long is not supported for profile \"{profile.Id}\": it renders through the Vidu " +
        "path, and a 70-90 scene long-form script there would mean 70-90 paid Vidu " +
        "submissions per video. --long is only wired for AllowsContextScenes profiles " +
        "(e.g. chronicleandchaos).");
    return 1;
}

var jsonOptions = Sidecar.Options;

try
{
    return mode switch
    {
        "prep" => await RunPrepAsync(),
        "resume" => await RunResumeAsync(),
        "manual" => await RunManualAsync(),
        "status" => await RunStatusAsync(),
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
        Path.Combine(cfg.WorkDirectory, profile.Id, $"job-{DateTime.Now:yyyyMMdd-HHmmss}")).FullName;
    Console.WriteLine($"Workspace: {workDir}");

    // 1. Character, then script. A pooled character is resolved first so the script can
    //    be written about the picture that already exists; an invented one is drawn
    //    afterwards, from the description the script came up with. Either order ends
    //    with the words and the artwork describing the same creature, which is the
    //    whole point - they used to disagree.
    var (pooled, addToPool) = profile.UsesCharacterMascot
        ? await CharacterSource.Resolve(profile, cfg, workDir)
        : (null, false);
    var recentNames = profile.UsesCharacterMascot && pooled is null
        ? CharacterSource.RecentNames(cfg, workDir) : [];

    var trendContext = topic is null ? await TrendResearch.FetchAsync(profile, cfg) : null;
    var recentTopics = topic is null ? await TopicHistory.RecentAsync(profile.Id) : [];
    var scriptProvider = ScriptProviderFactory.Create(cfg);
    var script = await ScriptGenerator.GenerateAsync(profile, topic, language!, scriptProvider, trendContext, pooled, recentNames, formatOverride, longForm, recentTopics);
    script.Language = language!;
    script.Profile = profile.Id;

    // 2. The branded intro bumper is scene zero: the same channel greeting every video,
    //    this video's character introducing itself, and a one-line preview of what it
    //    teaches. The greeting is the constant now that the cast is not. This clip is also
    //    what the YouTube thumbnail is cropped from (see ExtractThumbnailFrameAsync below),
    //    so its wording/motion is what actually delivers a format-appropriate thumbnail -
    //    calm formats get a gentler wave and a soft-pastel-but-colourful setting instead of
    //    the energetic bounce every format used to get.
    //
    //    Inserted before the earliest save below (not after the character draw, as it used
    //    to be) so a resumed job's scene indices - and therefore its scene00.mp3/
    //    scene00-art.png filenames - always match what --resume finds on disk. Building it
    //    only needs script.Format/CharacterName/CharacterDescription, all already set by
    //    GenerateAsync, so it never needed the drawn portrait to exist first.
    InsertIntroScene(script);

    // Persist right away, before the blocking manual-art step below - a script this far
    // along already spent real API quota to generate, so a crash, a closed terminal, or a
    // stuck art prompt must not lose it. --resume picks up from exactly this file (and
    // whatever partial audio/art already landed on disk) if anything past this point fails.
    // The topic-history log itself is NOT written here - it is only written by the
    // Uploader, once a render from this job actually publishes (see TopicHistory.cs) -
    // so a job that crashes or never gets approved never blocks a future run from
    // reusing its topic.
    await SaveScriptAsync(workDir, script);

    var characterImage = pooled?.ImagePath ?? await CharacterSource.DrawAsync(profile, cfg, script, workDir);
    script.CharacterImagePath = characterImage;
    if (addToPool) CharacterSource.SaveToPool(profile, script, characterImage);
    await SaveScriptAsync(workDir, script);

    Console.WriteLine($"Script: \"{script.Title}\" — {script.Format} — {script.Scenes.Count} scenes, starring {script.CharacterName}");

    // 3. Narration, then visuals - both shared with --resume, which calls the same two
    //    loops against a loaded-not-generated script and skips whatever is already on disk.
    await RunTtsLoopAsync(workDir, script);

    var credits = 0;
    if (profile.AllowsContextScenes)
        await RunVisualLoopAsync(workDir, script, characterImage);
    else
        credits = await RunViduLoopAsync(workDir, script, characterImage);

    await SaveScriptAsync(workDir, script);

    Console.WriteLine();
    if (profile.AllowsContextScenes)
    {
        Console.WriteLine($"Resolved visuals for {script.Scenes.Count} scenes (stock footage + manual art, no Vidu spend).");
        Console.WriteLine("Run --assemble to render with Remotion:");
    }
    else
    {
        Console.WriteLine($"Submitted {script.Scenes.Count} clips, {credits} credits (~${credits * 0.005:0.00}).");
        Console.WriteLine(cfg.ViduOffPeak
            ? "Off-peak: Vidu delivers within 48 hours. Run --assemble later to collect and render."
            : "Peak rate: clips usually land within minutes. Run --assemble to collect and render.");
    }
    Console.WriteLine($"  dotnet run -- --assemble --job {Path.GetFileName(workDir)}");
    return 0;
}

// The branded intro bumper as scene zero - shared by --prep and --manual, since a
// hand-written script needs the exact same channel greeting/thumbnail-source clip a
// generated one gets. See RunPrepAsync's comment above the call site for why this has to
// happen before the first save.
void InsertIntroScene(VideoScript script)
{
    var calmIntro = ScriptGenerator.PickFormat(profile, script.Format).IsCalm;
    var (introImagePrompt, introMotionPrompt) =
        profile.BuildIntroBumperPrompts(script.CharacterName, script.CharacterDescription, calmIntro);
    script.Scenes.Insert(0, new Scene
    {
        Narration = script.IntroText,
        ImagePrompt = introImagePrompt,
        MotionPrompt = introMotionPrompt,
    });
}

// Narration for every scene. Shared by --prep and --resume: a scene whose scene{i:D2}.mp3
// already exists on disk is skipped (just re-measured, since a script.json saved before the
// TTS loop ran has DurationSeconds still at 0), so re-running this against a job that died
// partway through only pays for the scenes that never finished. Saves after every new
// synth, not just at the end, so a second interruption loses at most one clip.
async Task RunTtsLoopAsync(string workDir, VideoScript script)
{
    var formatDef = ScriptGenerator.PickFormat(profile, script.Format);
    var tts = TtsProviderFactory.Create(cfg, profile, language!);
    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];
        var audioPath = Path.Combine(workDir, $"scene{i:D2}.mp3");

        if (File.Exists(audioPath))
        {
            scene.AudioPath = audioPath;
            scene.DurationSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, audioPath);
            Console.WriteLine($"TTS {i + 1}/{script.Scenes.Count}: already on disk ({scene.DurationSeconds:0.0}s)");
            continue;
        }

        await tts.SynthesizeAsync(scene.Narration, language!, audioPath, formatDef.Rate, formatDef.Pitch, scene.Mood);
        scene.AudioPath = audioPath;
        scene.DurationSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, audioPath);
        Console.WriteLine($"TTS {i + 1}/{script.Scenes.Count} ({scene.DurationSeconds:0.0}s)");
        await SaveScriptAsync(workDir, script);
    }

    var total = script.Scenes.Sum(s => s.DurationSeconds + 0.5);
    Console.WriteLine($"Narration total: {total:0}s");
}

// The "like, follow & subscribe" outro line is fixed per channel (see
// ContentProfile.OutroDestinationSpoken) rather than per-video, so it is synthesized once
// and cached on disk under VideoGen/assets/audio/outro - every later render for the same
// profile/language/text reuses the same file instead of paying TTS again for words that
// never change.
async Task<string> ResolveOutroAudioAsync(string spokenText)
{
    // Same hardcoded-absolute-path convention as BackgroundMusicPath/CharacterPoolPath in
    // GiggleGardenProfile.cs/MythologyProfile.cs, not one relative to the build output dir.
    const string outroAudioDir = @"D:\Business\BroadcastContentAutomation\VideoGen\assets\audio\outro";
    Directory.CreateDirectory(outroAudioDir);

    var key = new string(spokenText.ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    var audioPath = Path.Combine(outroAudioDir, $"{profile.Id}-{language}-{key}.mp3");
    if (File.Exists(audioPath)) return audioPath;

    var tts = TtsProviderFactory.Create(cfg, profile, language!);
    await tts.SynthesizeAsync(spokenText, language!, audioPath, "-4%", "+6%");
    Console.WriteLine($"Outro audio synthesized: {audioPath}");
    return audioPath;
}

// The no-Vidu hybrid pipeline's visuals (Deliverable 6, tasks/todo.md): a "context" scene's
// visual comes from Pexels (no human step), a "character" scene's comes from the manual
// Gemini prompt-and-pickup loop, referencing the character portrait for consistency the way
// Vidu's shared start frame used to provide automatically. Shared by --prep and --resume - a
// scene whose scene{i:D2}-stock.(mp4|jpg) or scene{i:D2}-art.(png|jpg|jpeg|webp) is already on
// disk is picked up as-is rather than re-downloaded/re-prompted, so resuming a job that died
// mid-loop never repeats a manual art step the user already did.
async Task RunVisualLoopAsync(string workDir, VideoScript script, string characterImage)
{
    var stock = new StockFootageClient(cfg);

    // Long-form (Scene.VisualGroup, see ScriptGenerator.cs): consecutive scenes sharing a
    // group number are one "visual beat" and reuse the same resolved image/clip instead of
    // each triggering its own Pexels/Gemini call - that's what makes ~12-18 art beats cover
    // 70-90 scenes. Scene 0 (the intro bumper) is excluded from both sides of the comparison
    // so it never accidentally matches - and never gets matched by - a real story scene that
    // happens to also default to group 0. A short-form script never sets VisualGroup, so
    // canReuse is always false there and every scene resolves independently.
    int? lastGroup = null;
    string? lastSceneKind = null;
    string? lastImagePath = null;
    string? lastClipPath = null;

    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];
        var label = $"{i + 1}/{script.Scenes.Count}";
        var isIntro = i == 0;
        var canReuse = script.IsLongForm && !isIntro && lastGroup == scene.VisualGroup && lastSceneKind == scene.SceneKind;

        if (scene.SceneKind == "context")
        {
            var stockBase = Path.Combine(workDir, $"scene{i:D2}-stock");
            var existing = File.Exists(stockBase + ".mp4") ? stockBase + ".mp4"
                : File.Exists(stockBase + ".jpg") ? stockBase + ".jpg" : null;

            if (existing is not null)
            {
                if (Path.GetExtension(existing).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    scene.ClipPath = existing;
                else
                    scene.ImagePath = existing;
                Console.WriteLine($"Stock {label}: already on disk -> {Path.GetFileName(existing)}");
            }
            else if (canReuse && (lastClipPath is not null || lastImagePath is not null))
            {
                scene.ClipPath = lastClipPath;
                scene.ImagePath = lastImagePath;
                Console.WriteLine($"Stock {label}: reused visual group {scene.VisualGroup} -> {Path.GetFileName(lastClipPath ?? lastImagePath!)}");
            }
            else
            {
                var downloaded = await stock.DownloadBestMatchAsync(scene.StockQuery!, stockBase, ImageClient.Orientation.Portrait)
                    ?? throw new Exception(
                        $"Scene {label}: Pexels had no match for \"{scene.StockQuery}\". Broaden the stockQuery " +
                        $"and re-run --prep, or hand-place a file at {stockBase}.jpg or {stockBase}.mp4 and re-run --assemble.");

                if (Path.GetExtension(downloaded).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    scene.ClipPath = downloaded;
                else
                    scene.ImagePath = downloaded;

                Console.WriteLine($"Stock {label}: \"{scene.StockQuery}\" -> {Path.GetFileName(downloaded)}");
            }
        }
        else
        {
            var artBase = Path.Combine(workDir, $"scene{i:D2}-art");
            var existing = ManualArtClient.ImageExtensions.Select(ext => artBase + ext).FirstOrDefault(File.Exists);

            if (existing is not null)
            {
                scene.ImagePath = existing;
                Console.WriteLine($"Art {label}: already on disk -> {Path.GetFileName(existing)}");
            }
            else if (canReuse && lastImagePath is not null)
            {
                scene.ImagePath = lastImagePath;
                Console.WriteLine($"Art {label}: reused visual group {scene.VisualGroup} -> {Path.GetFileName(lastImagePath)}");
            }
            else
            {
                var outputPath = artBase + ".png";
                var prompt = ImageClient.BuildPrompt(
                    scene.ImagePrompt, profile.CharacterStyle, profile.CharacterPortraitStyleSuffix,
                    ImageClient.Orientation.Portrait, matchReference: true);
                scene.ImagePath = await ManualArtClient.PromptAndWaitAsync(
                    prompt, outputPath, ImageClient.Orientation.Portrait, characterImage);

                Console.WriteLine($"Art {label}: saved {Path.GetFileName(scene.ImagePath)}");
            }
        }

        if (!isIntro)
        {
            lastGroup = scene.VisualGroup;
            lastSceneKind = scene.SceneKind;
            lastImagePath = scene.ImagePath;
            lastClipPath = scene.ClipPath;
        }

        await SaveScriptAsync(workDir, script);
    }
}

// Submits every clip to Vidu. All scenes seed from this video's one character frame rather
// than chaining each clip off the previous one's last frame: chaining is serial, which
// cannot work against an off-peak queue that may take 48 hours per clip, and it compounds
// drift over a dozen generations. Shared by --prep and --resume - a scene that already has a
// ViduTaskId is left alone rather than resubmitted (a real resubmit-on-failure only happens
// in --assemble's poll loop, which needs the terminal-state check that lives there).
async Task<int> RunViduLoopAsync(string workDir, VideoScript script, string characterImage)
{
    IVideoProvider vidu = new ViduClient(cfg);
    var credits = 0;

    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];

        if (!string.IsNullOrEmpty(scene.ViduTaskId))
        {
            Console.WriteLine($"Scene {i + 1}/{script.Scenes.Count}: already submitted (task {scene.ViduTaskId})");
            continue;
        }

        scene.StartFramePath = characterImage;

        var submission = await vidu.SubmitAsync(characterImage, scene.MotionPrompt, scene.DurationSeconds);
        scene.ViduTaskId = submission.TaskId;
        credits += submission.Credits;

        Console.WriteLine($"Submitted {i + 1}/{script.Scenes.Count} — task {submission.TaskId} ({submission.Credits} credits)");
        await SaveScriptAsync(workDir, script);
    }

    return credits;
}

// --------------------------------------------------------------- resume ------

// Continues a --prep run that never reached --assemble - a crashed terminal, a stuck
// manual-art prompt, a closed laptop - by reloading its script.json and re-running the same
// TTS/visual/Vidu loops --prep uses, which each skip any scene whose output file is already
// on disk. Only useful for jobs started after the early-save changes above; an older job
// whose process died before ever writing script.json has nothing here to resume from.
async Task<int> RunResumeAsync()
{
    var workDir = ResolveJobDirectory();
    Console.WriteLine($"Workspace: {workDir}");

    var script = await LoadScriptAsync(workDir);
    language = string.IsNullOrWhiteSpace(script.Language) ? language : script.Language;
    profile = string.IsNullOrWhiteSpace(script.Profile) ? profile : ContentProfileRegistry.Get(script.Profile);

    if (script.Scenes.Count == 0)
        throw new InvalidOperationException($"{workDir} has a script.json but no scenes yet - nothing to resume. Re-run --prep.");

    Console.WriteLine($"Resuming \"{script.Title}\" — {script.Format} — {script.Scenes.Count} scenes, starring {script.CharacterName}");

    var characterImage = script.CharacterImagePath is { Length: > 0 } saved && File.Exists(saved)
        ? saved
        : File.Exists(Path.Combine(workDir, "character.png"))
            ? Path.Combine(workDir, "character.png")
            : await CharacterSource.DrawAsync(profile, cfg, script, workDir);
    script.CharacterImagePath = characterImage;
    await SaveScriptAsync(workDir, script);

    await RunTtsLoopAsync(workDir, script);

    var credits = 0;
    if (profile.AllowsContextScenes)
        await RunVisualLoopAsync(workDir, script, characterImage);
    else
        credits = await RunViduLoopAsync(workDir, script, characterImage);

    await SaveScriptAsync(workDir, script);

    Console.WriteLine();
    if (profile.AllowsContextScenes)
    {
        Console.WriteLine("Resolved. Run --assemble to render with Remotion:");
    }
    else
    {
        Console.WriteLine(credits > 0
            ? $"Submitted {credits} additional credits (~${credits * 0.005:0.00})."
            : "Nothing new to submit - every scene already had a Vidu task.");
        Console.WriteLine(cfg.ViduOffPeak
            ? "Off-peak: Vidu delivers within 48 hours. Run --assemble later to collect and render."
            : "Peak rate: clips usually land within minutes. Run --assemble to collect and render.");
    }
    Console.WriteLine($"  dotnet run -- --assemble --job {Path.GetFileName(workDir)}");
    return 0;
}

// --------------------------------------------------------------- manual ------

// Starts a brand-new job from a hand-written script instead of calling the AI script
// provider - e.g. a script drafted together in a chat session and saved to a file. Runs the
// exact same TTS/visual/Vidu pipeline --prep does, via the same shared loops, so a manual
// job is assembled and rendered identically to a generated one. --format is required (not
// weighted-random picked) since whoever wrote the script already chose its tone/pacing.
async Task<int> RunManualAsync()
{
    if (scriptFile is null)
        throw new ArgumentException("--manual requires --script-file <path>. See --help.");
    if (formatOverride is null)
        throw new ArgumentException(
            $"--manual requires --format <id>. Choose one of: {string.Join(", ", profile.Formats.Select(f => f.Id))}.");
    if (!File.Exists(scriptFile))
        throw new FileNotFoundException($"No such script file: {scriptFile}", scriptFile);

    var draft = System.Text.Json.JsonSerializer.Deserialize<VideoScript>(
        await File.ReadAllTextAsync(scriptFile),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException($"{scriptFile} is not valid script JSON.");

    var script = ScriptGenerator.FinalizeManualScript(profile, draft, formatOverride);
    script.Language = language!;
    script.Profile = profile.Id;

    var workDir = Directory.CreateDirectory(
        Path.Combine(cfg.WorkDirectory, profile.Id, $"job-{DateTime.Now:yyyyMMdd-HHmmss}")).FullName;
    Console.WriteLine($"Workspace: {workDir}");

    InsertIntroScene(script);

    await SaveScriptAsync(workDir, script);
    // Topic-history is written by the Uploader once this job's render actually
    // publishes, not here - see TopicHistory.cs.

    // Manual scripts always draw fresh rather than pulling from the character pool - the
    // script already names a specific figure, not an interchangeable pool pick.
    var characterImage = await CharacterSource.DrawAsync(profile, cfg, script, workDir);
    script.CharacterImagePath = characterImage;
    await SaveScriptAsync(workDir, script);

    Console.WriteLine($"Script: \"{script.Title}\" — {script.Format} — {script.Scenes.Count} scenes, starring {script.CharacterName}");

    await RunTtsLoopAsync(workDir, script);

    var credits = 0;
    if (profile.AllowsContextScenes)
        await RunVisualLoopAsync(workDir, script, characterImage);
    else
        credits = await RunViduLoopAsync(workDir, script, characterImage);

    await SaveScriptAsync(workDir, script);

    Console.WriteLine();
    if (profile.AllowsContextScenes)
    {
        Console.WriteLine($"Resolved visuals for {script.Scenes.Count} scenes (stock footage + manual art, no Vidu spend).");
        Console.WriteLine("Run --assemble to render with Remotion:");
    }
    else
    {
        Console.WriteLine($"Submitted {script.Scenes.Count} clips, {credits} credits (~${credits * 0.005:0.00}).");
        Console.WriteLine(cfg.ViduOffPeak
            ? "Off-peak: Vidu delivers within 48 hours. Run --assemble later to collect and render."
            : "Peak rate: clips usually land within minutes. Run --assemble to collect and render.");
    }
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

    var formatDef = ScriptGenerator.PickFormat(profile, script.Format);
    var tts = TtsProviderFactory.Create(cfg, profile, language!);
    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];
        var audioPath = Path.Combine(workDir, $"scene{i:D2}.mp3");
        await tts.SynthesizeAsync(scene.Narration, language!, audioPath, formatDef.Rate, formatDef.Pitch, scene.Mood);
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
    await new AzureTtsProvider(cfg, profile.VoiceOverride).SynthesizeAsync(text, language!, azurePath, "-4%", "+6%");
    Console.WriteLine($"  Azure  -> {azurePath}");

    try
    {
        var googlePath = Path.Combine(outDir, $"google-{language}.mp3");
        await new GoogleTtsProvider(cfg).SynthesizeAsync(text, language!, googlePath, "-4%", "+6%");
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
    var script = await ScriptGenerator.GenerateAsync(profile, topic, language!, scriptProvider, trendContext, null, null, formatOverride, longForm);

    Console.WriteLine($"Format: {script.Format}   Title: \"{script.Title}\"   IsLongForm: {script.IsLongForm}");
    Console.WriteLine($"Character: {script.CharacterName} - {script.CharacterDescription}");
    Console.WriteLine($"MusicMood: {script.MusicMood}   NarrationStyle: {script.NarrationStyle}");
    Console.WriteLine($"Intro: {script.IntroText}");
    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var s = script.Scenes[i];
        var groupTag = script.IsLongForm ? $" [group {s.VisualGroup}]" : "";
        Console.WriteLine($"  Scene {i + 1} ({s.Narration.Length} chars){groupTag}: {s.Narration}");
    }
    if (script.IsLongForm)
    {
        var groups = script.Scenes.Select(s => s.VisualGroup).Distinct().Count();
        var totalChars = script.Scenes.Sum(s => s.Narration.Length);
        Console.WriteLine($"Long-form: {script.Scenes.Count} scenes, {groups} distinct visual groups, {totalChars} narration chars (~{totalChars / 5.0 / 140.0 * 60.0:0}s at 140wpm).");
    }
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

    // The no-Vidu hybrid pipeline (Deliverable 6) resolves every scene's visual
    // synchronously during --prep - stock footage and manual art are both already on
    // disk by the time --assemble runs, so there is nothing async to poll or collect.
    // Only a profile still on Vidu needs this step.
    if (!profile.AllowsContextScenes)
    {
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
    }

    // 6. Render
    var slug = Text.Slug(script.Title);
    var outputDir = Path.Combine(cfg.OutputDirectory, profile.Id);
    Directory.CreateDirectory(outputDir);
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

    // "LIKE, FOLLOW & SUBSCRIBE" outro card/voiceover appended to every render (see
    // ContentProfile.OutroDestinationText/-Spoken) - AllowsContextScenes profiles (today:
    // chronicleandchaos) additionally fold in a "watch the full video" line on the vertical
    // short only, since that render is a trimmed teaser for the landscape master rather
    // than the full video itself.
    var baseOutroLines = new[] { "LIKE, FOLLOW & SUBSCRIBE", profile.OutroDestinationText };
    var baseOutroSpoken = $"Like, follow, and subscribe on YouTube — {profile.OutroDestinationSpoken}!";
    var baseOutroAudio = await ResolveOutroAudioAsync(baseOutroSpoken);

    var vertical = Path.Combine(outputDir, $"{slug}-{language}-short.mp4");
    if (profile.AllowsContextScenes)
    {
        var shortOutroLines = new[]
        {
            "LIKE, FOLLOW & SUBSCRIBE", "Watch the full video on YouTube", profile.OutroDestinationText,
        };
        var shortOutroSpoken =
            $"Like, follow, and subscribe. Watch the full video on YouTube — {profile.OutroDestinationSpoken}.";
        var shortOutroAudio = await ResolveOutroAudioAsync(shortOutroSpoken);
        await VideoAssembler.AssembleWithRemotionAsync(profile, cfg, shortScenes, workDir, vertical, 1080, 1920, script.MusicMood,
            outroLines: shortOutroLines, outroAudioPath: shortOutroAudio);
    }
    else
    {
        await VideoAssembler.AssembleFromClipsAsync(profile, cfg, shortScenes, language!, workDir, vertical, 1080, 1920, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio);
    }
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
        MadeForKids = profile.MadeForKids,
        Channel = profile.Id,
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
    var landscape = Path.Combine(outputDir, $"{slug}-{language}.mp4");
    if (profile.AllowsContextScenes)
        await VideoAssembler.AssembleWithRemotionAsync(profile, cfg, script.Scenes, workDir, landscape, 1920, 1080, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio);
    else
        await VideoAssembler.AssembleFromClipsAsync(profile, cfg, script.Scenes, language!, workDir, landscape, 1920, 1080, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio);
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
        MadeForKids = profile.MadeForKids,
        Channel = profile.Id,
        Targets = [.. cfg.LandscapeTargets],
    }.SaveAsync(landscape);
    rendered.Add(landscape);

    Console.WriteLine();
    Console.WriteLine("Done. Review each video, set \"approved\": true in its sidecar JSON, then the publisher takes over:");
    foreach (var path in rendered) Console.WriteLine($"  {Sidecar.PathFor(path)}");
    return 0;
}

// --------------------------------------------------------------- status ------

// Walks every job-* folder that has a script.json and reports the ones that still need
// a human decision, so a started job can never quietly fall through the cracks now that
// topic-history is only written on confirmed publish (see TopicHistory.cs) rather than
// the moment a script is generated. Three states get flagged, per render (landscape and
// vertical are tracked independently, since the Uploader processes them independently):
//   - never rendered           -> the job stalled before --assemble ever produced this file
//   - rendered, not approved   -> sitting in cfg.OutputDirectory waiting on you
//   - processed, never published -> the Uploader ran it to a terminal state (moved to
//     \done or \failed) but every target failed or was skipped, so it never actually
//     went live anywhere
// A job with neither file anywhere is reported as never rendered for both renders; a job
// where every render already published successfully is not flagged at all.
async Task<int> RunStatusAsync()
{
    var jobDirs = ContentProfileRegistry.Ids
        .Select(id => Path.Combine(cfg.WorkDirectory, id))
        .Where(Directory.Exists)
        .SelectMany(dir => Directory.GetDirectories(dir, "job-*"))
        .Where(d => File.Exists(Path.Combine(d, "script.json")))
        .OrderBy(d => Path.GetFileName(d))
        .ToList();

    var flagged = new List<string>();

    foreach (var dir in jobDirs)
    {
        var jobName = Path.GetFileName(dir);
        VideoScript script;
        try { script = await LoadScriptAsync(dir); }
        catch (Exception ex) { flagged.Add($"[{jobName}] script.json unreadable: {ex.Message}"); continue; }

        // Pre-migration jobs have no Profile field (they all predate the second
        // channel), so they default to gigglegarden - the same channel their
        // files were migrated into on disk (see tasks/todo.md).
        var channelOutputDir = Path.Combine(cfg.OutputDirectory,
            string.IsNullOrWhiteSpace(script.Profile) ? "gigglegarden" : script.Profile);
        var doneDir = Path.Combine(channelOutputDir, "done");
        var failedDir = Path.Combine(channelOutputDir, "failed");

        var slug = Text.Slug(script.Title);

        foreach (var (suffix, kind) in new (string Suffix, string Kind)[]
                 { ("-short", "vertical/shorts"), ("", "landscape/YouTube") })
        {
            var fileName = $"{slug}-{script.Language}{suffix}.mp4";
            var liveVideo = Path.Combine(channelOutputDir, fileName);
            var doneVideo = Path.Combine(doneDir, Path.GetFileNameWithoutExtension(fileName), fileName);
            var failedVideo = Path.Combine(failedDir, Path.GetFileNameWithoutExtension(fileName), fileName);

            if (File.Exists(liveVideo))
            {
                var sidecar = await Sidecar.LoadAsync(liveVideo);
                if (sidecar is not null && sidecar.Approved != true)
                    flagged.Add($"[{jobName}] \"{script.Title}\" ({kind}) — rendered, awaiting your approval. " +
                        $"Set \"approved\": true in {Sidecar.PathFor(liveVideo)}, or delete both files to discard.");
            }
            else if (File.Exists(doneVideo) || File.Exists(failedVideo))
            {
                var terminalVideo = File.Exists(doneVideo) ? doneVideo : failedVideo;
                var sidecar = await Sidecar.LoadAsync(terminalVideo);
                if (sidecar is not null && !sidecar.AnyPublished())
                    flagged.Add($"[{jobName}] \"{script.Title}\" ({kind}) — processed but never published on any " +
                        $"target. Check {Sidecar.PathFor(terminalVideo)} for per-platform errors, then fix and " +
                        "re-approve, or discard.");
            }
            else
            {
                flagged.Add($"[{jobName}] \"{script.Title}\" ({kind}) — never rendered. Run " +
                    $"\"--assemble --job {jobName}\" (or \"--resume --job {jobName}\" first if TTS/art is " +
                    $"incomplete), or delete {dir} to discard.");
            }
        }
    }

    if (flagged.Count == 0)
    {
        Console.WriteLine("No unfinished work - every job is either fully published or still healthy mid-pipeline.");
        return 0;
    }

    Console.WriteLine($"{flagged.Count} item(s) need a decision:");
    foreach (var line in flagged) Console.WriteLine($"  - {line}");
    return 0;
}

// ----------------------------------------------------------- plumbing ------

string ResolveJobDirectory()
{
    if (job is not null)
    {
        if (Path.IsPathRooted(job))
        {
            if (!Directory.Exists(job))
                throw new DirectoryNotFoundException($"No such job folder: {job}");
            return job;
        }

        // --resume/--retts/--assemble all self-correct `profile` from the job's own
        // script.Profile once it's loaded, so the CLI-supplied --profile (or its
        // default) is only a hint here, not authoritative. Try that hint's subfolder
        // first (the common case), then fall back to searching every channel.
        var hinted = Path.Combine(cfg.WorkDirectory, profile.Id, job);
        if (Directory.Exists(hinted))
            return hinted;

        foreach (var id in ContentProfileRegistry.Ids)
        {
            var candidate = Path.Combine(cfg.WorkDirectory, id, job);
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            $"No such job folder \"{job}\" under {cfg.WorkDirectory} (checked every channel).");
    }

    // Default to the newest job that still has work outstanding, searched across
    // every channel's subfolder, so the common case (prep today, assemble
    // tomorrow) needs no arguments regardless of which channel it belongs to.
    var newest = ContentProfileRegistry.Ids
        .Select(id => Path.Combine(cfg.WorkDirectory, id))
        .Where(Directory.Exists)
        .SelectMany(dir => Directory.GetDirectories(dir, "job-*"))
        .Where(d => File.Exists(Path.Combine(d, "script.json")))
        .OrderByDescending(d => Path.GetFileName(d))
        .FirstOrDefault();

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
