// L11 — a corrupt sidecar must fail fast with the file and reason named,
// not burn 3 crash retries as a transient error.
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class SidecarTests
{
    [Fact]
    public async Task CorruptJson_ErrorNamesFileAndReason()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"sidecar-corrupt-{Guid.NewGuid():N}")).FullName;
        try
        {
            var video = Path.Combine(dir, "my-video-en.mp4");
            File.WriteAllText(Path.ChangeExtension(video, ".json"), "{ this is not json");
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Sidecar.LoadAsync(video));
            Assert.StartsWith("SIDECAR_CORRUPT:", ex.Message);
            Assert.Contains("my-video-en.json", ex.Message);
            Assert.Contains("corrupt sidecar JSON", ex.Message);
            Assert.Contains("move to failed/", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // L82 — a corrupt sidecar load must report the file path + reason
    // through the optional log callback (RetryHandler pattern), before
    // the SIDECAR_CORRUPT throw still fails the load fast.
    [Fact]
    public async Task Sidecar_LoadAsync_LogsViaCallback()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"sidecar-logcb-{Guid.NewGuid():N}")).FullName;
        try
        {
            var video = Path.Combine(dir, "my-video-en.mp4");
            File.WriteAllText(Path.ChangeExtension(video, ".json"), "{ this is not json");
            var lines = new List<string>();
            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => Sidecar.LoadAsync(video, lines.Add));
            Assert.StartsWith("SIDECAR_CORRUPT:", ex.Message);
            Assert.Single(lines);
            Assert.Contains("my-video-en.json", lines[0]);
            Assert.Contains("corrupt sidecar JSON", lines[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
