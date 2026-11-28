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
using System.Security.Cryptography;
using System.Text.Json;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables("GIGGLE_")
    .Build();

var cfg = config.Get<GenConfig>() ?? throw new InvalidOperationException("appsettings.json invalid");

string? topic = null, language = "en", job = null, profileId = null, scriptFile = null;
string? reportOut = null, checkMp4 = null, outDir = null, slug = null;
int? sourceScene = null;
double anchorXOverride = -1, anchorYOverride = -1;
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
        case "--rewrite-gate": mode = "rewrite-gate"; break;
        case "--write-props": mode = "write-props"; break;
        case "--check-quality": mode = "check-quality"; break;
        case "--repair-images": mode = "repair-images"; break;
        case "--make-thumbnails": mode = "make-thumbnails"; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--slug" when i + 1 < args.Length: slug = args[++i]; break;
        case "--source-scene" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out var sceneIdx))
            {
                Console.Error.WriteLine("--source-scene needs an integer scene index.");
                return 1;
            }
            sourceScene = sceneIdx;
            break;
        case "--anchor-x" when i + 1 < args.Length:
            if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ax))
            {
                Console.Error.WriteLine("--anchor-x needs a number 0..1.");
                return 1;
            }
            anchorXOverride = ax;
            break;
        case "--anchor-y" when i + 1 < args.Length:
            if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ay))
            {
                Console.Error.WriteLine("--anchor-y needs a number 0..1.");
                return 1;
            }
            anchorYOverride = ay;
            break;
        case "--report-out" when i + 1 < args.Length: reportOut = args[++i]; break;
        case "--mp4" when i + 1 < args.Length: checkMp4 = args[++i]; break;
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
            Console.WriteLine("Usage: VideoGen --prep [--topic \"...\"] [--language en|hi|pa] [--profile trendlinenow] [--format <id>] [--long]   (valid --format ids depend on --profile; see that profile's Formats catalog. --long generates a 12-15 minute YouTube documentary cut instead of the usual ~8-scene short; only supported by profiles with AllowsContextScenes.)");
            Console.WriteLine("       VideoGen --prep --job job-YYYYMMDD-HHMMSS [--long] [--profile ...]   resume a --prep that died during script generation, continuing from the next act after the job's script-partial.json checkpoint (acts already saved are reused, never regenerated)");
            Console.WriteLine("       VideoGen --assemble [--job job-YYYYMMDD-HHMMSS]");
            Console.WriteLine("       VideoGen --resume   [--job job-YYYYMMDD-HHMMSS]   continue an interrupted --prep, skipping any scene whose audio/art is already on disk (falls back to the script-partial.json checkpoint when no script.json exists yet)");
            Console.WriteLine("       VideoGen --manual --script-file <path> --format <id> [--language en|hi|pa] [--profile ...]   start a new job from a hand-written script.json-shaped file instead of calling the AI script provider");
            Console.WriteLine("       VideoGen --retts    [--job job-YYYYMMDD-HHMMSS]   re-voice an existing job");
            Console.WriteLine("       VideoGen --test-tts [--language en|hi|pa]   synthesize a sample line on every reachable provider, no Vidu/Claude spend");
            Console.WriteLine("       VideoGen --dry-script [--topic \"...\"] [--language en|hi|pa] [--format ...] [--long] [--job job-YYYYMMDD-HHMMSS]   one script-generation call only, no TTS/character-draw/Vidu spend (--job resumes generation from that job's script-partial.json checkpoint, for testing the resume path)");
            Console.WriteLine("       VideoGen --self-test   run the built-in checks (JSON salvage repair) with no API spend");
            Console.WriteLine("       VideoGen --rewrite-gate --job job-YYYYMMDD-HHMMSS   apply the fact-gate rewrite to a job whose prep died at the old hard gate (script-partial.json, no script.json yet) and save script.json for review — no TTS, visuals, render, or upload");
            Console.WriteLine("       VideoGen --write-props --job job-YYYYMMDD-HHMMSS   write both remotion-props-*.json files without rendering (laptop-render prep: the laptop needs the props before it can render; refuses non-Remotion profiles)");
            Console.WriteLine("       VideoGen --check-quality --job job-YYYYMMDD-HHMMSS [--report-out <path>] [--mp4 <path>]   read-only quality check of a job's script, images and music (plus the final mix when --mp4 is given); writes the report to <job>/quality-report.json unless --report-out says otherwise; exits 1 when any gate errors");
            Console.WriteLine("       VideoGen --make-thumbnails --job job-YYYYMMDD-HHMMSS --out <dir> [--slug <slug>] [--source-scene N] [--anchor-x 0..1] [--anchor-y 0..1]   build the 3 purpose-built thumbnail variants for a job (fix #3) without rendering — live-video re-thumbnailing; nothing is set on YouTube");
            Console.WriteLine("       VideoGen --repair-images --job job-YYYYMMDD-HHMMSS [--report-out <path>]   selective image repair: re-checks every image and regenerates ONLY the failing scenes, with an improved prompt (never the same prompt again); the original art is kept as sceneNN-art-square.jpg.bad until the repair passes; exits 1 when errors remain");
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
        $"[{DateTime.UtcNow:HH:mm:ss}Z] FATAL: {PrepContext.Prefix}received SIGTERM - killed externally " +
        "(driver/VM shutdown?). Last checkpoint is in script-partial.json; re-run --prep --job to resume.");
    ctx.Cancel = false;
});
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
{
    Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}Z] FATAL: {PrepContext.Prefix}received SIGINT - interrupted.");
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
        "rewrite-gate" => await RunRewriteGateAsync(),
        "write-props" => await RunWritePropsAsync(),
        "check-quality" => await RunCheckQualityAsync(),
        "repair-images" => await RunRepairImagesAsync(),
        "make-thumbnails" => await RunMakeThumbnailsAsync(),
        _ => await RunAssembleAsync(),
    };
}
catch (Exception ex)
{
    // L21: the old line had only the exception type + message — no job
    // id, no stack, no next action. The job comes from the ambient prep
    // identity (set at every prep entry point) or the --job argument.
    var fatalJob = PrepContext.Current?.JobId ?? (job is not null ? Path.GetFileName(job) : null);
    var firstFrame = ex.StackTrace?.Split('\n').Select(s => s.Trim())
        .FirstOrDefault(s => s.StartsWith("at ", StringComparison.Ordinal));
    var fatalNext = fatalJob is not null
        ? $"re-run --prep --job {fatalJob} to resume from the checkpoint; full log in that job's prep.log"
        : "fix the cause and re-run --prep; the log is in the job folder's prep.log";
    Console.Error.WriteLine($"FATAL: {(fatalJob is null ? "" : $"[{fatalJob}] ")}[{ex.GetType().Name}] {ex.Message}" +
        $"{(firstFrame is null ? "" : $" | {firstFrame}")} — {fatalNext}");
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
        // L10: install the per-job log before anything that can throw,
        // so even the early resume checks leave a record in prep.log.
        PrepLog.Install(resumeDir);
        // L21: from here on every downstream prep log line carries [jobId].
        PrepContext.Current = new PrepIdentity(Path.GetFileName(resumeDir), topic);
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
    // L10: mirror every Console line into <job>/prep.log from here on.
    PrepLog.Install(workDir);
    // L21: from here on every downstream prep log line carries [jobId].
    PrepContext.Current = new PrepIdentity(Path.GetFileName(workDir), topic);
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
    // L71: the trend fetch ran unwrapped — a throw killed the prep before
    // generation and the only record was the bare top-level FATAL. Continue
    // without trend context; the empty-facts warning below says so.
    TrendResearch.TrendReport? trendReport = null;
    if (topic is null)
    {
        try
        {
            trendReport = await TrendResearch.FetchReportAsync(profile, cfg, PrepContext.Current);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {PrepContext.Prefix}[prep] trend fetch failed: {ex.GetType().Name}: {ex.Message} — continuing without trend context");
        }
    }
    var trendContext = trendReport?.Text;
    // L18: record which grounding sources failed, for the degraded-prep
    // summary in FinishPrepAsync (a prep that completes with its sources
    // down must not look identical to a fully-grounded one).
    var forcedArticleEmpty = false;
    var forcedWikipediaEmpty = false;
    // Forced topic (2026-11-23): FetchReportAsync is skipped when --topic
    // is given, so a forced prep got NO headlines and NO article text —
    // only the single Wikipedia summary below (null for COP31), leaving
    // the model to invent numbers (video #4: 42 invented claims from a
    // headlines-only context). Fetch the article text for the forced topic
    // directly, mirroring FetchReportAsync's top-story fetch. Best-effort:
    // a missing helper or no matches just means a thinner context, never
    // a failed prep.
    if (topic is not null)
    {
        var articleSection = ArticleClient.BuildArticleSection(await ArticleClient.FetchForTopicAsync(topic, cfg, context: PrepContext.Current));
        forcedArticleEmpty = articleSection.Length == 0;
        if (articleSection.Length > 0)
            trendContext = (trendContext ?? "") + "\n" + articleSection;
    }
    var analyticsDir = Path.Combine(cfg.WorkDirectory, "analytics");
    // L8: surface corrupt log entries in the prep log instead of skipping
    // them silently.
    // L71: the brief load ran unwrapped — continue without it.
    string analyticsBrief;
    try
    {
        analyticsBrief = GiggleGarden.Shared.AnalyticsLog.Summarize(await GiggleGarden.Shared.AnalyticsLog.LoadAsync(analyticsDir, msg => Console.WriteLine($"  [analytics] {msg}")));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {PrepContext.Prefix}[prep] analytics brief load failed: {ex.GetType().Name}: {ex.Message} — continuing without it");
        analyticsBrief = "";
    }
    if (analyticsBrief.Length > 0 && !analyticsBrief.StartsWith("No published", StringComparison.Ordinal))
        trendContext = (trendContext ?? "") + "\nWhat worked on this channel before:\n" + analyticsBrief;
    // Wikipedia fact context (priority 4) when a topic is given: free, no key.
    // Backstory (2026-11-23): the old single unguarded FetchSummaryAsync
    // took the FIRST search result blindly — for COP31 that was "2026",
    // a calendar-year article, not the conference — so Act 1 had no
    // backstory years and the model invented them (2000, 2020, 2019,
    // 2027). Fetch the guarded plural backgrounds instead (up to 3
    // related articles, each entity-guarded against the topic): the
    // conference/parent/framework articles carry the real years the
    // backstory needs. Best-effort: no background just means a thinner
    // context, never a failed prep.
    if (topic is not null)
    {
        try
        {
            var forcedBackgrounds = await TrendResearch.FetchBackgroundForPickedTopicAsync(topic, null, PrepContext.Current);
            forcedWikipediaEmpty = forcedBackgrounds.WikipediaBackgrounds.Count == 0;
            if (forcedBackgrounds.WikipediaBackgrounds.Count > 0)
            {
                var wikiBlock = new System.Text.StringBuilder();
                wikiBlock.AppendLine("Wikipedia context for this topic (use real facts from here, not invented ones):");
                foreach (var bg in forcedBackgrounds.WikipediaBackgrounds)
                    wikiBlock.AppendLine($"{bg.Title}: {bg.Extract}");
                trendContext = (trendContext ?? "") + "\n" + wikiBlock.ToString().Trim();
            }
        }
        catch (Exception ex)
        {
            forcedWikipediaEmpty = true;
            Console.WriteLine($"  [wikipedia] forced-topic background fetch failed ({ex.Message}) — continuing without it.");
        }
    }
    // Trend snapshot per job (priority 3) + analytics brief (priority 1):
    // both are saved into the job folder so the script, tags, and later
    // learning all reference the same source, and so a job can be audited
    // without re-querying the APIs. Written AFTER all context assembly
    // (2026-11-23): the old write sat before the analytics/Wikipedia
    // appends, and for a forced topic trendContext was null here so NO
    // snapshot was written at all — the resume path and the fact gate then
    // had nothing to read, and a gate-failed resume sailed through to TTS
    // unchecked. A news prep must always leave a context behind.
    if (!string.IsNullOrWhiteSpace(trendContext))
        await File.WriteAllTextAsync(Path.Combine(workDir, "trend-snapshot.txt"), trendContext);
    // L71: the topic-history load ran unwrapped — continue without it.
    List<string> recentTopics = [];
    if (topic is null)
    {
        try
        {
            recentTopics = await TopicHistory.RecentAsync(profile.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {PrepContext.Prefix}[prep] topic history load failed: {ex.GetType().Name}: {ex.Message} — continuing without recent topics");
        }
    }
    var scriptProvider = ScriptProviderFactory.Create(cfg, PrepContext.Current);
    // Total acts for the checkpoint: news long-form has 3 acts, mascot
    // long-form has 4. (Older checkpoints recorded 4 for the news path -
    // the resume path derives the total from the profile instead.)
    var totalActs = longForm ? (profile.UsesCharacterMascot ? 4 : 3) : 1;
    VideoScript script;
    // L71: a headlines-only thin context used to sail into generation with
    // no warning (the only backstop was the no-context FATAL in
    // FinishPrepAsync, which fires only when the check context is empty).
    WarnOnEmptyFactsBlock(trendContext, profile.CategoryStyles is not null);
    try
    {
        script = await ScriptGenerator.GenerateAsync(profile, topic, language!, scriptProvider, trendContext, pooled, recentNames, formatOverride, longForm, recentTopics,
            onActComplete: (s, act) => SavePartialScriptAsync(workDir, s, act, totalActs),
            topicCategory: trendReport?.Category, newsItems: trendReport?.News, context: PrepContext.Current);
    }
    catch (Exception ex) when (ScriptGenerator.IsFloorFailure(ex))
    {
        // B17/A5 (fix sequence #2 ITEM 4): the floor failed, so the partial
        // checkpoint holds the same thin acts verbatim — resuming from it
        // would fail the same floor again (3 wasted cycles, then
        // escalation, no self-heal). Delete the partial so the driver's
        // next attempt starts a FRESH prep instead. Any other failure
        // (provider outage mid-act, ...) keeps the partial: those SHOULD
        // resume from the checkpoint.
        Console.WriteLine($"  [floor-failure] {ex.Message} - deleting script-partial.json so the next attempt starts fresh.");
        DeletePartialScriptFiles(workDir);
        throw;
    }
    var degradedSources = PrepDegradation.Collect(trendReport, forcedArticleEmpty, forcedWikipediaEmpty, pickedBackgroundFailed: false);
    return await FinishPrepAsync(workDir, script, pooled, addToPool, trendContext, trendReport?.News, DateTime.Today, degradedSources);
}

// Shared tail of --prep (fresh and resumed): once GenerateAsync has returned
// a full script, persist it, add the intro bumper, draw the character, and
// run the TTS/visual loops. Called by RunPrepAsync and RunPrepResumeAsync
// alike, so a resumed prep finishes identically to a fresh one.
async Task<int> FinishPrepAsync(string workDir, VideoScript script, CharacterBrief? pooled, bool addToPool,
    string? trendContext = null, IReadOnlyList<TrendResearch.NewsItem>? newsItems = null, DateTime prepDate = default,
    List<string>? degradedSources = null)
{
    // L21: every caller sets the ambient identity, but a future caller
    // that forgets still gets the job folder name on its log lines.
    PrepContext.Current ??= new PrepIdentity(Path.GetFileName(workDir));
    if (prepDate == default) prepDate = DateTime.Today;

    // Fact-traceability gate (review #2 §E, fix sequence #2 ITEM 2): video
    // #3 shipped invented facts because the model picked a story the trend
    // context had no facts for and nothing checked the finished script.
    // Runs FIRST, before script.json is saved or any TTS/art work starts.
    // News profiles only (CategoryStyles set): mascot/mythology scripts
    // are invented by design and have no trend context to trace against.
    //
    // Fact-gate rewrite (Rishi 2026-11-23): findings no longer FATAL the
    // prep — FactGateRewrite rewrites each untraceable claim into
    // attributed speculation (or drops it, for named persons), re-checks,
    // and only then does the prep proceed to save. A script that still
    // fails the re-check after the rewrite fails loudly (safety net), and
    // a script whose drops pushed it below the long-form floor gets its
    // missing scenes regenerated (RepairAfterGateDropsAsync) instead of
    // being dropped. The no-context guard below is NOT part of the
    // rewrite: a news prep with zero source material still fails loudly.
    // L18: the picked-topic background fetch is the one source failure
    // that happens inside FinishPrepAsync itself — record it for the
    // degraded-prep summary at the end instead of only logging it.
    var pickedBackgroundFailed = false;
    if (profile.CategoryStyles is not null)
    {
        if (trendContext is null)
        {
            var snapshotPath = Path.Combine(workDir, "trend-snapshot.txt");
            if (File.Exists(snapshotPath)) trendContext = await File.ReadAllTextAsync(snapshotPath);
        }
        string? pickedBackground = null;
        try
        {
            var picked = await TrendResearch.FetchBackgroundForPickedTopicAsync(script.Title, newsItems, PrepContext.Current);
            if (!string.IsNullOrWhiteSpace(TrendResearch.BuildPickedTopicContext(picked)))
            {
                pickedBackground = TrendResearch.BuildPickedTopicContext(picked);
                await File.WriteAllTextAsync(Path.Combine(workDir, "picked-topic-background.txt"), pickedBackground);
            }
        }
        catch (Exception ex)
        {
            pickedBackgroundFailed = true;
            Console.WriteLine($"  [fact-check] WARNING: picked-topic background fetch failed ({ex.Message}) — checking against the trend headlines only.");
        }
        var checkContext = FactTraceability.BuildCheckContext(trendContext, pickedBackground);
        if (string.IsNullOrWhiteSpace(checkContext))
        {
            // Gate-bypass guard (2026-11-23): the old code logged a warning
            // and SKIPPED the check when no trend context existed — a path
            // meant for hand-written/manual scripts. But this block only
            // runs for NEWS profiles (CategoryStyles set), and a news prep
            // with zero source material is guaranteed fabricated: the model
            // has nothing to write from but its own memory. A gate-failed
            // forced-topic resume exploited exactly this hole (no
            // trend-snapshot.txt, no Wikipedia background) and reached TTS
            // unchecked. A news script with no facts to check against must
            // fail loudly here, never sail through to TTS/render. Non-news
            // profiles never reach this block (CategoryStyles null), and
            // manual scripts never call FinishPrepAsync, so the old skip
            // behaviour for them is untouched.
            var noContextReport = "Fact-traceability check FAILED: no trend context available for this news prep — " +
                "no trend-snapshot.txt, no picked-topic background, and no article text. " +
                "A news script written with zero source material cannot be verified, so this job was NOT marked complete and will not render. " +
                "Fix the trend context (re-run the prep so the article/Wikipedia fetch runs) and re-run prep; do not resume from a partial that has no snapshot.";
            Console.WriteLine($"  [fact-check] FATAL {noContextReport}");
            await File.WriteAllTextAsync(Path.Combine(workDir, "fact-check-failures.txt"), noContextReport + "\n");
            throw new InvalidOperationException(noContextReport);
        }
        else
        {
            var findings = FactTraceability.Check(script, checkContext, prepDate);
            if (findings.Count > 0)
            {
                // Findings no longer FATAL the prep (fact-gate rewrite,
                // Rishi 2026-11-23): rewrite each untraceable claim into
                // attributed speculation, drop named-person claims, and
                // re-check. The prep proceeds on the rewritten script.
                foreach (var f in findings)
                    Console.WriteLine($"  [fact-check] {f}");
                // L72: Rewrite() logs each action itself now (with the job
                // id) — the caller loop that did it is gone, not duplicated.
                var result = FactGateRewrite.Rewrite(script, findings, checkContext, prepDate, PrepContext.Current);
                var rewriteReport = BuildFactGateRewriteReport(script.Title, prepDate, findings, result);
                if (result.RemainingFindings.Count > 0)
                {
                    // Safety net: the rewrite could not make the script
                    // gate-clean. Fail loudly rather than ship it.
                    await File.WriteAllTextAsync(Path.Combine(workDir, "fact-check-failures.txt"), rewriteReport);
                    throw new InvalidOperationException(rewriteReport);
                }
                // Dropped scenes can push a long-form script below the
                // floors — regenerate the missing scenes instead of
                // dropping the script.
                if (script.IsLongForm && result.BelowFloor)
                    await RepairAfterGateDropsAsync(script, checkContext, prepDate, trendContext);
                await File.WriteAllTextAsync(Path.Combine(workDir, "fact-check-rewritten.txt"), rewriteReport);
                var staleFailures = Path.Combine(workDir, "fact-check-failures.txt");
                if (File.Exists(staleFailures)) File.Delete(staleFailures);
                Console.WriteLine($"  {FactGateRewrite.OkSummary(result, script.Scenes.Count)}");
            }
            else
                Console.WriteLine($"  [fact-check] OK: all numbers, quotes, entities and chart sources in \"{script.Title}\" trace to the supplied trend context.");
        }
    }

    // (Full script generated: script.json, saved below, supersedes the
    // checkpoints — the partial files are deleted after the quality gate
    // below, so a gate failure still leaves the checkpoint to resume from.)
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
        var wikiFigure = await WikipediaClient.FetchSummaryAsync(script.CharacterName, PrepContext.Current);
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
    DeduplicateConsecutiveScenes(script);

    // Quality gate (Rishi 2026-11-23): script check — hook, questions,
    // speculation, structure, pace, title. Runs after the fact-gate
    // rewrite, before script.json is saved
    // and before any TTS/art spend. Hard errors throw like the fact
    // gate; warnings are logged and recorded in quality-report.json.
    // News profiles only (same condition as the fact gate); mascot /
    // mythology scripts are invented by design. The partial checkpoint
    // is deleted only after the gate passes, so a gate failure can be
    // resumed from script-partial.json instead of regenerating.
    if (profile.CategoryStyles is not null)
    {
        // Quality gate (Rishi 2026-11-23): the script must pass the
        // script gate AND the craft gate (correctness / scene fit /
        // retention + SEO, Rishi 2026-11-23 ~23:45) before any TTS/art
        // spend. Both gates' findings land in the same report section.
        var qualityFindings = QualityGate.CheckScript(script, topic, outroLines: profile.OutroLines);
        qualityFindings.AddRange(QualityGate.CheckScriptCraft(script, topic));
        foreach (var qf in qualityFindings)
            Console.WriteLine($"  [quality:script] {(qf.Severity == QualitySeverity.Error ? "ERROR" : "warn")} [{qf.Check}]{(qf.Scene is not null ? " " + qf.Scene : "")} {qf.Message}");
        QualityGate.AppendReport(Path.Combine(workDir, "quality-report.json"), Path.GetFileName(workDir), "script", qualityFindings);
        Console.WriteLine($"  {QualityGate.OneLineSummary("script", qualityFindings)}");
        QualityGate.ThrowOnErrors(qualityFindings, "script", workDir);
    }
    DeletePartialScriptFiles(workDir);

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

    await SaveScriptAsync(workDir, script);

    // Image batch gate at prep end (Ronaldo 2026-11-25: near-identical
    // statue photos scenes 5+6 and a byte-identical clip scenes 26+31
    // reached --assemble because nothing checked visuals at prep end —
    // the per-file check at generation time never compares scenes
    // against each other). Runs the same batch check --assemble runs
    // (prompt bans, per-file checks, exact + near duplicates, provider
    // mix, stock-subject); hard errors throw, with the --repair-images
    // pointer for art scenes (it regenerates the failing scene) and a
    // hand-fix note for stock clips (it skips context scenes). Remotion
    // profiles only: Vidu clips land at --assemble, so the check there
    // is their backstop.
    if (profile.UsesRemotionStills)
    {
        var imageFindings = QualityGate.CheckImagesBatch(workDir, script);
        foreach (var qf in imageFindings)
            Console.WriteLine($"  [quality:images] {(qf.Severity == QualitySeverity.Error ? "ERROR" : "warn")} [{qf.Check}]{(qf.Scene is not null ? " " + qf.Scene : "")} {qf.Message}");
        QualityGate.AppendReport(Path.Combine(workDir, "quality-report.json"), Path.GetFileName(workDir), "images", imageFindings);
        Console.WriteLine($"  {QualityGate.OneLineSummary("images", imageFindings)}");
        if (imageFindings.Any(f => f.Severity == QualitySeverity.Error))
            Console.WriteLine("  Run VideoGen --repair-images --job " + Path.GetFileName(workDir) + " to regenerate the failing art scenes (stock clips need a hand-swap), then re-run --prep or --assemble.");
        QualityGate.ThrowOnErrors(imageFindings, "images", workDir);
    }

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

    // L18: one aggregate outcome line — a prep that completes on degraded
    // sources must not look identical to a fully-grounded one. The list was
    // collected upstream (RunPrepAsync) or, for a resumed prep that does
    // not re-fetch, re-read from the original run's prep-warnings.txt.
    // Exit stays 0 (see PrepDegradation): the prep did not fail, its
    // inputs did — the line + file are the signal, not a failure code.
    var degraded = degradedSources is null
        ? await PrepDegradation.LoadExistingAsync(workDir)
        : new List<string>(degradedSources);
    if (pickedBackgroundFailed && !degraded.Contains(PrepDegradation.PickedBackground))
        degraded.Add(PrepDegradation.PickedBackground);
    await PrepDegradation.FinishAsync(workDir, degraded);
    return 0;
}

// Human-readable record of a fact-gate rewrite: the original findings,
// every rewrite action, and before/after narration for each affected
// scene (scene numbers as the gate reported them). Written to
// fact-check-rewritten.txt whenever a rewrite runs.
string BuildFactGateRewriteReport(string title, DateTime prepDate, List<string> findings, FactGateRewrite.RewriteResult result)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Fact-traceability check for \"{title}\" (prep {prepDate:yyyy-MM-dd}): {findings.Count} untraceable claim(s) found — rewritten, not dropped (fact-gate rewrite, Rishi 2026-11-23).");
    sb.AppendLine("Original findings:");
    foreach (var f in findings) sb.AppendLine($"  {f}");
    sb.AppendLine("Rewrite actions:");
    foreach (var a in result.Actions) sb.AppendLine($"  {a}");
    sb.AppendLine("Before / after (scene numbers as the gate reported them):");
    foreach (var (n, before, after) in result.Changes)
    {
        sb.AppendLine($"  scene {n} BEFORE: {before}");
        sb.AppendLine($"  scene {n} AFTER:  {after}");
    }
    sb.AppendLine(result.RemainingFindings.Count == 0
        ? $"Re-check clean: {result.SceneCount} scenes, {result.TotalChars} chars."
        : $"Re-check STILL FAILING ({result.RemainingFindings.Count} finding(s)):\n  {string.Join("\n  ", result.RemainingFindings)}");
    return sb.ToString();
}

