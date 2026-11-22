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
using System.Runtime.InteropServices;

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
        case "--self-test": mode = "self-test"; break;
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
            Console.WriteLine("       VideoGen --prep --job job-YYYYMMDD-HHMMSS [--long] [--profile ...]   resume a --prep that died during script generation, continuing from the next act after the job's script-partial.json checkpoint (acts already saved are reused, never regenerated)");
            Console.WriteLine("       VideoGen --assemble [--job job-YYYYMMDD-HHMMSS]");
            Console.WriteLine("       VideoGen --resume   [--job job-YYYYMMDD-HHMMSS]   continue an interrupted --prep, skipping any scene whose audio/art is already on disk (falls back to the script-partial.json checkpoint when no script.json exists yet)");
            Console.WriteLine("       VideoGen --manual --script-file <path> --format <id> [--language en|hi|pa] [--profile ...]   start a new job from a hand-written script.json-shaped file instead of calling the AI script provider");
            Console.WriteLine("       VideoGen --retts    [--job job-YYYYMMDD-HHMMSS]   re-voice an existing job");
            Console.WriteLine("       VideoGen --test-tts [--language en|hi|pa]   synthesize a sample line on every reachable provider, no Vidu/Claude spend");
            Console.WriteLine("       VideoGen --dry-script [--topic \"...\"] [--language en|hi|pa] [--format ...] [--long] [--job job-YYYYMMDD-HHMMSS]   one script-generation call only, no TTS/character-draw/Vidu spend (--job resumes generation from that job's script-partial.json checkpoint, for testing the resume path)");
            Console.WriteLine("       VideoGen --self-test   run the built-in checks (JSON salvage repair) with no API spend");
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

    // Linux port: if AssetsDirectory / TopicHistoryDirectory are set, re-root
    // the profile's hardcoded Windows asset paths and topic history there.
    // The profile record is immutable, so build a copy with `with` — the
    // original Windows paths are untouched when these settings are empty.
    // This MUST run before Validate: profile.Validate checks that
    // CharacterPoolPath exists, and the raw Windows path never does on Linux
    // (GiggleGarden's pool failed startup validation for exactly this reason
    // until the order was fixed, 2026-11-13).
    if (!string.IsNullOrWhiteSpace(cfg.TopicHistoryDirectory))
        TopicHistory.DirectoryOverride = cfg.TopicHistoryDirectory;
    profile = ApplyAssetMapping(profile);

    cfg.Validate(profile, language);
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

// If the process is killed by a signal (driver shutdown, VM teardown), say
// so in the log instead of vanishing silently - the last checkpoint line
// plus this line pin down where it died. SIGKILL/OOM cannot be caught;
// the per-act heartbeat lines in ScriptGenerator are the backstop there.
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    Console.Error.WriteLine(
        $"[{DateTime.UtcNow:HH:mm:ss}Z] FATAL: received SIGTERM - killed externally " +
        "(driver/VM shutdown?). Last checkpoint is in script-partial.json; re-run --prep --job to resume.");
    ctx.Cancel = false;
});
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
{
    Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}Z] FATAL: received SIGINT - interrupted.");
    ctx.Cancel = false;
});

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
        "self-test" => RunSelfTest(),
        _ => await RunAssembleAsync(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: [{ex.GetType().Name}] {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------- prep ------

async Task<int> RunPrepAsync()
{
    // Resume-from-partial: --prep --job <existing job dir> continues script
    // generation from the next act after the job's script-partial.json
    // checkpoint. The saved acts are reused verbatim; only the missing acts
    // are generated, then the normal prep tail (intro, TTS, visuals) runs.
    if (job is not null)
    {
        var resumeDir = ResolveJobDirectory();
        if (File.Exists(Path.Combine(resumeDir, "script.json")))
            throw new InvalidOperationException(
                $"{resumeDir} already has a script.json - run --resume --job {Path.GetFileName(resumeDir)} instead.");
        if (!File.Exists(Path.Combine(resumeDir, "script-partial.json")))
            throw new InvalidOperationException(
                $"--prep --job {job}: no script-partial.json in {resumeDir} - nothing to resume. Omit --job for a fresh prep.");
        return await RunPrepResumeAsync(resumeDir);
    }

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

    // The news profile needs the structured report (category + headline list)
    // for its faceless prompt; other profiles only ever read the text block,
    // so FetchAsync's plain-text wrapper still serves them unchanged.
    var trendReport = topic is null ? await TrendResearch.FetchReportAsync(profile, cfg) : null;
    var trendContext = trendReport?.Text;
    // Trend snapshot per job (priority 3) + analytics brief (priority 1):
    // both are saved into the job folder so the script, tags, and later
    // learning all reference the same source, and so a job can be audited
    // without re-querying the APIs.
    if (trendContext is not null)
        await File.WriteAllTextAsync(Path.Combine(workDir, "trend-snapshot.txt"), trendContext);
    var analyticsDir = Path.Combine(cfg.WorkDirectory, "analytics");
    var analyticsBrief = GiggleGarden.Shared.AnalyticsLog.Summarize(await GiggleGarden.Shared.AnalyticsLog.LoadAsync(analyticsDir));
    if (!analyticsBrief.StartsWith("No published", StringComparison.Ordinal))
        trendContext = (trendContext ?? "") + "\nWhat worked on this channel before:\n" + analyticsBrief;
    // Wikipedia fact context (priority 4) when a topic is given: free, no key.
    if (topic is not null)
    {
        var wiki = await WikipediaClient.FetchSummaryAsync(topic);
        if (wiki is not null) trendContext = (trendContext ?? "") + "\nWikipedia context for this topic (use real facts from here, not invented ones):\n" + wiki;
    }
    var recentTopics = topic is null ? await TopicHistory.RecentAsync(profile.Id) : [];
    var scriptProvider = ScriptProviderFactory.Create(cfg);
    // Total acts for the checkpoint: news long-form has 3 acts, mascot
    // long-form has 4. (Older checkpoints recorded 4 for the news path -
    // the resume path derives the total from the profile instead.)
    var totalActs = longForm ? (profile.UsesCharacterMascot ? 4 : 3) : 1;
    var script = await ScriptGenerator.GenerateAsync(profile, topic, language!, scriptProvider, trendContext, pooled, recentNames, formatOverride, longForm, recentTopics,
        onActComplete: (s, act) => SavePartialScriptAsync(workDir, s, act, totalActs),
        topicCategory: trendReport?.Category, newsItems: trendReport?.News);
    return await FinishPrepAsync(workDir, script, pooled, addToPool);
}

// Shared tail of --prep (fresh and resumed): once GenerateAsync has returned
// a full script, persist it, add the intro bumper, draw the character, and
// run the TTS/visual loops. Called by RunPrepAsync and RunPrepResumeAsync
// alike, so a resumed prep finishes identically to a fresh one.
async Task<int> FinishPrepAsync(string workDir, VideoScript script, CharacterBrief? pooled, bool addToPool)
{
    // Full script generated: script.json (saved below) supersedes the checkpoints.
    DeletePartialScriptFiles(workDir);
    // GenerateAsync sets these early now, so they are normally already set;
    // only fill them in when a script arrived without them.
    if (string.IsNullOrWhiteSpace(script.Language)) script.Language = language!;
    if (string.IsNullOrWhiteSpace(script.Profile)) script.Profile = profile.Id;
    Console.WriteLine($"Hook score: {script.HookScore}/100, Title SEO score: {script.SeoScoreValue}/100 — \"{script.Title}\"");
    if (script.HookScore < 50)
        Console.WriteLine("  Warning: hook score is low — scene 1 may not stop a scroll. Consider rewriting it before TTS.");
    // Post-generation Wikipedia check for Chronicle figures (priority 4).
    if (profile.AllowsContextScenes && !string.IsNullOrWhiteSpace(script.CharacterName))
    {
        var wikiFigure = await WikipediaClient.FetchSummaryAsync(script.CharacterName);
        if (wikiFigure is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "fact-check.txt"), wikiFigure);
            var allNarration = string.Join(" ", script.Scenes.Select(s => s.Narration));
            var years = WikipediaClient.YearsToCheck(allNarration, wikiFigure);
            if (years.Count > 0)
                Console.WriteLine($"  Fact-check warning: years mentioned but not in Wikipedia summary for {script.CharacterName}: {string.Join(", ", years)} — verify before publishing.");
        }
    }

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

    // Curiosity brief (Rishi 2026-11-21, video #3 onward): build the brief
    // scenes from script.Brief (2 teaser sentences) BEFORE the TTS loop so
    // their narration is voiced with the rest. Images are NOT set here - the
    // main scenes' art is only resolved by RunVisualLoopAsync below, so
    // ResolveBriefImages(script) fills them in after that loop, reusing the
    // most dramatic main-scene images (no new art generated).
    BuildBriefScenes(script);

    // Persist right away, before the blocking manual-art step below - a script this far
    // along already spent real API quota to generate, so a crash, a closed terminal, or a
    // stuck art prompt must not lose it. --resume picks up from exactly this file (and
    // whatever partial audio/art already landed on disk) if anything past this point fails.
    // The topic-history log itself is NOT written here - it is only written by the
    // Uploader, once a render from this job actually publishes (see TopicHistory.cs) -
    // so a job that crashes or never gets approved never blocks a future run from
    // reusing its topic.
    await SaveScriptAsync(workDir, script);

    // Faceless profiles have no character to draw - the intro bumper is a title
    // card and every scene is art, stock, or a data visual. characterImage stays
    // empty; RunVisualLoopAsync never uses it for an automated art provider.
    var characterImage = !profile.UsesCharacterMascot ? ""
        : pooled?.ImagePath ?? await CharacterSource.DrawAsync(profile, cfg, script, workDir);
    script.CharacterImagePath = characterImage;
    if (addToPool) CharacterSource.SaveToPool(profile, script, characterImage);
    await SaveScriptAsync(workDir, script);

    Console.WriteLine(profile.UsesCharacterMascot
        ? $"Script: \"{script.Title}\" — {script.Format} — {script.Scenes.Count} scenes, starring {script.CharacterName}"
        : $"Script: \"{script.Title}\" — {script.Format} — {script.Scenes.Count} scenes, category {script.TopicCategory}");

    // 3. Narration, then visuals - both shared with --resume, which calls the same two
    //    loops against a loaded-not-generated script and skips whatever is already on disk.
    await RunTtsLoopAsync(workDir, script);

    var credits = 0;
    if (profile.UsesRemotionStills)
        await RunVisualLoopAsync(workDir, script, characterImage);
    else
        credits = await RunViduLoopAsync(workDir, script, characterImage);

    // Brief images (Rishi 2026-11-21): now that the main scenes' art is
    // resolved, fill the brief scenes' images from the most dramatic ones.
    ResolveBriefImages(script);

    await SaveScriptAsync(workDir, script);

    Console.WriteLine();
    if (profile.UsesRemotionStills)
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

// Loads a job's script-partial.json + script-progress.json: the script as of
// the last completed act, and how many acts that was. Throws a clear error
// when there is nothing usable to resume from.
async Task<(VideoScript Script, int CompletedActs)> LoadPartialScriptAsync(string workDir)
{
    var partialPath = Path.Combine(workDir, "script-partial.json");
    var script = System.Text.Json.JsonSerializer.Deserialize<VideoScript>(
        await File.ReadAllTextAsync(partialPath), jsonOptions)
        ?? throw new InvalidDataException($"{partialPath} is not valid script JSON.");
    if (script.Scenes.Count == 0)
        throw new InvalidDataException($"{partialPath} has no scenes - nothing to resume.");
    if (string.IsNullOrWhiteSpace(script.Title))
        throw new InvalidDataException($"{partialPath} has no title - nothing to resume.");
    var completedActs = 0;
    var progressPath = Path.Combine(workDir, "script-progress.json");
    if (File.Exists(progressPath))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(progressPath));
            if (doc.RootElement.TryGetProperty("completedActs", out var ca) && ca.TryGetInt32(out var n))
                completedActs = n;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: could not read {progressPath}: {ex.Message}");
        }
    }
    if (completedActs <= 0)
        throw new InvalidDataException(
            $"{progressPath} is missing or has no completedActs - cannot tell how many acts are done.");
    return (script, completedActs);
}

