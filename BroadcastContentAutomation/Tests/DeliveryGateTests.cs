// L53 + L55 — the delivery gate / ffmpeg failure logging: a failed
// ffprobe in the delivery gate must warn with the file + exit code, and
// an ffmpeg failure must name the operation + output file.
using System.Diagnostics;

namespace BroadcastContentAutomation.Tests;

public class DeliveryGateTests
{
    private static void Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, $"{file} {args} failed ({proc.ExitCode}): {stderr}");
    }

    private static string MakeTinyVideo(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        // A 440 Hz tone, not silence: loudnorm cannot measure silent
        // audio (it exits 234), and the test needs the re-encode to
        // succeed after the ffprobe warning.
        Run("ffmpeg",
            $"-y -v error -f lavfi -i testsrc=duration=1:size=320x240:rate=30 " +
            $"-f lavfi -i sine=frequency=440:duration=1 -t 1 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{path}\"");
        return path;
    }

    [Fact]
    public async Task GetVideoFormat_FfprobeFailureLogsWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qg-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var video = MakeTinyVideo(dir, "joined.mp4");
            // ffprobe stub: fails immediately, so the delivery gate falls
            // back to re-encoding — the warning must name the file + code.
            var stub = Path.Combine(dir, "ffprobe-stub.sh");
            await File.WriteAllTextAsync(stub, "#!/bin/sh\necho 'stub ffprobe: boom' >&2\nexit 3\n");
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var sb = new System.Text.StringBuilder();
            var oldOut = Console.Out;
            try
            {
                Console.SetOut(new StringWriter(sb));
                await VideoAssembler.NormalizeLoudnessAsync(new GenConfig { FfprobePath = stub }, video);
            }
            finally
            {
                Console.SetOut(oldOut);
            }

            var log = sb.ToString();
            Assert.Contains("WARNING: ffprobe failed (exit 3)", log);
            Assert.Contains(video, log);
            Assert.Contains("re-encoding to limited-range yuv420p", log);
            Assert.True(File.Exists(video), "NormalizeLoudness must still produce the re-encoded video.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // L55 — an ffmpeg failure must name the operation and the output
    // file: the thumbnail-variant build failing must say so, not just
    // "ffmpeg failed (exit 1)".
    [Fact]
    public async Task RunFfmpeg_FailureNamesOperation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qg-runffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var baseImage = Path.Combine(dir, "base.jpg");
            Run("ffmpeg", $"-y -v error -f lavfi -i color=c=0x336699:s=1024x1024 -frames:v 1 \"{baseImage}\"");
            // ffmpeg stub: fails immediately, so the variant build dies
            // inside RunFfmpegAsync.
            var stub = Path.Combine(dir, "ffmpeg-stub.sh");
            await File.WriteAllTextAsync(stub, "#!/bin/sh\necho 'stub ffmpeg: boom' >&2\nexit 1\n");
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var output = Path.Combine(dir, "ronaldo.thumb-v1.jpg");
            var ex = await Assert.ThrowsAsync<Exception>(() =>
                VideoAssembler.BuildThumbnailVariantAsync(
                    new GenConfig { FfmpegPath = stub }, baseImage, "MLS PLAYOFF", 100,
                    "/nonexistent/font.ttf", 1.55, 0.5, 0.08, output));
            Assert.Contains("thumbnail variant", ex.Message);
            Assert.Contains("ronaldo.thumb-v1.jpg", ex.Message);
            Assert.Contains("ffmpeg failed (exit 1)", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
