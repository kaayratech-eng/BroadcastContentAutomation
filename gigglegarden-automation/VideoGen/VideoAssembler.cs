using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GiggleGarden.Shared;

static partial class VideoAssembler
{
    // Builds each scene clip (image + zoompan + narration audio + wrapped subtitle),
    // crossfades them together, then mixes background music under the narration.
    public static async Task AssembleAsync(
        ContentProfile profile, GenConfig cfg, IReadOnlyList<Scene> scenes, string language,
        string workDir, string outputPath, int w, int h, bool preferVerticalImages,
        string? musicMood = null)
    {
        if (scenes.Count == 0) throw new ArgumentException("No scenes to assemble.", nameof(scenes));

        // Resolved up front, not at the mix step several minutes of encoding later, so a
        // bad path fails before any work is done rather than after all of it.
        var music = ResolveBackgroundMusic(profile, workDir, musicMood);

        var sceneClips = new List<string>();
        var durations = new List<double>();
        var tag = $"{w}x{h}";

        for (var i = 0; i < scenes.Count; i++)
        {
            var s = scenes[i];
            var duration = s.DurationSeconds + 0.5;              // breathing room
            var clip = Path.Combine(workDir, $"clip{i:D2}-{tag}.mp4");

            var image = preferVerticalImages && s.VerticalImagePath is { } vertical && File.Exists(vertical)
                ? vertical
                : s.ImagePath ?? throw new InvalidOperationException($"Scene {i} has no image.");

            var image2 = preferVerticalImages && s.VerticalImagePath2 is { } vertical2 && File.Exists(vertical2)
                ? vertical2
                : s.ImagePath2 is { } landscape2 && File.Exists(landscape2) ? landscape2 : null;

            var fontSize = h > w ? 52 : 46;                       // slightly larger on vertical
            var subtitle = BuildSubtitleFilter(cfg, s.Narration, language, workDir, i, tag, w, h, fontSize);

            // Rotate through 4 distinct Ken Burns moves instead of just 2, so adjacent
            // scenes don't all breathe the same way.
            var pattern = i % 4;

            var videoOnly = image2 is not null
                ? await BuildSplitImageClipAsync(cfg, image, image2, duration, w, h, subtitle, pattern, (pattern + 2) % 4, workDir, tag, i)
                : await BuildImageClipAsync(cfg, image, duration, w, h, subtitle, pattern, Path.Combine(workDir, $"clip{i:D2}-{tag}-video.mp4"));

            // Mux narration audio onto the (video-only, already exactly `duration` long) clip.
            // apad pads the audio with silence to match — using -shortest here (as before)
            // would cut the clip the instant narration audio ends, silently discarding the
            // breathing room and hard-cutting mid-beat into the next scene.
            await RunFfmpegAsync(cfg,
                $"-y -i \"{videoOnly}\" -i \"{s.AudioPath}\" -af \"apad\" -t {Text.Invariant(duration)} " +
                $"-c:v copy -c:a aac -b:a 128k \"{clip}\"");

            sceneClips.Add(clip);
            durations.Add(duration);
        }

        // Crossfade scene clips together instead of hard-cutting between them.
        var concatPath = Path.Combine(workDir, $"concat-{tag}.mp4");
        var total = await BuildCrossfadedConcatAsync(cfg, sceneClips, durations, concatPath);

        await MixBackgroundMusicAsync(profile, cfg, concatPath, music, total, outputPath);
    }