// Continues a --prep that died during script generation: loads the job's
// partial checkpoint, generates only the missing acts, then runs the normal
// prep tail. The saved acts are never regenerated. The trend snapshot the
// original prep saved is the facts source for the remaining acts - trending
// news is NOT re-fetched, which could pick a different story and break
// continuity with the saved acts.
async Task<int> RunPrepResumeAsync(string workDir)
{
    Console.WriteLine($"Workspace: {workDir}");
    var (partial, completedActs) = await LoadPartialScriptAsync(workDir);
    // A partial only ever exists for long-form (short-form has no acts).
    // The total is derived from the profile, not the stored value: older
    // checkpoints recorded 4 for the news path, which only has 3 acts.
    var totalActs = profile.UsesCharacterMascot ? 4 : 3;
    // A newer checkpoint carries the profile it was written under, which
    // wins over the CLI hint; an older checkpoint has an empty Profile and
    // the CLI --profile is then authoritative.
    profile = ApplyAssetMapping(string.IsNullOrWhiteSpace(partial.Profile) ? profile : ContentProfileRegistry.Get(partial.Profile));
    Console.WriteLine(
        $"  Resuming \"{partial.Title}\" from act {completedActs}/{totalActs} " +
        $"({partial.Scenes.Count} scenes kept, category {partial.TopicCategory}, profile {profile.Id})");
    if (completedActs >= totalActs)
    {
        Console.WriteLine("  All acts are already saved - finalizing the partial as the full script.");
        return await FinishPrepAsync(workDir, partial, null, false);
    }
    var snapshotPath = Path.Combine(workDir, "trend-snapshot.txt");
    var trendContext = File.Exists(snapshotPath) ? await File.ReadAllTextAsync(snapshotPath) : null;
    if (trendContext is null)
        Console.WriteLine("  Warning: no trend-snapshot.txt in this job - continuation acts validate against the recap only.");
    // Language: the CLI flag wins when it is non-default; otherwise the
    // partial's recorded language (new checkpoints carry the right one).
    var resumeLanguage = language != "en" ? language!
        : string.IsNullOrWhiteSpace(partial.Language) ? "en" : partial.Language;
    var formatOverrideForResume = string.IsNullOrWhiteSpace(partial.Format) ? formatOverride : partial.Format;
    var scriptProvider = ScriptProviderFactory.Create(cfg);
    var script = await ScriptGenerator.GenerateAsync(profile, topic: null, resumeLanguage, scriptProvider, trendContext,
        character: null, avoidNames: null, formatOverride: formatOverrideForResume, longForm: true, avoidTopics: [],
        onActComplete: (s, act) => SavePartialScriptAsync(workDir, s, act, totalActs),
        topicCategory: partial.TopicCategory, newsItems: null,
        resumeFrom: new ScriptResume(partial, completedActs));
    return await FinishPrepAsync(workDir, script, null, false);
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
        // Faceless profiles have no character: the intro bumper is an art scene,
        // not a character moment (it used to keep the Scene default "character",
        // which mislabelled scene 1 in every Trendline Now job).
        SceneKind = profile.UsesCharacterMascot ? "character" : "art",
    });
}

