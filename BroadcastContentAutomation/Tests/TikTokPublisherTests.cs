// L12 — a corrupt TikTok token file is a permanent auth failure, not a
// transient crash: PublishAsync must return Fatal with a re-authorize
// instruction instead of a retry.
using System.Reflection;
using GiggleGarden.Shared;
using GiggleGarden.Uploader.Publishing;

namespace BroadcastContentAutomation.Tests;

public class TikTokPublisherTests
{
    [Fact]
    public async Task CorruptTokenFile_ReturnsFatalWithReauthInstruction()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"tiktok-corrupt-token-{Guid.NewGuid():N}")).FullName;
        try
        {
            var tokenPath = Path.Combine(dir, "tiktok-token.json");
            File.WriteAllText(tokenPath, "{ this is not json");
            var video = Path.Combine(dir, "my-video-en.mp4");
            await File.WriteAllTextAsync(video, string.Empty);

            // TikTokPublisher / TikTokConfig are internal to Uploader —
            // construct them via reflection (same pattern as the
            // LoadCrashRetryState test).
            var asm = ReflectionHelper.Uploader;
            var cfgType = ReflectionHelper.FindType(asm, "TikTokConfig");
            var cfg = Activator.CreateInstance(cfgType)!;
            Set(cfgType, cfg, "ClientKey", "test-key");
            Set(cfgType, cfg, "ClientSecret", "test-secret");
            Set(cfgType, cfg, "TokenStorePath", tokenPath);
            Set(cfgType, cfg, "Enabled", true);
            var logger = new GiggleGarden.Uploader.Logger(Path.Combine(dir, "logs"));
            var pubType = ReflectionHelper.FindType(asm, "TikTokPublisher");
            var publisher = Activator.CreateInstance(pubType, cfg, logger)!;
            var publish = pubType.GetMethod("PublishAsync", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException("TikTokPublisher.PublishAsync");
            var request = new PublishRequest(video, new Sidecar());
            var task = (Task<PublishResult>)publish.Invoke(publisher, [request, CancellationToken.None])!;
            var result = await task;

            Assert.False(result.Success);
            Assert.True(result.Permanent);
            Assert.Contains("TikTok token file corrupt", result.Error);
            Assert.Contains(tokenPath, result.Error);
            Assert.Contains("re-authorize", result.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Set(Type type, object instance, string name, object? value)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException($"TikTokConfig.{name}");
        prop.SetValue(instance, value);
    }
}
