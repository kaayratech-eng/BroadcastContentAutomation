// L8 (failure-logging plan): a corrupt analytics-log entry must be
// reported (count + first offset + reason) through the log sink instead
// of vanishing silently, and an append failure must throw a wrapped
// IOException naming the video id and the next action.
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class AnalyticsLogTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tn-analyticslog-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static List<AnalyticsEntry> Load(string dir, Action<string> log)
        => AnalyticsLog.LoadAsync(dir, log).GetAwaiter().GetResult();

    private static void Append(string dir, AnalyticsEntry entry)
        => AnalyticsLog.AppendAsync(dir, entry).GetAwaiter().GetResult();

    [Fact]
    public void CorruptEntry_ReportsSkippedCountAndId()
    {
        // File with 1 corrupt + 1 good entry: the skip is reported
        // (count 1, first offset, reason) and the good entry still loads.
        var dir = TempDir("corrupt");
        try
        {
            var good = """{"VideoId":"aaa","Title":"Good one","Channel":"trendlinenow","Aspect":"landscape","PublishedAt":"2026-11-21T00:00:00+00:00","Views":0}""";
            File.WriteAllText(AnalyticsLog.PathFor(dir), good + "\n{\"VideoId\": broken}\n");
            var messages = new List<string>();
            var loaded = Load(dir, messages.Add);
            Assert.Single(loaded);
            Assert.Equal("aaa", loaded[0].VideoId);
            Assert.Single(messages);
            Assert.Contains("skipped 1 corrupt", messages[0]);
            Assert.Contains("offset", messages[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AppendAsync_FailureWrapsWithVideoId()
    {
        // The append target cannot be created (the "directory" is an
        // existing file): the throw must name the video id and say the
        // publish already happened, instead of a bare IOException.
        var dir = TempDir("append-fail");
        try
        {
            var blocker = Path.Combine(dir, "blocked");
            File.WriteAllText(blocker, "not a directory");
            var entry = new AnalyticsEntry
            {
                VideoId = "aaaaaaaaaaa",
                Title = "Blocked video",
                Channel = "trendlinenow",
                Aspect = "landscape",
                PublishedAt = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero),
            };
            var ex = Assert.Throws<IOException>(() => Append(blocker, entry));
            Assert.Contains("aaaaaaaaaaa", ex.Message);
            Assert.Contains("publish already recorded", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