// Curiosity brief (Rishi 2026-11-21, video #3 onward): split script.Brief
// (the model's 2 teaser sentences) into one BriefScene per sentence, so the
// teaser plays as 2 short beats before the intro sting. Images are NOT set
// here - the main scenes' art is only resolved by RunVisualLoopAsync, so
// ResolveBriefImages fills them in after that loop. A script with no Brief
// (pre-feature, video #1/#2) gets no brief scenes at all.
void BuildBriefScenes(VideoScript script)
{
    script.BriefScenes = [];
    if (string.IsNullOrWhiteSpace(script.Brief)) return;
    // Split into sentences on ". " / "! " / "? " boundaries; take the first
    // two (the model was asked for exactly two). A single-sentence brief
    // still yields one brief scene.
    var sentences = script.Brief
        .Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .Take(2)
        .Select(s => s + ".")
        .ToList();
    foreach (var sentence in sentences)
    {
        script.BriefScenes.Add(new Scene
        {
            Narration = sentence,
            // Art scene so the assembler renders it as an image (not a data
            // visual); the image itself is filled in by ResolveBriefImages.
            SceneKind = "art",
        });
    }
    if (script.BriefScenes.Count > 0)
        Console.WriteLine($"  Brief: {script.BriefScenes.Count} teaser scene(s) ({script.BriefScenes.Sum(s => s.Narration.Length)} chars) - plays before the intro sting.");
}

// Fill the brief scenes' images from the main scenes' most dramatic art:
// the first two story scenes (index >= 1, skipping the intro bumper) that
// have a resolved image. Reuses art, so no new image is generated for the
// brief. If no main scene has an image yet, the brief is dropped (a brief
// with no visual cannot render).
void ResolveBriefImages(VideoScript script)
{
    if (script.BriefScenes.Count == 0) return;
    var dramatic = script.Scenes
        .Skip(1)
        .Where(s => s.ImagePath is not null || s.LandscapeImagePath is not null)
        .Take(script.BriefScenes.Count)
        .ToList();
    for (var i = 0; i < script.BriefScenes.Count; i++)
    {
        if (i >= dramatic.Count) { script.BriefScenes.RemoveRange(i, script.BriefScenes.Count - i); break; }
        script.BriefScenes[i].ImagePath = dramatic[i].ImagePath;
        script.BriefScenes[i].LandscapeImagePath = dramatic[i].LandscapeImagePath;
        script.BriefScenes[i].ClipPath = dramatic[i].ClipPath;
        script.BriefScenes[i].LandscapeClipPath = dramatic[i].LandscapeClipPath;
    }
    if (script.BriefScenes.Count == 0)
        Console.WriteLine("  Brief dropped: no dramatic main-scene image to reuse.");
}

// Narration for every scene. Shared by --prep and --resume: a scene whose scene{i:D2}.mp3
// already exists on disk is skipped (just re-measured, since a script.json saved before the
// TTS loop ran has DurationSeconds still at 0), so re-running this against a job that died
// partway through only pays for the scenes that never finished. Saves after every new
// synth, not just at the end, so a second interruption loses at most one clip.
async Task RunTtsLoopAsync(string workDir, VideoScript script)
{
    var formatDef = ScriptGenerator.PickFormat(profile, script.Format);
    // Topic-aware pacing (Trendline Now): the resolved category's rate/pitch win
    // over the format's, so a gaming video reads faster than a finance one while
    // the voice (DavisNeural) stays the same.
    var (rate, pitch) = ResolvePacing(profile, script, formatDef);
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

        // Voice variations OFF (Rishi 2026-11-17): no per-scene mood styles for
        // either channel - the express-as variations sound poor. Narration is
        // plain (no styleOverride); the profile's fixed style is null too, so
        // the voice stays consistent across every scene.
        await tts.SynthesizeAsync(scene.Narration, language!, audioPath, rate, pitch, null);
        // Silence trim (priority 6, free FFmpeg): TTS files often start/end
        // with dead air, and dead air at the start of a Short is a scroll-past.
        // Trimming here also shortens DurationSeconds, which is what the
        // render timings are built from, so the video stays in sync.
        await VideoAssembler.TrimSilenceAsync(cfg, audioPath);
        scene.AudioPath = audioPath;
        scene.DurationSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, audioPath);
        Console.WriteLine($"TTS {i + 1}/{script.Scenes.Count} ({scene.DurationSeconds:0.0}s)");
        await SaveScriptAsync(workDir, script);
        // Pace the calls (2026-11-21): Azure Free F0 throttles with HTTP 429
        // after ~20-35 rapid synth calls - the first long-form prep died at
        // 34/43. A 2s gap between calls keeps the burst rate down; the
        // provider's retry (AzureHelperTtsProvider) covers any throttle
        // that still lands.
        if (i < script.Scenes.Count - 1) await Task.Delay(TimeSpan.FromSeconds(2));
    }

    // Brief narration (Rishi 2026-11-21, video #3 onward): same voice/pace as
    // the main scenes, saved as brief00.mp3 / brief01.mp3 so they never clash
    // with scene00.mp3. Skipped when the script has no brief (pre-feature).
    for (var i = 0; i < script.BriefScenes.Count; i++)
    {
        var scene = script.BriefScenes[i];
        var audioPath = Path.Combine(workDir, $"brief{i:D2}.mp3");

        if (File.Exists(audioPath))
        {
            scene.AudioPath = audioPath;
            scene.DurationSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, audioPath);
            Console.WriteLine($"Brief TTS {i + 1}/{script.BriefScenes.Count}: already on disk ({scene.DurationSeconds:0.0}s)");
            continue;
        }

        await tts.SynthesizeAsync(scene.Narration, language!, audioPath, rate, pitch, null);
        await VideoAssembler.TrimSilenceAsync(cfg, audioPath);
        scene.AudioPath = audioPath;
        scene.DurationSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, audioPath);
        Console.WriteLine($"Brief TTS {i + 1}/{script.BriefScenes.Count} ({scene.DurationSeconds:0.0}s)");
        await SaveScriptAsync(workDir, script);
        if (i < script.BriefScenes.Count - 1) await Task.Delay(TimeSpan.FromSeconds(2));
    }

    var total = script.Scenes.Sum(s => s.DurationSeconds + 0.5);
    Console.WriteLine($"Narration total: {total:0}s");
}

// Category pacing override for profiles with a CategoryStyles table: the
// script's persisted TopicCategory picks the style; anything unresolved falls
// back to the format's own rate/pitch. DavisNeural stays the voice throughout
// - only the delivery speed/energy shifts with the topic.
(string Rate, string Pitch) ResolvePacing(ContentProfile p, VideoScript s, ContentFormatDef f) =>
    !string.IsNullOrWhiteSpace(s.TopicCategory) && p.CategoryStyles is not null &&
    p.CategoryStyles.TryGetValue(s.TopicCategory, out var style)
        ? (style.PacingRate, style.PacingPitch)
        : (f.Rate, f.Pitch);