    // Clip-based assembly: each scene is a Vidu-generated animated clip rather than a
    // still with a camera move over it. Same crossfade/subtitle/music treatment as the
    // image path, so the two produce interchangeable output.
    public static async Task AssembleFromClipsAsync(
        ContentProfile profile, GenConfig cfg, IReadOnlyList<Scene> scenes, string language,
        string workDir, string outputPath, int w, int h, string? musicMood = null)
    {
        if (scenes.Count == 0) throw new ArgumentException("No scenes to assemble.", nameof(scenes));

        var music = ResolveBackgroundMusic(profile, workDir, musicMood);

        var sceneClips = new List<string>();
        var durations = new List<double>();
        var tag = $"{w}x{h}";

        for (var i = 0; i < scenes.Count; i++)
        {
            var s = scenes[i];
            if (s.ClipPath is not { } clipPath || !File.Exists(clipPath))
                throw new InvalidOperationException($"Scene {i + 1} has no downloaded clip.");

            var duration = s.DurationSeconds + 0.5;              // breathing room
            var fontSize = h > w ? 52 : 46;
            var subtitle = BuildSubtitleFilter(cfg, s.Narration, language, workDir, i, tag, w, h, fontSize);

            var videoOnly = Path.Combine(workDir, $"clip{i:D2}-{tag}-video.mp4");
            await BuildVideoClipAsync(cfg, clipPath, duration, w, h, subtitle, videoOnly);

            var clip = Path.Combine(workDir, $"clip{i:D2}-{tag}.mp4");
            await RunFfmpegAsync(cfg,
                $"-y -i \"{videoOnly}\" -i \"{s.AudioPath}\" -af \"apad\" -t {Text.Invariant(duration)} " +
                $"-c:v copy -c:a aac -b:a 128k \"{clip}\"");

            sceneClips.Add(clip);
            durations.Add(duration);
        }

        var concatPath = Path.Combine(workDir, $"concat-{tag}.mp4");
        var total = await BuildCrossfadedConcatAsync(cfg, sceneClips, durations, concatPath);

        await MixBackgroundMusicAsync(profile, cfg, concatPath, music, total, outputPath);
    }

    private const double MusicFadeSeconds = 1.5;

    private static readonly string[] MusicExtensions =
        [".mp3", ".m4a", ".wav", ".ogg", ".flac", ".aac", ".opus"];

