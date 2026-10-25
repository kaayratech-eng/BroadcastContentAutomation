namespace GiggleGarden.Shared;

// Free hook + SEO scoring (2026-10-25, priority 2): pure heuristics, no API.
// Scores are stored on the script/sidecar so the analytics loop can later
// correlate score with views. They are a gate for a rewrite prompt, not a
// guarantee — a high score does not mean a good video, it means the title
// and opening line are not making the three most common avoidable mistakes:
// a slow generic opening, a keyword buried past the truncation point, and
// a title that gives no concrete subject.
public static class SeoScore
{
    private static readonly string[] WeakOpenings = ["long ago", "welcome to", "in this video", "today we will", "once upon a time"];
    private static readonly string[] HookSignals = ["?", "what if", "why", "real", "never", "secret", "truth", "actually", "dared", "feared"];

    public static int ScoreTitle(string title)
    {
        var score = 0;
        if (string.IsNullOrWhiteSpace(title)) return 0;
        if (title.Length <= 100) score += 20;
        if (title.Length is >= 30 and <= 70) score += 20;
        // Front-load: first 30 chars should not be generic filler.
        var head = title.Length > 30 ? title[..30].ToLowerInvariant() : title.ToLowerInvariant();
        if (!head.StartsWith("the story of") && !head.StartsWith("a story about") && !head.StartsWith("welcome")) score += 20;
        if (HookSignals.Any(s => title.ToLowerInvariant().Contains(s))) score += 20;
        if (title.Any(char.IsDigit)) score += 10;   // a number / count / year is concrete
        if (title.Count(c => c == ':') == 1) score += 10;  // subject: angle pattern
        return Math.Min(100, score);
    }

    public static int ScoreHook(string firstNarration)
    {
        var score = 0;
        if (string.IsNullOrWhiteSpace(firstNarration)) return 0;
        if (firstNarration.Length <= 115) score += 25;
        var lower = firstNarration.ToLowerInvariant();
        if (WeakOpenings.Any(w => lower.StartsWith(w))) score -= 25;
        else score += 25;
        if (HookSignals.Any(s => lower.Contains(s))) score += 25;
        // A concrete proper noun / place / name in the opening line beats an abstraction.
        if (firstNarration.Any(char.IsUpper) && firstNarration.Split(' ').Length >= 8) score += 25;
        return Math.Clamp(score, 0, 100);
    }

    public static List<string> TitleVariants(string title)
    {
        var variants = new List<string> { title };
        if (!title.Contains('?') && !title.EndsWith("?", StringComparison.Ordinal))
            variants.Add($"What really happened in {title.TrimEnd('.')}?");
        if (!title.StartsWith("The real", StringComparison.OrdinalIgnoreCase))
            variants.Add($"The real story behind {title.TrimEnd('.')}");
        return variants.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    // YouTube tags have a 500-character total limit; trim the list, keeping
    // trending/evergreen tags first (callers order them that way).
    public static List<string> TrimTagsForYouTube(IReadOnlyList<string> tags)
    {
        var kept = new List<string>();
        var total = 0;
        foreach (var tag in tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cost = tag.Length + 1;
            if (kept.Count > 0 && total + cost > 500) break;
            kept.Add(tag);
            total += cost;
        }
        return kept;
    }
}