// The "like, follow & subscribe" outro line is fixed per channel (see
// ContentProfile.OutroDestinationSpoken) rather than per-video, so it is synthesized once
// and cached on disk under VideoGen/assets/audio/outro - every later render for the same
// profile/language/text reuses the same file instead of paying TTS again for words that
// never change.
// The outro voiceover uses the same pacing as the scenes (the resolved
// category/format rate/pitch, passed in by the caller) so the end card sounds
// like the same narrator, not a different read.
async Task<string> ResolveOutroAudioAsync(string spokenText, string rate, string pitch)
{
    // Linux port: outro voiceovers are cached under the run's WorkDirectory
    // (shared across jobs) instead of a hardcoded Windows assets folder, so
    // the same code works on Windows and Linux without a config change.
    var outroAudioDir = Path.Combine(cfg.WorkDirectory, "outro");
    Directory.CreateDirectory(outroAudioDir);

    var key = new string(spokenText.ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    var audioPath = Path.Combine(outroAudioDir, $"{profile.Id}-{language}-{key}.mp3");
    if (File.Exists(audioPath)) return audioPath;

    var tts = TtsProviderFactory.Create(cfg, profile, language!);
    await tts.SynthesizeAsync(spokenText, language!, audioPath, rate, pitch);
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
    // Stock footage: direct client when a real Pexels key is in config,
    // vault-backed helper client otherwise (same selection rule as
    // GenConfig.Validate - a missing key is fine when the helper exists).
    IStockFootageClient stock = GenConfig.HasRealKey(cfg.PexelsApiKey)
        ? new StockFootageClient(cfg)
        : new StockFootageHelperClient(cfg);

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
    string? lastLandscapeImagePath = null;

    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];
        var label = $"{i + 1}/{script.Scenes.Count}";
        var isIntro = i == 0;
        var canReuse = script.IsLongForm && !isIntro && lastGroup == scene.VisualGroup && lastSceneKind == scene.SceneKind;

        if (scene.SceneKind == "visual")
        {
            // Data visuals are drawn by Remotion from the spec at assemble time -
            // nothing to download or generate here. Re-check the spec (a
            // hand-edited script.json could carry a bad one); the spec travels
            // with the scene into the Remotion props. Group reuse below treats it
            // like any other resolved visual (its ImagePath/ClipPath stay null,
            // so a following art/context scene in the same group never reuses it).
            if (scene.VisualSpec is null)
                throw new InvalidOperationException($"Scene {label}: a \"visual\" scene needs a visualSpec.");
            Console.WriteLine($"Visual {label}: {scene.VisualSpec.Kind} \"{scene.VisualSpec.Title ?? scene.VisualSpec.Text}\"");
        }
        else if (scene.SceneKind == "context")
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
                scene.LandscapeImagePath = lastLandscapeImagePath;
                Console.WriteLine($"Stock {label}: reused visual group {scene.VisualGroup} -> {Path.GetFileName(lastClipPath ?? lastImagePath!)}");
            }
            else
            {
                // Single-clip rule (2026-11-11, user feedback): ONE Pexels
                // clip per context scene, downloaded landscape-first. The
                // landscape render uses it as-is; the portrait render
                // cover-crops it at render time (Assembly.tsx MotionVideo:
                // objectFit cover, top-anchored), which keeps the full
                // height — heads included — and cuts only the sides. If
                // Pexels has no landscape match for the query, a portrait
                // clip is the single source instead; it is never two
                // different clips.
                var downloaded = await stock.DownloadBestMatchAsync(scene.StockQuery!, stockBase, ImageClient.Orientation.Landscape)
                    ?? await stock.DownloadBestMatchAsync(scene.StockQuery!, stockBase, ImageClient.Orientation.Portrait)
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
                scene.LandscapeImagePath = lastLandscapeImagePath;
                Console.WriteLine($"Art {label}: reused visual group {scene.VisualGroup} -> {Path.GetFileName(lastImagePath)}");
            }
            else if (IsAutomatedProvider(cfg))
            {
                // Identical-image rule (2026-11-11, user feedback on the
                // first test videos): ONE square generation per scene, and
                // both orientations are cover-crops of that one
                // image, so portrait and landscape show the same scene,
                // same character, same composition — just framed
                // differently. No second generation per scene, no blur-pad
                // for scene art; the square source is kept on disk
                // (sceneNN-art-square.*) so a resume never regenerates it.
                // Landscape framing (2026-11-13, user feedback: the 16:9
                // top-biased cover-crop cut full-body figures off at the
                // waist): the landscape asset is the square itself, NOT a
                // 1920x1080 pre-crop. Remotion's KenBurnsImage contain-fits
                // it over the blurred cover-fill, so the whole figure stays
                // visible in landscape — the blur-pad framing the 2026-08-19
                // fix used for portrait art in landscape. The portrait crop
                // keeps the full height.
                var squarePrompt = ImageClient.BuildPrompt(
                    scene.ImagePrompt, profile.CharacterStyle, profile.CharacterPortraitStyleSuffix,
                    ImageClient.Orientation.Square, matchReference: profile.UsesCharacterMascot);
                var squarePath = await GenerateSquareArtAsync(cfg, squarePrompt, artBase + "-square");
                scene.ImagePath = await DeriveArtAsync(cfg, squarePath, artBase + ".png", 1080, 1920);
                scene.LandscapeImagePath = squarePath;
                Console.WriteLine($"Art {label}: square source {Path.GetFileName(squarePath)} -> portrait + landscape");
            }
            else
            {
                var prompt = ImageClient.BuildPrompt(
                    scene.ImagePrompt, profile.CharacterStyle, profile.CharacterPortraitStyleSuffix,
                    ImageClient.Orientation.Portrait, matchReference: profile.UsesCharacterMascot);
                scene.ImagePath = await ManualArtClient.PromptAndWaitAsync(
                    prompt, artBase + ".png", ImageClient.Orientation.Portrait, characterImage);

                Console.WriteLine($"Art {label}: saved {Path.GetFileName(scene.ImagePath)}");
            }

            // Dual-orientation: landscape counterpart for the YouTube master.
            // Automated providers derive it from the same square source (or,
            // on a resume where only the portrait art survived, from the
            // portrait art) — never a second generation. Manual mode keeps
            // the original second prompt-and-wait call.
            if (scene.LandscapeImagePath is null)
            {
                if (IsAutomatedProvider(cfg))
                {
                    var squareBase = artBase + "-square";
                    var squareExisting = ManualArtClient.ImageExtensions.Select(ext => squareBase + ext).FirstOrDefault(File.Exists);
                    // Same as the main loop: the square itself is the
                    // landscape asset (blur-pad contain-fit in Remotion).
                    // A stale -landscape.png from the old top-biased
                    // pre-crop is ignored when a square exists; without a
                    // square the portrait asset contain-fits the same way.
                    scene.LandscapeImagePath = squareExisting ?? scene.ImagePath;
                }
                else
                {
                    var landscapeBase = artBase + "-landscape";
                    var landscapeExisting = ManualArtClient.ImageExtensions.Select(ext => landscapeBase + ext).FirstOrDefault(File.Exists);
                    if (landscapeExisting is not null)
                    {
                        scene.LandscapeImagePath = landscapeExisting;
                    }
                    else
                    {
                        var landscapePrompt = ImageClient.BuildPrompt(
                            scene.ImagePrompt, profile.CharacterStyle, profile.CharacterPortraitStyleSuffix,
                            ImageClient.Orientation.Landscape, matchReference: profile.UsesCharacterMascot);
                        scene.LandscapeImagePath = await ManualArtClient.PromptAndWaitAsync(
                            landscapePrompt, landscapeBase + ".png", ImageClient.Orientation.Landscape, characterImage);
                    }
                }
            }
        }

        if (!isIntro)
        {
            lastGroup = scene.VisualGroup;
            lastSceneKind = scene.SceneKind;
            lastImagePath = scene.ImagePath;
            lastClipPath = scene.ClipPath;
            lastLandscapeImagePath = scene.LandscapeImagePath;
        }

        await SaveScriptAsync(workDir, script);
    }
}

// Automated providers are "pollinations" and "cloudflare". Anything else
// (today: "manual") keeps the original human prompt-and-wait loop.
static bool IsAutomatedProvider(GenConfig cfg) =>
    cfg.SceneArtProvider.Equals("pollinations", StringComparison.OrdinalIgnoreCase) ||
    cfg.SceneArtProvider.Equals("cloudflare", StringComparison.OrdinalIgnoreCase);

