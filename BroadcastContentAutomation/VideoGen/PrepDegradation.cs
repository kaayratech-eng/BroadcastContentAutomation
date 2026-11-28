// L18 — a prep that completes on best-effort degradation (all trend feeds
// down, no articles, no Wikipedia background) used to exit 0 with only
// scattered warnings, indistinguishable from a fully-grounded prep. The
// degraded sources are collected where the fetches happen (RunPrepAsync)
// and summarized here, in FinishPrepAsync, before the clean return 0: one
// "prep completed DEGRADED: ..." line in the run log and a durable
// prep-warnings.txt in the job folder. Exit stays 0 — a degraded prep
// still produced a complete, gate-checked script, so the driver must not
// retry or escalate it as a failure; the line + file are the signal.
// (A distinct exit code was considered and rejected for exactly that
// reason: the prep did not fail, its inputs did.)
static class PrepDegradation
{
    public const string WarningsFile = "prep-warnings.txt";
    public const string PickedBackground = "Wikipedia picked-topic background";

    // Sources that failed, from what the prep observed. trendReport is the
    // picked-topic report (null for forced topics, where it is skipped);
    // forcedArticleEmpty / forcedWikipediaEmpty are the forced-topic fetches
    // (false when not applicable — a picked topic never fetches them);
    // pickedBackgroundFailed is the FinishPrepAsync picked-topic background
    // fetch (the only fetch that happens inside FinishPrepAsync itself).
    // Granularity is per source-group, not per feed: which individual RSS
    // feed died is per-source silence (L63/L64, High) and is not tracked
    // here. An empty picked-topic report means every trend source failed —
    // the report text is empty only when YouTube, Google News, the
    // Wikipedia top-story background AND the top-story articles all came
    // back with nothing.
    public static List<string> Collect(
        TrendResearch.TrendReport? trendReport,
        bool forcedArticleEmpty, bool forcedWikipediaEmpty, bool pickedBackgroundFailed)
    {
        var degraded = new List<string>();
        if (trendReport is not null && string.IsNullOrWhiteSpace(trendReport.Text) && trendReport.News.Count == 0)
            degraded.Add("trend feeds (Google News + YouTube returned no headlines, no articles, no Wikipedia background)");
        if (forcedArticleEmpty)
            degraded.Add("article fetch (no article text for the forced topic)");
        if (forcedWikipediaEmpty)
            degraded.Add("Wikipedia background (no backgrounds for the forced topic)");
        if (pickedBackgroundFailed)
            degraded.Add(PickedBackground);
        return degraded;
    }

    // The durable record from an earlier run: a resumed prep does not
    // re-fetch, so it reuses the original run's assessment instead of
    // claiming a clean prep it cannot verify.
    public static async Task<List<string>> LoadExistingAsync(string workDir)
    {
        var path = Path.Combine(workDir, WarningsFile);
        if (!File.Exists(path)) return [];
        var lines = await File.ReadAllLinesAsync(path);
        return lines.Where(l => l.StartsWith("  - ", StringComparison.Ordinal)).Select(l => l[4..]).ToList();
    }

    public static string SummaryLine(IReadOnlyList<string> degraded) =>
        degraded.Count == 0
            ? "prep completed OK"
            : $"prep completed DEGRADED: {string.Join("; ", degraded)}";

    // Logs the one-line outcome and records the degraded sources in the
    // job folder. A clean prep deletes a stale warnings file so the folder
    // never claims a degradation that did not happen on this run.
    public static async Task FinishAsync(string workDir, IReadOnlyList<string> degraded)
    {
        Console.WriteLine($"  {SummaryLine(degraded)}");
        var path = Path.Combine(workDir, WarningsFile);
        if (degraded.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("prep completed DEGRADED — the script was written with these sources missing:");
        foreach (var d in degraded) sb.AppendLine($"  - {d}");
        sb.AppendLine("The prep still exited 0: the script passed the fact + quality gates on what was available. Re-run the prep if the missing sources matter for this topic.");
        await File.WriteAllTextAsync(path, sb.ToString());
    }
}
