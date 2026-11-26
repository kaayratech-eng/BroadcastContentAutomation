// Star ranking (Rishi 2026-11-24 ~23:10 EST): post about the BIGGEST stars
// first, ranked by Instagram followers, then move on to the others. The
// list is StarRanking.Ordered; the topic picker adds a log-scaled star
// boost in TrendResearch.GlobalViewsScore. Every test here pins that
// behaviour with canned data — no test touches the network, a real API,
// or a render. Counts are the November 2026 snapshot (see StarRanking.cs
// for sourcing); tests assert ORDER and relative size, not exact live
// counts, so a later refresh does not break them unless the order itself
// changes.
using System.Collections;
using System.Reflection;
using Xunit;

namespace BroadcastContentAutomation.Tests;

public class StarRankingTests
{
    private static readonly DateTime BaseTime = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private static Type StarRankingType =>
        ReflectionHelper.FindType(ReflectionHelper.VideoGen, "StarRanking");

    private static IList OrderedStars()
    {
        var field = StarRankingType.GetField("Ordered", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("StarRanking.Ordered");
        return (IList)field.GetValue(null)!;
    }

    private static string StarName(object star) =>
        (string)star.GetType().GetProperty("Name")!.GetValue(star)!;

    private static double StarFollowers(object star) =>
        (double)star.GetType().GetProperty("InstagramFollowersMillions")!.GetValue(star)!;

    private static string StarCategory(object star) =>
        (string)star.GetType().GetProperty("Category")!.GetValue(star)!;

    // TryMatchStar returns (Star, int)? — a null Nullable boxes to null,
    // a match boxes to a ValueTuple with Item1 = Star, Item2 = rank.
    private static (object Star, int Rank)? MatchStar(string text)
    {
        var method = StarRankingType.GetMethod("TryMatchStar", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMethodException("StarRanking.TryMatchStar");
        var result = method.Invoke(null, [text]);
        if (result is null) return null;
        var item1 = result.GetType().GetField("Item1")!.GetValue(result)!;
        var item2 = (int)result.GetType().GetField("Item2")!.GetValue(result)!;
        return (item1, item2);
    }

    private static double Boost(object star)
    {
        var method = StarRankingType.GetMethod("StarBoost", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMethodException("StarRanking.StarBoost");
        return (double)method.Invoke(null, [star])!;
    }

    private static object MakeNewsItem(string title)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "NewsItem");
        return Activator.CreateInstance(type, title, "SomeOutlet", "https://example.com/x",
            null, BaseTime, new[] { "US" }, 0.0)!;
    }

    private static IList EmptyYoutube()
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.VideoGen, "TrendItem");
        return (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
    }

    private static double Score(object newsItem) =>
        ReflectionHelper.Invoke<double>(ReflectionHelper.VideoGen, "GlobalViewsScore", newsItem, EmptyYoutube());

    [Fact]
    public void Ordered_IsBiggestFirst_RonaldoOnTop()
    {
        var ordered = OrderedStars();
        Assert.True(ordered.Count >= 50, $"Expected 50+ ranked stars, got {ordered.Count}.");
        Assert.Equal("Cristiano Ronaldo", StarName(ordered[0]!));
        Assert.Equal(681, StarFollowers(ordered[0]!));
        Assert.Equal("Lionel Messi", StarName(ordered[1]!));
        Assert.Equal("Selena Gomez", StarName(ordered[2]!));
        // Strictly descending by followers (ties allowed only between
        // adjacent equal counts, e.g. Drake / Chris Brown at 139M).
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(StarFollowers(ordered[i]!) <= StarFollowers(ordered[i - 1]!),
                $"Star {StarName(ordered[i]!)} ({StarFollowers(ordered[i]!)}) is out of order after {StarName(ordered[i - 1]!)} ({StarFollowers(ordered[i - 1]!)})");
    }

