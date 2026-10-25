using System.Text.Json;
using System.Text.RegularExpressions;

// Free fact-check (2026-10-25, priority 4): Wikipedia REST API, no key.
// Used two ways: (a) before script generation when --topic is given, the
// topic's summary is appended to the trend context so the model starts from
// real facts; (b) after generation for Chronicle, the figure's summary is
// saved to the job folder and any year mentioned in narration but absent
// from the summary is logged as a warning — a heuristic, not a proof, but
// it catches the most common mythology hallucination (a wrong date) for
// free. Never blocks a run: every failure returns null.
static class WikipediaClient
{
    public static async Task<string?> FetchSummaryAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("User-Agent", "BroadcastContentAutomation/1.0 (free fact-check)");
            var title = Uri.EscapeDataString(query.Trim().Replace(' ', '_'));
            var resp = await http.GetAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{title}");
            if (!resp.IsSuccessStatusCode)
            {
                // Fall back to search, then take the first result's summary.
                var search = await http.GetAsync($"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&format=json");
                if (!search.IsSuccessStatusCode) return null;
                using var sdoc = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
                var first = sdoc.RootElement.GetProperty("query").GetProperty("search").EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("title", out var t)) return null;
                var title2 = Uri.EscapeDataString(t.GetString()!.Replace(' ', '_'));
                resp = await http.GetAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{title2}");
                if (!resp.IsSuccessStatusCode) return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("extract", out var extract) ? extract.GetString() : null;
        }
        catch { return null; }
    }

    // Years in narration (1000-2099) that do not appear in the Wikipedia
    // summary — worth a human check, not an automatic rejection.
    public static List<string> YearsToCheck(string narration, string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return [];
        var years = Regex.Matches(narration, @"\b(1[0-9]{3}|20[0-9]{2})\b").Select(m => m.Value).Distinct().ToList();
        return years.Where(y => !summary.Contains(y)).ToList();
    }
}
