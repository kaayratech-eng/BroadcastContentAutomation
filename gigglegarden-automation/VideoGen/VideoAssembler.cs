using System.Diagnostics;
using System.Globalization;
using System.Text;
using GiggleGarden.Shared;

static class VideoAssembler
{
    // Builds each scene clip (image + zoompan + narration audio + wrapped subtitle),
    // concats them, then mixes background music under the narration.
    public static async Task AssembleAsync(
        GenConfig cfg, IReadOnlyList<Scene> scenes, string language,
        string workDir, string outputPath, int w, int h, bool preferVerticalImages)
    {
        if (scenes.Count == 0) throw new ArgumentException("No scenes to assemble.", nameof(scenes));

        var sceneClips = new List<string>();
        var tag = $"{w}x{h}";

        for (var i = 0; i < scenes.Count; i++)
        {
            var s = scenes[i];
            var duration = s.DurationSeconds + 0.5;              // breathing room
            var clip = Path.Combine(workDir, $"clip{i:D2}-{tag}.mp4");

            var image = preferVerticalImages && s.VerticalImagePath is { } vertical && File.Exists(vertical)
                ? vertical
                : s.ImagePath ?? throw new InvalidOperationException($"Scene {i} has no image.");

            // Alternate zoom-in / zoom-out for visual variety.
            var zoom = i % 2 == 0
                ? "zoompan=z='min(zoom+0.0012,1.15)':d=125*10:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)'"
                : "zoompan=z='if(lte(zoom,1.0),1.15,max(1.0,zoom-0.0012))':d=125*10:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)'";

            var fontSize = h > w ? 52 : 46;                       // slightly larger on vertical
            var subtitle = BuildSubtitleFilter(cfg, s.Narration, language, workDir, i, tag, w, h, fontSize);

            // scale to cover -> crop -> ken burns -> scale to target -> subtitle block
            var vf = $"scale={w * 2}:{h * 2}:force_original_aspect_ratio=increase," +
                     $"crop={w * 2}:{h * 2},{zoom},scale={w}:{h}{subtitle}";

            await RunFfmpegAsync(cfg,
                $"-y -loop 1 -i \"{image}\" -i \"{s.AudioPath}\" " +
                $"-t {Text.Invariant(duration)} " +
                $"-vf \"{vf}\" -r 25 -pix_fmt yuv420p " +
                $"-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 128k -shortest \"{clip}\"");

            sceneClips.Add(clip);
        }

        // Concat scene clips
        var listFile = Path.Combine(workDir, $"concat-{tag}.txt");
        await File.WriteAllLinesAsync(listFile, sceneClips.Select(c => $"file '{c.Replace("'", "'\\''")}'"), new UTF8Encoding(false));
        var concatPath = Path.Combine(workDir, $"concat-{tag}.mp4");
        await RunFfmpegAsync(cfg, $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{concatPath}\"");

        // Mix background music (looped, ducked under narration) if provided
        if (!string.IsNullOrEmpty(cfg.BackgroundMusicPath) && File.Exists(cfg.BackgroundMusicPath))
        {
            await RunFfmpegAsync(cfg,
                $"-y -i \"{concatPath}\" -stream_loop -1 -i \"{cfg.BackgroundMusicPath}\" " +
                "-filter_complex \"[1:a]volume=0.12[m];[0:a][m]amix=inputs=2:duration=first:dropout_transition=2[a]\" " +
                $"-map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 160k \"{outputPath}\"");
        }
        else
        {
            File.Copy(concatPath, outputPath, overwrite: true);
        }
    }

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
