// Regression tests for the length fix (2026-11-23).
//
// Root cause: Groq act calls run with an output budget from groq_chat.py
// (max_tokens = max(1500, 7500 - prompt/4)), and the prompts grow per act
// while the budget shrinks - so 4 of 7 acts in the COP31/Bisleri prep logs
// came back truncated and were salvaged thin (15 of 20-25, 11 of 12-14, 19
// of 20-25, 18 of 20-25), and Bisleri act 1 returned valid JSON with only
// 10 scenes. CompleteAndParseAsync returned a salvaged thin act immediately
// with just a WARNING, and the shortfall was only caught at the end by
// ValidateLongFormFloors, which deleted the partial and wasted the prep.
// The fix: a thin act is repaired in place with a continuation call asking
// for only the missing scenes (small output, fits the budget), up to twice,
// then fails loudly; and a validation failure names its defect on the retry
// prompt instead of re-asking blindly.
//
// Seams under test (fake IScriptProvider, no network):
//   (a) a salvaged-thin continuation act triggers the repair and appends
//       the missing scenes, with visualGroups continuing numbering;
//   (b) an act that stays thin after two repairs throws instead of being
//       accepted;
//   (c) the per-act minimums are the low end of each act's ask;
//   (d) a validation failure (the Bisleri line-chart defect) is named in
//       the retry prompt;
//   (e) end-to-end: a thin Act 1 (10 scenes) is repaired to 12 and the
//       full script clears the 50-scene / 5,500-char floor.
using System.Text.Json;

namespace BroadcastContentAutomation.Tests;

public class LengthFixTests
{
    private sealed class QueueProvider : IScriptProvider
    {
        private readonly Queue<string> _responses;
        public List<string> Prompts { get; } = [];

        public QueueProvider(IEnumerable<string> responses) =>
            _responses = new Queue<string>(responses);

