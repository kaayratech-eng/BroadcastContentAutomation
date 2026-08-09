using System.Diagnostics;
using System.Globalization;
using System.Text;
using GiggleGarden.Shared;

static class VideoAssembler
{
    // Builds each scene clip (image + zoompan + narration audio + wrapped subtitle),
    // crossfades them together, then mixes background music under the narration.
    public static async Task AssembleAsync(
        GenConfig cfg, IReadOnlyList<Scene> scenes, string language,
        string workDir, string outputPath, int w, int h, bool preferVerticalImages)
    {
        if (scenes.Count == 0) throw new ArgumentException("No scenes to assemble.", nameof(scenes));

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
        await BuildCrossfadedConcatAsync(cfg, sceneClips, durations, concatPath);

        // Mix background music (looped, ducked under narration) if provided
        if (!string.IsNullOrEmpty(cfg.BackgroundMusicPath) && File.Exists(cfg.BackgroundMusicPath))
        {
            await RunFfmpegAsync(cfg,
                $"-y -i \"{concatPath}\" -stream_loop -1 -i \"{cfg.BackgroundMusicPath}\" " +
                "-filter_complex \"[1:a]volume=0.12[m];[0:a][m]amix=inputs=2:duration=first:dropout_transition=2[a]\" " +
                $"-map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 160k -ar 44100 \"{outputPath}\"");
        }
        else
        {
            File.Copy(concatPath, outputPath, overwrite: true);
        }
    }

    // Clip-based assembly: each scene is a Vidu-generated animated clip rather than a
    // still with a camera move over it. Same crossfade/subtitle/music treatment as the
    // image path, so the two produce interchangeable output.
    public static async Task AssembleFromClipsAsync(
        GenConfig cfg, IReadOnlyList<Scene> scenes, string language,
        string workDir, string outputPath, int w, int h)
    {
        if (scenes.Count == 0) throw new ArgumentException("No scenes to assemble.", nameof(scenes));

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
        await BuildCrossfadedConcatAsync(cfg, sceneClips, durations, concatPath);

        if (!string.IsNullOrEmpty(cfg.BackgroundMusicPath) && File.Exists(cfg.BackgroundMusicPath))
        {
            await RunFfmpegAsync(cfg,
                $"-y -i \"{concatPath}\" -stream_loop -1 -i \"{cfg.BackgroundMusicPath}\" " +
                "-filter_complex \"[1:a]volume=0.12[m];[0:a][m]amix=inputs=2:duration=first:dropout_transition=2[a]\" " +
                $"-map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 160k -ar 44100 \"{outputPath}\"");
        }
        else
        {
            File.Copy(concatPath, outputPath, overwrite: true);
        }
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

    // Custom YouTube thumbnail: the base illustration (already generated with the same
    // "empty lower third" framing used for subtitles) plus a bold, high-contrast overlay
    // phrase. Thumbnail is the single biggest click-through lever on YouTube, so this is
    // worth a dedicated pass rather than letting the platform auto-pick a video frame.
    public static async Task BuildThumbnailAsync(
        GenConfig cfg, string baseImagePath, string text, string language, string outputPath)
    {
        const int w = 1280, h = 720; // YouTube's canonical thumbnail size
        const int fontSize = 96;
        var workDir = Path.GetDirectoryName(outputPath)!;

        var perLine = Text.CharsPerLineFor(language, w, fontSize);
        var lines = Text.WrapLines(text, perLine, maxLines: 2);
        if (lines.Count == 0) lines = [text];

        var lineHeight = (int)(fontSize * 1.3);
        var font = FilterPath(cfg.SubtitleFontPath);
        var vf = new StringBuilder($"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}");

        for (var i = 0; i < lines.Count; i++)
        {
            var textFile = Path.Combine(workDir, $"thumb-text-{i}.txt");
            File.WriteAllText(textFile, lines[i], new UTF8Encoding(false));
            var y = h - 70 - (lines.Count - i) * lineHeight;

            vf.Append($",drawtext=textfile='{FilterPath(textFile)}':fontfile='{font}':")
              .Append($"fontsize={fontSize}:fontcolor=yellow:borderw=6:bordercolor=black:")
              .Append("box=1:boxcolor=black@0.45:boxborderw=18:")
              .Append($"x=(w-text_w)/2:y={y}");
        }

        await RunFfmpegAsync(cfg, $"-y -i \"{baseImagePath}\" -vf \"{vf}\" -frames:v 1 -q:v 2 \"{outputPath}\"");
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
    private static async Task BuildCrossfadedConcatAsync(
        GenConfig cfg, IReadOnlyList<string> clips, IReadOnlyList<double> durations, string outputPath)
    {
        if (clips.Count == 1)
        {
            File.Copy(clips[0], outputPath, overwrite: true);
            return;
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
    }

    // FFmpeg filter arguments need forward slashes and an escaped drive colon on Windows.
    private static string FilterPath(string path) => path.Replace('\\', '/').Replace(":", "\\:");

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

    private static async Task RunFfmpegAsync(GenConfig cfg, string arguments)
    {
        var ffmpeg = string.IsNullOrEmpty(cfg.FfmpegPath) ? "ffmpeg" : cfg.FfmpegPath;
        var psi = new ProcessStartInfo(ffmpeg, arguments) { RedirectStandardError = true, UseShellExecute = false };

        using var p = StartOrThrow(psi, ffmpeg);
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        if (p.ExitCode != 0)
            throw new Exception($"ffmpeg failed (exit {p.ExitCode}):\n{Text.Tail(err, 2000)}");
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
