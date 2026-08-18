namespace GiggleGarden.Shared;

// The permanent, cross-process log of every title actually published for a channel.
// VideoGen reads it (RecentAsync) to steer new scripts away from an already-covered
// topic; the Uploader is the only writer (AppendAsync), and only once a sidecar has an
// actual published target - not merely a rendered or approved one - since VideoGen's own
// process exits long before a sidecar is approved and picked up by the Uploader's
// separate, differently-scheduled run.
public static class TopicHistory
{
    private static string PathFor(string channel) =>
        Path.Combine(@"D:\Business\BroadcastContentAutomation\VideoGen\topic-history", $"{channel}.md");

    public static async Task<List<string>> RecentAsync(string channel, int max = 30)
    {
        var path = PathFor(channel);
        if (!File.Exists(path)) return [];

        var lines = await File.ReadAllLinesAsync(path);
        var titles = new List<string>();
        for (var i = lines.Length - 1; i >= 0 && titles.Count < max; i--)
        {
            var line = lines[i];
            var sep = line.IndexOf(": ", StringComparison.Ordinal);
            if (line.StartsWith("- ") && sep > 0) titles.Add(line[(sep + 2)..]);
        }

        return titles;
    }

    public static async Task AppendAsync(string channel, string title)
    {
        var path = PathFor(channel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, $"- {DateTime.Now:yyyy-MM-dd}: {title}{Environment.NewLine}");
    }
}