        public Task<string> CompleteAsync(string prompt)
        {
            Prompts.Add(prompt);
            if (_responses.Count == 0)
                throw new InvalidOperationException("QueueProvider ran out of queued responses.");
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static string Narration(int n, int chars = 130)
    {
        var text = $"Scene {n} carries the story forward with the next verified fact from the supplied report, adding detail. ";
        while (text.Length < chars) text += "More context follows in this same beat. ";
        return text[..chars];
    }

    private static string ArtSceneJson(int n, int group) =>
        JsonSerializer.Serialize(new
        {
            narration = Narration(n),
            imagePrompt = $"Documentary news illustration for scene {n}.",
            sceneKind = "art",
            visualGroup = group,
        });

    private static string ScenesJson(IEnumerable<string> sceneJsons) =>
        "{\"scenes\": [" + string.Join(",", sceneJsons) + "]}";

    private static string ArtScenesJson(int firstScene, int count, int firstGroup) =>
        ScenesJson(Enumerable.Range(0, count).Select(i => ArtSceneJson(firstScene + i, firstGroup + i)));

    private static string TruncatedArtScenesJson(int completeScenes, int firstGroup)
    {
        // A response cut off mid-scene: the last scene's object never closes,
        // so deserialization fails and salvage keeps exactly `completeScenes`.
        var full = ArtScenesJson(1, completeScenes + 1, firstGroup);
        var marker = $"Scene {completeScenes + 1} carries";
        var cutAt = full.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return full[..cutAt];
    }

    private static string Act1Json(int sceneCount, int firstGroup) =>
        JsonSerializer.Serialize(new
        {
            title = "Test water safety story",
            description = "Test description.",
            tags = new[] { "test" },
            tiktokCaption = "Test caption #news",
            reelsCaption = "Test caption #news",
            thumbnailPrompt = "Test thumbnail.",
            introText = "Test intro.",
            brief = "",
            topicCategory = "general",
            musicMood = "tense atmospheric",
            scenes = Enumerable.Range(1, sceneCount).Select(i => new
            {
                narration = Narration(i),
                imagePrompt = $"Documentary news illustration for scene {i}.",
                sceneKind = "art",
                visualGroup = firstGroup + i - 1,
            }).ToList(),
        });

    private static (ContentProfile Profile, ContentFormatDef FormatDef, string Rules, TopicStyleProfile Style)
        NewsActDeps()
    {
        var profile = ContentProfileRegistry.Get("trendlinenow");
        var formatDef = profile.Formats[0];
        var rules = formatDef.Instructions.LongFormContentRules ?? formatDef.Instructions.ContentRules;
        var style = TopicCategoryStyles.Get("general");
        return (profile, formatDef, rules, style);
    }

    private static VideoScript ScriptWithAct1(int scenes = 10) =>
        new()
        {
            Scenes = Enumerable.Range(1, scenes).Select(i => new Scene
            {
                Narration = Narration(i),
                ImagePrompt = $"Documentary news illustration for scene {i}.",
                SceneKind = "art",
                VisualGroup = i,
            }).ToList(),
        };

    // --- (a) salvaged-thin act repaired in place -------------------------

    [Fact]
    public async Task ContinuationAct_SalvagedThin_RepairsAndAppends()
    {
        // Act 2 asks 20-24 (min 20). First call truncates: salvage keeps 15
        // (groups 11-25). The repair asks for exactly 5 more from group 26.
        var (profile, formatDef, rules, style) = NewsActDeps();
        var provider = new QueueProvider([
            TruncatedArtScenesJson(completeScenes: 15, firstGroup: 11),
            ArtScenesJson(firstScene: 16, count: 5, firstGroup: 26),
        ]);
        var scenes = await ScriptGenerator.GenerateNewsContinuationActAsync(
            profile, formatDef, rules, style, "English", provider,
            ScriptWithAct1(), actNumber: 2,
            actRole: "the backstory and the context that explains how we got here",
            sourceFacts: null);

        Assert.Equal(20, scenes.Count);
        Assert.Equal(Enumerable.Range(11, 20), scenes.Select(s => s.VisualGroup));
        Assert.Equal(2, provider.Prompts.Count);
        Assert.Contains("5 MORE scenes", provider.Prompts[1]);
        Assert.Contains("START THIS ACT'S FIRST GROUP AT 26", provider.Prompts[1]);
    }

    // --- (b) still thin after two repairs: fail loudly -------------------

    [Fact]
    public async Task ContinuationAct_AlwaysThin_ThrowsAfterTwoRepairs()
    {
        // Act 2 min is 20. First call returns 10, repair 1 returns 5 (total
        // 15), repair 2 returns 3 (total 18) - still short, so the act must
        // throw instead of being accepted thin.
        var (profile, formatDef, rules, style) = NewsActDeps();
        var provider = new QueueProvider([
            ArtScenesJson(firstScene: 1, count: 10, firstGroup: 11),
            ArtScenesJson(firstScene: 11, count: 5, firstGroup: 21),
            ArtScenesJson(firstScene: 16, count: 3, firstGroup: 26),
        ]);
        var ex = await Assert.ThrowsAsync<Exception>(() =>
            ScriptGenerator.GenerateNewsContinuationActAsync(
                profile, formatDef, rules, style, "English", provider,
                ScriptWithAct1(), actNumber: 2,
                actRole: "the backstory and the context that explains how we got here",
                sourceFacts: null));

        Assert.Contains("after two continuation repairs", ex.Message);
        Assert.Equal(3, provider.Prompts.Count);
        Assert.Contains("10 MORE scenes", provider.Prompts[1]);
        Assert.Contains("5 MORE scenes", provider.Prompts[2]);
    }

    // --- (c) per-act minimums are the low end of each act's ask ----------

    [Fact]
    public void NewsActMinimums_AreLowEndOfAsk()
    {
        Assert.Equal(12, ScriptGenerator.NewsActMinScenes(1));
        Assert.Equal(20, ScriptGenerator.NewsActMinScenes(2));
        Assert.Equal(18, ScriptGenerator.NewsActMinScenes(3));
        Assert.Equal(14, ScriptGenerator.NewsActAskHigh(1));
        Assert.Equal(24, ScriptGenerator.NewsActAskHigh(2));
        Assert.Equal(22, ScriptGenerator.NewsActAskHigh(3));
    }

    // --- (d) validation failure names the defect on retry ----------------

    [Fact]
    public async Task CompleteAndParse_RetryPrompt_NamesValidationDefect()
    {
        // The Bisleri act-3 defect: a "line" chart with 3 labels but 1 value.
        // The first attempt must fail validation; the retry prompt must name
        // the defect instead of re-asking blindly.
        var badChart = $$"""{"narration": "{{Narration(1)}}", "imagePrompt": "", "sceneKind": "visual", "visualSpec": {"kind": "line", "title": "Chart", "labels": ["A", "B", "C"], "values": [1], "unit": "%"}, "visualGroup": 1}""";
        var provider = new QueueProvider([
            ScenesJson([badChart, ArtSceneJson(2, 2)]),
            ArtScenesJson(firstScene: 1, count: 2, firstGroup: 1),
        ]);
        var result = await ScriptGenerator.CompleteAndParseAsync<ScenesOnly>(
            provider, "Write scenes.", "test act",
            validate: r => ScriptGenerator.ValidateNewsScenes(r.Scenes));

        Assert.Equal(2, result.Scenes.Count);
        Assert.Equal(2, provider.Prompts.Count);
        Assert.Contains("PREVIOUS ATTEMPT FAILED", provider.Prompts[1]);
        Assert.Contains("needs the same number of values as labels", provider.Prompts[1]);
    }

    // --- (e) end-to-end: thin Act 1 repaired, floor cleared -------------

    [Fact]
    public async Task GenerateNews_ThinAct1_RepairsAndClearsFloor()
    {
        // Bisleri's exact shape: Act 1 returns 10 valid scenes, Act 2
        // returns 20, Act 3 returns 18 - 48 scenes, a floor failure before
        // the fix. The Act 1 repair adds 2 scenes, taking the script to 50
        // scenes / 6,500 chars and clearing the floor.
        var profile = ContentProfileRegistry.Get("trendlinenow");
        var provider = new QueueProvider([
            Act1Json(sceneCount: 10, firstGroup: 1),
            ArtScenesJson(firstScene: 11, count: 2, firstGroup: 11),
            ArtScenesJson(firstScene: 13, count: 20, firstGroup: 13),
            ArtScenesJson(firstScene: 33, count: 18, firstGroup: 33),
        ]);
        var script = await ScriptGenerator.GenerateAsync(
            profile, topic: "Test water safety story", language: "en", provider,
            trendContext: "Test facts: a water sample failed safety checks and the regulator ordered wider checks.",
            longForm: true, topicCategory: "general");

        Assert.Equal(50, script.Scenes.Count);
        Assert.True(script.Scenes.Sum(s => s.Narration.Length) >= 5500);
        Assert.Equal(4, provider.Prompts.Count);
        Assert.Contains("2 MORE scenes", provider.Prompts[1]);
        Assert.Contains("START THIS ACT'S FIRST GROUP AT 11", provider.Prompts[1]);
    }
}
