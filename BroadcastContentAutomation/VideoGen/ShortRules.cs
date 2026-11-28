// Short rules (fix #2, Rishi 2026-11-26, Shorts→long-form bridge).
//
// The Short is a prefix trim of the long-form script: the first N scenes
// that fit the vertical cap, plus the "Watch the full video" outro
// (see the vertical scene trim in Program.cs, RunWritePropsAsync and
// RunAssembleAsync). The payoff lives in Act 3, so the trim never includes
// it — but nothing in the trim guarantees the Short ENDS on an open loop.
// The last scene admitted by the budget is arbitrary: the Messi Short ended
// on "...these adjustments are part of a broader, mandatory review of the
// entire competition calendar." — a flat summary sentence, not a tease.
//
// The rule that actually governs the Short's content is the script guidance
// (TrendlineNowProfile LongFormContentRules: "THE SHORT IS THE OPENING" —
// the opening must end on an open loop and never state the outcome). This
// file is the mechanical backstop: it judges whether a trimmed Short ends
// on an open loop, so the trim sites can warn at --write-props / --assemble
// time instead of silently shipping a flat ending.
//
// It is deliberately a HEURISTIC and a WARNING, never a block: scenes carry
// no act/cliffhanger marker, so the check reads the last scene's final
// sentence — a question, an exclamation, or a forward-looking tease
// ("next", "coming", "will", "what happens", "at stake", ...). A verdict
// sentence ("...proving his dominance is far from over.") or a flat
// summary fails it. False negatives are possible (a forward-looking ending
// phrased without any of the markers), which is why this warns instead of
// failing the job: the reviewer decides, the rule in the profile guidance
// is what makes the next prep better.

internal static class ShortRules
{
    // Forward-looking words/phrases in the last sentence of the Short's
    // final scene. Matched as case-insensitive substrings; kept generic on
    // purpose — story-specific words (a weekday, a team name) would make
    // the check pass for the wrong reason on the next story.
    private static readonly string[] OpenLoopMarkers =
    [
        "next", "coming", "ahead", "will", "yet to", "to come", "find out",
        "what happens", "nobody knows", "no one knows",
        "can he", "can she", "can they", "could he", "could she",
        "prepar", "await", "at stake", "on the line", "countdown", "loom",
    ];

    // True when the Short's last scene ends on an open loop: its final
    // sentence is a question, an exclamation, or a forward-looking tease.
    // An empty selection has nothing to judge (true); an empty last
    // narration cannot be an open loop (false).
    internal static bool ShortEndsOnOpenLoop(IReadOnlyList<Scene> shortScenes)
    {
        if (shortScenes.Count == 0)
            return true;

        var narration = (shortScenes[^1].Narration ?? "").Trim();
        if (narration.Length == 0)
            return false;
        if (narration.EndsWith('?') || narration.EndsWith('!'))
            return true;

        var last = LastSentence(narration).ToLowerInvariant();
        return OpenLoopMarkers.Any(m => last.Contains(m, StringComparison.Ordinal));
    }

    // The text after the last sentence boundary (". ", "? ", "! "), or the
    // whole narration when it is a single sentence. Narration here is plain
    // declarative sentences, so abbreviation edge cases ("Dr. Smith") do not
    // matter: a wrong split only ever judges the tail, never the head.
    private static string LastSentence(string narration)
    {
        var text = narration.Trim();
        var idx = Math.Max(
            Math.Max(text.LastIndexOf(". ", StringComparison.Ordinal),
                     text.LastIndexOf("? ", StringComparison.Ordinal)),
            text.LastIndexOf("! ", StringComparison.Ordinal));
        return idx < 0 ? text : text[(idx + 2)..].Trim();
    }

    // ------------------------------------------------------------------
    // THE SECOND SHORT, THE NEW WAY (Rishi 2026-11-27 17:59 EST — this
    // replaces the fix-#5 tail cut, which shipped the Drake 2nd Short as a
    // plain ffmpeg tail cut of the vertical master reusing the main Short's
    // title and auto thumbnail on the same day. That execution self-collided:
    // same day + same title + same auto thumbnail + overlapping footage
    // (scenes 3-11 in both) made the two Shorts compete with each other
    // instead of reaching new viewers. The rule, all four parts together:
    //
    //   (1) DISTINCT MOMENT, ZERO OVERLAP: the 2nd Short is a different,
    //       self-contained moment — the payoff stretch near the end, cut
    //       from the landscape final and verticalized — never a tail cut
    //       of the main Short. It starts strictly after the main Short's
    //       last scene (SecondShortOverlapsMainShort).
    //   (2) ITS OWN TITLE, never the long-form / main-Short title: title
    //       variant 3 when one exists, else a title derived from the
    //       opening narration of the payoff stretch (PickSecondShortTitle).
    //   (3) ITS OWN THUMBNAIL, never the auto-picked frame duplicating
    //       the main Short (thumbnail-setting code, not this file).
    //   (4) POSTED ~12h AFTER THE MAIN SHORT GOES LIVE, never the same
    //       day: the 2nd Short's sidecar carries PublishAfterUtc and the
    //       uploader holds it until then. The 12h is ANCHORED ON THE MAIN
    //       SHORT GOING LIVE — its YouTube publication time — NOT on the
    //       finish (SecondShortHoldUntil).
    //
    // CADENCE (Rishi 2026-11-27 18:19 EST): upload something every 12h.
    // The long-form + main Short go out together; 12h later the 2nd
    // Short; 12h after that the next story's long-form + main Short.
    // One long-form per day; uploads alternate every 12h. That cadence
    // is WHY the hold is anchored on the main Short going live: the
    // finish can happen hours before publication, so a finish-anchored
    // hold would let the 2nd Short out early and break the alternation.
    //
    // Two helpers because the anchor is known at two different times:
    // the finish step writes a PROVISIONAL hold into PublishAfterUtc —
    // finish time + 12h (SecondShortHoldUntilFromFinish) — because the
    // main Short has not gone live yet; when it does, the uploader
    // re-anchors the hold to its publication time + 12h
    // (SecondShortHoldUntil). The provisional hold is only a floor: it
    // must never be treated as permission to publish before the
    // re-anchored hold when the main Short went live late.
    //
    // These helpers are pure so both sides of the pipeline can share the
    // contract: the Python finish step implements the same picker and the
    // same provisional hold, and the uploader enforces PublishAfterUtc.
    // ------------------------------------------------------------------