// Floor repair after a gate rewrite (Rishi 2026-11-23): dropping scenes
// can push a long-form script below the 50-scene / 5,500-char floors.
// Regenerate the missing scenes through the existing RepairThinActAsync
// path (a continuation call asking only for the missing scenes, as act 3),
// exactly as a thin act is repaired during generation — never by
// re-running the whole prep. The new scenes are fresh model output, so
// they are normalised (speculation safety net, scene kinds, visual
// accents) and gated+rewritten too before the floors are re-validated.
// Throws loudly when the repair cannot reach the floor: the "never drop"
// rule covers gate findings, not a script that cannot reach the floor.
async Task RepairAfterGateDropsAsync(VideoScript script, string checkContext, DateTime prepDate, string? trendContext, int introScenes = 0)
{
    // Floors count STORY scenes: in FinishPrepAsync the intro bumper is
    // not inserted yet (introScenes 0), while --rewrite-gate repairs after
    // the intro is in place (introScenes 1) — the intro must not count
    // toward the 50-scene floor.
    var storyCount = script.Scenes.Count - introScenes;
    var totalChars = script.Scenes.Skip(introScenes).Sum(s => s.Narration.Length);
    var neededByScenes = ScriptGenerator.LongFormMinScenes - storyCount;
    var neededByChars = totalChars < ScriptGenerator.LongFormHardFailChars
        ? (int)Math.Ceiling((ScriptGenerator.LongFormHardFailChars - totalChars) / 150.0) : 0;
    var needed = Math.Max(neededByScenes, neededByChars);
    if (needed <= 0) return;
    Console.WriteLine($"  [fact-check] rewrite dropped scenes below the floor ({storyCount} scenes, {totalChars} chars) - regenerating {needed} replacement scene(s) ...");
    var formatDef = ScriptGenerator.PickFormat(profile, string.IsNullOrWhiteSpace(script.Format) ? null : script.Format);
    var (contentRules, _, _, _, _, _, longFormContentRules) = formatDef.Instructions;
    var effectiveContentRules = longFormContentRules ?? contentRules;
    var style = TopicCategoryStyles.Get(string.IsNullOrWhiteSpace(script.TopicCategory) ? "general" : script.TopicCategory);
    var langName = Text.LanguageName(string.IsNullOrWhiteSpace(script.Language) ? "en" : language!);
    var repairProvider = ScriptProviderFactory.Create(cfg);
    await ScriptGenerator.RepairThinActAsync(script.Scenes, script.Scenes.Count + needed, "fact-gate rewrite repair",
        async (missing, nextGroup) =>
        {
            // The count line in the continuation prompt is chosen by
            // whether actScenesSoFar is empty: empty asks for a FULL act
            // (18-22 scenes), non-empty asks for exactly `missing` more.
            // Passing the last kept scene makes this a true repair call,
            // not a fresh act. The chunk is still truncated to `missing`
            // as a safety net: a model that ignores the count must not
            // overshoot the floor target (a 1-scene repair once returned
            // 20 scenes and bloated the script to 70).
            var actSoFar = script.Scenes.Count == 0 ? [] : script.Scenes[^1..];
            var chunk = await ScriptGenerator.GenerateNewsContinuationActScenesAsync(
                profile, formatDef, effectiveContentRules, style, langName, repairProvider, script, actSoFar,
                3, "what's happening now, why it matters, and what happens next",
                trendContext, missing, nextGroup);
            return chunk.Take(missing).ToList();
        });
    ScriptGenerator.NormalizeNewsScenes(script.Scenes, style);
    var postRepair = FactTraceability.Check(script, checkContext, prepDate);
    if (postRepair.Count > 0)
    {
        // L72: Rewrite() logs each action itself (tagged "rewrite
        // (repair)" here) — no caller loop.
        var repairResult = FactGateRewrite.Rewrite(script, postRepair, checkContext, prepDate, PrepContext.Current, "rewrite (repair)");
        if (repairResult.RemainingFindings.Count > 0)
        {
            var report = BuildFactGateRewriteReport(script.Title, prepDate, postRepair, repairResult);
            throw new InvalidOperationException(
                $"{PrepContext.Prefix}Fact-gate rewrite of the repaired scenes still fails the re-check:\n{report}");
        }
    }
    ScriptGenerator.ValidateLongFormFloors(script.Scenes.Count - introScenes, script.Scenes.Skip(introScenes).Sum(s => s.Narration.Length), PrepContext.Current);
    Console.WriteLine($"  [fact-check] floor repaired: {script.Scenes.Count - introScenes} scenes, {script.Scenes.Skip(introScenes).Sum(s => s.Narration.Length)} chars.");
}

