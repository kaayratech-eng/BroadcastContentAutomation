// Star ranking — Spotlight Now content queue order (Rishi 2026-11-24 ~23:10 EST).
//
// "Post about the BIGGEST stars first, ranked by their Instagram follower
// count, then move on to the others." The list below IS the queue order:
// biggest first, across Hollywood, Bollywood, singers and sports. The
// topic picker (TrendResearch.GlobalViewsScore) adds StarBoost for a
// candidate whose headline names a ranked star, so when two star stories
// are both in the news the bigger star's story wins, and a star story
// beats a no-star story. The scene-art chain (Program.cs) uses the same
// match to try a real Wikimedia Commons photo of the star first.
//
// HOW THE COUNTS WERE SOURCED (2026-11-24, all public sources):
//   - Global top 50: Wikipedia, "List of most-followed Instagram
//     accounts", November 2026 snapshot (page crawled 2026-11-14).
//     Ronaldo 681M, Messi 517M, Selena Gomez 402M, Dwayne Johnson 380M,
//     Ariana Grande 362M, Beyonce 298M, Justin Bieber 285M, Taylor
//     Swift 272M, Virat Kohli 271M, Neymar 241M, Jennifer Lopez 239M,
//     Miley Cyrus 205M, Katy Perry 195M, Zendaya 178M, Kevin Hart 171M,
//     Cardi B 160M, LeBron James 153M, Demi Lovato 147M, Rihanna 145M,
//     Drake 139M, Chris Brown 139M, Kylian Mbappe 137M, Billie Eilish
//     123M, Lisa 105M, Vin Diesel 103M, Shakira 103M, Gal Gadot 103M,
//     Priyanka Chopra 92.7M, Shraddha Kapoor 92.7M, Jennie 89.3M,
//     Dua Lipa 89.0M, David Beckham 87.5M, plus the Kardashian/Jenner
//     reality stars (Kylie 381M, Kim 343M, Khloe 291M, Kendall 277M,
//     Kourtney 208M) — entertainment figures the cover channels cover
//     constantly, listed here under category "reality".
//   - Bollywood men (not in the global top 50): Filmfare's June 30,
//     2026 World Social Media Day listicle "India's 10 Most Followed
//     Male Actors on Instagram", cross-checked against the @bollywood
//     September 23, 2026 listicle. Salman Khan 72.6M, Akshay Kumar
//     64.9M, Kartik Aaryan 49.6M, Hrithik Roshan 48.6M, Shah Rukh Khan
//     48.4M, Shahid Kapoor 48.1M, Ranveer Singh 47.8M, Varun Dhawan
//     45.0M, Kapil Sharma 44.3M, Tiger Shroff 39.2M.
//   - Bollywood women: trendingpunjabi.com live Instagram tracker,
//     refreshed November 24, 2026 (Shraddha Kapoor 92.7M, Priyanka
//     Chopra 92.6M, Alia Bhatt 85.1M, Katrina Kaif 78.5M, Deepika
//     Padukone 77.7M, Jacqueline Fernandez 67.9M, Anushka Sharma 67.8M,
//     Urvashi Rautela 67.5M, Disha Patani 60.3M, Kriti Sanon 57.9M),
//     cross-checked against the @bollywood September 23, 2026 listicle.
//   Counts move daily and close pairs swap order — this is a snapshot,
//   refresh it monthly, and never treat a 0.1M gap as a real ranking
//   difference. Stars with no Instagram account (Ranbir Kapoor, Aamir
//   Khan, Amitabh Bachchan) cannot be ranked by this rule and are not
//   listed; they still reach the queue through the normal news feeds.
//
// FUTURE, NOT NOW (Rishi 2026-11-24 ~23:14 EST): billionaires are a
// future target category. Do NOT add Musk, Bezos, Ambani, etc. to this
// list until Rishi says so.
using System.Globalization;

static class StarRanking
{
    internal sealed record Star(
        string Name,
        double InstagramFollowersMillions,
        string Category,
        string[] Aliases)
    {
        internal Star(string name, double followersMillions, string category)
            : this(name, followersMillions, category, []) { }
    }

