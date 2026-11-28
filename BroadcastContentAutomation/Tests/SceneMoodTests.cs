// L83 — an off-menu mood tag is nulled (scene falls back to the default
// style) and that drop must be logged with the scene number, not silent.
namespace BroadcastContentAutomation.Tests;

public class SceneMoodTests
{
    [Fact]
    public void MoodNull_LogsDefaulting()
    {
        var scenes = new[]
        {
            new Scene { Narration = "Scene one.", Mood = "dramatic" },
            new Scene { Narration = "Scene two.", Mood = "calm" },
            new Scene { Narration = "Scene three.", Mood = null },
        };
        var oldOut = Console.Out;
        var sb = new StringWriter();
        Console.SetOut(sb);
        try
        {
            ScriptGenerator.NormalizeSceneMoods(scenes, ["calm", "tense"]);
        }
        finally
        {
            Console.SetOut(oldOut);
        }
        var log = sb.ToString();
        Assert.Null(scenes[0].Mood);
        Assert.Equal("calm", scenes[1].Mood);
        Assert.Null(scenes[2].Mood);
        Assert.Contains("scene 1", log);
        Assert.Contains("default", log);
        Assert.Contains("dramatic", log);
        // Only the dropped mood logs — a valid mood and an already-null
        // mood stay silent.
        Assert.DoesNotContain("scene 2", log);
        Assert.DoesNotContain("scene 3", log);
    }
}