// One-off: apply the fact-gate rewrite to a job whose prep completed all
// acts but died at the old hard gate (script-partial.json exists, no
// script.json yet), and save script.json for review — WITHOUT running TTS,
// visuals, render, or upload. The check context comes from the files the
// original prep saved (trend-snapshot.txt + picked-topic-background.txt):
// no network fetch, so the gate judges the script against exactly what
// the model was given. The saved script.json is exactly what a completed
// prep would have saved at this point (intro bumper, dedup), so a later
// --resume works normally. The partial is kept as
// the pre-rewrite checkpoint. Rishi reviews script.json before any render.
async Task<int> RunRewriteGateAsync()
{
    if (job is null)
        throw new ArgumentException("--rewrite-gate requires --job <job-...>. See --help.");
    var workDir = ResolveJobDirectory();
    Console.WriteLine($"Workspace: {workDir}");
    if (File.Exists(Path.Combine(workDir, "script.json")))
        throw new InvalidOperationException(
            $"{workDir} already has a script.json - nothing to rewrite. Run --resume --job {Path.GetFileName(workDir)} instead.");
    var (partial, completedActs) = await LoadPartialScriptAsync(workDir);
    profile = ApplyAssetMapping(string.IsNullOrWhiteSpace(partial.Profile) ? profile : ContentProfileRegistry.Get(partial.Profile));
    if (profile.CategoryStyles is null)
        throw new InvalidOperationException(
            $"--rewrite-gate only applies to news profiles (CategoryStyles set); \"{profile.Id}\" is not one.");
    Console.WriteLine($"  Rewriting gate findings for \"{partial.Title}\" ({partial.Scenes.Count} scenes, {completedActs} act(s) complete).");

    var snapshotPath = Path.Combine(workDir, "trend-snapshot.txt");
    if (!File.Exists(snapshotPath))
        throw new InvalidOperationException(
            $"No trend-snapshot.txt in {workDir} - no source material to check the script against. Re-run --prep instead.");
    var trendContext = await File.ReadAllTextAsync(snapshotPath);
    var pickedPath = Path.Combine(workDir, "picked-topic-background.txt");
    var pickedBackground = File.Exists(pickedPath) ? await File.ReadAllTextAsync(pickedPath) : null;
    var checkContext = FactTraceability.BuildCheckContext(trendContext, pickedBackground);
    if (string.IsNullOrWhiteSpace(checkContext))
        throw new InvalidOperationException(
            "Fact-traceability check FAILED: no trend context available for this news prep — " +
            "no trend-snapshot.txt, no picked-topic background, and no article text. " +
            "A news script written with zero source material cannot be verified, so this job was NOT marked complete and will not render.");
    var prepDate = DateTime.Today;
    var findings = FactTraceability.Check(partial, checkContext, prepDate);
    FactGateRewrite.RewriteResult? result = null;
    if (findings.Count > 0)
    {
        foreach (var f in findings)
            Console.WriteLine($"  [fact-check] {f}");
        // L72: Rewrite() logs each action itself now (with the job id).
        result = FactGateRewrite.Rewrite(partial, findings, checkContext, prepDate, PrepContext.Current);
        if (result.RemainingFindings.Count > 0)
        {
            var failReport = BuildFactGateRewriteReport(partial.Title, prepDate, findings, result);
            await File.WriteAllTextAsync(Path.Combine(workDir, "fact-check-failures.txt"), failReport);
            throw new InvalidOperationException(failReport);
        }
        if (partial.IsLongForm && result.BelowFloor)
            Console.WriteLine($"  [fact-check] rewrite left the script below the long-form floor ({result.SceneCount} scenes, {result.TotalChars} chars) — regenerating the missing scene(s) after the intro/dedup step below.");
        Console.WriteLine($"  {FactGateRewrite.OkSummary(result, result.SceneCount)}");
    }
    else
    {
        Console.WriteLine("  [fact-check] OK: no untraceable claims — saving the script as-is.");
    }

    InsertIntroScene(partial);
    DeduplicateConsecutiveScenes(partial);
    // Floors are checked on the FINAL shape: the intro-dedup can drop a
    // story scene (COP31's first story scene repeated the intro line), so
    // a script that cleared the floor after the rewrite can still end up
    // below it. Repair here, through the same path FinishPrepAsync uses —
    // never by re-running the whole prep. No-op when already above floor.
    if (partial.IsLongForm)
        await RepairAfterGateDropsAsync(partial, checkContext, prepDate, trendContext, introScenes: 1);
    await SaveScriptAsync(workDir, partial);
    if (result is not null)
    {
        await File.WriteAllTextAsync(Path.Combine(workDir, "fact-check-rewritten.txt"),
            BuildFactGateRewriteReport(partial.Title, prepDate, findings, result));
        var staleFailures = Path.Combine(workDir, "fact-check-failures.txt");
        if (File.Exists(staleFailures)) File.Delete(staleFailures);
        Console.WriteLine();
        Console.WriteLine("Before / after (scene numbers as the gate reported them):");
        foreach (var (n, before, after) in result.Changes)
        {
            Console.WriteLine($"  scene {n} BEFORE: {before}");
            Console.WriteLine($"  scene {n} AFTER:  {after}");
        }
    }
    Console.WriteLine($"Saved script.json ({partial.Scenes.Count} scenes incl. intro) — review before any render. No TTS, visuals, render, or upload ran.");
    return 0;
}

// Loads a job's script-partial.json: the script as of the last completed
// act, and how many acts that was (stored INSIDE the partial as
// CompletedActs, so the count and the scenes are one atomic write - see
// SavePartialScriptAsync). Throws a clear error when there is nothing
// usable to resume from.
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
    var completedActs = script.CompletedActs;
    // Backward compat: checkpoints written before CompletedActs lived in the
    // partial carried the count in a separate script-progress.json.
    if (completedActs <= 0)
    {
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
    }
    if (completedActs <= 0)
        throw new InvalidDataException(
            $"{partialPath} has no completedActs - cannot tell how many acts are done.");
    // Cross-check: every completed act must have contributed at least one
    // scene. A partial whose scene count cannot cover its act count is torn
    // or hand-edited - resume would regenerate an act that is already there
    // and duplicate it, so fail here instead.
    if (script.Scenes.Count < completedActs)
        throw new InvalidDataException(
            $"{partialPath} claims {completedActs} completed acts but only has {script.Scenes.Count} scene(s) - " +
            "the checkpoint is inconsistent. Delete it and re-run --prep.");
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
    // L21: a resumed prep's downstream lines carry this job's id.
    PrepContext.Current ??= new PrepIdentity(Path.GetFileName(workDir), topic);
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
    var scriptProvider = ScriptProviderFactory.Create(cfg, PrepContext.Current);
    VideoScript script;
    // L71: same empty-facts warning as the fresh prep — a resumed prep
    // reads its facts from the snapshot, which may itself be empty.
    WarnOnEmptyFactsBlock(trendContext, profile.CategoryStyles is not null);
    try
    {
        script = await ScriptGenerator.GenerateAsync(profile, topic: null, resumeLanguage, scriptProvider, trendContext,
            character: null, avoidNames: null, formatOverride: formatOverrideForResume, longForm: true, avoidTopics: [],
            onActComplete: (s, act) => SavePartialScriptAsync(workDir, s, act, totalActs),
            topicCategory: partial.TopicCategory, newsItems: null,
            resumeFrom: new ScriptResume(partial, completedActs), context: PrepContext.Current);
    }
    catch (Exception ex) when (ScriptGenerator.IsFloorFailure(ex))
    {
        // B17/A5 (fix sequence #2 ITEM 4): same as RunPrepAsync — the
        // resumed partial's saved acts are the thin ones that just failed
        // the floor, so keeping the checkpoint would re-feed the failure.
        // Delete it so the driver's next attempt starts a FRESH prep.
        Console.WriteLine($"  [floor-failure] {ex.Message} - deleting script-partial.json so the next attempt starts fresh.");
        DeletePartialScriptFiles(workDir);
        throw;
    }
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

// Duplicate-line guard (Rishi 2026-11-22): video #2 and #3 both shipped with the
// opening line twice - the intro scene (IntroText) and the first story scene
// said the same thing in near-identical words. The model writes the hook as
// IntroText AND as scene 1, and InsertIntroScene puts them back-to-back. This
// runs right after InsertIntroScene and drops any scene whose narration is a
// near-duplicate of the previous scene's (same words, reordered or with the
// channel tag added/removed). Dropping is safe: a restatement adds no new
// information, and the next scene already carries the story forward. The
// check is word-overlap (Jaccard) >= 0.8 after normalising case/punctuation
// and stripping the "this is trendline now" tag, plus a containment check for
// a short line fully inside a longer one.
void DeduplicateConsecutiveScenes(VideoScript script)
{
    static string Normalize(string text)
    {
        var t = text.ToLowerInvariant();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"this is trendline now\.?", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[^a-z0-9\s]", " ");
        return System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
    }

    static double Jaccard(string a, string b)
    {
        var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (setA.Count == 0 || setB.Count == 0) return 0;
        var inter = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return (double)inter / union;
    }

    for (var i = 1; i < script.Scenes.Count; i++)
    {
        var prev = Normalize(script.Scenes[i - 1].Narration);
        var cur = Normalize(script.Scenes[i].Narration);
        if (prev.Length == 0 || cur.Length == 0) continue;
        var contained = prev.Contains(cur) || cur.Contains(prev);
        if (contained || Jaccard(prev, cur) >= 0.8)
        {
            Console.WriteLine(
                $"  Dedup: dropping scene {i} - narration repeats scene {i - 1} " +
                $"(\"{script.Scenes[i].Narration[..Math.Min(60, script.Scenes[i].Narration.Length)]}...\")");
            script.Scenes.RemoveAt(i);
            i--;
        }
    }
}

// Synthesizes to a temp file and moves it into place, so a kill
// mid-synthesis never leaves a truncated-but-parseable MP3 that resume
// trusts as complete (ffprobe happily durations a clipped file, and it used
// to ship as clipped narration). The TTS providers write directly to
// whatever path they are given - the Azure/GCP helpers included - so the
// atomicity lives here, at the call sites, not in each provider.
async Task SynthesizeAtomicAsync(ITtsProvider tts, string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null, string? sceneLabel = null)
{
    var tmp = outputPath + ".tmp";
    try
    {
        await tts.SynthesizeAsync(text, language, tmp, rate, pitch, styleOverride, sceneLabel);
        File.Move(tmp, outputPath, overwrite: true);
    }
    finally
    {
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
    }
}