// Generates ONE square source image per scene with the provider chain:
// primary per SceneArtProvider, then the other automated provider's
// square, then Cloudflare SDXL square. The square is the single source
// both orientations are derived from — portrait and landscape are centre
// cover-crops of it, so they are identical by construction. Each step
// returns immediately when its file is already on disk, so a resumed job
// never regenerates an image it already has.
static async Task<string> GenerateSquareArtAsync(
    GenConfig cfg, string prompt, string squareBase)
{
    var existing = ManualArtClient.ImageExtensions
        .Select(ext => squareBase + ext).FirstOrDefault(File.Exists);
    if (existing is not null) return existing;

    var primaryIsPollinations = cfg.SceneArtProvider.Equals(
        "pollinations", StringComparison.OrdinalIgnoreCase);
    var attempts = new List<Func<Task<string>>>
    {
        // Free pool first (2026-11-22): OVHcloud SDXL -> Pollinations
        // anonymous -> AI Horde -> Pexels stock. All three keyed providers
        // failed at once on 2026-11-21, so the pool is the primary path now;
        // the keyed providers below remain as backup.
        () => ArtPoolClient.GenerateSquareAsync(cfg, prompt, squareBase + ".jpg"),
        () => primaryIsPollinations
            ? PollinationsKeyedClient.GenerateSquareAsync(cfg, prompt, squareBase + ".jpg")
            : CloudflareArtClient.GenerateFluxSquareAsync(cfg, prompt, squareBase + ".jpg"),
        () => primaryIsPollinations
            ? CloudflareArtClient.GenerateFluxSquareAsync(cfg, prompt, squareBase + ".jpg")
            : PollinationsKeyedClient.GenerateSquareAsync(cfg, prompt, squareBase + ".jpg"),
        () => CloudflareArtClient.GenerateAsync(cfg, prompt, squareBase + ".png", 1024, 1024),
    };
    Exception? lastError = null;
    foreach (var attempt in attempts)
    {
        try
        {
            return await attempt();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Square art provider step failed ({ex.Message}), trying next provider...");
            lastError = ex;
        }
    }
    throw new Exception(
        $"All scene-art providers failed for {squareBase} (1024x1024 square).", lastError);
}

// Derives one orientation from a square source image with a cover
// crop — no blur-pad, no second generation. Returns immediately when the
// file is already on disk. verticalBias is passed through to
// CropScaleToExactAsync; the portrait derivation stays centred (0.5),
// where the full height is kept anyway. The landscape derivation no
// longer pre-crops (2026-11-13: the old top-biased 16:9 pre-crop cut
// full-body figures off at the waist) — the square itself is the
// landscape asset and Remotion contain-fits it over the blur-pad.
static async Task<string> DeriveArtAsync(
    GenConfig cfg, string sourcePath, string outputPath, int width, int height, double verticalBias = 0.5)
{
    if (ProcessHelper.AlreadyOnDisk(outputPath)) return outputPath;
    await VideoAssembler.CropScaleToExactAsync(cfg, sourcePath, outputPath, width, height, verticalBias);
    return outputPath;
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

    // No script.json yet but a partial checkpoint exists: the prep died
    // during script generation, so resume generation from the next act
    // instead of failing - the same path --prep --job takes.
    if (!File.Exists(Path.Combine(workDir, "script.json")) &&
        File.Exists(Path.Combine(workDir, "script-partial.json")))
    {
        Console.WriteLine("  No script.json yet - resuming script generation from the partial checkpoint.");
        return await RunPrepResumeAsync(workDir);
    }

    var script = await LoadScriptAsync(workDir);
    language = string.IsNullOrWhiteSpace(script.Language) ? language : script.Language;
    profile = ApplyAssetMapping(string.IsNullOrWhiteSpace(script.Profile) ? profile : ContentProfileRegistry.Get(script.Profile));

    if (script.Scenes.Count == 0)
        throw new InvalidOperationException($"{workDir} has a script.json but no scenes yet - nothing to resume. Re-run --prep.");

    Console.WriteLine(profile.UsesCharacterMascot
        ? $"Resuming \"{script.Title}\" — {script.Format} — {script.Scenes.Count} scenes, starring {script.CharacterName}"
        : $"Resuming \"{script.Title}\" — {script.Format} — {script.Scenes.Count} scenes, category {script.TopicCategory}");

    // Faceless profiles have no character image to resolve.
    var characterImage = !profile.UsesCharacterMascot ? ""
        : script.CharacterImagePath is { Length: > 0 } saved && File.Exists(saved)
        ? saved
        : File.Exists(Path.Combine(workDir, "character.png"))
            ? Path.Combine(workDir, "character.png")
            : await CharacterSource.DrawAsync(profile, cfg, script, workDir);
    script.CharacterImagePath = characterImage;
    await SaveScriptAsync(workDir, script);

    await RunTtsLoopAsync(workDir, script);

    var credits = 0;
    if (profile.UsesRemotionStills)
        await RunVisualLoopAsync(workDir, script, characterImage);
    else
        credits = await RunViduLoopAsync(workDir, script, characterImage);

    await SaveScriptAsync(workDir, script);

    Console.WriteLine();
    if (profile.UsesRemotionStills)
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

    var script = ScriptGenerator.FinalizeManualScript(profile, draft, formatOverride, language!);
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
    // Faceless profiles have no character to draw.
    var characterImage = !profile.UsesCharacterMascot ? ""
        : await CharacterSource.DrawAsync(profile, cfg, script, workDir);
    script.CharacterImagePath = characterImage;
    await SaveScriptAsync(workDir, script);

    Console.WriteLine(profile.UsesCharacterMascot
        ? $"Script: \"{script.Title}\" — {script.Format} — {script.Scenes.Count} scenes, starring {script.CharacterName}"
        : $"Script: \"{script.Title}\" — {script.Format} — {script.Scenes.Count} scenes, category {script.TopicCategory}");

    await RunTtsLoopAsync(workDir, script);

    var credits = 0;
    if (profile.UsesRemotionStills)
        await RunVisualLoopAsync(workDir, script, characterImage);
    else
        credits = await RunViduLoopAsync(workDir, script, characterImage);

    await SaveScriptAsync(workDir, script);

    Console.WriteLine();
    if (profile.UsesRemotionStills)
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
    profile = ApplyAssetMapping(string.IsNullOrWhiteSpace(script.Profile) ? profile : ContentProfileRegistry.Get(script.Profile));

    var formatDef = ScriptGenerator.PickFormat(profile, script.Format);
    var (rate, pitch) = ResolvePacing(profile, script, formatDef);
    var tts = TtsProviderFactory.Create(cfg, profile, language!);
    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];
        var audioPath = Path.Combine(workDir, $"scene{i:D2}.mp3");
        // Voice variations OFF (Rishi 2026-11-17): no per-scene mood styles -
        // see the prep call site. Plain narration, no styleOverride.
        await tts.SynthesizeAsync(scene.Narration, language!, audioPath, rate, pitch, null);
        scene.AudioPath = audioPath;

        var before = scene.DurationSeconds;
        scene.DurationSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, audioPath);
        Console.WriteLine($"TTS {i + 1}/{script.Scenes.Count} ({before:0.0}s -> {scene.DurationSeconds:0.0}s)");
        // Pace the calls (2026-11-21): Azure Free F0 throttles with HTTP 429
        // after ~20-35 rapid synth calls. A 2s gap plus the provider's retry
        // (AzureHelperTtsProvider) keeps a 77-scene re-voice from dying
        // partway through.
        if (i < script.Scenes.Count - 1) await Task.Delay(TimeSpan.FromSeconds(2));
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
    var outDir = Directory.CreateDirectory(Path.Combine(cfg.WorkDirectory, "dry-script")).FullName;
    VideoScript script;
    if (job is not null)
    {
        // --dry-script --job <dir>: resume generation from that job's partial
        // checkpoint (no TTS/art, no writes to the job dir) - the way to prove
        // the resume path works without spending anything on a real prep.
        var resumeDir = ResolveJobDirectory();
        var (partial, completedActs) = await LoadPartialScriptAsync(resumeDir);
        var totalActs = profile.UsesCharacterMascot ? 4 : 3;
        profile = ApplyAssetMapping(string.IsNullOrWhiteSpace(partial.Profile) ? profile : ContentProfileRegistry.Get(partial.Profile));
        var snapshotPath = Path.Combine(resumeDir, "trend-snapshot.txt");
        var trendContext = File.Exists(snapshotPath) ? await File.ReadAllTextAsync(snapshotPath) : null;
        var scriptProvider = ScriptProviderFactory.Create(cfg);
        var resumeLanguage = language != "en" ? language!
            : string.IsNullOrWhiteSpace(partial.Language) ? "en" : partial.Language;
        var formatOverrideForResume = string.IsNullOrWhiteSpace(partial.Format) ? formatOverride : partial.Format;
        Console.WriteLine($"Workspace: {resumeDir} (dry-script resume - the job dir is not modified)");
        Console.WriteLine(
            $"  Resuming \"{partial.Title}\" from act {completedActs}/{totalActs} " +
            $"({partial.Scenes.Count} scenes kept, category {partial.TopicCategory}, profile {profile.Id})");
        // Snapshot the partial's scene content BEFORE generation: GenerateAsync
        // appends to the same list object, so comparing against `partial`
        // after the call would compare the list to itself.
        var partialSceneContent = partial.Scenes
            .Select(s => (s.Narration, s.ImagePrompt, s.StockQuery))
            .ToList();
        script = await ScriptGenerator.GenerateAsync(profile, topic: null, resumeLanguage, scriptProvider, trendContext,
            character: null, avoidNames: null, formatOverride: formatOverrideForResume, longForm: true, avoidTopics: [],
            onActComplete: (s, act) => SavePartialScriptAsync(outDir, s, act, totalActs),
            topicCategory: partial.TopicCategory, newsItems: null,
            resumeFrom: new ScriptResume(partial, completedActs));
        // Verify the saved acts came back verbatim: the resumed script's
        // first N scenes must carry the same model-authored content as the
        // partial's. (SceneKind/IsSpeculative are excluded - the
        // post-generation normalization pass may legitimately adjust those,
        // exactly as a fresh full run would.)
        var kept = script.Scenes.Take(partialSceneContent.Count).ToList();
        var verbatim = kept.Count == partialSceneContent.Count &&
            kept.Zip(partialSceneContent).All(p =>
                p.First.Narration == p.Second.Narration &&
                p.First.ImagePrompt == p.Second.ImagePrompt &&
                p.First.StockQuery == p.Second.StockQuery);
        Console.WriteLine(
            $"  Resume check: first {partialSceneContent.Count} scenes " +
            $"{(verbatim ? "IDENTICAL to the partial (reused verbatim)" : "DIFFER - resume did not reuse the saved acts!")}; " +
            $"total scenes now {script.Scenes.Count} (was {partialSceneContent.Count})");
        if (!verbatim)
            throw new InvalidDataException("Resume verification failed - the resumed script did not reuse the partial's scenes verbatim.");
    }
    else
    {
        var trendReport = topic is null ? await TrendResearch.FetchReportAsync(profile, cfg) : null;
        var trendContext = trendReport?.Text;
        var scriptProvider = ScriptProviderFactory.Create(cfg);
        var totalActs = longForm ? (profile.UsesCharacterMascot ? 4 : 3) : 1;
        script = await ScriptGenerator.GenerateAsync(profile, topic, language!, scriptProvider, trendContext, null, null, formatOverride, longForm,
            onActComplete: (s, act) => SavePartialScriptAsync(outDir, s, act, totalActs),
            topicCategory: trendReport?.Category, newsItems: trendReport?.News);
    }
    // Full script generated: the timestamped save below supersedes the checkpoints.
    DeletePartialScriptFiles(outDir);

    Console.WriteLine($"Format: {script.Format}   Title: \"{script.Title}\"   IsLongForm: {script.IsLongForm}");
    if (profile.UsesCharacterMascot)
        Console.WriteLine($"Character: {script.CharacterName} - {script.CharacterDescription}");
    else
        Console.WriteLine($"Topic category: {script.TopicCategory}");
    Console.WriteLine($"MusicMood: {script.MusicMood}   NarrationStyle: {script.NarrationStyle}");
    Console.WriteLine($"Intro: {script.IntroText}");
    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var s = script.Scenes[i];
        var groupTag = script.IsLongForm ? $" [group {s.VisualGroup}]" : "";
        var kindTag = profile.UsesCharacterMascot ? "" : $" [{s.SceneKind}]";
        var visualTag = s.SceneKind == "visual" && s.VisualSpec is not null
            ? $" -> {s.VisualSpec.Kind} \"{s.VisualSpec.Title ?? s.VisualSpec.Text}\""
            : "";
        Console.WriteLine($"  Scene {i + 1} ({s.Narration.Length} chars){groupTag}{kindTag}{visualTag}: {s.Narration}");
    }
    if (script.IsLongForm)
    {
        var groups = script.Scenes.Select(s => s.VisualGroup).Distinct().Count();
        var totalChars = script.Scenes.Sum(s => s.Narration.Length);
        Console.WriteLine($"Long-form: {script.Scenes.Count} scenes, {groups} distinct visual groups, {totalChars} narration chars (~{totalChars / 5.0 / 140.0 * 60.0:0}s at 140wpm).");
    }
    Console.WriteLine($"Hashtags: {string.Join(" ", script.Tags.Where(t => t.StartsWith('#')))}");

    var outPath = Path.Combine(outDir, $"{script.Format}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(outPath, System.Text.Json.JsonSerializer.Serialize(script, jsonOptions));
    Console.WriteLine($"Saved -> {outPath}");

    return 0;
}

