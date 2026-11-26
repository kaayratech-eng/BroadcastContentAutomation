using System.Diagnostics;
using System.Text.Json;

namespace GiggleGarden.Uploader;

// Minimal ffprobe wrapper: reads a video file's duration, codecs, dimensions
// and frame rate without pulling in an FFmpeg binding. Synchronous — ffprobe
// on a local file takes well under a second, and the callers (the Story
// duration gate in Accepts, the Instagram pre-flight) need the answer before
// they decide anything.
//
// Returns null when ffprobe is unavailable or the file can't be probed.
// Callers treat that as "unknown", not as a failure, so a missing ffprobe
// never blocks an upload by itself — it only forfeits the fail-fast check.
static class MediaProbe
{
    public sealed record ProbeResult(
        double DurationSeconds, string? VideoCodec, string? AudioCodec, int Width, int Height, double Fps);

    public static ProbeResult? Probe(string videoPath, Logger? log = null)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = "-v error -show_entries stream=index,codec_type,codec_name,width,height,r_frame_rate " +
                                "-show_entries format=duration -of json " +
                                "\"" + videoPath.Replace("\"", "", StringComparison.Ordinal) + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                log?.Warn($"  [ffprobe] timed out probing {Path.GetFileName(videoPath)}.");
                return null;
            }
            if (proc.ExitCode != 0)
            {
                log?.Warn($"  [ffprobe] could not probe {Path.GetFileName(videoPath)}: {error.Trim()}");
                return null;
            }

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            double duration = 0;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var dur) &&
                double.TryParse(dur.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                duration = parsed;

            string? videoCodec = null, audioCodec = null;
            var width = 0;
            var height = 0;
            var fps = 0.0;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                    if (type == "video" && videoCodec is null)
                    {
                        videoCodec = codec;
                        width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        if (stream.TryGetProperty("r_frame_rate", out var r))
                            fps = ParseFrameRate(r.GetString());
                    }
                    else if (type == "audio" && audioCodec is null)
                    {
                        audioCodec = codec;
                    }
                }
            }

            return new ProbeResult(duration, videoCodec, audioCodec, width, height, fps);
        }
        catch (Exception ex)
        {
            // ffprobe missing, file unreadable, bad JSON — probe is best-effort.
            log?.Warn($"  [ffprobe] probe failed for {Path.GetFileName(videoPath)}: {ex.Message}");
            return null;
        }
    }

    public static double? TryGetDurationSeconds(string videoPath, Logger? log = null) =>
        Probe(videoPath, log)?.DurationSeconds;

    private static double ParseFrameRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var den) &&
            den != 0)
            return num / den;
        return double.TryParse(rate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var single)
            ? single
            : 0;
    }
}