    // True when a proposed 2nd Short would overlap the main Short. The
    // main Short covers scenes 0..mainLastScene and the 2nd Short starts
    // at secondFirstScene; both ranges are contiguous from the same scene
    // ordering, so a strictly-later start is full disjointness and any
    // start at or before the main Short's last scene is an overlap —
    // including the adjacent-looking case secondFirstScene == mainLastScene,
    // which reuses that scene.
    internal static bool SecondShortOverlapsMainShort(int mainLastScene, int secondFirstScene) =>
        secondFirstScene <= mainLastScene;

    // Picks the 2nd Short's own title. Variant 3 = variants[2] when
    // present, non-empty, and different from the main title
    // (case-insensitive, trimmed) — used as-is, UsedFallback = false.
    // Otherwise derives a title from the opening narration: its first
    // sentence (split on ". " / "? " / "! ", or the whole text), trimmed
    // and truncated to 100 chars (the YouTube title limit) — the sentence's
    // terminal "?" / "!" is kept (it is part of the title's hook); a
    // trailing "." is dropped, so the title is words, not narration
    // punctuation. UsedFallback = false for a derived title.
    // Only when there is no usable variant AND no usable narration does it
    // fall back to the main title, with UsedFallback = true — the caller
    // must log a loud warning and flag the sidecar, because a 2nd Short
    // wearing the main Short's title is the defect this rule exists to
    // prevent.
    internal static (string Title, bool UsedFallback) PickSecondShortTitle(
        string mainTitle, IReadOnlyList<string>? variants, string? openingNarration)
    {
        if (variants is not null && variants.Count > 2)
        {
            var candidate = variants[2];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                candidate = candidate.Trim();
                if (!candidate.Equals((mainTitle ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    return (candidate, false);
            }
        }

        var derived = FirstSentence(openingNarration);
        if (derived.Length > 0)
            return (derived, false);

        return (mainTitle ?? "", true);
    }

    // Earliest publish instant for a 2nd Short: 12h after the MAIN SHORT
    // GOES LIVE (its YouTube publication time) — this is the anchor the
    // cadence runs on (Rishi 2026-11-27 18:19 EST: upload something every
    // 12h — long-form + main Short together, 2nd Short 12h later, next
    // story 12h after that). The uploader re-anchors Sidecar.PublishAfterUtc
    // to this instant once the main Short has published; a null hold means
    // "no hold" (every old sidecar, every long-form / main-Short sidecar).
    internal static DateTimeOffset SecondShortHoldUntil(DateTimeOffset mainShortPublishedAt) =>
        mainShortPublishedAt.AddHours(12);

    // Provisional hold, written by the finish step into
    // Sidecar.PublishAfterUtc when the main Short has not gone live yet:
    // finish time + 12h. A floor only — it must never be treated as
    // permission to publish before the re-anchored hold (SecondShortHoldUntil)
    // when the main Short went live late, or the 12h alternation breaks.
    internal static DateTimeOffset SecondShortHoldUntilFromFinish(DateTimeOffset finishTime) =>
        finishTime.AddHours(12);

    // The first sentence of a narration: the text up to and including the
    // first ". " / "? " / "! " boundary, or the whole text when it is a
    // single sentence. A trailing "." is stripped (a title is not a
    // sentence); a trailing "?" / "!" is kept. Truncated to 100 chars.
    // Empty when the narration is null, blank, or yields no words.
    private static string FirstSentence(string? narration)
    {
        var text = (narration ?? "").Trim();
        if (text.Length == 0)
            return "";

        var idx = EarliestSentenceBoundary(text);
        var sentence = (idx < 0 ? text : text[..(idx + 1)]).Trim();
        if (sentence.EndsWith('.'))
            sentence = sentence[..^1].TrimEnd();
        if (sentence.Length > 100)
            sentence = sentence[..100].TrimEnd();
        return sentence;
    }

    private static int EarliestSentenceBoundary(string text)
    {
        var idx = -1;
        foreach (var boundary in new[] { ". ", "? ", "! " })
        {
            var found = text.IndexOf(boundary, StringComparison.Ordinal);
            if (found >= 0 && (idx < 0 || found < idx))
                idx = found;
        }
        return idx;
    }
}