    // Null means "deliberately no music"; a configured path that isn't there is a
    // mistake and is treated as one.
    //
    // This used to fall through to a silent File.Copy, and the configured path had drifted
    // to a folder that does not exist - so every video ever rendered shipped with no music
    // at all and nothing in the log said so. A misconfiguration that produces a
    // plausible-looking output file is the worst kind, hence the throw.
    //
    // BackgroundMusicPath is a single file or a folder of them; a folder is the useful
    // case, since the same bed under every video on the channel gets old fast.
    //
    // Which track a video gets is a stable hash of its job folder - deliberately not
    // Random, and deliberately not string.GetHashCode, which .NET seeds per process. The
    // 16:9 and 9:16 cuts are rendered by two separate calls and have to land on the same
    // track, and re-running --assemble days later has to reproduce the earlier render.
    //
    // mood (script.MusicMood, e.g. "soft piano lullaby") is tried as a subfolder of
    // BackgroundMusicPath first - {BackgroundMusicPath}/{slugified-mood}/ - so a channel
    // that eventually sources separate calm/energetic pools gets format-appropriate music
    // for free. Until that subfolder exists (or while it's empty), this falls straight
    // through to today's flat-folder behaviour unchanged - no mood pools exist yet, so
    // this is currently a no-op in practice.
    private static string? ResolveBackgroundMusic(ContentProfile profile, string workDir, string? mood = null)
    {
        if (string.IsNullOrWhiteSpace(profile.BackgroundMusicPath)) return null;
        if (File.Exists(profile.BackgroundMusicPath)) return profile.BackgroundMusicPath;

        if (!Directory.Exists(profile.BackgroundMusicPath))
            throw new Exception(
                $"BackgroundMusicPath points at \"{profile.BackgroundMusicPath}\", which does not exist. " +
                "Point it at a track, or at a folder of tracks, or clear it to render without music.");

        var key = Path.GetFileName(workDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (!string.IsNullOrWhiteSpace(mood))
        {
            var moodDir = Path.Combine(profile.BackgroundMusicPath, Slugify(mood));
            if (Directory.Exists(moodDir))
            {
                var moodTrack = PickTrack(moodDir, key);
                if (moodTrack is not null) return moodTrack;
            }
        }

        var track = PickTrack(profile.BackgroundMusicPath, key);
        if (track is null)
            Console.WriteLine(
                $"  Music: none - \"{profile.BackgroundMusicPath}\" has no tracks in it, rendering narration only. " +
                $"Drop instrumentals in ({string.Join(", ", MusicExtensions)}) to get a bed.");
        return track;
    }

    private static string Slugify(string text) =>
        Regex.Replace(text.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    // An empty pool is a legitimate waiting state - the folder is there, the tracks are
    // not sourced yet - so the caller renders without music rather than failing. Said out
    // loud, though: silence here once hid a genuinely broken path for every video the
    // channel had published.
    private static string? PickTrack(string folder, string stableKey)
    {
        var tracks = Directory.EnumerateFiles(folder)
            .Where(f => MusicExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tracks.Count == 0) return null;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(stableKey));
        return tracks[(int)(BitConverter.ToUInt32(digest, 0) % (uint)tracks.Count)];
    }

    // Measured once per track per process: both orientations of a video mix the same
    // track, and there is no sense paying for the scan twice.
    private static readonly Dictionary<string, double> LoudnessCache = new(StringComparer.OrdinalIgnoreCase);

    // Tracks arrive at whatever level whoever mastered them felt like. The starter track
    // measures -8.6 LUFS; library instrumentals commonly sit nearer -20. A fixed volume
    // multiplier tuned against one of those makes the other either inaudible or intrusive,
    // which is exactly the failure mode a folder full of mixed-provenance downloads
    // invites. So each track is measured and given the precise gain that puts it on the
    // configured bed level, whatever it started at.
    private static async Task<double> MeasureLoudnessAsync(GenConfig cfg, string path)
    {
        if (LoudnessCache.TryGetValue(path, out var cached)) return cached;

        var stderr = await RunFfmpegAsync(cfg, $"-hide_banner -i \"{path}\" -af ebur128=framelog=quiet -f null -");

        // The summary block prints as "  I:         -8.6 LUFS".
        var match = IntegratedLoudness().Match(stderr);
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs))
            throw new Exception(
                $"Could not read the loudness of {Path.GetFileName(path)}. " +
                $"It may not be a valid audio file:\n{Text.Tail(stderr, 500)}");

