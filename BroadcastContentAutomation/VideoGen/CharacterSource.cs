using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// A video's character: the picture Vidu animates, plus the words the script and every
// scene prompt describe it with.
//
// The two travel together on purpose. The old pipeline let the script invent a name
// and species per video while the art stayed one fixed duckling, so the narration and
// the picture were describing different characters. Whichever way a character is
// obtained here, the description returned is the description of the actual image.
//
// Catchphrase is optional and pool-only: a signature line that follows a cast member
// across videos so it reads as the same character rather than a design that happens to
// repeat. Null for a freshly-invented character that hasn't been asked for one, or for
// an older pool sidecar written before this field existed.
record CharacterBrief(string Name, string Description, string ImagePath, string? Catchphrase = null);

// Supplies the character for a video, either by picking one out of a pool or by
// drawing the one the script invented.
static class CharacterSource
{
    // Sidecar beside each pooled image: milo-fox.png + milo-fox.json. Source and
    // licence are not read by anything - they are here because a monetised kids'
    // channel needs to be able to answer where a character came from, and the answer
    // has to live next to the file rather than in someone's memory.
    private sealed record PoolEntry(string? Name, string? Description, string? Catchphrase, string? Source, string? Licence);

    // Null means "no pool configured, invent one instead" - the normal path.
    //
    // Which character a video gets is a stable hash of its job folder, matching how
    // background music is picked: --assemble may run days after --prep and has to
    // reproduce the same choice, and a resubmitted scene must not come back as a
    // different character.
    public static async Task<CharacterBrief?> FromPool(
        ContentProfile profile, GenConfig cfg, string workDir, IReadOnlySet<string>? exclude = null)
    {
        if (string.IsNullOrWhiteSpace(profile.CharacterPoolPath)) return null;

        var images = File.Exists(profile.CharacterPoolPath)
            ? [profile.CharacterPoolPath]
            : Directory.EnumerateFiles(profile.CharacterPoolPath)
                .Where(f => ManualArtClient.ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // An empty folder is a legitimate waiting state - the pool exists, the art is
        // not sourced yet - so it falls back to drawing one rather than failing.
        if (images.Count == 0)
        {
            Console.WriteLine(
                $"  Character: \"{profile.CharacterPoolPath}\" has no images in it, drawing one instead. " +
                $"Drop character art in ({string.Join(", ", ManualArtClient.ImageExtensions)}) with a matching .json to use a pool.");
            return null;
        }

        // A character on cooldown (used too recently, see RecentPoolPicks) is skipped -
        // unless that would leave nothing to pick from, since a pool smaller than the
        // cooldown window must still produce someone.
        var eligible = exclude is { Count: > 0 }
            ? images.Where(f => !exclude.Contains(Path.GetFullPath(f))).ToList()
            : images;
        if (eligible.Count == 0) eligible = images;

        var picked = eligible[StableIndex(workDir, eligible.Count)];
        var sidecarPath = Path.ChangeExtension(picked, ".json");

        if (!File.Exists(sidecarPath))
            throw new FileNotFoundException(
                $"{Path.GetFileName(picked)} has no {Path.GetFileName(sidecarPath)} beside it. The script is " +
                "written about whatever is in the picture, so each pooled character needs a sidecar giving its " +
                "name and appearance:\n" +
                """
                  { "name": "Milo the Fox", "description": "a small orange fox cub with a cream chest, big
                    round eyes and a blue woolly scarf", "source": "https://...", "licence": "CC0" }
                """,
                sidecarPath);

        var entry = JsonSerializer.Deserialize<PoolEntry>(File.ReadAllText(sidecarPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (string.IsNullOrWhiteSpace(entry?.Name) || string.IsNullOrWhiteSpace(entry.Description))
            throw new Exception($"{Path.GetFileName(sidecarPath)} needs both a \"name\" and a \"description\".");

        var imagePath = await EnsurePortraitAsync(cfg, picked);

        Console.WriteLine($"  Character: {entry.Name} from the pool ({Path.GetFileName(picked)})");
        return new CharacterBrief(entry.Name, entry.Description, imagePath, entry.Catchphrase);
    }

    // DrawAsync always fits a freshly-generated character to true 1080x1920 before it
    // reaches the pool (SaveToPool), but a pool is also meant to take hand-dropped art
    // (see FromPool's "no images in it" message above) - nothing enforced that art
    // actually be portrait. A landscape or square image sent to Vidu as the start frame
    // comes back landscape too, and the vertical assembly's fill+crop (VideoAssembler.
    // BuildVideoClipAsync) then has to cut most of the width off to force it back to
    // 9:16. Fitting in place here, once, the first time a mis-shaped pool image is
    // picked, means it never needs fixing again and every later video that draws this
    // character gets it for free.
    private static async Task<string> EnsurePortraitAsync(GenConfig cfg, string imagePath)
    {
        var (width, height) = await VideoAssembler.GetImageDimensionsAsync(cfg, imagePath);
        if (Math.Abs(width / (double)height - 9.0 / 16.0) < 0.02) return imagePath;

        Console.WriteLine(
            $"  Character: {Path.GetFileName(imagePath)} is {width}x{height}, not 9:16 - fitting to portrait in place.");

        // Always lands on .png regardless of the source extension - matches what
        // DrawAsync/SaveToPool already produce for every other pool entry. The sidecar
        // is found by base name (Path.ChangeExtension(picked, ".json")), so renaming the
        // image here doesn't orphan it. Fits into a distinct temp file first since ffmpeg
        // is still reading imagePath as the source while writing it.
        var directory = Path.GetDirectoryName(imagePath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(imagePath);
        var temp = Path.Combine(directory, baseName + "-fitting.png");
        var finalPath = Path.Combine(directory, baseName + ".png");

        await VideoAssembler.FitToPortraitAsync(cfg, imagePath, temp);
        File.Delete(imagePath);
        File.Move(temp, finalPath, overwrite: true);

        return finalPath;
    }

    // Draws the character the script just invented, so the picture matches the words
    // rather than the other way round.
    //
    // Goes through the human-in-the-loop Gemini prompt-and-pickup loop (ManualArtClient)
    // rather than ImageClient's OpenAI/Stability calls - Deliverable 6 (tasks/todo.md)
    // moved both profiles' character art off paid image APIs onto free, by-hand
    // generation. ImageClient's automated code stays in the repo but is now dormant;
    // BuildPrompt is the one piece of it this path still reuses.
    public static async Task<string> DrawAsync(ContentProfile profile, GenConfig cfg, VideoScript script, string workDir)
    {
        var raw = Path.Combine(workDir, "character-raw.png");
        var fitted = Path.Combine(workDir, "character.png");

        Console.WriteLine($"  Character: drawing {script.CharacterName}");

        // One picture, and every scene animates from it. The alternative - chaining each
        // scene off the previous clip's last frame - is serial, and off-peak Vidu only
        // promises delivery within 48 hours per clip, so a nine-clip chain is up to
        // eighteen days. Seeding every scene from one frame keeps submission parallel
        // and has no generation-to-generation drift either.
        var prompt = ImageClient.BuildPrompt(
            $"{script.CharacterName}: {script.CharacterDescription}. Full body, standing, facing the viewer " +
            "with a friendly welcoming pose, the whole character visible with space around it, plain " +
            "uncluttered background.",
            profile.CharacterStyle, profile.CharacterPortraitStyleSuffix, ImageClient.Orientation.Portrait, matchReference: false);
        var saved = await ManualArtClient.PromptAndWaitAsync(prompt, raw, ImageClient.Orientation.Portrait);

        await VideoAssembler.FitToPortraitAsync(cfg, saved, fitted);
        return fitted;
    }

    // Character names used by recent videos, so the invent-a-new-character prompt can
    // be told not to repeat one. Each generation is otherwise independent and has no
    // memory of earlier runs - without this, two unrelated videos can both land on
    // "Pip" for two different animals purely by chance.
    //
    // Best-effort: a folder with no script.json yet (mid --prep) or one that fails to
    // parse (hand-edited, from before CharacterName existed) is skipped rather than
    // thrown on - this is flavour for the prompt, not something that should block a run.
    public static List<string> RecentNames(GenConfig cfg, string currentWorkDir, int max = 20)
    {
        if (!Directory.Exists(cfg.WorkDirectory)) return [];

        var jobDirs = Directory.EnumerateDirectories(cfg.WorkDirectory, "job-*")
            .Where(d => !Path.GetFullPath(d).Equals(Path.GetFullPath(currentWorkDir), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);

        var names = new List<string>();
        foreach (var dir in jobDirs)
        {
            if (names.Count >= max) break;

            var scriptPath = Path.Combine(dir, "script.json");
            if (!File.Exists(scriptPath)) continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(scriptPath));
                if (doc.RootElement.TryGetProperty("CharacterName", out var nameEl) &&
                    nameEl.GetString() is { Length: > 0 } name &&
                    !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
            catch (JsonException)
            {
            }
        }

        return names;
    }

    // How many characters are already in the pool, so Resolve knows whether it's still
    // building up, mixing, or full. 0 when CharacterPoolPath isn't a folder (empty, or a
    // single pinned file - neither has a "size" in this sense).
    public static int PoolSize(ContentProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.CharacterPoolPath) || !Directory.Exists(profile.CharacterPoolPath))
            return 0;

        return Directory.EnumerateFiles(profile.CharacterPoolPath)
            .Count(f => ManualArtClient.ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
    }

    // Copies a just-drawn character into the pool so a later video can reuse it instead
    // of every video being a stranger. imagePath is DrawAsync's already-1080x1920-fitted
    // character.png - nothing here resizes it. Writes the sidecar FromPool already knows
    // how to read, so no changes were needed on the read side.
    public static void SaveToPool(ContentProfile profile, VideoScript script, string imagePath)
    {
        var slug = Slugify(script.CharacterName);
        var extension = Path.GetExtension(imagePath);

        var destination = Path.Combine(profile.CharacterPoolPath, slug + extension);
        for (var suffix = 2; File.Exists(destination) || File.Exists(Path.ChangeExtension(destination, ".json")); suffix++)
            destination = Path.Combine(profile.CharacterPoolPath, $"{slug}-{suffix}{extension}");

        File.Copy(imagePath, destination);
        File.WriteAllText(Path.ChangeExtension(destination, ".json"), JsonSerializer.Serialize(
            new {
                name = script.CharacterName, description = script.CharacterDescription,
                catchphrase = script.CharacterCatchphrase, source = "generated", licence = "internal",
            },
            new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"  Character: saved {script.CharacterName} to the pool ({Path.GetFileName(destination)})");
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder();
        var lastWasDash = false;
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); lastWasDash = false; }
            else if (!lastWasDash) { sb.Append('-'); lastWasDash = true; }
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 0 ? slug : "character";
    }

    // Which pool images recent videos picked, so a reuse pick can skip anyone who
    // appeared too recently - the cooldown window, in image-path form. Only counts
    // paths that actually live under CharacterPoolPath; a job that invented its own
    // one-off character doesn't put anyone on cooldown. Best-effort like RecentNames: a
    // job with no script.json yet, or one that fails to parse, is skipped rather than
    // thrown on.
    public static HashSet<string> RecentPoolPicks(ContentProfile profile, GenConfig cfg, string currentWorkDir, int max)
    {
        var picks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(cfg.WorkDirectory) || string.IsNullOrWhiteSpace(profile.CharacterPoolPath)) return picks;

        var poolPath = Path.GetFullPath(profile.CharacterPoolPath);

        var jobDirs = Directory.EnumerateDirectories(cfg.WorkDirectory, "job-*")
            .Where(d => !Path.GetFullPath(d).Equals(Path.GetFullPath(currentWorkDir), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);

        foreach (var dir in jobDirs)
        {
            if (picks.Count >= max) break;

            var scriptPath = Path.Combine(dir, "script.json");
            if (!File.Exists(scriptPath)) continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(scriptPath));
                if (doc.RootElement.TryGetProperty("CharacterImagePath", out var pathEl) &&
                    pathEl.GetString() is { Length: > 0 } imagePath)
                {
                    var full = Path.GetFullPath(imagePath);
                    if (full.StartsWith(poolPath, StringComparison.OrdinalIgnoreCase))
                        picks.Add(full);
                }
            }
            catch (JsonException)
            {
            }
        }

        return picks;
    }

    // The build-up/mix/reuse policy: invent while the pool is small, mix once there's
    // enough to draw from, stop inventing once it's full - so the cast grows on its own
    // instead of staying a stranger every video. Only CharacterPoolPath being a *folder*
    // engages this; empty (always invent) and a single file (always pin) are exactly as
    // they were before this existed.
    public static async Task<(CharacterBrief? Pooled, bool AddToPool)> Resolve(ContentProfile profile, GenConfig cfg, string workDir)
    {
        if (string.IsNullOrWhiteSpace(profile.CharacterPoolPath)) return (null, false);
        if (File.Exists(profile.CharacterPoolPath)) return (await FromPool(profile, cfg, workDir), false);

        var size = PoolSize(profile);
        if (size < profile.CharacterPoolBuildupSize) return (null, true);

        if (size >= profile.CharacterPoolMaxSize)
            return (await FromPool(profile, cfg, workDir, RecentPoolPicks(profile, cfg, workDir, profile.CharacterPoolCooldown)), false);

        // The mixing zone: still occasionally let a new character in, but mostly reuse.
        // This flip doesn't need to be reproducible across --prep/--assemble the way the
        // pool *pick itself* does - it's decided once, and the outcome (character name
        // and image) is what actually gets persisted into script.json for everything
        // downstream.
        return Random.Shared.Next(2) == 0
            ? (null, true)
            : (await FromPool(profile, cfg, workDir, RecentPoolPicks(profile, cfg, workDir, profile.CharacterPoolCooldown)), false);
    }

    private static int StableIndex(string workDir, int count)
    {
        // Not Random, and not string.GetHashCode - .NET Core seeds that per process, so
        // it cannot reproduce a choice across the --prep / --assemble split.
        var key = Path.GetFileName(workDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return (int)(BitConverter.ToUInt32(digest, 0) % (uint)count);
    }
}