    // Ordered biggest-first. The index IS the queue position: rank 0 is
    // the first star the channel should cover.
    internal static readonly IReadOnlyList<Star> Ordered =
    [
        new("Cristiano Ronaldo", 681, "sports"),
        new("Lionel Messi", 517, "sports"),
        new("Selena Gomez", 402, "singer"),
        new("Kylie Jenner", 381, "reality"),
        new("Dwayne Johnson", 380, "hollywood", ["The Rock"]),
        new("Ariana Grande", 362, "singer"),
        new("Kim Kardashian", 343, "reality"),
        new("Beyoncé", 298, "singer", ["Beyonce"]),
        new("Khloé Kardashian", 291, "reality", ["Khloe Kardashian"]),
        new("Justin Bieber", 285, "singer"),
        new("Kendall Jenner", 277, "reality"),
        new("Taylor Swift", 272, "singer"),
        new("Virat Kohli", 271, "sports"),
        new("Neymar", 241, "sports"),
        new("Jennifer Lopez", 239, "singer", ["J.Lo", "J Lo"]),
        new("Kourtney Kardashian", 208, "reality"),
        new("Miley Cyrus", 205, "singer"),
        new("Katy Perry", 195, "singer"),
        new("Zendaya", 178, "hollywood"),
        new("Kevin Hart", 171, "hollywood"),
        new("Cardi B", 160, "singer"),
        new("LeBron James", 153, "sports"),
        new("Demi Lovato", 147, "singer"),
        new("Rihanna", 145, "singer"),
        new("Drake", 139, "singer"),
        new("Chris Brown", 139, "singer"),
        new("Kylian Mbappé", 137, "sports", ["Kylian Mbappe"]),
        new("Billie Eilish", 123, "singer"),
        new("Lisa", 105, "singer"),
        new("Vin Diesel", 103, "hollywood"),
        new("Shakira", 103, "singer"),
        new("Gal Gadot", 103, "hollywood"),
        new("Shraddha Kapoor", 92.7, "bollywood"),
        new("Priyanka Chopra", 92.6, "bollywood", ["Priyanka Chopra Jonas"]),
        new("Jennie", 89.3, "singer"),
        new("Dua Lipa", 89.0, "singer"),
        new("David Beckham", 87.5, "sports"),
        new("Alia Bhatt", 85.1, "bollywood"),
        new("Katrina Kaif", 78.5, "bollywood"),
        new("Deepika Padukone", 77.7, "bollywood"),
        new("Salman Khan", 72.6, "bollywood"),
        new("Jacqueline Fernandez", 67.9, "bollywood"),
        new("Anushka Sharma", 67.8, "bollywood"),
        new("Urvashi Rautela", 67.5, "bollywood"),
        new("Akshay Kumar", 64.9, "bollywood"),
        new("Disha Patani", 60.3, "bollywood"),
        new("Kriti Sanon", 57.9, "bollywood"),
        new("Kartik Aaryan", 49.6, "bollywood"),
        new("Hrithik Roshan", 48.6, "bollywood"),
        new("Shah Rukh Khan", 48.4, "bollywood", ["SRK"]),
        new("Shahid Kapoor", 48.1, "bollywood"),
        new("Ranveer Singh", 47.8, "bollywood"),
        new("Varun Dhawan", 45.0, "bollywood"),
        new("Kapil Sharma", 44.3, "bollywood"),
        new("Tiger Shroff", 39.2, "bollywood"),
    ];

    // Log-scaled star boost added to a candidate's topic score: Ronaldo
    // (681M) adds ~2.83, Tiger Shroff (39.2M) ~1.59 — a 1.24-point gap
    // between the biggest and the smallest ranked star, decisive between
    // two star stories, and enough for any star story to beat a no-star
    // story, without swamping the edition bonus (+2 per extra edition)
    // or the YouTube views signal (up to +6).
    internal static double StarBoost(Star star) =>
        Math.Log10(star.InstagramFollowersMillions);

    // Highest-ranked star named in the text (headline, description or
    // narration), or null when no ranked star is named. Matching is on
    // normalised text (lowercase, diacritics stripped, punctuation to
    // spaces) with whole-word boundaries, so "Lisa" never matches
    // "Elisa" and "Drake" never matches "Drakes". The FIRST match in
    // list order wins — a story naming both Ronaldo and Messi boosts on
    // Ronaldo, the bigger star.
    internal static (Star Star, int Rank)? TryMatchStar(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var haystack = " " + Normalise(text) + " ";
        for (var i = 0; i < Ordered.Count; i++)
        {
            var star = Ordered[i];
            if (ContainsName(haystack, star.Name)) return (star, i);
            foreach (var alias in star.Aliases)
                if (ContainsName(haystack, alias)) return (star, i);
        }
        return null;
    }

    private static bool ContainsName(string haystack, string name) =>
        haystack.Contains(" " + Normalise(name) + " ", StringComparison.Ordinal);

    // Lowercase, strip diacritics (Beyoncé -> beyonce, Mbappé -> mbappe,
    // Khloé -> khloe), punctuation -> spaces, collapse whitespace.
    internal static string Normalise(string text)
    {
        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }
        return sb.ToString().Trim();
    }
}