// Built-in checks with no API spend: verifies the JSON salvage repair
// (ScriptGenerator.SalvageTruncatedJson) against synthetic truncated
// payloads. Exit 0 when every case passes, 1 otherwise.
int RunSelfTest()
{
    var failures = 0;
    void Check(bool ok, string name)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name}");
        if (!ok) failures++;
    }

    // Case 1: a response cut off mid-scene inside the scenes array - the
    // exact failure mode from job-20261122-000432 ("Expected depth to be
    // zero ... Path: $.scenes[11]"). The 10 complete scenes must survive.
    var scenes = string.Join(",\n", Enumerable.Range(1, 12).Select(i =>
        $$"""{"narration": "Scene {{i}} narration here.", "imagePrompt": "Image {{i}}", "sceneKind": "art", "visualGroup": {{i}}}"""));
    var full = $$"""{"title": "Test", "description": "d", "tags": [], "scenes": [{{scenes}}]}""";
    // Truncate inside scene 11's narration (after "Scene 11 narr").
    var cutAt = full.IndexOf("Scene 11 narr", StringComparison.Ordinal) + "Scene 11 narr".Length;
    var truncated = full[..cutAt];
    var salvaged = ScriptGenerator.SalvageTruncatedJson(truncated);
    Check(salvaged is not null, "truncated mid-scene response salvages to parseable JSON");
    if (salvaged is not null)
    {
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(salvaged);
            var count = parsed is not null && parsed.TryGetValue("scenes", out var arr) ? arr.GetArrayLength() : -1;
            Check(count == 10, $"salvage keeps the 10 complete scenes (got {count})");
            Check(parsed is not null && parsed.TryGetValue("title", out var t) && t.GetString() == "Test",
                "salvage keeps the metadata head (title)");
        }
        catch (Exception ex)
        {
            Check(false, $"salvaged JSON parses ({ex.Message})");
        }
    }

    // Case 2: a complete, valid response passes through untouched.
    Check(ScriptGenerator.SalvageTruncatedJson(full) == full, "complete valid JSON is returned unchanged");

    // Case 3: garbage with no scenes array cannot be salvaged.
    Check(ScriptGenerator.SalvageTruncatedJson("not json at all") is null, "garbage with no scenes array returns null");
    Check(ScriptGenerator.SalvageTruncatedJson("""{"title": "x", "scenes": []}""") is null,
        "a scenes array with no complete scenes returns null");

    // Case 4: markdown fences around a truncated response are stripped first.
    var fenced = "```json\n" + truncated + "\n```";
    Check(ScriptGenerator.SalvageTruncatedJson(fenced) is not null, "markdown-fenced truncated response salvages");

    Console.WriteLine(failures == 0 ? "self-test: all checks passed" : $"self-test: {failures} check(s) FAILED");
    return failures == 0 ? 0 : 1;
}

