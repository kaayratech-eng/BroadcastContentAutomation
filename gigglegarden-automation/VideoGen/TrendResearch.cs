using System.Text;
using System.Text.Json;

// Lightweight trend lookup for topic inspiration - uses the YouTube Data API's public
// search.list endpoint with a simple API key (no OAuth needed, unlike the Uploader's
// authorized upload flow). Feeds real trending kids' video titles into the script
// prompt so topic selection isn't just Claude picking from a generic static list.
static class TrendResearch
{
    public static async Task<string?> FetchAsync(GenConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.YouTubeApiKey) || cfg.YouTubeApiKey.StartsWith("PUT-"))
            return null;
        if (cfg.TrendQueries.Length == 0) return null;

        // One query per run, not all of them - unlike the Uploader's rare no-sidecar
        // fallback, this runs on every single video generation, so quota use needs to
        // stay frugal. Rotating a random query still surfaces fresh material over time.
        var query = cfg.TrendQueries[Random.Shared.Next(cfg.TrendQueries.Length)];

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var url = "https://www.googleapis.com/youtube/v3/search" +
                       "?part=snippet&type=video&order=viewCount&safeSearch=strict&maxResults=5" +
                       $"&publishedAfter={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-30).ToString("O"))}" +
                       $"&q={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(cfg.YouTubeApiKey)}";

            var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  [trends] lookup failed (HTTP {(int)resp.StatusCode}), continuing without trend context.");
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var sb = new StringBuilder();
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var title = item.GetProperty("snippet").GetProperty("title").GetString();
                if (!string.IsNullOrWhiteSpace(title)) sb.AppendLine($"- \"{title}\"");
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [trends] lookup failed, continuing without trend context: {ex.Message}");
            return null;
        }
    }
}
