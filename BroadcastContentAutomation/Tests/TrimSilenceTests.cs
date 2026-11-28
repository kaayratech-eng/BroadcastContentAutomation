// L19 — a silence-trim failure must be logged with the file + reason and
// leave the original audio intact (best-effort trim, never a silent
// quality loss and never a lost file).
namespace BroadcastContentAutomation.Tests;

public class TrimSilenceTests
{
    [Fact]
    public async Task TrimSilence_FailureLogsWarningAndKeepsOriginal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qg-trimsil-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // ffmpeg stub: fails immediately, so no trim output is produced.
            var stub = Path.Combine(dir, "ffmpeg-stub.sh");
            await File.WriteAllTextAsync(stub, "#!/bin/sh\necho 'stub ffmpeg: boom' >&2\nexit 1\n");
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var audio = Path.Combine(dir, "scene01.mp3");
            var original = new byte[] { 0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x21, 0x76 };
            await File.WriteAllBytesAsync(audio, original);

            var sb = new System.Text.StringBuilder();
            var oldOut = Console.Out;
            try
            {
                Console.SetOut(new StringWriter(sb));
                await VideoAssembler.TrimSilenceAsync(new GenConfig { FfmpegPath = stub }, audio);
            }
            finally
            {
                Console.SetOut(oldOut);
            }

            var log = sb.ToString();
            Assert.Contains("silence trim failed", log);
            Assert.Contains(audio, log);
            Assert.Contains("keeping untrimmed audio", log);
            Assert.Contains("ffmpeg failed (exit 1)", log);
            Assert.Equal(original, await File.ReadAllBytesAsync(audio));
            Assert.False(File.Exists(audio + ".trim.mp3"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