    [Fact]
    public void Ordered_CoversHollywoodBollywoodSingersAndSports()
    {
        var categories = OrderedStars().Cast<object>().Select(StarCategory).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("hollywood", categories);
        Assert.Contains("bollywood", categories);
        Assert.Contains("singer", categories);
        Assert.Contains("sports", categories);
        // Bollywood's biggest names are all present, in order.
        var names = OrderedStars().Cast<object>().Select(StarName).ToList();
        Assert.True(names.IndexOf("Shraddha Kapoor") < names.IndexOf("Salman Khan"));
        Assert.True(names.IndexOf("Salman Khan") < names.IndexOf("Shah Rukh Khan"));
        Assert.True(names.IndexOf("Shah Rukh Khan") < names.IndexOf("Tiger Shroff"));
    }

    [Fact]
    public void Ordered_HasNoBillionaires_Yet()
    {
        // Rishi 2026-11-24 ~23:14: billionaires are a FUTURE category,
        // not built into the queue yet. If one appears here, the queue
        // was extended without his go-ahead.
        var names = OrderedStars().Cast<object>().Select(StarName).ToList();
        foreach (var billionaire in new[] { "Musk", "Bezos", "Ambani", "Gates", "Zuckerberg", "Buffett" })
            Assert.DoesNotContain(names, n => n.Contains(billionaire, StringComparison.Ordinal));
    }

    [Fact]
    public void TryMatchStar_FindsStarInTitle()
    {
        var match = MatchStar("Shah Rukh Khan's Comeback Broke Every Record");
        Assert.NotNull(match);
        Assert.Equal("Shah Rukh Khan", StarName(match!.Value.Star));
        Assert.Equal(49, match.Value.Rank);
    }

    [Fact]
    public void TryMatchStar_HighestRankedWins_WhenTwoStarsNamed()
    {
        var match = MatchStar("Tiger Shroff trains with Cristiano Ronaldo ahead of the big match");
        Assert.NotNull(match);
        Assert.Equal("Cristiano Ronaldo", StarName(match!.Value.Star));
        Assert.Equal(0, match.Value.Rank);
    }

    [Fact]
    public void TryMatchStar_NoMatch_ForNonStarText()
    {
        Assert.Null(MatchStar("GTA 6 trailer breaks records"));
        Assert.Null(MatchStar("Cyclone hits the coast, thousands evacuated"));
        Assert.Null(MatchStar(""));
        // Word boundaries: "Elisa" must not match the star "Lisa".
        Assert.Null(MatchStar("Elisa sings at the festival"));
    }

    [Fact]
    public void TryMatchStar_AliasesWork()
    {
        var srk = MatchStar("SRK spotted at the airport");
        Assert.NotNull(srk);
        Assert.Equal("Shah Rukh Khan", StarName(srk!.Value.Star));
        var rock = MatchStar("The Rock announces a new film");
        Assert.NotNull(rock);
        Assert.Equal("Dwayne Johnson", StarName(rock!.Value.Star));
    }

    [Fact]
    public void StarBoost_BiggerStarBeatsSmallerStar()
    {
        var ordered = OrderedStars();
        var ronaldo = ordered[0]!;
        var tiger = ordered.Cast<object>().First(s => StarName(s) == "Tiger Shroff");
        var boostRonaldo = Boost(ronaldo);
        var boostTiger = Boost(tiger);
        Assert.True(boostRonaldo > boostTiger,
            $"Ronaldo boost ({boostRonaldo}) must exceed Tiger Shroff boost ({boostTiger}).");
        Assert.Equal(Math.Log10(681), boostRonaldo, precision: 6);
        Assert.Equal(Math.Log10(39.2), boostTiger, precision: 6);
    }

    [Fact]
    public void GlobalViewsScore_StarStoryBeatsNoStarStory()
    {
        var star = Score(MakeNewsItem("Tiger Shroff's new film opens to packed houses"));
        var plain = Score(MakeNewsItem("Local festival draws record crowds"));
        Assert.True(star > plain,
            $"Star story ({star}) must outscore an otherwise identical no-star story ({plain}).");
    }

    [Fact]
    public void GlobalViewsScore_BiggerStarBeatsSmallerStar()
    {
        var big = Score(MakeNewsItem("Cristiano Ronaldo scores again in front of a packed stadium"));
        var small = Score(MakeNewsItem("Tiger Shroff scores again in front of a packed stadium"));
        Assert.True(big > small,
            $"Ronaldo story ({big}) must outscore the same story about Tiger Shroff ({small}).");
    }
}