// A truncated MP3 is still parseable, so "file exists" is not proof the
// audio is complete. Sanity-check the duration against the narration
// length: at ~170 wpm (+10% rate) a narration of N words takes ~N/2.83s,
// so anything under 40% of a conservative 150-wpm estimate (N/2.5s) is
// treated as truncated and re-synthesized. Deliberately loose - it only
// catches gross truncation, never legitimate delivery variation.
bool IsPlausibleAudio(string narration, double durationSeconds)
{
    if (durationSeconds <= 0) return false;
    var words = narration.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
    if (words == 0) return durationSeconds > 0;
    return durationSeconds >= 0.4 * (words / 2.5);
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
            if (IsPlausibleAudio(scene.Narration, scene.DurationSeconds))
            {
                Console.WriteLine($"TTS {i + 1}/{script.Scenes.Count}: already on disk ({scene.DurationSeconds:0.0}s)");
                continue;
            }
            Console.WriteLine($"TTS {i + 1}/{script.Scenes.Count}: on-disk audio is implausibly short " +
                $"({scene.DurationSeconds:0.0}s for {scene.Narration.Length} chars) - re-synthesizing.");
        }

        // Voice variations OFF (Rishi 2026-11-17): no per-scene mood styles for
        // either channel - the express-as variations sound poor. Narration is
        // plain (no styleOverride); the profile's fixed style is null too, so
        // the voice stays consistent across every scene.
        // L42: the "{i+1}/{n}" scene label threads into the TTS providers so
        // their log lines name the scene (job id via PrepContext.Prefix).
        await SynthesizeAtomicAsync(tts, scene.Narration, language!, audioPath, rate, pitch, null, $"{i + 1}/{script.Scenes.Count}");
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

    // The cache key includes rate + pitch: the outro text is identical every
    // video, so without them a pacing change silently reuses an outro
    // synthesized at the old rate (video #4 would have kept a -10% outro
    // after the +10% fix). The voice is pinned by profile.Id, which is
    // already in the name. Old files (without the pacing segment) are
    // simply orphaned, never reused.
    static string SanitizePacing(string v) =>
        new(v.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    var key = new string(spokenText.ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    var audioPath = Path.Combine(outroAudioDir, $"{profile.Id}-{language}-{SanitizePacing(rate)}-{SanitizePacing(pitch)}-{key}.mp3");
    if (File.Exists(audioPath))
    {
        // A truncated cache file would be trusted forever (the exists check
        // returns early), so verify it is plausible before reusing it.
        var cachedDuration = await VideoAssembler.GetAudioDurationAsync(cfg, audioPath);
        if (IsPlausibleAudio(spokenText, cachedDuration)) return audioPath;
        Console.WriteLine($"Outro audio cache is implausibly short ({cachedDuration:0.0}s) - re-synthesizing.");
    }

    var tts = TtsProviderFactory.Create(cfg, profile, language!);
    await SynthesizeAtomicAsync(tts, spokenText, language!, audioPath, rate, pitch);
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

    // One fresh visual per scene (review 2026-11-22, fix sequence #2 ITEM 3):
    // video #3 shipped 16 VisualGroups for 40 scenes, so one image held the
    // screen for up to 42.6s. The loop used to reuse the previous scene's
    // resolved image/clip whenever the group matched - that reuse is gone.
    // Every scene resolves its own visual from its own ImagePrompt /
    // StockQuery, in both orientations (both are derived from the one
    // resolved visual), so the picture changes with every scene. The
    // distinctness guard below is the backstop: if a scene would still show
    // the previous scene's visual (a hand-edited script pointing two scenes
    // at one file, or Pexels returning the same best clip for two adjacent
    // context scenes), it resolves a different one instead and logs it - a
    // scene never ships the previous scene's image.
    string? lastImagePath = null;
    string? lastClipPath = null;
    string? lastLandscapeImagePath = null;
    string? lastStockHash = null;

    // Per-job Pexels seen-list (Ronaldo 2026-11-25: the stock-video path
    // had no dedupe — scenes 26+31 got the same clip). Loaded once,
    // appended to as context scenes resolve, and consulted for the
    // previous three scenes' video ids on every download.
    var pexelsUsedPath = Path.Combine(workDir, "pexels-video-used.json");
    var pexelsUsed = PexelsSeenList.Load(pexelsUsedPath);

    // Records a stock download's provenance on the scene (persisted in
    // script.json) and in the per-job manifest (audit + seen-list).
    void RecordStockDownload(Scene scene, int index, StockDownload dl)
    {
        scene.PexelsVideoId = dl.VideoId;
        scene.PexelsPhotoId = dl.PhotoId;
        scene.PexelsUrl = dl.PageUrl;
        scene.PexelsAlt = dl.Alt;
        if ((dl.VideoId ?? dl.PhotoId) is { } id)
        {
            var entry = new PexelsUsedEntry(dl.Kind, id, index, scene.StockQuery ?? "", dl.PageUrl);
            pexelsUsed.Add(entry);
            PexelsSeenList.Append(pexelsUsedPath, entry);
        }
    }

    for (var i = 0; i < script.Scenes.Count; i++)
    {
        var scene = script.Scenes[i];
        var label = $"{i + 1}/{script.Scenes.Count}";
        var artBase = Path.Combine(workDir, $"scene{i:D2}-art");
        var stockBase = Path.Combine(workDir, $"scene{i:D2}-stock");

        try
        {
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
            // No image at all: Remotion draws the chart/table/callout from
            // the spec. Recorded so the props show the scene was never an
            // art or stock visual (B11).
            scene.ArtSource = "remotion-visual";
            Console.WriteLine($"Visual {label}: {scene.VisualSpec.Kind} \"{scene.VisualSpec.Title ?? scene.VisualSpec.Text}\"");
        }
        else if (scene.SceneKind == "context")
        {
            var existing = File.Exists(stockBase + ".mp4") ? stockBase + ".mp4"
                : File.Exists(stockBase + ".jpg") ? stockBase + ".jpg" : null;

            if (existing is not null)
            {
                if (Path.GetExtension(existing).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    scene.ClipPath = existing;
                else
                    scene.ImagePath = existing;
                // A sceneNN-stock.* file is only ever written by a Pexels
                // download (this loop or a resumed run of it) — a real
                // photo/clip, not generated art (B11).
                scene.ArtSource = "pexels-stock";
                Console.WriteLine($"Stock {label}: already on disk -> {Path.GetFileName(existing)}");
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
                //
                // Seen-list (Ronaldo 2026-11-25): the video ids used by
                // the previous three scenes are excluded, so the same
                // clip can never come back within < 4 scenes (Rishi's
                // reuse rule). The detailed download also returns the
                // provenance, recorded on the scene and in the manifest.
                var stockExclude = PexelsSeenList.ExcludedIds(pexelsUsed, i);
                var downloaded = await stock.DownloadBestMatchDetailedAsync(scene.StockQuery!, stockBase, ImageClient.Orientation.Landscape, stockExclude)
                    ?? await stock.DownloadBestMatchDetailedAsync(scene.StockQuery!, stockBase, ImageClient.Orientation.Portrait, stockExclude)
                    ?? throw new Exception(
                        $"Scene {label}: Pexels had no match for \"{scene.StockQuery}\". Broaden the stockQuery " +
                        $"and re-run --prep, or hand-place a file at {stockBase}.jpg or {stockBase}.mp4 and re-run --assemble.");

                if (Path.GetExtension(downloaded.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    scene.ClipPath = downloaded.Path;
                else
                    scene.ImagePath = downloaded.Path;
                RecordStockDownload(scene, i, downloaded);

                // A Pexels download is a real photo/clip, not generated art
                // — record it so review can tell (B11).
                scene.ArtSource = "pexels-stock";
                Console.WriteLine($"Stock {label}: \"{scene.StockQuery}\" -> {Path.GetFileName(downloaded.Path)}" +
                    (downloaded.VideoId is not null ? $" (Pexels video {downloaded.VideoId})" : downloaded.PhotoId is not null ? $" (Pexels photo {downloaded.PhotoId})" : ""));
            }
        }
        else
        {
            var existing = ManualArtClient.ImageExtensions.Select(ext => artBase + ext).FirstOrDefault(File.Exists);

            if (existing is not null)
            {
                scene.ImagePath = existing;
                Console.WriteLine($"Art {label}: already on disk -> {Path.GetFileName(existing)}");
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
                var (squarePath, artSource, squareProvenance) = await GenerateSquareArtAsync(cfg, squarePrompt, artBase + "-square", i, scene.ImagePrompt,
                    script.Title, script.Tags, scene.Narration);
                if (artSource is not null) scene.ArtSource = artSource;
                RecordSquareProvenance(scene, squareProvenance);
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
                // Hand-placed art (the manual provider loop), not pool art.
                scene.ArtSource = "manual";

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

        // Distinctness guard (ITEM 3): a scene must never show the previous
        // scene's visual. Art scenes collide only when two scenes resolve to
        // the same file (a hand-edited script); stock scenes collide when
        // Pexels returns the same best clip for two adjacent scenes with the
        // same query - same footage under different per-scene filenames - so
        // compare downloaded bytes, not paths. On a collision, resolve a
        // different visual and log it. "visual" scenes draw their own spec
        // and never enter this guard.
        if (scene.SceneKind == "art" && lastImagePath is not null
            && (string.Equals(scene.ImagePath, lastImagePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(scene.LandscapeImagePath, lastLandscapeImagePath, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"Art {label}: same visual as the previous scene ({Path.GetFileName(lastImagePath)}) - generating a fresh variant instead.");
            var variantPrompt = ImageClient.BuildPrompt(
                scene.ImagePrompt + " (different angle and composition from the previous scene)",
                profile.CharacterStyle, profile.CharacterPortraitStyleSuffix,
                ImageClient.Orientation.Square, matchReference: profile.UsesCharacterMascot);
            var (variantSquare, variantSource, variantProvenance) = await GenerateSquareArtAsync(cfg, variantPrompt, artBase + "-square-variant", i, scene.ImagePrompt,
                script.Title, script.Tags, scene.Narration);
            if (variantSource is not null) scene.ArtSource = variantSource;
            RecordSquareProvenance(scene, variantProvenance);
            scene.ImagePath = await DeriveArtAsync(cfg, variantSquare, artBase + "-variant.png", 1080, 1920);
            scene.LandscapeImagePath = variantSquare;
        }
        else if (scene.SceneKind == "context" && lastStockHash is not null)
        {
            var stockFile = scene.ClipPath ?? scene.ImagePath;
            if (stockFile is not null && File.Exists(stockFile)
                && string.Equals(StockFileHash(stockFile), lastStockHash, StringComparison.Ordinal))
            {
                Console.WriteLine($"Stock {label}: same visual as the previous scene (\"{scene.StockQuery}\") - trying a different clip instead.");
                var alt = await stock.DownloadBestMatchDetailedAsync(scene.StockQuery!, stockBase + "-alt", ImageClient.Orientation.Portrait, PexelsSeenList.ExcludedIds(pexelsUsed, i));
                if (alt is not null && !string.Equals(StockFileHash(alt.Path), lastStockHash, StringComparison.Ordinal))
                {
                    if (Path.GetExtension(alt.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        scene.ClipPath = alt.Path;
                        scene.ImagePath = null;
                    }
                    else
                    {
                        scene.ImagePath = alt.Path;
                        scene.ClipPath = null;
                    }
                    // The scene's provenance now describes the alt, not
                    // the rejected first download (the manifest keeps
                    // both entries — the rejected clip stays in the
                    // seen-list so it cannot come back within < 4 scenes
                    // either).
                    RecordStockDownload(scene, i, alt);
                    scene.LandscapeImagePath = null;
                    Console.WriteLine($"Stock {label}: different clip -> {Path.GetFileName(alt.Path)}");
                }
                else
                {
                    // No different clip available: generate an editorial
                    // illustration from the stock query instead of showing
                    // the previous scene's footage again.
                    Console.WriteLine($"Stock {label}: no different clip available - generating an illustration variant instead.");
                    var (variantSquare, variantSource, variantProvenance) = await GenerateSquareArtAsync(cfg,
                        ImageClient.BuildPrompt($"Editorial news illustration of {scene.StockQuery}",
                            profile.CharacterStyle, profile.CharacterPortraitStyleSuffix,
                            ImageClient.Orientation.Square, matchReference: profile.UsesCharacterMascot),
                        artBase + "-square-variant", i, $"Editorial news illustration of {scene.StockQuery}",
                        script.Title, script.Tags, scene.Narration);
                    if (variantSource is not null) scene.ArtSource = variantSource;
                    RecordSquareProvenance(scene, variantProvenance);
                    scene.ClipPath = null;
                    scene.LandscapeClipPath = null;
                    scene.ImagePath = await DeriveArtAsync(cfg, variantSquare, artBase + "-variant.png", 1080, 1920);
                    scene.LandscapeImagePath = variantSquare;
                }
            }
        }

        lastImagePath = scene.ImagePath;
        lastClipPath = scene.ClipPath;
        lastLandscapeImagePath = scene.LandscapeImagePath;
        lastStockHash = scene.SceneKind == "context"
            && (scene.ClipPath ?? scene.ImagePath) is { } resolved && File.Exists(resolved)
            ? StockFileHash(resolved)
            : null;

        await SaveScriptAsync(workDir, script);
        }
        catch (Exception ex)
        {
            // FLUX quota exhaustion (image pool, 2026-11-24) is NOT a
            // per-scene failure: FLUX is out for the day and the
            // fallbacks could not cover this scene, so every remaining
            // scene would fail the same way. Stop the prep here instead
            // of placeholdering the whole video — the [FATAL] marker
            // above tells the driver to back off until 00:00 UTC, and
            // --resume picks up the art already on disk. The exception
            // escapes only when the scene ended art-less; a
            // distinctness-variant failure on a scene whose main visual
            // already resolved keeps the normal B12 flag-and-continue.
            if (ex is FluxQuotaExhaustedException && !HasResolvedVisual(scene))
            {
                Console.WriteLine($"  [FATAL] scene {label} has no art and FLUX quota is exhausted — stopping the prep (FLUX_QUOTA_EXHAUSTED, resume after 00:00 UTC).");
                throw;
            }
            // B12 (fix sequence #2 ITEM 4): one scene's visual failing must
            // never kill the whole prep — hours of script/TTS/art work
            // would be discarded for one bad stockQuery or one provider
            // outage. Flag the scene, record it for review, give it a
            // plain placeholder so assemble + prep_complete still have a
            // real visual file, and continue with the next scene. The scene
            // still gets narration/TTS (that loop already ran); only its
            // visual is missing. A scene that already resolved a visual
            // before failing (e.g. the distinctness variant failed) keeps
            // it — only a scene with no visual at all gets the placeholder.
            var reason = Text.Tail(ex.Message.Replace('\n', ' ').Replace('\r', ' '), 300);
            FlagVisualFailure(workDir, script, i, reason);
            try
            {
                if (!HasResolvedVisual(scene))
                {
                    var placeholder = await VideoAssembler.CreatePlaceholderImageAsync(cfg, artBase + "-placeholder.png");
                    scene.ImagePath = placeholder;
                    scene.LandscapeImagePath = placeholder;
                }
            }
            catch (Exception placeholderEx)
            {
                Console.WriteLine($"  [visual] WARNING: placeholder also failed for scene {label} ({Text.Tail(placeholderEx.Message, 200)}) - assemble will reuse the previous scene's asset.");
            }
            lastImagePath = scene.ImagePath;
            lastClipPath = scene.ClipPath;
            lastLandscapeImagePath = scene.LandscapeImagePath;
            lastStockHash = null;
            await SaveScriptAsync(workDir, script);
        }
    }

    // Review summary (B11): a stock photo substituted by the pool's last
    // resort — or a context scene's Pexels clip — is a real photo, not
    // generated art. Name the scenes here so the prep log says so plainly
    // instead of the substitution being visible only in script.json.
    var stockScenes = script.Scenes
        .Select((s, idx) => (Scene: s, Index: idx + 1))
        .Where(x => string.Equals(x.Scene.ArtSource, "pexels-stock", StringComparison.Ordinal))
        .Select(x => x.Index.ToString())
        .ToList();
    if (stockScenes.Count > 0)
        Console.WriteLine($"  [visual] {stockScenes.Count} scene(s) used Pexels stock (real photo/clip, not AI-generated art): scenes {string.Join(", ", stockScenes)} — see ArtSource in script.json.");
}

// Flags one scene's visual as failed (B12): marks the scene in memory,
// appends a line to the job's visual-failures.txt so the failure is
// visible at review time, and logs it. Never throws: a failure to record
// must not become a second failure mode.
void FlagVisualFailure(string workDir, VideoScript script, int sceneIndex, string reason)
{
    var scene = script.Scenes[sceneIndex];
    scene.VisualMissing = true;
    scene.VisualMissingReason = reason;
    var label = $"{sceneIndex + 1}/{script.Scenes.Count}";
    Console.WriteLine($"  [visual] FAILED scene {label}: {reason} - skipping visual, narration/TTS continue (flagged in visual-failures.txt).");
    try
    {
        File.AppendAllText(Path.Combine(workDir, "visual-failures.txt"),
            $"scene {sceneIndex + 1}: {reason}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [visual] WARNING: could not write visual-failures.txt ({ex.Message}).");
    }
}

// A scene counts as visually resolved when any of its visual files is
// actually on disk — a path left over from a failed step does not count.
static bool HasResolvedVisual(Scene s) =>
    (s.ImagePath is { } ip && File.Exists(ip))
    || (s.ClipPath is { } cp && File.Exists(cp))
    || (s.LandscapeImagePath is { } li && File.Exists(li))
    || (s.LandscapeClipPath is { } lc && File.Exists(lc));

// Automated providers are "pollinations" and "cloudflare". Anything else
// (today: "manual") keeps the original human prompt-and-wait loop.
static bool IsAutomatedProvider(GenConfig cfg) =>
    cfg.SceneArtProvider.Equals("pollinations", StringComparison.OrdinalIgnoreCase) ||
    cfg.SceneArtProvider.Equals("cloudflare", StringComparison.OrdinalIgnoreCase);

// SHA-256 hex of a file, for the visual-loop distinctness guard (ITEM 3):
// two adjacent context scenes can download the same Pexels clip under
// different per-scene filenames, so only bytes prove sameness.
static string StockFileHash(string path) =>
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

// Generates ONE square source image per scene with the rebuilt pool
// chain (2026-11-24 bake-off — see ImagePool.cs + hidden_files/
// image-pool.md): Wikimedia Commons real photo FIRST, but only when the
// scene names a ranked star (Rishi 2026-11-24 ~23:14 — a real photo of
// the actual person beats AI art) → Cloudflare FLUX (governor-gated on
// the shared 10k neuron/day bucket) → Pexels stock photo (topic/entity
// query, per-job photo dedupe) → OVHcloud SDXL filler → Pollinations
// keyed / Z.ai CogView / Leonardo (dormant until their keys exist). The square
// is the single source both orientations are derived from — portrait
// and landscape are centre cover-crops of it, so they are identical by
// construction. Each step returns immediately when its file is already
// on disk, so a resumed job never regenerates an image it already has.
// Returns the path plus the ArtSource of the step that produced it
// (B11: "pexels-stock" when the pool fell back to a real Pexels photo,
// keyed-provider labels otherwise); null when the file was already on
// disk and the source is unknown from the file alone — the caller then
// keeps the scene's saved ArtSource.
//
// Quota boundary: when FLUX's daily quota is out, the chain fills the
// scene from Pexels + OVH. Only when a scene still has no art after
// every leg does the prep fail — loudly, with FluxQuotaExhaustedException
// (marker FLUX_QUOTA_EXHAUSTED + "resume after 00:00 UTC"), so the
// driver backs off instead of burning retries, and --resume picks up
// the art already on disk. The discarded legs (Pollinations anonymous,
// AI Horde) are never attempted, and Cloudflare SDXL is gone entirely.
// Records a pool leg's square-crop provenance on the scene, so the
// batch quality gate can re-check the crop at prep end (see
// QualityGate.CheckSquareCrop). Null provenance (native-square
// generation, already-on-disk file) leaves the fields untouched.
static void RecordSquareProvenance(Scene scene, ArtProvenance? provenance)
{
    if (provenance is null) return;
    scene.ArtSourceWidth = provenance.RawWidth;
    scene.ArtSourceHeight = provenance.RawHeight;
    scene.ArtCropAnchor = provenance.VerticalBias <= 0.0 ? "top" : "centre";
}

static async Task<(string Path, string? ArtSource, ArtProvenance? Provenance)> GenerateSquareArtAsync(
    GenConfig cfg, string prompt, string squareBase, int sceneIndex = -1, string? rawPrompt = null,
    string? scriptTitle = null, IReadOnlyList<string>? scriptTags = null, string? narration = null)
{
    // Quality gate (video #4): the prompt that reaches the model must
    // never ask for rendered text — free models cannot spell, so a
    // title card / tweet / headline request comes back as gibberish.
    // Sanitize here (and in ArtPoolClient, the other funnel) so a text
    // request is stripped or the prompt falls back to clean editorial
    // art, and every prompt carries the "no text" suffix.
    var (cleanPrompt, removedTerms) = QualityGate.SanitizeImagePrompt(prompt);
    if (removedTerms.Count > 0)
        Console.WriteLine($"  Square art prompt sanitised (removed: {string.Join("; ", removedTerms.Select(r => r.Length > 60 ? r[..60] + "…" : r))})");
    prompt = cleanPrompt;

    var existing = ManualArtClient.ImageExtensions
        .Select(ext => squareBase + ext).FirstOrDefault(File.Exists);
    if (existing is not null) return (existing, null, null);

    var governor = ImagePool.GovernorFor(cfg);
    var fluxQuotaExhausted = false;

    // Pexels query for this scene: topic/entity keywords from the
    // script title + tags + scene narration — never the art prompt's
    // first words (the old first-words query returned off-topic
    // photos). The seen-file lives in the job dir (squareBase's
    // directory) so no Pexels photo repeats in one job.
    var pexelsQuery = ImagePool.BuildPexelsQuery(scriptTitle, scriptTags, narration);
    var seenFile = Path.Combine(Path.GetDirectoryName(squareBase) ?? ".", "pexels-used.json");

    // FLUX first, governor-gated on the shared 10k neuron/day bucket
    // (~173 neurons/image). A denial means the bucket is empty or the
    // leg is parked after a quota refusal — either way FLUX is done
    // for today, so the scene falls through to Pexels + OVH and the
    // quota flag is set for the loud-failure path below.
    async Task<(string Path, string? Source)> TryFluxAsync()
    {
        var decision = await governor.AcquireAsync(ImagePool.CloudflareGovernorKey, units: governor.CloudflareUnitsPerImageCall);
        if (!decision.Allowed)
        {
            fluxQuotaExhausted = true;
            Console.WriteLine($"  Square art scene {sceneIndex} FLUX skipped by governor ({decision.Reason}; resets {decision.ResetsAt ?? "unknown"}) — falling through to Pexels + OVH.");
            throw new ImagePoolSkipException("FLUX skipped by governor.");
        }
        try
        {
            var path = await CloudflareArtClient.GenerateFluxSquareAsync(cfg, prompt, squareBase + ".jpg");
            return (path, "cloudflare-flux");
        }
        catch (Exception ex) when (ImagePool.IsFluxQuotaError(ex.Message))
        {
            fluxQuotaExhausted = true;
            var untilSeconds = ImagePool.SecondsUntilUtcMidnight(DateTime.UtcNow);
            await governor.MarkCooledAsync(ImagePool.CloudflareGovernorKey,
                Text.Tail(ex.Message.Replace('\n', ' ').Replace('\r', ' '), 200),
                until: untilSeconds.ToString());
            Console.WriteLine($"  Square art scene {sceneIndex} FLUX quota exhausted ({Text.Tail(ex.Message.Replace('\n', ' ').Replace('\r', ' '), 200).Trim()}) — FLUX parked for {untilSeconds}s until 00:00 UTC; filling this scene with Pexels + OVH.");
            throw new ImagePoolSkipException("FLUX quota exhausted.");
        }
    }

    // Dormant keyed legs (Pollinations keyed / Leonardo): a
    // missing key is a clean skip, never an error that burns retries.
    // The first attempt in a process classifies the leg (key-blocked
    // error, missing helper, or an unmet readiness condition such as
    // Leonardo's empty model id) and later scenes skip it without
    // calling. A live leg is still governor-gated, so a 429 parks it
    // instead of being hammered.
    async Task<(string Path, string? Source)> TryDormantLegAsync(
        string leg, string source, string helperPath, string? notReadyReason, Func<Task<string>> generate)
    {
        if (ImagePool.IsDormant(leg))
            throw new ImagePoolSkipException($"{leg} dormant — skipping.");
        if (!File.Exists(helperPath))
        {
            ImagePool.NoteDormant(leg);
            Console.WriteLine($"  Square art scene {sceneIndex} {leg} dormant (helper missing: {helperPath}) — skipping for the rest of this run.");
            throw new ImagePoolSkipException($"{leg} dormant (helper missing).");
        }
        if (notReadyReason is not null)
        {
            ImagePool.NoteDormant(leg);
            Console.WriteLine($"  Square art scene {sceneIndex} {leg} dormant ({notReadyReason}) — skipping for the rest of this run.");
            throw new ImagePoolSkipException($"{leg} dormant ({notReadyReason}).");
        }
        var decision = await governor.AcquireAsync(ImagePool.GovernorKeyForLeg(leg));
        if (!decision.Allowed)
        {
            Console.WriteLine($"  Square art scene {sceneIndex} {leg} skipped by governor ({decision.Reason}) — trying next provider.");
            throw new ImagePoolSkipException($"{leg} skipped by governor.");
        }
        try
        {
            var path = await generate();
            return (path, source);
        }
        catch (Exception ex) when (ImagePool.IsKeyBlockedError(ex.Message))
        {
            ImagePool.NoteDormant(leg);
            Console.WriteLine($"  Square art scene {sceneIndex} {leg} dormant ({Text.Tail(ex.Message.Replace('\n', ' ').Replace('\r', ' '), 160).Trim()}) — skipping for the rest of this run.");
            throw new ImagePoolSkipException($"{leg} dormant (no key).");
        }
    }

    // Star-photo pre-leg (Rishi 2026-11-24 ~23:14): when the scene is
    // about a ranked star, a real freely-licensed photo of the actual
    // person beats AI art. Wikimedia Commons is tried FIRST, but only
    // when the script title or the scene narration names a ranked star
    // (StarRanking, ordered by Instagram followers); a miss falls
    // through to FLUX as before, so a scene about an unranked person
    // or a non-person topic never pays the Commons lookup. The
    // seen-file lives next to the Pexels one (commons-used.json) so no
    // Commons photo repeats in one job.
    var starMatch = StarRanking.TryMatchStar((scriptTitle ?? "") + " " + (narration ?? ""));
    var commonsSeenFile = Path.Combine(Path.GetDirectoryName(squareBase) ?? ".", "commons-used.json");

    // The attempt also returns the square-crop provenance when the
    // leg reports it (pool legs only): the raw photo's dimensions +
    // crop anchor, so the head-crop check below can judge the crop.
    // Native-square legs (FLUX, dormant keyed) report null — no crop
    // happens there, so there is nothing to check.
    var attempts = new List<(string Source, Func<Task<(string Path, string? Source, ArtProvenance? Provenance)>>)> 
    {
        // FLUX first (bake-off 2026-11-24): the old SceneArtProvider
        // primary/secondary swap is gone — the order is fixed now.
        ("cloudflare-flux", async () => { var (p, s) = await TryFluxAsync(); return (p, s, (ArtProvenance?)null); }),
        // Pexels #2 for news scenes: real photo, topic/entity query.
        ("pexels", async () => await ArtPoolClient.GenerateSquareWithProvenanceAsync(cfg, prompt, squareBase + ".jpg",
            only: "pexels", query: pexelsQuery.Length == 0 ? null : pexelsQuery, seenFile: seenFile)),
        // OVHcloud filler only (SDXL retires 2027-02-10).
        ("ovhcloud", async () => await ArtPoolClient.GenerateSquareWithProvenanceAsync(cfg, prompt, squareBase + ".jpg",
            only: "ovh", query: null, seenFile: null)),
        // Dormant keyed legs, in order. Pollinations keyed is dormant
        // until Pollen exists (402); Leonardo until a key + model id
        // are set.
        ("pollinations-keyed", async () => { var (p, s) = await TryDormantLegAsync("pollinations-keyed", "pollinations-keyed", cfg.PollinationsHelperPath, null,
            () => PollinationsKeyedClient.GenerateSquareAsync(cfg, prompt, squareBase + ".jpg")); return (p, s, (ArtProvenance?)null); }),
        ("leonardo-image", async () => { var (p, s) = await TryDormantLegAsync("leonardo-image", "leonardo-image", cfg.LeonardoHelperPath,
            string.IsNullOrWhiteSpace(cfg.LeonardoModelId) ? "no model id configured (LeonardoModelId empty)" : null,
            () => LeonardoImageClient.GenerateSquareAsync(cfg, prompt, squareBase + ".jpg")); return (p, s, (ArtProvenance?)null); }),
    };
    if (starMatch is not null)
        attempts.Insert(0, ("wikimedia-commons", async () => await ArtPoolClient.GenerateSquareWithProvenanceAsync(cfg, prompt, squareBase + ".jpg",
            only: "wikimedia", query: starMatch.Value.Star.Name, seenFile: commonsSeenFile)));
    Exception? lastError = null;
    foreach (var (label, attempt) in attempts)
    {
        try
        {
            var (path, source, provenance) = await attempt();
            // Quality gate (video #4): check the file before accepting
            // it — a bad image is deleted and the next provider is tried
            // instead of shipping a blurry/blank upscale to the render.
            // The raw scene prompt (not the built prompt) drives the
            // photo-preference / stock-relevance warnings.
            var fileFindings = QualityGate.CheckImageFile(path, source ?? label, rawPrompt ?? prompt, sceneIndex);
            var fileErrors = fileFindings.Where(f => f.Severity == QualitySeverity.Error).ToList();
            foreach (var wf in fileFindings.Where(f => f.Severity == QualitySeverity.Warning))
                Console.WriteLine($"  Square art quality warn [{wf.Check}]: {wf.Message}");
            if (fileErrors.Count > 0)
            {
                foreach (var ef in fileErrors)
                    Console.WriteLine($"  Square art quality ERROR [{ef.Check}]: {ef.Message} — rejecting, trying the next provider.");
                try { File.Delete(path); } catch { }
                throw new InvalidOperationException($"Quality gate rejected art from {label}: {string.Join("; ", fileErrors.Select(e => $"[{e.Check}] {e.Message}"))}");
            }
            // Square head-crop (Drake 2026-11-27): when the leg reports
            // the raw photo's dimensions + crop anchor, flag a portrait
            // original whose square crop discarded the top region. An
            // error rejects the file exactly like a file error above —
            // the next provider is tried instead of shipping a headless
            // photo to the render. A warning is logged and the file is
            // kept. Legs without provenance (native-square generation)
            // have no crop to check and skip this.
            if (provenance is not null)
            {
                var cropFindings = QualityGate.CheckSquareCrop(
                    provenance.RawWidth, provenance.RawHeight, provenance.VerticalBias,
                    sceneIndex, source ?? label, Path.GetFileName(path));
                var cropErrors = cropFindings.Where(f => f.Severity == QualitySeverity.Error).ToList();
                foreach (var wf in cropFindings.Where(f => f.Severity == QualitySeverity.Warning))
                    Console.WriteLine($"  Square art quality warn [{wf.Check}]: {wf.Message}");
                if (cropErrors.Count > 0)
                {
                    foreach (var ef in cropErrors)
                        Console.WriteLine($"  Square art quality ERROR [{ef.Check}]: {ef.Message} — rejecting, trying the next provider.");
                    try { File.Delete(path); } catch { }
                    throw new InvalidOperationException($"Quality gate rejected art from {label}: {string.Join("; ", cropErrors.Select(e => $"[{e.Check}] {e.Message}"))}");
                }
            }
            // A step that produced the file knows its source; the pool step
            // reports the provider it actually used, the keyed steps their
            // fixed label.
            return (path, source ?? label, provenance);
        }
        catch (ImagePoolSkipException)
        {
            // The leg already logged its own skip reason — move on
            // quietly instead of logging the same skip twice.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Square art scene {sceneIndex} leg {label} failed: {ex.Message} — trying next provider");
            lastError = ex;
        }
    }
    if (fluxQuotaExhausted)
    {
        // Loud, resumable failure: FLUX is out for the day and the
        // fallbacks could not cover this scene. Nothing weaker remains
        // to fall through to — the discarded legs are never attempted.
        // The marker tells the driver to back off until the 00:00 UTC
        // reset; --resume picks up the art already on disk.
        var quotaEx = new FluxQuotaExhaustedException(Path.GetFileName(squareBase), lastError);
        Console.WriteLine($"  [FATAL] {quotaEx.Message}");
        throw quotaEx;
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
    // L10: the resume mode continues a prep — same per-job log.
    PrepLog.Install(workDir);
    // L21: the TTS/visual loops' downstream lines carry [jobId].
    PrepContext.Current = new PrepIdentity(Path.GetFileName(workDir), topic);
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

    // Image batch gate at prep end (same as FinishPrepAsync — a resumed
    // prep completes the same way, so the same check applies).
    if (profile.UsesRemotionStills)
    {
        var imageFindings = QualityGate.CheckImagesBatch(workDir, script);
        foreach (var qf in imageFindings)
            Console.WriteLine($"  [quality:images] {(qf.Severity == QualitySeverity.Error ? "ERROR" : "warn")} [{qf.Check}]{(qf.Scene is not null ? " " + qf.Scene : "")} {qf.Message}");
        QualityGate.AppendReport(Path.Combine(workDir, "quality-report.json"), Path.GetFileName(workDir), "images", imageFindings);
        Console.WriteLine($"  {QualityGate.OneLineSummary("images", imageFindings)}");
        if (imageFindings.Any(f => f.Severity == QualitySeverity.Error))
            Console.WriteLine("  Run VideoGen --repair-images --job " + Path.GetFileName(workDir) + " to regenerate the failing art scenes (stock clips need a hand-swap), then re-run --resume.");
        QualityGate.ThrowOnErrors(imageFindings, "images", workDir);
    }

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
    // L10: mirror every Console line into <job>/prep.log from here on.
    PrepLog.Install(workDir);
    // L21: from here on every downstream prep log line carries [jobId].
    PrepContext.Current = new PrepIdentity(Path.GetFileName(workDir), topic);
    Console.WriteLine($"Workspace: {workDir}");

    InsertIntroScene(script);
    DeduplicateConsecutiveScenes(script);

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
        // L42: scene label threads into the TTS providers (see RunTtsLoopAsync).
        await SynthesizeAtomicAsync(tts, scene.Narration, language!, audioPath, rate, pitch, null, $"{i + 1}/{script.Scenes.Count}");
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
        // L21: dry-script resume lines carry the resumed job's id.
        PrepContext.Current = new PrepIdentity(Path.GetFileName(resumeDir), topic);
        var (partial, completedActs) = await LoadPartialScriptAsync(resumeDir);
        var totalActs = profile.UsesCharacterMascot ? 4 : 3;
        profile = ApplyAssetMapping(string.IsNullOrWhiteSpace(partial.Profile) ? profile : ContentProfileRegistry.Get(partial.Profile));
        var snapshotPath = Path.Combine(resumeDir, "trend-snapshot.txt");
        var trendContext = File.Exists(snapshotPath) ? await File.ReadAllTextAsync(snapshotPath) : null;
        var scriptProvider = ScriptProviderFactory.Create(cfg, PrepContext.Current);
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
            resumeFrom: new ScriptResume(partial, completedActs), context: PrepContext.Current);
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
        var trendReport = topic is null ? await TrendResearch.FetchReportAsync(profile, cfg, PrepContext.Current) : null;
        var trendContext = trendReport?.Text;
        // Same forced-topic gap as RunPrepAsync (2026-11-23): a --topic
        // dry-script skipped FetchReportAsync and got no article text, so
        // a dry run could not measure the fix. Fetch the articles for the
        // forced topic here too.
        if (topic is not null)
        {
            var articleSection = ArticleClient.BuildArticleSection(await ArticleClient.FetchForTopicAsync(topic, cfg, context: PrepContext.Current));
            if (articleSection.Length > 0)
                trendContext = (trendContext ?? "") + "\n" + articleSection;
        }
        // Same backstory gap as RunPrepAsync (2026-11-23): a --topic
        // dry-script also skipped the Wikipedia background, so a dry run
        // could not measure the backstory fix either. Fetch the guarded
        // plural backgrounds for the forced topic here too, under the same
        // forced-topic marker, so the dry run mirrors the real prep.
        if (topic is not null)
        {
            try
            {
                var forcedBackgrounds = await TrendResearch.FetchBackgroundForPickedTopicAsync(topic, null, PrepContext.Current);
                if (forcedBackgrounds.WikipediaBackgrounds.Count > 0)
                {
                    var wikiBlock = new System.Text.StringBuilder();
                    wikiBlock.AppendLine("Wikipedia context for this topic (use real facts from here, not invented ones):");
                    foreach (var bg in forcedBackgrounds.WikipediaBackgrounds)
                        wikiBlock.AppendLine($"{bg.Title}: {bg.Extract}");
                    trendContext = (trendContext ?? "") + "\n" + wikiBlock.ToString().Trim();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [wikipedia] forced-topic background fetch failed ({ex.Message}) — continuing without it.");
            }
        }
        var scriptProvider = ScriptProviderFactory.Create(cfg, PrepContext.Current);
        var totalActs = longForm ? (profile.UsesCharacterMascot ? 4 : 3) : 1;
        script = await ScriptGenerator.GenerateAsync(profile, topic, language!, scriptProvider, trendContext, null, null, formatOverride, longForm,
            onActComplete: (s, act) => SavePartialScriptAsync(outDir, s, act, totalActs),
            topicCategory: trendReport?.Category, newsItems: trendReport?.News, context: PrepContext.Current);
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

// --write-props: write both Remotion props files without rendering.
// Laptop-render prep (2026-11-23): the laptop cannot render until the props
// exist, and the VM must never render (Rishi: VM does code + orchestration
// only, all heavy lifting on the laptop). The driver calls this from
// prep_done in laptop mode instead of --assemble. Mirrors the
// RunAssembleAsync preamble exactly — same outro resolution (cached, so a
// cache hit makes no API call) and same vertical scene trim — so these
// props are byte-identical to what --assemble would write. Deterministic inputs only;
// safe to re-run. Refuses non-Remotion profiles (the Vidu/clips path never
// reads a props file).
async Task<int> RunWritePropsAsync()
{
    var workDir = ResolveJobDirectory();
    Console.WriteLine($"Workspace: {workDir}");

    var script = await LoadScriptAsync(workDir);
    language = string.IsNullOrWhiteSpace(script.Language) ? language : script.Language;
    profile = ApplyAssetMapping(string.IsNullOrWhiteSpace(script.Profile) ? profile : ContentProfileRegistry.Get(script.Profile));
    var formatDef = ScriptGenerator.PickFormat(profile, script.Format);

    if (!profile.UsesRemotionStills)
        throw new InvalidOperationException(
            $"--write-props is only for Remotion profiles; profile \"{profile.Id}\" does not use Remotion stills.");

    var (outroRate, outroPitch) = ResolvePacing(profile, script, formatDef);
    var baseOutroLines = profile.OutroLines ?? ["LIKE, FOLLOW & SUBSCRIBE", profile.OutroDestinationText];
    var baseOutroSpoken = profile.OutroSpoken
        ?? $"Like, follow, and subscribe on YouTube — {profile.OutroDestinationSpoken}!";
    var baseOutroAudio = await ResolveOutroAudioAsync(baseOutroSpoken, outroRate, outroPitch);
    // Shorts end card (Rishi 2026-11-25 13:56 EST): the vertical cut ends by
    // sending viewers to the full story on YouTube — spoken AND on the end
    // card. A profile may set its own catchy line (ShortOutroLine/Spoken);
    // without one the generic "Watch the full video on YouTube" stands.
    var shortOutroLines = profile.AllowsContextScenes
        ? [.. baseOutroLines, profile.ShortOutroLine ?? "Watch the full video on YouTube"]
        : baseOutroLines;
    var shortOutroSpoken = profile.AllowsContextScenes
        ? $"{baseOutroSpoken} {profile.ShortOutroSpoken ?? "Watch the full video on YouTube."}"
        : baseOutroSpoken;
    var shortOutroAudio = profile.AllowsContextScenes
        ? await ResolveOutroAudioAsync(shortOutroSpoken, outroRate, outroPitch)
        : baseOutroAudio;

    // Vertical scene trim (same as --assemble): the Short is trimmed to whole
    // scenes that fit the vertical cap, since Reels reject anything over it.
    // No intro sting any more (removed 2026-11-25) — the whole cap is scenes
    // + outro.
    var shortOutroSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, shortOutroAudio) + 0.75;
    var shortSceneBudget = cfg.VerticalMaxSeconds - shortOutroSeconds;
    var shortScenes = new List<Scene>();
    var running = 0.0;
    foreach (var scene in script.Scenes)
    {
        var next = running + scene.DurationSeconds + 0.5 + VideoAssembler.PostNarrationHoldSeconds;
        if (shortScenes.Count > 0 && next > shortSceneBudget) break;
        shortScenes.Add(scene);
        running = next;
    }
    if (shortScenes.Count < script.Scenes.Count)
    {
        Console.WriteLine($"Short: trimmed to {shortScenes.Count}/{script.Scenes.Count} scenes ({running:0}s scenes + {shortOutroSeconds:0.#}s outro) for the {cfg.VerticalMaxSeconds}s cap.");
        // Fix #2: the Short must end on an open loop, never a flat summary.
        // Warning only (ShortRules is a heuristic — scenes carry no
        // cliffhanger marker): the reviewer decides, and the profile
        // guidance is what makes the next prep better. Only when a trim
        // actually happened — a whole-script Short ends on the video's own
        // ending, not on a teaser cut.
        if (!ShortRules.ShortEndsOnOpenLoop(shortScenes))
            Console.WriteLine($"Short: WARNING — ends on scene {shortScenes.Count - 1}, which ends flat, not on an open loop (fix #2: the Short must tease the payoff, never resolve it).");
    }

    var (landscapePropsPath, landscapeMusic) = await VideoAssembler.BuildAndWritePropsAsync(profile, cfg, script.Scenes, workDir, 1920, 1080, script.MusicMood,
        outroLines: baseOutroLines, outroAudioPath: baseOutroAudio, isCalm: formatDef.IsCalm,
        outroHandles: profile.OutroHandles);
    Console.WriteLine($"Landscape props written: {landscapePropsPath}");

    // The Short's outro lines/audio are the "watch the full video" pair for
    // AllowsContextScenes profiles, the base pair else.
    var (verticalPropsPath, _) = await VideoAssembler.BuildAndWritePropsAsync(profile, cfg, shortScenes, workDir, 1080, 1920, script.MusicMood,
        outroLines: shortOutroLines, outroAudioPath: shortOutroAudio, isCalm: formatDef.IsCalm,
        outroHandles: profile.OutroHandles);
    Console.WriteLine($"Vertical props written: {verticalPropsPath}");

    if (landscapeMusic is not null)
        Console.WriteLine($"Music track: {landscapeMusic}");
    return 0;
}

// Read-only quality check of a finished (or in-progress) job:
// script + images + music, and the final mix when --mp4 is given.
// Writes the machine-readable report to <job>/quality-report.json
// unless --report-out says otherwise, prints one PASS/WARN/FAIL line
// per gate, and exits 1 when any gate errors. Changes nothing in the
// job except the report file itself (use --report-out to keep even
// that out of the job dir).
async Task<int> RunCheckQualityAsync()
{
    var workDir = ResolveJobDirectory();
    Console.WriteLine($"Quality check: {workDir} (read-only)");

    var script = await LoadScriptAsync(workDir);
    profile = ApplyAssetMapping(string.IsNullOrWhiteSpace(script.Profile) ? profile : ContentProfileRegistry.Get(script.Profile));
    var reportPath = reportOut ?? Path.Combine(workDir, "quality-report.json");
    var all = new List<QualityFinding>();

    void LogGate(string gate, List<QualityFinding> findings)
    {
        foreach (var qf in findings)
            Console.WriteLine($"  [quality:{gate}] {(qf.Severity == QualitySeverity.Error ? "ERROR" : "warn")} [{qf.Check}]{(qf.Scene is not null ? " " + qf.Scene : "")} {qf.Message}");
        QualityGate.AppendReport(reportPath, Path.GetFileName(workDir), gate, findings);
        Console.WriteLine($"  {QualityGate.OneLineSummary(gate, findings)}");
        all.AddRange(findings);
    }

    // Script: no topic is stored in the job, so the title-keyword
    // checks are skipped in check mode (passing the title as its own
    // topic would be circular); hook, questions, speculation,
    // structure, pace and description checks all still run. The craft
    // gate (correctness / scene fit / retention + SEO) runs too — its
    // topic-dependent checks (description keywords, tag coverage,
    // stuffing) skip without a topic, but the rest are topic-free.
    var scriptFindings = QualityGate.CheckScript(script, topic: null, outroLines: profile.OutroLines);
    scriptFindings.AddRange(QualityGate.CheckScriptCraft(script, topic: null));
    LogGate("script", scriptFindings);

    LogGate("images", QualityGate.CheckImagesBatch(workDir, script));

    // Music: the track the job resolved — script.MusicTrack first,
    // then the landscape props' backgroundMusicPath. A bare filename
    // resolves against the profile's music folder.
    string? trackRef = string.IsNullOrWhiteSpace(script.MusicTrack) ? null : script.MusicTrack;
    if (trackRef is null)
    {
        var propsPath = Path.Combine(workDir, "remotion-props-1920x1080.json");
        if (File.Exists(propsPath))
        {
            try
            {
                using var props = JsonDocument.Parse(File.ReadAllText(propsPath));
                if (props.RootElement.TryGetProperty("backgroundMusicPath", out var bg) &&
                    bg.ValueKind == JsonValueKind.String)
                    trackRef = bg.GetString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [quality:music] warn: could not read {Path.GetFileName(propsPath)} ({ex.Message})");
            }
        }
    }
    string? trackPath = trackRef;
    if (trackPath is not null && !Path.IsPathRooted(trackPath) && !File.Exists(trackPath))
    {
        var musicDir = profile.BackgroundMusicPath;
        if (!string.IsNullOrWhiteSpace(musicDir) && Directory.Exists(musicDir))
            trackPath = Path.Combine(musicDir, Path.GetFileName(trackPath));
    }
    var musicFindings = QualityGate.CheckMusicTrack(trackPath, script.MusicMood, videoSeconds: 0, description: script.Description);
    if (checkMp4 is not null)
        musicFindings.AddRange(QualityGate.CheckFinalMix(checkMp4, expectedSeconds: 0));
    LogGate("music", musicFindings);

    var errors = all.Count(f => f.Severity == QualitySeverity.Error);
    var warnings = all.Count(f => f.Severity == QualitySeverity.Warning);
    Console.WriteLine($"Quality check: {(errors > 0 ? "FAIL" : warnings > 0 ? "WARN" : "PASS")} ({errors} error(s), {warnings} warning(s)) — report: {reportPath}");
    return errors > 0 ? 1 : 0;
}

// Selective image repair (Rishi 2026-11-23 ~23:37): re-check every
// image in a finished prep and regenerate ONLY the failing ones, with
// an IMPROVED prompt (never the same prompt again). This is the step
// to run before --assemble when the batch check fails: the overnight
// manual repair (COP31 scenes 6/47/49, Bisleri 0/10/21/26/32/41) did
// exactly this by hand — rewrite the prompt clearer/more concrete
// and text-free, regenerate only that scene, re-check it.
//   - Scenes with no error are never touched (their art, prompts and
//     files stay exactly as prepped).
//   - A bad file is moved aside (sceneNN-art-square.jpg.bad) before
//     regeneration, and restored if the regeneration fails — a failed
//     repair never loses the original.
//   - The derived portrait (sceneNN-art.png) is re-derived from the
//     new square so the two orientations cannot go stale.
//   - script.json is saved only when at least one scene was repaired.
// Exits 1 when errors remain after repair (re-run, or fix by hand).
async Task<int> RunRepairImagesAsync()
{
    var workDir = ResolveJobDirectory();
    Console.WriteLine($"Image repair: {workDir} (selective — only failing scenes are regenerated)");

    var script = await LoadScriptAsync(workDir);
    profile = ApplyAssetMapping(string.IsNullOrWhiteSpace(script.Profile) ? profile : ContentProfileRegistry.Get(script.Profile));
    var reportPath = reportOut ?? Path.Combine(workDir, "quality-report.json");

    var before = QualityGate.CheckImagesBatch(workDir, script);
    var failing = before
        .Where(f => f.Severity == QualitySeverity.Error && f.Scene is not null)
        .GroupBy(f => f.Scene!)
        .ToDictionary(g => g.Key, g => g.Select(f => f.Check).Distinct().ToList());
    if (failing.Count == 0)
    {
        Console.WriteLine("  No image errors — nothing to repair.");
        QualityGate.AppendReport(reportPath, Path.GetFileName(workDir), "images", before);
        Console.WriteLine($"  {QualityGate.OneLineSummary("images", before)}");
        return 0;
    }

    Console.WriteLine($"  {failing.Count} scene(s) with image errors: {string.Join(", ", failing.Keys)}");
    var repaired = new List<string>();
    var failed = new List<string>();
    var skipped = new List<string>();

    foreach (var (label, checks) in failing)
    {
        if (!int.TryParse(label.Replace("scene", "", StringComparison.OrdinalIgnoreCase), out var i) ||
            i < 0 || i >= script.Scenes.Count)
        {
            skipped.Add($"{label} (unparsable scene label)");
            continue;
        }
        var scene = script.Scenes[i];
        if (scene.SceneKind == "visual" || scene.SceneKind == "context")
        {
            // Remotion-drawn visuals and Pexels stock clips are not
            // generated art — there is no prompt to improve.
            skipped.Add($"{label} ({scene.SceneKind} scene — no generated art to repair)");
            continue;
        }

        // The square source is the single asset both orientations derive
        // from (prep writes it to sceneNN-art-square.* and records it
        // as LandscapeImagePath).
        var squarePath = scene.LandscapeImagePath;
        if (string.IsNullOrWhiteSpace(squarePath) ||
            !squarePath.Contains("-square", StringComparison.OrdinalIgnoreCase))
        {
            var imagePath = scene.ImagePath;
            if (!string.IsNullOrWhiteSpace(imagePath) &&
                imagePath.Contains("-square", StringComparison.OrdinalIgnoreCase))
                squarePath = imagePath;
        }
        if (string.IsNullOrWhiteSpace(squarePath))
        {
            skipped.Add($"{label} (no square source file recorded)");
            continue;
        }
        var squareFull = Path.IsPathRooted(squarePath) ? squarePath : Path.Combine(workDir, squarePath);
        if (!File.Exists(squareFull))
        {
            skipped.Add($"{label} (square source file missing: {Path.GetFileName(squareFull)})");
            continue;
        }

        var squareBase = Path.Combine(
            Path.GetDirectoryName(squareFull) ?? workDir,
            Path.GetFileNameWithoutExtension(squareFull));
        var artBase = squareBase.EndsWith("-square", StringComparison.OrdinalIgnoreCase)
            ? squareBase[..^"-square".Length]
            : squareBase;
        var portraitPath = artBase + ".png";

        var (improved, changes) = QualityGate.ImproveImagePrompt(scene.ImagePrompt, scene.Narration, checks);
        Console.WriteLine($"  Repair {label}: {string.Join("; ", changes)}");
        Console.WriteLine($"    new prompt: {improved}");

        // Move the bad file aside (restored on failure) — the generator
        // early-returns when the square already exists on disk, so the
        // file must be gone before regeneration.
        var backupPath = squareFull + ".bad";
        var backupCounter = 1;
        while (File.Exists(backupPath))
            backupPath = squareFull + $".bad{backupCounter++}";
        File.Move(squareFull, backupPath);
        var portraitExisted = File.Exists(portraitPath);
        if (portraitExisted)
            File.Delete(portraitPath);

        try
        {
            var squarePrompt = ImageClient.BuildPrompt(
                improved, profile.CharacterStyle, profile.CharacterPortraitStyleSuffix,
                ImageClient.Orientation.Square, matchReference: profile.UsesCharacterMascot);
            var (newSquare, artSource, repairProvenance) = await GenerateSquareArtAsync(cfg, squarePrompt, squareBase, i, scene.ImagePrompt,
                script.Title, script.Tags, scene.Narration);
            if (artSource is not null) scene.ArtSource = artSource;
            RecordSquareProvenance(scene, repairProvenance);
            // Save the improved prompt back into the script: the
            // batch check judges the prompt recorded here, so keeping
            // the old text-asking prompt would make the repair look
            // like it never happened (the re-check below would flag
            // the same prompt-text error on the new art).
            scene.ImagePrompt = improved;
            scene.LandscapeImagePath = newSquare;
            scene.ImagePath = await DeriveArtAsync(cfg, newSquare, portraitPath, 1080, 1920);
            try { File.Delete(backupPath); } catch { }
            repaired.Add(label);
            Console.WriteLine($"  Repaired {label} -> {Path.GetFileName(newSquare)} ({scene.ArtSource})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Repair {label} FAILED ({ex.Message}) — restoring the original art.");
            try
            {
                if (File.Exists(squareFull)) File.Delete(squareFull);
                File.Move(backupPath, squareFull);
                if (portraitExisted)
                    scene.ImagePath = await DeriveArtAsync(cfg, squareFull, portraitPath, 1080, 1920);
            }
            catch (Exception restoreEx)
            {
                Console.WriteLine($"  WARNING: could not restore {label}'s original art ({restoreEx.Message}) — backup kept at {backupPath}.");
            }
            failed.Add($"{label} ({ex.Message})");
        }
    }

    if (repaired.Count > 0)
        await SaveScriptAsync(workDir, script);

    var after = QualityGate.CheckImagesBatch(workDir, script);
    QualityGate.AppendReport(reportPath, Path.GetFileName(workDir), "images", after);
    Console.WriteLine($"  {QualityGate.OneLineSummary("images", after)}");
    Console.WriteLine($"  Repaired: {(repaired.Count == 0 ? "none" : string.Join(", ", repaired))}");
    if (skipped.Count > 0)
        Console.WriteLine($"  Skipped: {string.Join("; ", skipped)}");
    if (failed.Count > 0)
        Console.WriteLine($"  Failed: {string.Join("; ", failed)}");
    var remainingErrors = after.Count(f => f.Severity == QualitySeverity.Error);
    Console.WriteLine(remainingErrors > 0
        ? $"Image repair: {remainingErrors} error(s) remain — fix by hand or re-run --repair-images."
        : "Image repair: all image errors cleared.");
    return remainingErrors > 0 ? 1 : 0;
}

async Task<int> RunMakeThumbnailsAsync()
{
    // Live-video re-thumbnailing (fix #3): builds the 3 purpose-built
    // variants for a finished job without rendering anything and without
    // touching YouTube — setting a variant on a live video is the
    // Uploader's --set-thumbnail step, after Rishi picks.
    if (job is null)
    {
        Console.Error.WriteLine("--make-thumbnails needs --job <job-id>.");
        return 1;
    }
    if (outDir is null)
    {
        Console.Error.WriteLine("--make-thumbnails needs --out <dir>.");
        return 1;
    }
    var workDir = ResolveJobDirectory();
    var script = await LoadScriptAsync(workDir);
    var slugValue = slug ?? Path.GetFileName(workDir);
    var outputBase = Path.Combine(outDir, slugValue + ".thumb");

    // Legacy base for the CLI: first story scene still art, then scene 0.
    // (The finish path also tries clip frames and a render frame; the CLI
    // has no render to pull a frame from, and every live job has still
    // art — a job with none fails loudly instead of guessing.)
    string? legacyBase = null;
    for (var i = 1; i < script.Scenes.Count && legacyBase is null; i++)
    {
        var scene = script.Scenes[i];
        if (!string.IsNullOrWhiteSpace(scene.LandscapeImagePath) && File.Exists(scene.LandscapeImagePath))
            legacyBase = scene.LandscapeImagePath;
        else if (!string.IsNullOrWhiteSpace(scene.ImagePath) && File.Exists(scene.ImagePath))
            legacyBase = scene.ImagePath;
    }
    if (legacyBase is null && script.Scenes.Count > 0)
    {
        var scene = script.Scenes[0];
        if (!string.IsNullOrWhiteSpace(scene.LandscapeImagePath) && File.Exists(scene.LandscapeImagePath))
            legacyBase = scene.LandscapeImagePath;
        else if (!string.IsNullOrWhiteSpace(scene.ImagePath) && File.Exists(scene.ImagePath))
            legacyBase = scene.ImagePath;
    }
    if (legacyBase is null)
    {
        Console.Error.WriteLine($"--make-thumbnails: no still art in {workDir} — nothing to build a thumbnail from.");
        return 1;
    }

    var built = await ThumbnailBuilder.BuildVariantsAsync(
        cfg, script, legacyBase, outputBase,
        new ThumbnailBuilder.ThumbnailBuildOptions(sourceScene, anchorXOverride, anchorYOverride));
    var star = StarRanking.TryMatchStar(script.Title);
    if (star is not null && built.Source is null)
        Console.WriteLine($"Thumbnail WARNING: ranked star '{star.Value.Star.Name}' has no verified real photo in this job — variants use the legacy first-story art.");
    else if (built.Source is not null)
        Console.WriteLine($"Thumbnail: source is scene {built.Source.SceneIndex} ({built.Source.ArtSource}).");
    foreach (var variant in built.Variants)
        Console.WriteLine(
            $"{variant.Path} | words='{variant.Words}' font={variant.FontSizePx} zoom={ThumbnailBuilder.Invariant(variant.Zoom)} " +
            $"anchor=({ThumbnailBuilder.Invariant(variant.AnchorX)},{ThumbnailBuilder.Invariant(variant.AnchorY)}) " +
            $"320px text height={variant.TextHeightAt320Px:F1}px mobile-check={(variant.MobileCheckPass ? "PASS" : "WARN")}");
    return 0;
}

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
    //
    // Quality gate (video #4): batch image check before ANY render —
    // prompt bans, per-file checks, duplicate art, provider mix. Hard
    // errors throw; warnings are logged and recorded in
    // quality-report.json. Runs on every --assemble so a bad image can
    // never reach the renderer, even for a job prepped before the gate
    // existed (the per-file check at generation time is the first line
    // of defence; this is the backstop).
    var imageFindings = QualityGate.CheckImagesBatch(workDir, script);
    foreach (var qf in imageFindings)
        Console.WriteLine($"  [quality:images] {(qf.Severity == QualitySeverity.Error ? "ERROR" : "warn")} [{qf.Check}]{(qf.Scene is not null ? " " + qf.Scene : "")} {qf.Message}");
    QualityGate.AppendReport(Path.Combine(workDir, "quality-report.json"), Path.GetFileName(workDir), "images", imageFindings);
    Console.WriteLine($"  {QualityGate.OneLineSummary("images", imageFindings)}");
    if (imageFindings.Any(f => f.Severity == QualitySeverity.Error))
        Console.WriteLine("  Run VideoGen --repair-images --job " + Path.GetFileName(workDir) + " to regenerate only the failing images with improved prompts, then re-run --assemble.");
    QualityGate.ThrowOnErrors(imageFindings, "images", workDir);
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
    // that render is a trimmed teaser for the landscape master — the line
    // comes from the profile (ShortOutroLine/Spoken) when it sets one.
    var (outroRate, outroPitch) = ResolvePacing(profile, script, formatDef);
    var baseOutroLines = profile.OutroLines ?? ["LIKE, FOLLOW & SUBSCRIBE", profile.OutroDestinationText];
    var baseOutroSpoken = profile.OutroSpoken
        ?? $"Like, follow, and subscribe on YouTube — {profile.OutroDestinationSpoken}!";
    var baseOutroAudio = await ResolveOutroAudioAsync(baseOutroSpoken, outroRate, outroPitch);
    var shortOutroLines = profile.AllowsContextScenes
        ? [.. baseOutroLines, profile.ShortOutroLine ?? "Watch the full video on YouTube"]
        : baseOutroLines;
    var shortOutroSpoken = profile.AllowsContextScenes
        ? $"{baseOutroSpoken} {profile.ShortOutroSpoken ?? "Watch the full video on YouTube."}"
        : baseOutroSpoken;
    var shortOutroAudio = profile.AllowsContextScenes
        ? await ResolveOutroAudioAsync(shortOutroSpoken, outroRate, outroPitch)
        : baseOutroAudio;

    // The outro card's rendered length (voiceover + trailing beat) counts
    // against the vertical cap - Reels reject anything over 90s, so the scene
    // budget is VerticalMaxSeconds minus the outro, not the full cap. No
    // intro sting any more (removed 2026-11-25) — the whole cap is scenes +
    // outro, and the hook narration starts at 0:00.
    var shortOutroSeconds = await VideoAssembler.GetAudioDurationAsync(cfg, shortOutroAudio) + 0.75;
    var shortSceneBudget = cfg.VerticalMaxSeconds - shortOutroSeconds;

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
    {
        Console.WriteLine($"Short: trimmed to {shortScenes.Count}/{script.Scenes.Count} scenes ({running:0}s scenes + {shortOutroSeconds:0.#}s outro) for the {cfg.VerticalMaxSeconds}s cap.");
        // Fix #2: same open-loop warning as --write-props above — the Short
        // must end on a tease, never a flat summary or a resolution.
        if (!ShortRules.ShortEndsOnOpenLoop(shortScenes))
            Console.WriteLine($"Short: WARNING — ends on scene {shortScenes.Count - 1}, which ends flat, not on an open loop (fix #2: the Short must tease the payoff, never resolve it).");
    }

    // Shared crossfade for this format's SRT/chapters (Root.tsx: 0.2s brisk,
    // 0.45s calm) - the timeline math must use the same value as the render.
    var transitionSeconds = formatDef.IsCalm ? VideoAssembler.CalmTransitionSeconds : VideoAssembler.TransitionSeconds;

    // Landscape props up front (2026-11-23): the landscape chunk render
    // needs remotion-props-1920x1080.json, but the landscape render call
    // below only writes it after the vertical render finishes — and the
    // vertical render keeps dying from memory at frame ~50-96, so the props
    // were never written and the driver looped prep_done → assemble →
    // render_chunks → prep_done forever. Writing them here, with the exact
    // same arguments the landscape render call uses, lets the driver
    // chunk-render the landscape even when the vertical assemble dies.
    // The inputs are all known before either render (outro from the cached
    // audio), and the music pick is
    // deterministic by job-folder hash, so these props are identical to
    // what the landscape render would write. Remotion profiles only — the
    // Vidu/clips path never reads a props file.
    if (profile.UsesRemotionStills)
    {
        var (landscapePropsPath, _) = await VideoAssembler.BuildAndWritePropsAsync(profile, cfg, script.Scenes, workDir, 1920, 1080, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio, isCalm: formatDef.IsCalm,
            outroHandles: profile.OutroHandles);
        Console.WriteLine($"Landscape props written up front: {landscapePropsPath}");
    }

    var vertical = Path.Combine(outputDir, $"{slug}-{language}-short.mp4");
    // The background-music track actually used, recorded on the script and
    // sidecar (MusicTrack) so the Uploader can append the matching CC BY
    // credit. The Vidu path does not return one (not a Trendline Now path).
    string? verticalMusic = null;
    if (profile.AllowsContextScenes)
    {
        verticalMusic = await VideoAssembler.AssembleWithRemotionAsync(profile, cfg, shortScenes, workDir, vertical, 1080, 1920, script.MusicMood,
            outroLines: shortOutroLines, outroAudioPath: shortOutroAudio, isCalm: formatDef.IsCalm,
            outroHandles: profile.OutroHandles);
    }
    else if (profile.UsesRemotionStills)
    {
        // Stills pipeline, but the short is the whole video (not a teaser for the
        // landscape master), so it keeps the base outro, not the "watch the full
        // video" line the context-scene profiles add above.
        verticalMusic = await VideoAssembler.AssembleWithRemotionAsync(profile, cfg, shortScenes, workDir, vertical, 1080, 1920, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio, isCalm: formatDef.IsCalm,
            outroHandles: profile.OutroHandles);
    }
    else
    {
        await VideoAssembler.AssembleFromClipsAsync(profile, cfg, shortScenes, language!, workDir, vertical, 1080, 1920, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio);
    }
    await VideoAssembler.NormalizeLoudnessAsync(cfg, vertical);
    // No brief, no sting: the Short's first caption starts at 0:00.
    await VideoAssembler.WriteSrtAsync(shortScenes, Path.ChangeExtension(vertical, ".srt"), transitionSeconds: transitionSeconds);
    Console.WriteLine($"Rendered: {vertical}");
    if (verticalMusic is not null)
    {
        script.MusicTrack = verticalMusic;
        await SaveScriptAsync(workDir, script);
    }

    // Canonical hashtags (review 2026-11-22, SEO #6): the Short's captions
    // reuse the long-form Description's hashtag line, so a video and its
    // own Short never drift apart. Reels keeps up to 8, TikTok up to 4
    // (the first ones are the most specific — channel, then topic).
    var canonicalTags = CanonicalHashtags(script.Description);
    var shortReelsCaption = WithCanonicalHashtags(script.ReelsCaption, canonicalTags, maxHashtags: 8, maxLength: 300);
    var shortTikTokCaption = WithCanonicalHashtags(script.TikTokCaption, canonicalTags, maxHashtags: 4, maxLength: 150);

    await new Sidecar
    {
        // Short title (review 2026-11-22, SEO #5): the plain script title,
        // no " #Shorts" suffix. YouTube auto-detects Shorts from the
        // vertical format, so the suffix adds nothing — and it pushed #2's
        // Short title to 85 chars. The description keeps its #Shorts
        // hashtag (the YouTube marker); only the title drops it.
        Title = Text.Fit(script.Title, 100),
        Description = $"{shortReelsCaption}\n\n#Shorts",
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
        MusicTrack = verticalMusic,
        CaptionOverrides =
        {
            // YouTube keeps the #Shorts marker; the others must not carry it.
            [Platforms.Instagram] = shortReelsCaption,
            [Platforms.Facebook] = shortReelsCaption,
            [Platforms.TikTok] = shortTikTokCaption,
        },
    }.SaveAsync(vertical, msg => Console.WriteLine($"  [sidecar] {msg}"));
    rendered.Add(vertical);

    // The 16:9 master reuses the same clips on a blurred blow-up of themselves rather
    // than generating a second orientation, which would double the per-video spend.
    // No sting, no brief (removed 2026-11-25, Rishi): the long-form opens
    // directly with the hook (scene 0), same as the Short.
    // (The landscape props are computed up front, before the vertical render,
    // so the landscape props can be written before any rendering starts —
    // see above.)
    var landscape = Path.Combine(outputDir, $"{slug}-{language}.mp4");
    string? landscapeMusic = null;
    if (profile.UsesRemotionStills)
        landscapeMusic = await VideoAssembler.AssembleWithRemotionAsync(profile, cfg, script.Scenes, workDir, landscape, 1920, 1080, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio, isCalm: formatDef.IsCalm,
            outroHandles: profile.OutroHandles);
    else
        await VideoAssembler.AssembleFromClipsAsync(profile, cfg, script.Scenes, language!, workDir, landscape, 1920, 1080, script.MusicMood,
            outroLines: baseOutroLines, outroAudioPath: baseOutroAudio);
    await VideoAssembler.NormalizeLoudnessAsync(cfg, landscape);
    // No brief, no sting: the long-form's first caption starts at 0:00, from
    // the same shared timeline as the chapters.
    await VideoAssembler.WriteSrtAsync(script.Scenes, Path.ChangeExtension(landscape, ".srt"), transitionSeconds: transitionSeconds);
    Console.WriteLine($"Rendered: {landscape}");
    if (landscapeMusic is not null)
    {
        script.MusicTrack = landscapeMusic;
        await SaveScriptAsync(workDir, script);
    }

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
        var scene = script.Scenes[i];
        if (scene.LandscapeImagePath is { } storyArt && File.Exists(storyArt))
        {
            thumbBase = storyArt;
            Console.WriteLine($"Thumbnail: using landscape art for scene {i} (a major story picture, not the title card).");
            break;
        }
        // A stock-video scene has no still art, only a clip - it used to be
        // skipped entirely, so a video whose early scenes are all stock
        // footage fell through to the scene-0 title card (the video #1
        // thumbnail bug). Pull a frame from the clip instead.
        var clip = scene.LandscapeClipPath is { } lc && File.Exists(lc) ? lc
            : scene.ClipPath is { } c && File.Exists(c) ? c : null;
        if (clip is not null)
        {
            var framePath = Path.Combine(workDir, $"thumbnail-frame-scene{i:D2}.jpg");
            var clipDuration = await VideoAssembler.GetVideoDurationAsync(cfg, clip);
            await VideoAssembler.ExtractThumbnailFrameAsync(cfg, clip, Math.Max(0.5, clipDuration * 0.4), framePath);
            thumbBase = framePath;
            Console.WriteLine($"Thumbnail: using a frame from the stock clip for scene {i} (a major story picture, not the title card).");
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
    // Purpose-built thumbnails (fix #3, audit 2026-11-26): real verified
    // star face 60-70% frame + 1-3 words, 3 variants, Rishi picks. The
    // builder prefers a Wikimedia Commons / Pexels real photo over the
    // legacy first-story-art base (which may be FLUX); for a ranked star
    // with no real photo on file it falls back to that base with a loud
    // warning instead of failing the finish — the variant pick is the
    // identity check, and a missing thumbnail must never block a video.
    // Any failure in the variant build falls back to the old plain
    // single thumb, same reason.
    var thumbBaseForSidecar = Path.ChangeExtension(landscape, ".thumb.jpg");
    try
    {
        var built = await ThumbnailBuilder.BuildVariantsAsync(
            cfg, script, thumbBase, Path.ChangeExtension(landscape, ".thumb"));
        var starForLog = StarRanking.TryMatchStar(script.Title);
        if (starForLog is not null && built.Source is null)
            Console.WriteLine(
                $"Thumbnail WARNING: ranked star '{starForLog.Value.Star.Name}' has no verified real photo " +
                "in this job — variants use the legacy first-story art (possibly AI-generated). " +
                "Check the face before upload.");
        else if (built.Source is not null)
            Console.WriteLine(
                $"Thumbnail: source is scene {built.Source.SceneIndex} ({built.Source.ArtSource}).");
        foreach (var variant in built.Variants)
            Console.WriteLine(
                $"Thumbnail: {Path.GetFileName(variant.Path)} words='{variant.Words}' font={variant.FontSizePx} " +
                $"320px text height={variant.TextHeightAt320Px:F1}px mobile-check={(variant.MobileCheckPass ? "PASS" : "WARN")}.");
        thumbBaseForSidecar = built.Variants[0].Path;
        Console.WriteLine("Thumbnail variants generated (1280x720 x3, default v1).");
    }
    catch (Exception ex)
    {
        // L57: the variant-build failure line must name the job, and a
        // failing fallback must say so explicitly (it used to propagate
        // to the top-level FATAL with only the ffmpeg error).
        var thumbJobId = Path.GetFileName(workDir);
        Console.WriteLine($"Thumbnail: variant build failed ({ex.Message}) — falling back to the plain single thumb (job {thumbJobId}).");
        try
        {
            await VideoAssembler.BuildThumbnailAsync(cfg, thumbBase, Path.ChangeExtension(landscape, ".thumb.jpg"));
        }
        catch (Exception fallbackEx)
        {
            Console.WriteLine($"Thumbnail fallback also failed ({fallbackEx.Message}) — assemble aborting without thumbnail (job {thumbJobId})");
            throw;
        }
        Console.WriteLine("Thumbnail generated (1280x720).");
    }

    // Long-form chapters (priority 5): at most 12, first at 0:00
    // (the opening hook), one per visual-group boundary, time-spaced
    // when boundaries exceed the cap, labels are the first complete
    // sentence of the scene's narration. Free text in the YouTube
    // description — no API, no extra render — and it measurably helps
    // long-form retention because viewers can jump to a beat.
    // No brief, no sting: the timeline starts at the hook, 0:00.
    var chapters = script.IsLongForm ? BuildChapters([], 0, script.Scenes, transitionSeconds) : null;
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
        ThumbnailPath = thumbBaseForSidecar,
        HookScore = script.HookScore,
        SeoScore = script.SeoScoreValue,
        LoudnessLUFS = -14,
        MusicTrack = landscapeMusic,
    }.SaveAsync(landscape, msg => Console.WriteLine($"  [sidecar] {msg}"));
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
                // L82: wire the helper's log sink into the run log.
                var sidecar = await Sidecar.LoadAsync(liveVideo, msg => Console.WriteLine($"  [sidecar] {msg}"));
                if (sidecar is not null && sidecar.Approved != true)
                    flagged.Add($"[{jobName}] \"{script.Title}\" ({kind}) — rendered, awaiting your approval. " +
                        $"Set \"approved\": true in {Sidecar.PathFor(liveVideo)}, or delete both files to discard.");
            }
            else if (File.Exists(doneVideo) || File.Exists(failedVideo))
            {
                var terminalVideo = File.Exists(doneVideo) ? doneVideo : failedVideo;
                // L82: wire the helper's log sink into the run log.
                var sidecar = await Sidecar.LoadAsync(terminalVideo, msg => Console.WriteLine($"  [sidecar] {msg}"));
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
// script-so-far is saved to script-partial.json, so a failure in a later act
// (Gemini 402 at Act 3 on 2026-11-12) no longer loses the acts already
// generated. The act count lives INSIDE the partial (VideoScript.CompletedActs)
// and is written in the same atomic temp+move as the scenes - it used to live
// in a separate script-progress.json, and a kill between the two writes left
// the partial holding act N while progress said N-1, so resume regenerated
// act N and duplicated a whole act. The partial is explicitly NOT script.json:
// --resume/--assemble must never treat an unfinished script as a complete
// video. Once GenerateAsync returns, the full script.json is saved by the
// caller and these files are deleted.
// Failures are NOT swallowed: a checkpoint that cannot be written means the
// resume path is broken, and failing generation loudly beats silently losing
// the acts already paid for (a failed checkpoint used to be a mere log line).
async Task SavePartialScriptAsync(string dir, VideoScript script, int completedAct, int totalActs)
{
    script.CompletedActs = completedAct;
    var path = Path.Combine(dir, "script-partial.json");
    var temp = path + ".tmp";
    try
    {
        await File.WriteAllTextAsync(temp, System.Text.Json.JsonSerializer.Serialize(script, jsonOptions));
        File.Move(temp, path, overwrite: true);
    }
    catch (Exception ex)
    {
        // L71: a failed checkpoint used to surface as a raw I/O exception
        // with no act number and no word on whether the partial on disk is
        // stale. It still throws — a checkpoint that cannot be written
        // means resume is broken — but now it says which act and that the
        // partial on disk is stale.
        Console.WriteLine($"  {PrepContext.Prefix}[prep] checkpoint save failed for act {completedAct}/{totalActs}: {ex.GetType().Name}: {ex.Message} — the partial on disk is stale (act {completedAct - 1} or none); failing generation rather than losing the acts already paid for");
        throw;
    }
    Console.WriteLine($"  Checkpoint: act {completedAct}/{totalActs} saved ({script.Scenes.Count} scenes) -> {path}");
}

// L71: warn when generation starts with an empty facts block (news
// profiles only — mascot scripts are invented by design). Kept free of
// captured locals so tests can invoke it by name via ReflectionHelper.
void WarnOnEmptyFactsBlock(string? trendContext, bool isNewsProfile)
{
    if (!isNewsProfile || !string.IsNullOrWhiteSpace(trendContext)) return;
    Console.WriteLine($"  {PrepContext.Prefix}[prep] WARNING: generating with an empty facts block (no headlines, articles or Wikipedia background) — the script will be judged against an empty context; check the trend fetch above");
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
        catch (Exception ex)
        {
            // L71: the old bare `catch { }` hid a failed delete — on a
            // floor failure the next attempt then silently resumed the same
            // thin partial, the exact failure mode the delete exists to
            // prevent. (Tagged [prep], not [floor-failure]: this helper is
            // also the success-path cleanup, so the floor tag would
            // mislabel those calls.)
            Console.WriteLine($"  {PrepContext.Prefix}[prep] could not delete {Path.Combine(dir, name)}: {ex.GetType().Name}: {ex.Message} — next attempt may resume the thin partial");
        }
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
// Long-form chapters: one per visual group, timestamped from the shared
// render timeline (VideoAssembler.ComputeSegmentStarts) so chapter times
// match the rendered video. Each scene advances the timeline by its
// rendered length (DurationSeconds + 0.5 + hold - transition). The old
// version started at 0 and advanced by DurationSeconds + 0.5, drifting
// ~39-55s early by the end of a long video.
// Fix (review 2026-11-22, SEO #4): at most 12 chapters, labelled with the
// first complete sentence of the scene's narration — never a 40-char
// truncation (that produced mid-sentence fragments like "Rockstar finally
// released GTA 6, and it…"). Video #1 had 77 chapters because the model
// barely reused VisualGroups, so the chapter block WAS the description.
// The first chapter is at 0:00 (YouTube ignores a chapter list whose
// first timestamp is not 0:00); it covers the opening hook and is labelled
// from the hook's opening line — the brief's, on old scripts that still
// carry one, else scene 0's (the hook title card). When there are more
// visual-group boundaries than the
// remaining slots, evenly-spaced ones are kept so the chapters still
// span the whole video.
List<string> BuildChapters(
    IReadOnlyList<Scene> briefScenes, double introSeconds,
    IReadOnlyList<Scene> scenes, double transitionSeconds = VideoAssembler.TransitionSeconds)
{
    const int MaxChapters = 12;
    var chapters = new List<string>();
    var starts = VideoAssembler.ComputeSegmentStarts(briefScenes, introSeconds, scenes, transitionSeconds);
    var firstScene = briefScenes.Count + (introSeconds > 0 ? 1 : 0);

    // Chapter 1 at 0:00 — the opening hook. (The brief/intro parameters
    // stay for old scripts: videos #1–#4 and COP31 carry brief scenes +
    // a sting in their saved props, and their chapters still derive from
    // the same timeline math. New videos pass empty/0.)
    var introLabel = briefScenes.Count > 0
        ? ChapterLabel(briefScenes[0].Narration)
        : scenes.Count > 0 ? ChapterLabel(scenes[0].Narration) : "Intro";
    chapters.Add($"0:00 - {introLabel}");

    // Visual-group boundaries in the main scenes.
    var boundaries = new List<(double StartSeconds, string Narration)>();
    int? lastGroup = null;
    for (var i = 0; i < scenes.Count; i++)
    {
        var scene = scenes[i];
        if (scene.VisualGroup != lastGroup)
        {
            boundaries.Add((starts[firstScene + i], scene.Narration));
            lastGroup = scene.VisualGroup;
        }
    }

    // Cap the total at MaxChapters. When there are more boundaries than
    // the remaining slots, keep ones evenly spaced in TIME (not by index):
    // divide the span from the first to the last boundary into equal slots
    // and take the boundary nearest each slot. Time spacing keeps chapters
    // comfortably apart (for a video of >= ~2 min, 11 chapters over the
    // span gives >= ~10s each) instead of bunching two group changes that
    // happen to fall a few seconds apart.
    var remaining = MaxChapters - chapters.Count;
    List<(double StartSeconds, string Narration)> selected;
    if (boundaries.Count <= remaining)
    {
        selected = boundaries;
    }
    else
    {
        selected = new List<(double, string)>();
        var firstTime = boundaries[0].StartSeconds;
        var lastTime = boundaries[^1].StartSeconds;
        var span = lastTime - firstTime;
        var used = new HashSet<int>();
        for (var k = 0; k < remaining; k++)
        {
            var target = firstTime + span * k / (remaining - 1);
            var best = -1;
            var bestDist = double.MaxValue;
            for (var j = 0; j < boundaries.Count; j++)
            {
                if (used.Contains(j)) continue;
                var dist = Math.Abs(boundaries[j].StartSeconds - target);
                if (dist < bestDist) { bestDist = dist; best = j; }
            }
            if (best >= 0) { used.Add(best); selected.Add(boundaries[best]); }
        }
        selected = selected.OrderBy(b => b.StartSeconds).ToList();
    }

    // YouTube requires every chapter to be at least 10 seconds long, or it
    // ignores the whole list. Drop a boundary that lands less than 10s
    // after the previously kept chapter (the 0:00 chapter is at 0.0). The
    // time-based selection above makes this a no-op in practice; it is a
    // safety net for a pathological script whose groups all bunch together.
    var kept = new List<(double StartSeconds, string Narration)>();
    var lastKept = 0.0;
    foreach (var boundary in selected)
    {
        if (boundary.StartSeconds - lastKept < 10.0) continue;
        kept.Add(boundary);
        lastKept = boundary.StartSeconds;
    }
    // Re-fill (R5): the filter above can drop the final boundary — or the
    // first selected one — and leave the list short with no last-beat
    // chapter. The re-fill pool is every boundary that is not already kept
    // (a filter-dropped boundary can never qualify — it sits <10s from a
    // kept chapter by construction — but a boundary time-spacing did NOT
    // select can): walk them in time order and add back any that sits
    // >= 10s from the 0:00 chapter and from every chapter already kept,
    // until the cap is reached.
    var refillCandidates = boundaries.Where(b => !kept.Contains(b)).OrderBy(b => b.StartSeconds).ToList();
    foreach (var boundary in refillCandidates)
    {
        if (chapters.Count + kept.Count >= MaxChapters) break;
        if (boundary.StartSeconds < 10.0) continue;
        if (kept.All(k => Math.Abs(k.StartSeconds - boundary.StartSeconds) >= 10.0))
            kept.Add(boundary);
    }
    foreach (var (startSeconds, narration) in kept.OrderBy(k => k.StartSeconds))
    {
        var cursor = TimeSpan.FromSeconds(startSeconds);
        chapters.Add($"{(int)cursor.TotalMinutes}:{cursor.Seconds:00} - {ChapterLabel(narration)}");
    }
    return chapters;
}

// Chapter label: the first complete sentence of a scene's narration.
// A sentence ends at . ! or ? followed by a space and an uppercase letter
// (or the end of the string, optionally past a closing quote — R5: a
// sentence end at the second-to-last character, or followed by a quote,
// used to be missed and the label fell back to the whole narration) — the
// word before the punctuation must not be an abbreviation, so "U.S." and
// "Mr." do not cut the label short. The cap (200 chars, word boundary,
// never mid-word and never with an ellipsis) is a safety net for
// pathological input only — a real scene's first sentence is ~60-150 chars
// and always fits whole, so labels are complete sentences, never fragments.
string ChapterLabel(string narration)
{
    var text = (narration ?? "").Trim();
    if (text.Length == 0) return "Untitled";
    // Abbreviations whose trailing period is not a sentence end. Includes
    // the news-common titles (R5): "Sen. Cruz warned..." must not be cut
    // to just "Sen."
    var abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Mr", "Mrs", "Ms", "Dr", "St", "Jr", "Sr", "vs", "etc", "e.g", "i.e", "U.S", "U.K", "D.C", "a.m", "p.m",
          "Sen", "Rep", "Gov", "Gen", "Prof", "Col" };
    for (var i = 0; i < text.Length; i++)
    {
        var c = text[i];
        if (c is not ('.' or '!' or '?')) continue;
        // A closing quote right after the punctuation still ends the
        // sentence ("...works." then the end, or "...works." "Next...").
        var k = i + 1;
        if (k < text.Length && text[k] is '"' or '\'' or '”' or '’')
        {
            k += 1;
        }
        var isEnd = k == text.Length;
        var nextIsSpaceUpper = k + 1 < text.Length && text[k] == ' ' && char.IsUpper(text[k + 1]);
        if (!isEnd && !nextIsSpaceUpper) continue;
        // Word immediately before this punctuation.
        var wordStart = text.LastIndexOf(' ', i - 1) + 1;
        var word = text[wordStart..i];
        if (word.Length == 1 && char.IsUpper(word[0])) continue; // single-letter abbrev ("U.S.")
        if (abbreviations.Contains(word)) continue;
        return TruncateAtWord(text[..k].Trim(), 200);
    }
    return TruncateAtWord(text, 200);
}

static string TruncateAtWord(string text, int maxLength)
{
    if (text.Length <= maxLength) return text;
    var cut = text.LastIndexOf(' ', maxLength);
    return (cut > 0 ? text[..cut] : text[..maxLength]).TrimEnd();
}

// Canonical hashtags (review 2026-11-22, SEO #6): the long-form
// Description's hashtag line is the single source of truth for a video's
// hashtags. The Short's captions reuse them so hashtags never drift
// between a video and its own Short (#trendline vs #TrendlineNow,
// #gaming vs #gamingnews, off-topic #tech on GTA). The model writes a
// different hashtag set into each caption; this replaces them in code.
static List<string> CanonicalHashtags(string description)
{
    // The hashtag line is the description's line 2 (index 1), 7-12
    // hashtags, channel first then topic — prefer it outright (R6). A
    // keyword-stuffing line elsewhere in the description used to win the
    // max-count contest and beat the intended line 2. Fall back to the
    // max-count line only when line 2 carries no hashtags.
    var lines = (description ?? "").Split('\n');
    if (lines.Length > 1)
    {
        var line2 = ExtractHashtags(lines[1]);
        if (line2.Count > 0) return line2;
    }
    var best = new List<string>();
    foreach (var line in lines)
    {
        var tags = ExtractHashtags(line);
        if (tags.Count > best.Count) best = tags;
    }
    return best;
}

static List<string> ExtractHashtags(string text)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var tags = new List<string>();
    foreach (System.Text.RegularExpressions.Match m in
        System.Text.RegularExpressions.Regex.Matches(text ?? "", @"#[\p{L}\p{N}_]+"))
    {
        if (seen.Add(m.Value)) tags.Add(m.Value);
    }
    return tags;
}

// Rebuild a caption with the canonical hashtags: strip only the TRAILING
// hashtags the model put in, append up to maxHashtags canonical ones (the
// first ones are the most specific — channel, then topic), and fit the
// whole thing to maxLength. The text is fit first and the hashtags
// appended last, so the hashtags are never the part that gets cut. When
// there are no canonical hashtags, the caption is left exactly as the
// model wrote it. Inline hashtags are part of the caption text and are
// kept ("Watch #GTA6 gameplay now" keeps #GTA6) — the old regex stripped
// every hashtag anywhere in the caption (R6).
static string WithCanonicalHashtags(string caption, IReadOnlyList<string> canonical, int maxHashtags, int maxLength)
{
    if (canonical.Count == 0) return Text.Fit(caption ?? "", maxLength);
    var text = System.Text.RegularExpressions.Regex.Replace(caption ?? "", @"(\s+#[\p{L}\p{N}_]+)+\s*$", "");
    text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
    var tags = canonical.Take(maxHashtags).ToList();
    if (tags.Count == 0) return Text.Fit(text, maxLength);
    var tagStr = string.Join(" ", tags);
    var room = maxLength - tagStr.Length - 1;
    // Floor guard (R6): if the canonical tags leave too little room for
    // any caption text, keep the text and drop the tags. The old code
    // clamped room to 0 and returned hashtags only, wiping the caption.
    const int MinCaptionTextRoom = 20;
    if (room < MinCaptionTextRoom) return Text.Fit(text, maxLength);
    var fitted = Text.Fit(text, room, ellipsis: "");
    return $"{fitted} {tagStr}".Trim();
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
    };
}