        return LoudnessCache[path] = lufs;
    }

    [GeneratedRegex(@"I:\s*(-?\d+(?:\.\d+)?)\s*LUFS")]
    private static partial Regex IntegratedLoudness();

    // Lays the music bed under the finished narration track.
    //
    // sidechaincompress does real ducking - the music dips whenever the narrator speaks
    // and comes back in the gaps - so the bed can stay soft without disappearing entirely
    // underneath the voice.
    //
    // The pan upmix is not cosmetic: Azure returns mono narration, and letting ffmpeg
    // rematrix mono to stereo on its own applies its usual -3 dB power-preserving gain,
    // so simply mixing in stereo music made the voice measurably quieter than before.
    // pan copies the single channel to both at unity instead.
    private static async Task MixBackgroundMusicAsync(
        ContentProfile profile, GenConfig cfg, string concatPath, string? musicPath, double totalSeconds, string outputPath)
    {
        if (musicPath is null)
        {
            File.Copy(concatPath, outputPath, overwrite: true);
            return;
        }

        var gainDb = profile.BackgroundMusicLufs - await MeasureLoudnessAsync(cfg, musicPath);
        var fadeOutAt = Math.Max(0, totalSeconds - MusicFadeSeconds);

        Console.WriteLine($"  Music: {Path.GetFileName(musicPath)} at {profile.BackgroundMusicLufs:0.#} LUFS ({gainDb:+0.#;-0.#} dB)");

        var filter =
            "[0:a]aresample=44100,aformat=sample_fmts=fltp,pan=stereo|c0=c0|c1=c0,asplit=2[voice][key];" +
            "[1:a]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo," +
            $"volume={Text.Invariant(gainDb)}dB," +
            $"afade=t=in:st=0:d={Text.Invariant(MusicFadeSeconds)}," +
            $"afade=t=out:st={Text.Invariant(fadeOutAt)}:d={Text.Invariant(MusicFadeSeconds)}[music];" +
            "[music][key]sidechaincompress=threshold=0.05:ratio=6:attack=20:release=400[ducked];" +
            "[voice][ducked]amix=inputs=2:duration=first:dropout_transition=0:normalize=0," +
            "alimiter=limit=0.95:level=false[a]";

        // -c:v copy keeps the bitrate cap applied during concat; only audio is re-encoded.
        await RunFfmpegAsync(cfg,
            $"-y -i \"{concatPath}\" -stream_loop -1 -i \"{musicPath}\" " +
            $"-filter_complex \"{filter}\" " +
            $"-map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 160k -ar 44100 \"{outputPath}\"");
    }

    // Fits a generated clip to its scene's exact duration and burns in the subtitle.
    // tpad clones the final frame when the clip runs marginally short of its narration
    // (clip length is a whole number of seconds, narration is not), and -t trims the
    // usual case where it runs over; without the pad, a scene whose narration rounds
    // just past the clip length ends on a black frame.
    private static async Task BuildVideoClipAsync(
        GenConfig cfg, string sourceClip, double duration, int w, int h, string subtitleFilter, string outputPath)
    {
        // Vidu renders vertical; the 16:9 master puts that pillar on a blurred blow-up
        // of itself rather than hard black bars, which reads as deliberate on YouTube.
        var fit = w > h
            ? $"split[bg][fg];[bg]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},gblur=sigma=28[bgb];" +
              $"[fg]scale=-2:{h}[fgs];[bgb][fgs]overlay=(W-w)/2:0"
            : $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}";

        var vf = $"{fit},tpad=stop_mode=clone:stop_duration=3{subtitleFilter}";

        await RunFfmpegAsync(cfg,
            $"-y -i \"{sourceClip}\" -t {Text.Invariant(duration)} " +
            $"-filter_complex \"[0:v]{vf}[v]\" -map \"[v]\" -r 25 -pix_fmt yuv420p " +
            $"-an -c:v libx264 -preset medium -crf 20 \"{outputPath}\"");
    }

    // The last frame of scene N seeds scene N+1's generation, which is what keeps the
    // mascot on-model across a whole video without paying for reference-to-video mode.
    // JPEG rather than PNG purely to keep the base64 request payload small.
    public static async Task ExtractLastFrameAsync(GenConfig cfg, string videoPath, string outputPath)
    {
        await RunFfmpegAsync(cfg,
            $"-y -sseof -0.4 -i \"{videoPath}\" -update 1 -frames:v 1 -q:v 3 \"{outputPath}\"");

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new Exception($"Could not extract a last frame from {Path.GetFileName(videoPath)}.");
    }

    // Thumbnail source frame, pulled from the finished render instead of a separately
    // generated still - the art is already on-model and it costs nothing.
    public static async Task ExtractThumbnailFrameAsync(GenConfig cfg, string videoPath, double atSeconds, string outputPath)
    {
        await RunFfmpegAsync(cfg,
            $"-y -ss {Text.Invariant(atSeconds)} -i \"{videoPath}\" -update 1 -frames:v 1 -q:v 2 \"{outputPath}\"");

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new Exception($"Could not extract a thumbnail frame from {Path.GetFileName(videoPath)}.");
    }

    // Custom YouTube thumbnail: a straight crop of the base illustration to YouTube's
    // canonical size, no overlay text. Every thumbnail sampled from the researched
    // channels carried none - the enormous face and oversized object the image prompt
    // already asks for are what get the click, and the audience can't read anyway.
    public static async Task BuildThumbnailAsync(GenConfig cfg, string baseImagePath, string outputPath)
    {
        const int w = 1280, h = 720; // YouTube's canonical thumbnail size

        await RunFfmpegAsync(cfg,
            $"-y -i \"{baseImagePath}\" -vf \"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}\" " +
            $"-frames:v 1 -q:v 2 \"{outputPath}\"");
    }

    // Real caption file (SRT) alongside the burned-in subtitles: gives platforms and
    // accessibility tools indexable/toggleable caption text, which drawtext (burned into
    // the pixels) cannot provide. Uses the same per-scene duration math as the clips
    // above (DurationSeconds + 0.5s breathing room) so cue timing matches the video.
    public static async Task WriteSrtAsync(IReadOnlyList<Scene> scenes, string outputPath)
    {
        var sb = new StringBuilder();
        var cursor = TimeSpan.Zero;

        for (var i = 0; i < scenes.Count; i++)
        {
            var duration = TimeSpan.FromSeconds(scenes[i].DurationSeconds + 0.5);
            var end = cursor + duration;

            sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{FormatSrtTime(cursor)} --> {FormatSrtTime(end)}");
            sb.AppendLine(scenes[i].Narration);
            sb.AppendLine();

            cursor = end;
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static string FormatSrtTime(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";

    // drawtext performs no word wrapping of its own, so a full sentence at fontsize 46+
    // runs off both edges of the frame. Wrap into lines and stack one drawtext per line.
    //
    // The text is passed via textfile= rather than text=, which sidesteps drawtext's
    // escaping rules entirely — important for Devanagari and Gurmukhi, and for the
    // apostrophes, colons and commas that show up in ordinary narration.
    private static string BuildSubtitleFilter(
        GenConfig cfg, string narration, string language,
        string workDir, int sceneIndex, string tag, int w, int h, int fontSize)
    {
        var perLine = Text.CharsPerLineFor(language, w, fontSize);
        var lines = Text.WrapLines(narration, perLine, cfg.SubtitleMaxLines);
        if (lines.Count == 0) return "";

        var lineHeight = (int)(fontSize * 1.35);
        var bottomMargin = h > w ? 220 : 70;   // clear of the Reels/TikTok caption UI
        var font = FilterPath(cfg.SubtitleFontPath);
        var sb = new StringBuilder();

        for (var line = 0; line < lines.Count; line++)
        {
            var textFile = Path.Combine(workDir, $"sub-{tag}-{sceneIndex:D2}-{line}.txt");
            File.WriteAllText(textFile, lines[line], new UTF8Encoding(false));

            var y = h - bottomMargin - (lines.Count - line) * lineHeight;

            sb.Append($",drawtext=textfile='{FilterPath(textFile)}':fontfile='{font}':")
              .Append($"fontsize={fontSize}:fontcolor=white:borderw=3:bordercolor=black:")
              .Append($"x=(w-text_w)/2:y={y}");
        }

        return sb.ToString();
    }

    // 4 distinct moves instead of the original 2 (zoom-in/zoom-out only), so a run of
    // scenes doesn't all breathe identically. Pan patterns hold zoom constant at a mild
    // 1.12 and drift the crop window across the frame, clamped to stay in-bounds — d is
    // a deliberately oversized frame budget (safe up to ~50s of output; real clips are
    // always shorter) so -t truncates the motion mid-sweep rather than looping it.
    private static string ZoomPanFilter(int pattern) => pattern switch
    {
        0 => "zoompan=z='min(zoom+0.0012,1.15)':d=125*10:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)'",
        1 => "zoompan=z='if(lte(zoom,1.0),1.15,max(1.0,zoom-0.0012))':d=125*10:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)'",
        2 => "zoompan=z='1.12':d=125*10:x='max(0,(iw-iw/zoom)-on*1.5)':y='ih/2-(ih/zoom/2)'",
        _ => "zoompan=z='1.12':d=125*10:x='min((iw-iw/zoom),on*1.5)':y='ih/2-(ih/zoom/2)'",
    };

    // Renders one still image into a video-only (no audio) clip of exactly `duration`
    // seconds with a Ken Burns move and the burned-in subtitle applied.
    private static async Task<string> BuildImageClipAsync(
        GenConfig cfg, string image, double duration, int w, int h, string subtitleFilter, int pattern, string outputPath)
    {
        var vf = $"scale={w * 2}:{h * 2}:force_original_aspect_ratio=increase," +
                 $"crop={w * 2}:{h * 2},{ZoomPanFilter(pattern)},scale={w}:{h}{subtitleFilter}";

        await RunFfmpegAsync(cfg,
            $"-y -loop 1 -i \"{image}\" -t {Text.Invariant(duration)} " +
            $"-vf \"{vf}\" -r 25 -pix_fmt yuv420p -an -c:v libx264 -preset medium -crf 20 \"{outputPath}\"");

        return outputPath;
    }

    // Long-narration scenes get two images instead of one: build each half as its own
    // Ken Burns clip, then crossfade between them internally so a single static photo
    // doesn't have to carry the whole line. Each half runs slightly over duration/2 so
    // the crossfade's overlap doesn't shorten the combined clip below `duration`.
    private static async Task<string> BuildSplitImageClipAsync(
        GenConfig cfg, string imageA, string imageB, double duration, int w, int h, string subtitleFilter,
        int patternA, int patternB, string workDir, string tag, int sceneIndex)
    {
        const double innerFade = 0.35;
        var half = duration / 2 + innerFade / 2;

        var subA = await BuildImageClipAsync(cfg, imageA, half, w, h, subtitleFilter, patternA,
            Path.Combine(workDir, $"clip{sceneIndex:D2}-{tag}-a.mp4"));
        var subB = await BuildImageClipAsync(cfg, imageB, half, w, h, subtitleFilter, patternB,
            Path.Combine(workDir, $"clip{sceneIndex:D2}-{tag}-b.mp4"));

        var combined = Path.Combine(workDir, $"clip{sceneIndex:D2}-{tag}-combined.mp4");
        var offset = half - innerFade;
        await RunFfmpegAsync(cfg,
            $"-y -i \"{subA}\" -i \"{subB}\" " +
            $"-filter_complex \"[0:v][1:v]xfade=transition=fade:duration={Text.Invariant(innerFade)}:offset={Text.Invariant(offset)}[v]\" " +
            $"-map \"[v]\" -r 25 -pix_fmt yuv420p -c:v libx264 -preset medium -crf 20 \"{combined}\"");

        return combined;
    }

    // Replaces a hard-cut concat with a real dissolve between every scene. Each input
    // clip's exact duration is already known (set by the apad fix above), so the
    // pairwise xfade/acrossfade offsets can be computed directly instead of probed.
    // Returns the finished length, which the caller needs to time the music fade-out.
    private static async Task<double> BuildCrossfadedConcatAsync(
        GenConfig cfg, IReadOnlyList<string> clips, IReadOnlyList<double> durations, string outputPath)
    {
        if (clips.Count == 1)
        {
            File.Copy(clips[0], outputPath, overwrite: true);
            return durations[0];
        }

        const double fade = 0.4;
        var inputs = string.Join(" ", clips.Select(c => $"-i \"{c}\""));
        var vLabel = "0:v";
        var aLabel = "0:a";
        var cumulative = durations[0];
        var filters = new List<string>();

        for (var k = 1; k < clips.Count; k++)
        {
            var offset = cumulative - fade;
            var nextV = $"v{k}";
            var nextA = $"a{k}";

            filters.Add($"[{vLabel}][{k}:v]xfade=transition=fade:duration={Text.Invariant(fade)}:offset={Text.Invariant(offset)}[{nextV}]");
            filters.Add($"[{aLabel}][{k}:a]acrossfade=d={Text.Invariant(fade)}[{nextA}]");

            vLabel = nextV;
            aLabel = nextA;
            cumulative = cumulative + durations[k] - fade;
        }

        // Bitrate is deliberately capped here rather than left to CRF alone. The source
        // is 540p upscaled, so a high bitrate buys no visible quality on flat cartoon
        // art, and an 80-second render at ~3.8 Mbps came out at 36 MB - large enough
        // that Instagram's upload endpoint rejected it outright with a bare 400. The
        // same video at ~1.8 Mbps published without complaint and looks identical.
        await RunFfmpegAsync(cfg,
            $"-y {inputs} -filter_complex \"{string.Join(";", filters)}\" " +
            $"-map \"[{vLabel}]\" -map \"[{aLabel}]\" " +
            $"-r 25 -pix_fmt yuv420p -c:v libx264 -preset medium -crf 23 -maxrate 2200k -bufsize 4400k " +
            $"-c:a aac -b:a 128k -ar 44100 \"{outputPath}\"");

        return cumulative;
    }

    // FFmpeg filter arguments need forward slashes and an escaped drive colon on Windows.
    private static string FilterPath(string path) => path.Replace('\\', '/').Replace(":", "\\:");

    // Vidu's output aspect ratio follows its input image's, and everything downstream
    // assumes 9:16. Generated character art comes back 2:3 and pooled art arrives at
    // whatever size its artist drew it, so both are fitted to 1080x1920 first.
    //
    // Fits inside and fills the gap with a blurred copy rather than cropping to fill:
    // a crop on a full-body character takes the top of its head or its feet off, and
    // that frame is the one every scene in the video animates from.
    public static async Task FitToPortraitAsync(GenConfig cfg, string sourcePath, string outputPath)
    {
        const string filter =
            "split[bg][fg];" +
            "[bg]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,gblur=sigma=28[bgb];" +
            "[fg]scale=1080:1920:force_original_aspect_ratio=decrease[fgs];" +
            "[bgb][fgs]overlay=(W-w)/2:(H-h)/2";

        await RunFfmpegAsync(cfg, $"-y -i \"{sourcePath}\" -filter_complex \"{filter}\" -frames:v 1 \"{outputPath}\"");
    }

    public static async Task<double> GetAudioDurationAsync(GenConfig cfg, string audioPath)
    {
        var ffprobe = string.IsNullOrEmpty(cfg.FfprobePath) ? "ffprobe" : cfg.FfprobePath;
        var psi = new ProcessStartInfo(ffprobe,
            $"-v error -show_entries format=duration -of csv=p=0 \"{audioPath}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

        using var p = StartOrThrow(psi, ffprobe);
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        // Without this check, a missing or failing ffprobe surfaces as an opaque
        // FormatException from double.Parse("") rather than the real cause.
        if (p.ExitCode != 0)
            throw new Exception($"ffprobe failed (exit {p.ExitCode}) on {Path.GetFileName(audioPath)}:\n{Text.Tail(stderr, 500)}");

        if (!double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            throw new Exception($"ffprobe returned no usable duration for {Path.GetFileName(audioPath)} (got \"{stdout.Trim()}\").");

        return seconds;
    }

    // Returns stderr, which is where ffmpeg puts everything interesting - including the
    // ebur128 loudness summary the music mix reads back.
    private static async Task<string> RunFfmpegAsync(GenConfig cfg, string arguments)
    {
        var ffmpeg = string.IsNullOrEmpty(cfg.FfmpegPath) ? "ffmpeg" : cfg.FfmpegPath;
        var psi = new ProcessStartInfo(ffmpeg, arguments) { RedirectStandardError = true, UseShellExecute = false };

        using var p = StartOrThrow(psi, ffmpeg);
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        if (p.ExitCode != 0)
            throw new Exception($"ffmpeg failed (exit {p.ExitCode}):\n{Text.Tail(err, 2000)}");

        return err;
    }

    private static Process StartOrThrow(ProcessStartInfo psi, string exe)
    {
        try
        {
            return Process.Start(psi) ?? throw new Exception($"Could not start {exe}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new Exception(
                $"'{exe}' was not found. Install FFmpeg from https://www.gyan.dev/ffmpeg/builds/ " +
                $"and add its bin folder to PATH, or set FfmpegPath/FfprobePath in appsettings.json.", ex);
        }
    }
}
