// Fix #2 (Rishi 2026-11-26, Shorts→long-form bridge): the Short must end
// on an open loop, never a flat summary or a resolution. The rule that
// governs the Short's content is the profile guidance (TrendlineNowProfile
// LongFormContentRules: "THE SHORT IS THE OPENING"); ShortRules is the
// mechanical backstop that judges a trimmed Short's last scene, so the trim
// sites can warn instead of silently shipping a flat ending.
//
// The detector is a heuristic on the last scene's final sentence — a
// question, an exclamation, or a forward-looking tease — because scenes
// carry no act/cliffhanger marker. These tests pin the boundary: real
// open-loop endings pass, real flat endings from the Messi script fail,
// and only the LAST scene matters.
using System.IO;
using Xunit;

namespace BroadcastContentAutomation.Tests;

public class ShortOpenLoopTests
{
    private static List<Scene> Scenes(params string[] narrations) =>
        narrations.Select(n => new Scene { Narration = n }).ToList();

    [Fact]
    public void QuestionEnding_IsOpenLoop()
    {
        Assert.True(ShortRules.ShortEndsOnOpenLoop(Scenes(
            "He lost the first game. Can he pull it off on Friday?")));
    }

    [Fact]
    public void ExclamationEnding_IsOpenLoop()
    {
        Assert.True(ShortRules.ShortEndsOnOpenLoop(Scenes(
            "Nobody believed it. And then he did the impossible!")));
    }

    [Fact]
    public void WhatHappensNext_IsOpenLoop()
    {
        Assert.True(ShortRules.ShortEndsOnOpenLoop(Scenes(
            "The league announced a new format. What happens next will change everything.")));
    }

    [Fact]
    public void YetToCome_IsOpenLoop()
    {
        Assert.True(ShortRules.ShortEndsOnOpenLoop(Scenes(
            "The record still stands. But the best is yet to come.")));
    }

    [Fact]
    public void PreparesForMatch_IsOpenLoop()
    {
        // A forward-looking ending phrased as preparation, not a question.
        Assert.True(ShortRules.ShortEndsOnOpenLoop(Scenes(
            "The fans are watching every touch as he prepares for Friday's high-stakes match.")));
    }

    [Fact]
    public void AtStake_IsOpenLoop()
    {
        Assert.True(ShortRules.ShortEndsOnOpenLoop(Scenes(
            "Everything is on the line now. A legacy is at stake.")));
    }

    [Fact]
    public void VerdictSentence_IsNotOpenLoop()
    {
        // A verdict states the outcome — the resolution belongs in Act 3,
        // never at the end of the Short.
        Assert.False(ShortRules.ShortEndsOnOpenLoop(Scenes(
            "He broke the record. His dominance is far from over, proving he is the greatest.")));
    }

    [Fact]
    public void FlatSummary_IsNotOpenLoop()
    {
        // The Messi Short's actual ending (scene 10): a flat summary
        // sentence about a competition review. This is the defect fix #2
        // exists to catch.
        Assert.False(ShortRules.ShortEndsOnOpenLoop(Scenes(
            "The league keeps changing its format. These adjustments are part of a broader, mandatory review of the entire competition calendar.")));
    }

    [Fact]
    public void OnlyLastSceneMatters()
    {
        Assert.True(ShortRules.ShortEndsOnOpenLoop(Scenes(
            "A flat summary sentence about the competition calendar.",
            "But what happens next will change everything.")));
        Assert.False(ShortRules.ShortEndsOnOpenLoop(Scenes(
            "But what happens next will change everything.",
            "A flat summary sentence about the competition calendar.")));
    }

    [Fact]
    public void EmptySelection_HasNothingToJudge()
    {
        Assert.True(ShortRules.ShortEndsOnOpenLoop([]));
    }

    [Fact]
    public void EmptyNarration_IsNotOpenLoop()
    {
        Assert.False(ShortRules.ShortEndsOnOpenLoop(Scenes("")));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "production", "finish-chunks.py")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repo root (production/finish-chunks.py).");
    }

    [Fact]
    public void OpenLoopRule_LivesInProfileGuidance()
    {
        // The rule that governs the Short's content is the script guidance:
        // the Act 1 prompt must carry the open-loop rule, or the next prep
        // writes the same flat opening again.
        var root = RepoRoot();
        var profile = File.ReadAllText(Path.Combine(root, "BroadcastContentAutomation", "VideoGen", "TrendlineNowProfile.cs"));
        Assert.Contains("THE SHORT IS THE OPENING", profile);
        Assert.Contains("END ON AN OPEN LOOP", profile);
        Assert.Contains("The payoff lives in Act 3 only", profile);
    }

    [Fact]
    public void OpenLoopCheck_WiredAtBothTrimSites()
    {
        // The trim sites are where a Short's ending is decided, and the
        // trim is duplicated (--write-props and --assemble) — the warning
        // must be wired at both, or one path ships flat endings silently.
        var root = RepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "BroadcastContentAutomation", "VideoGen", "Program.cs"));
        var calls = program.Split("ShortRules.ShortEndsOnOpenLoop").Length - 1;
        Assert.Equal(2, calls);
    }
}