// ------------------------------------------------------------ assemble ------

async Task<int> RunAssembleAsync()
{
    var workDir = ResolveJobDirectory();
    Console.WriteLine($"Workspace: {workDir}");

    var script = await LoadScriptAsync(workDir);
    language = string.IsNullOrWhiteSpace(script.Language) ? language : script.Language;
    profile = ApplyAssetMapping(string.IsNullOrWhiteSpace(script.Profile) ? profile : ContentProfileRegistry.Get(script.Profile));
    // Scores are computed at prep time, but jobs created before that change
    // (or hand-written scripts) carry 0s — recompute here so their sidecars
    // are not published with a misleading 0/100 hook/SEO score.
    if (script.HookScore == 0 && script.SeoScoreValue == 0 && script.Scenes.Count > 0)
    {
        script.SeoScoreValue = GiggleGarden.Shared.SeoScore.ScoreTitle(script.Title);
        script.HookScore = GiggleGarden.Shared.SeoScore.ScoreHook(script.Scenes[0].Narration);
        if (script.TitleVariants.Count == 0) script.TitleVariants = GiggleGarden.Shared.SeoScore.TitleVariants(script.Title);
        if (string.IsNullOrWhiteSpace(script.CommentPrompt))
            script.CommentPrompt = profile.UsesCharacterMascot
                ? $"Which part of {script.CharacterName}'s story surprised you most? Tell us in the comments."
                : "What do you think happens next? Tell us in the comments.";
    }
    var formatDef = ScriptGenerator.PickFormat(profile, script.Format);

    // The no-Vidu hybrid pipeline (Deliverable 6) resolves every scene's visual
    // synchronously during --prep - stock footage and manual art are both already on
    // disk by the time --assemble runs, so there is nothing async to poll or collect.
    // Only a profile still on Vidu needs this step.
    if (!profile.UsesRemotionStills)
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

    // Outro card/voiceover appended to every render. Profiles with branded
    // OutroLines/OutroSpoken (Trendline Now) use those; everyone else keeps the
    // legacy "LIKE, FOLLOW & SUBSCRIBE" + destination pair. The outro voiceover
    // uses the same pacing as the scenes (ResolvePacing), so the end card
    // sounds like the same narrator. AllowsContextScenes profiles additionally
    // fold a "watch the full video" line into the vertical short only, since
    // that render is a trimmed teaser for the landscape master.
    var (outroRate, outroPitch) = ResolvePacing(profile, script, formatDef);
    var baseOutroLines = profile.OutroLines ?? ["LIKE, FOLLOW & SUBSCRIBE", profile.OutroDestinationText];
    var baseOutroSpoken = profile.OutroSpoken
        ?? $"Like, follow, and subscribe on YouTube — {profile.OutroDestinationSpoken}!";
    var baseOutroAudio = await ResolveOutroAudioAsync(baseOutroSpoken, outroRate, outroPitch);
    var shortOutroLines = profile.AllowsContextScenes
        ? [.. baseOutroLines, "Watch the full video on YouTube"]
        : baseOutroLines;
    var shortOutroSpoken = profile.AllowsContextScenes
        ? $"{baseOutroSpoken} Watch the full video on YouTube."
        : baseOutroSpoken;
    var shortOutroAudio = profile.AllowsContextScenes
        ? await ResolveOutroAudioAsync(shortOutroSpoken, outroRate, outroPitch)
        : baseOutroAudio;

    // Animated intro sting (Trendline Now): fail fast here, before either
    // render, if the profile asks for one but the logo file is not where
    // ApplyAssetMapping put it - a missing logo must never silently drop the
    // intro from a finished video.
    var introSeconds = profile.IntroSeconds;
    string? introLogoPath = null;
    if (introSeconds > 0)
    {
        if (string.IsNullOrWhiteSpace(profile.IntroLogoPath) || !File.Exists(profile.IntroLogoPath))
            throw new InvalidOperationException(
                $"IntroSeconds is {introSeconds} but the intro logo \"{profile.IntroLogoPath}\" does not exist. " +
                "Copy the brand logo into the assets folder (see TrendlineNowProfile.IntroLogoPath).");
        introLogoPath = profile.IntroLogoPath;
    }

    // The outro card's rendered length (voiceover + trailing beat) and the
    // intro sting both count against the vertical cap - Reels reject anything
    // over 90s, so the scene budget is VerticalMaxSeconds minus both, not the
    // full cap. The outro runs after the last scene, the intro before the
    // first, so neither overlaps a scene.
    var shortOutroSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, shortOutroAudio) + 0.75;
    var shortSceneBudget = cfg.VerticalMaxSeconds - introSeconds - shortOutroSeconds;

    // Reels reject anything over 90s, so the vertical cut is trimmed to whole scenes
    // that fit the budget rather than shipping a render the platform will refuse.
    var shortScenes = new List<Scene>();
    var running = 0.0;
    foreach (var scene in script.Scenes)
    {
        // Budget in rendered seconds (narration + breathing room + post-narration
        // hold), so the trimmed teaser stays within VerticalMaxSeconds as rendered.
        var next = running + scene.DurationSeconds + 0.5 + VideoAssembler.PostNarrationHoldSeconds;
        if (shortScenes.Count > 0 && next > shortSceneBudget) break;
        shortScenes.Add(scene);
        running = next;
    }

    if (shortScenes.Count < script.Scenes.Count)
        Console.WriteLine($"Short: trimmed to {shortScenes.Count}/{script.Scenes.Count} scenes ({running:0}s scenes + {introSeconds:0.#}s intro + {shortOutroSeconds:0.#}s outro) for the {cfg.VerticalMaxSeconds}s cap.");

    var vertical = Path.Combine(outputDir, $"{slug}-{language}-short.mp4");
    if (profile.AllowsContextScenes)
    {
        await VideoAssembler.AssembleWithRemotionAsync(profile, cfg, shortScenes, workDir, vertical, 1080, 1920, script.MusicMood,
            outroLines: shortOutroLines, outroAudioPath: shortOutroAudio, isCalm: formatDef.IsCalm,
            introLogoPath: introLogoPath, introSeconds: introSeconds, outroHandles: profile.OutroHandles);
    }
    else if (profile.UsesRemotionStills)
    {
        // Stills pipeline, but the short is the whole video (not a teaser for the
        // landscape master), so it keeps the base outro, not the "watch the full
        // video" line the context-scene profiles add above.
        await VideoAssembler.AssembleWithRemotionAsync(profile, cfg, shortScenes, workDir, vertical, 1080, 1920, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio, isCalm: formatDef.IsCalm,
            introLogoPath: introLogoPath, introSeconds: introSeconds, outroHandles: profile.OutroHandles);
    }
    else
    {
        await VideoAssembler.AssembleFromClipsAsync(profile, cfg, shortScenes, language!, workDir, vertical, 1080, 1920, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio);
    }
    await VideoAssembler.NormalizeLoudnessAsync(cfg, vertical);
    await VideoAssembler.WriteSrtAsync(shortScenes, Path.ChangeExtension(vertical, ".srt"), introSeconds);
    Console.WriteLine($"Rendered: {vertical}");

    await new Sidecar
    {
        Title = Text.Fit($"{script.Title} #Shorts", 100),
        Description = $"{script.ReelsCaption}\n\n#Shorts",
        Tags = GiggleGarden.Shared.SeoScore.TrimTagsForYouTube([.. script.Tags.Take(12), "shorts"]),
        Approved = false,
        Language = language!,
        Aspect = "vertical",
        MadeForKids = profile.MadeForKids,
        Channel = profile.Id,
        Targets = [.. cfg.VerticalTargets],
        TitleVariants = script.TitleVariants,
        HookText = script.Scenes.Count > 0 ? script.Scenes[0].Narration : null,
        CommentPrompt = string.IsNullOrWhiteSpace(script.CommentPrompt) ? null : script.CommentPrompt,
        HookScore = script.HookScore,
        SeoScore = script.SeoScoreValue,
        LoudnessLUFS = -14,
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
    if (profile.UsesRemotionStills)
        await VideoAssembler.AssembleWithRemotionAsync(profile, cfg, script.Scenes, workDir, landscape, 1920, 1080, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio, isCalm: formatDef.IsCalm,
            introLogoPath: introLogoPath, introSeconds: introSeconds, outroHandles: profile.OutroHandles,
            // Curiosity brief (Rishi 2026-11-21, video #3 onward): plays before
            // the intro sting on the long-form only - the Short's opening scenes
            // already are its hook, so it carries no brief.
            briefScenes: script.BriefScenes);
    else
        await VideoAssembler.AssembleFromClipsAsync(profile, cfg, script.Scenes, language!, workDir, landscape, 1920, 1080, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio);
    await VideoAssembler.NormalizeLoudnessAsync(cfg, landscape);
    await VideoAssembler.WriteSrtAsync(script.Scenes, Path.ChangeExtension(landscape, ".srt"), introSeconds);
    Console.WriteLine($"Rendered: {landscape}");

    // Dedicated thumbnail (priority 7, free): prefer a MAJOR story picture
    // (Rishi 2026-11-21: video #1's scene-0 title-card thumbnail was wrong)
    // - the first story scene (index >= 1) with landscape art - over a frame
    // pulled from the finished render. A render frame carries burned-in
    // subtitles and a Ken Burns crop, so the face is smaller and text competes
    // with it; the raw landscape art is full-bleed, uncaptioned, and reads at
    // phone size. Fall back to scene 0, then to a render frame.
    string? thumbBase = null;
    for (int i = 1; i < script.Scenes.Count; i++)
    {
        if (script.Scenes[i].LandscapeImagePath is { } storyArt && File.Exists(storyArt))
        {
            thumbBase = storyArt;
            Console.WriteLine($"Thumbnail: using landscape art for scene {i} (a major story picture, not the title card).");
            break;
        }
    }
    if (thumbBase is null && script.Scenes.Count > 0 && script.Scenes[0].LandscapeImagePath is { } landscapeArt && File.Exists(landscapeArt))
    {
        thumbBase = landscapeArt;
        Console.WriteLine("Thumbnail: using landscape art for scene 0 (fallback - no story scene art).");
    }
    if (thumbBase is null)
    {
        thumbBase = Path.Combine(workDir, "thumbnail-frame.jpg");
        var grabAt = Math.Min(3.0, Math.Max(0.5, script.Scenes[0].DurationSeconds * 0.6));
        await VideoAssembler.ExtractThumbnailFrameAsync(cfg, landscape, grabAt, thumbBase);
    }
    await VideoAssembler.BuildThumbnailAsync(cfg, thumbBase, Path.ChangeExtension(landscape, ".thumb.jpg"));
    Console.WriteLine("Thumbnail generated (1280x720).");

    // Long-form chapters (priority 5): one chapter per visual group, with
    // cumulative timestamps from scene durations. Free text in the YouTube
    // description — no API, no extra render — and it measurably helps
    // long-form retention because viewers can jump to a beat.
    var chapters = script.IsLongForm ? BuildChapters(script.Scenes) : null;
    var landscapeDescription = chapters is { Count: > 0 }
        ? script.Description + "\n\nChapters:\n" + string.Join("\n", chapters)
        : script.Description;
    if (!string.IsNullOrWhiteSpace(script.CommentPrompt))
        landscapeDescription += "\n\n" + script.CommentPrompt;
    await new Sidecar
    {
        Title = script.Title,
        Description = landscapeDescription,
        Tags = GiggleGarden.Shared.SeoScore.TrimTagsForYouTube(script.Tags),
        Approved = false,
        Language = language!,
        Aspect = "landscape",
        MadeForKids = profile.MadeForKids,
        Channel = profile.Id,
        Targets = [.. cfg.LandscapeTargets],
        TitleVariants = script.TitleVariants,
        HookText = script.Scenes.Count > 0 ? script.Scenes[0].Narration : null,
        CommentPrompt = string.IsNullOrWhiteSpace(script.CommentPrompt) ? null : script.CommentPrompt,
        Chapters = chapters,
        ThumbnailPath = Path.ChangeExtension(landscape, ".thumb.jpg"),
        HookScore = script.HookScore,
        SeoScore = script.SeoScoreValue,
        LoudnessLUFS = -14,
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

// Per-act checkpoint for long-form scripts: after each act completes, the
// script-so-far is saved to script-partial.json with the act count in
// script-progress.json, so a failure in a later act (Gemini 402 at Act 3 on
// 2026-11-12) no longer loses the acts already generated. The partial is
// explicitly NOT script.json: --resume/--assemble must never treat an
// unfinished script as a complete video. Once GenerateAsync returns, the full
// script.json is saved by the caller and these files are deleted.
// Best-effort by design: a failed checkpoint must never fail generation.
async Task SavePartialScriptAsync(string dir, VideoScript script, int completedAct, int totalActs)
{
    try
    {
        var path = Path.Combine(dir, "script-partial.json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, System.Text.Json.JsonSerializer.Serialize(script, jsonOptions));
        File.Move(temp, path, overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(dir, "script-progress.json"),
            System.Text.Json.JsonSerializer.Serialize(new { completedActs = completedAct, totalActs }, jsonOptions));
        Console.WriteLine($"  Checkpoint: act {completedAct}/{totalActs} saved ({script.Scenes.Count} scenes) -> {path}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Warning: could not save act {completedAct} checkpoint: {ex.Message}");
    }
}

void DeletePartialScriptFiles(string dir)
{
    foreach (var name in new[] { "script-partial.json", "script-progress.json" })
    {
        try
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
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


// Linux port helper: re-roots a profile's hardcoded Windows asset folders
// under cfg.AssetsDirectory when set, otherwise returns it unchanged.
// Path.GetFileName does NOT split on backslashes on Linux, so a Windows
// path's last segment is split manually, otherwise the whole D:\... string
// would be appended as one folder name. Called at startup AND after every
// RunAssemble/RunResume/RunRetts profile reassignment from script.Profile,
// which would otherwise lose the startup mapping.
List<string> BuildChapters(IReadOnlyList<Scene> scenes)
{
    var chapters = new List<string>();
    var cursor = TimeSpan.Zero;
    int? lastGroup = null;
    foreach (var scene in scenes)
    {
        if (scene.VisualGroup != lastGroup)
        {
            var label = scene.Narration.Length > 40 ? scene.Narration[..40].TrimEnd() + "…" : scene.Narration;
            chapters.Add($"{(int)cursor.TotalMinutes}:{cursor.Seconds:00} - {label}");
            lastGroup = scene.VisualGroup;
        }
        cursor += TimeSpan.FromSeconds(scene.DurationSeconds + 0.5);
    }
    return chapters;
}

ContentProfile ApplyAssetMapping(ContentProfile p)
{
    if (string.IsNullOrWhiteSpace(cfg.AssetsDirectory)) return p;
    static string LastSegment(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
    return p with
    {
        BackgroundMusicPath = Path.Combine(cfg.AssetsDirectory, LastSegment(p.BackgroundMusicPath)),
        CharacterPoolPath = string.IsNullOrWhiteSpace(p.CharacterPoolPath)
            ? p.CharacterPoolPath
            : Path.Combine(cfg.AssetsDirectory, LastSegment(p.CharacterPoolPath)),
        // The intro logo is a brand asset, not a Windows-origin path - but the
        // same last-segment re-root keeps it working wherever the assets folder
        // lives, and keeps the profile's value a plain filename.
        IntroLogoPath = string.IsNullOrWhiteSpace(p.IntroLogoPath)
            ? p.IntroLogoPath
            : Path.Combine(cfg.AssetsDirectory, LastSegment(p.IntroLogoPath)),
    };
}
