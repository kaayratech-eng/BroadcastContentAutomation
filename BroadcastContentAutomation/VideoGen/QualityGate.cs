// Pipeline quality gate (Rishi 2026-11-23): video #4 shipped to review with
// broken images and nobody checked them before render.
//
// Video #4's art, as reviewed 2026-11-23: scene 0's prompt asks for
// "TRENDLINE NOW" lettering and the image has gibberish "NEN..." text;
// scene 5 asks for a Zelenskiy tweet + quote bubble with the word
// "exemplary" and the image is a creepy face with garbled text; scene 7
// asks for a Russian newspaper headline with the word "stealth"; scene 20's
// prompt says "no text" and AI Horde STILL baked in giant gibberish
// ".CII:I"; scene 31 is labelled ArtSource=pexels-stock but the image is a
// Batman-like masked figure, not a news anchor. Root causes, all encoded
// here as rules:
//   (1) image prompts ask models to draw text — free models cannot spell;
//   (2) the weakest legs hallucinate text even when the prompt says "no text";
//   (3) the best leg (OVHcloud) was rate-limited during that prep, so 44/57
//       scenes fell through to Pollinations (23) and AI Horde (16);
//   (4) nothing checked an image — or the script, or the music — before render.
//
// This class is the checker. It is pure and testable: every rule is a
// function over its inputs, and the only ffmpeg/ffprobe calls are the small
// loaders (LoadGrayMetricsAsync, ProbeAudioAsync, MeasureTrackLevelAsync,
// MeasureFinalMixAsync). Severity: Error blocks the pipeline step that
// called the gate (the caller throws, like the fact gate does); Warning is
// logged and recorded in the per-job quality report.
//
// Gates (see the design note, hidden_files/quality-checker.md):
//   (a) script — end of FinishPrepAsync, after the fact-gate rewrite,
//       before any TTS/art spend.
//   (b) image — prompt sanitised in GenerateSquareArtAsync (and in
//       ArtPoolClient, the other funnel); each generated file checked
//       before it is accepted (a bad file is deleted and the next
//       provider is tried); the whole job re-checked in batch before
//       --assemble renders, and by --check-quality.
//   (c) music — at track selection (ResolveBackgroundMusic) and at finish
//       time (finish-chunks.py verify_final + CheckFinalMixAsync here).
//
// Deliberately NOT here: OCR text-in-image detection — tesseract is not
// installed on this VM and no paid vision API is allowed, so the prompt
// ban + dimension/blank/blur checks are the enforcement now and OCR is a
// documented follow-up. Act boundaries are not stored in script.json, so
// the "acts 1-3 present" rule is checked as the long-form scene floor.
//
// Perceptual-hash duplicate detection IS here since 2026-11-26 (Ronaldo:
// the checker missed near-identical statue photos scenes 5+6 and a
// byte-identical clip scenes 26+31 because it never looked at image
// content — see CheckImagesBatch step (4)). aHash + grey-histogram
// cosine over every scene's resolved visual, with Rishi's reuse rule
// (2026-11-25 23:49): the same visual may return only 4-7+ scenes
// later — distance < 4 is an error, 4-7 a warning, 8+ allowed.
// The stock-subject check (step 5) is the same incident's other half:
// Pexels' search ranks loosely and the client picks by file quality,
// so scene 4 got an ice-hockey clip for a football story.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

internal enum QualitySeverity
{
    Warning,
    Error,
}

internal sealed record QualityFinding(
    string Gate,
    string Check,
    QualitySeverity Severity,
    string Message,
    string? Scene = null);

internal sealed class QualityReport
{
    public string Job { get; set; } = "";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public List<QualityFinding> Findings { get; set; } = [];

    public int ErrorCount => Findings.Count(f => f.Severity == QualitySeverity.Error);
    public int WarningCount => Findings.Count(f => f.Severity == QualitySeverity.Warning);
    public bool HasErrors => ErrorCount > 0;
    public string Status => HasErrors ? "FAIL" : WarningCount > 0 ? "WARN" : "PASS";
}

internal static class QualityGate
{
    // ------------------------------------------------------------------
    // Music pool (canonical for VideoGen; keep in sync with
    // Uploader.MusicCredits and VideoGen/assets/music-trendline/README.md).
    // The claimed bed alex-morgan-tense-suspense-rising-dread-573934.mp3
    // has NO code-level blocklist anywhere else (README note only) — the
    // blocklist here is the enforcement: it can never be selected again.
    // ------------------------------------------------------------------

    internal sealed record MusicTrackInfo(
        string License, string[] Moods, string? Credit);

    internal static readonly Dictionary<string, MusicTrackInfo> ApprovedTracks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tense-newsroom-01.mp3"] = new("CC0",
                ["tense"],
                null),
            ["tense-newsroom-02.mp3"] = new("CC0",
                ["tense"],
                null),
            ["uplifting-tech-01.mp3"] = new("CC BY",
                ["uplifting", "epic", "driving"],
                "Music: \"Rising Light (Uplifting Orchestral)\" by AurosonMusic — https://freemusicarchive.org/music/aurosonmusic/single/rising-light-uplifting-orchestral/ — CC BY 4.0"),
            ["calm-neutral-01.mp3"] = new("CC BY",
                ["calm", "ambient", "curious", "neutral"],
                "Music: \"Old chantry\" by Serat — https://freemusicarchive.org/music/serat/piano-textures/old-chantry-1/ — CC BY 4.0"),
            ["cinematic-epic-01.mp3"] = new("CC BY",
                ["cinematic", "epic"],
                "Music: \"A Hero for our Time\" by Geoff Harvey (Purple Planet Music) — https://freemusicarchive.org/music/geoff-harvey-purple-planet-music/epic-missions/a-hero-for-our-timemp3/ — CC BY 4.0"),
        };

    // Copyright-claimed bed (video #1's Short was blocked worldwide by an
    // Elite Alliance Music claim on this track). Must never be selected or
    // referenced by any job again — the check errors on the filename
    // wherever it appears (selection, sidecar, script).
    internal static readonly HashSet<string> BlockedTracks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "alex-morgan-tense-suspense-rising-dread-573934.mp3",
        };

    // Moods a video's MusicMood maps to (same mapping as
    // VideoAssembler.PickTrackForMood, so the check and the picker agree).
    internal static string[] MoodKeywords(string mood)
    {
        var m = (mood ?? "").Trim().ToLowerInvariant();
        if (m.Contains("tense")) return ["tense"];
        if (m.Contains("epic") || m.Contains("driving")) return ["uplifting", "cinematic-epic"];
        if (m.Contains("uplift") || m.Contains("energetic") || m.Contains("upbeat")) return ["uplifting"];
        if (m.Contains("cinematic")) return ["cinematic-epic"];
        if (m.Contains("curious") || m.Contains("ambient") || m.Contains("calm") || m.Contains("neutral")) return ["calm"];
        return [];
    }

    // Known ArtSource values (Scene.ArtSource, B11). Anything else means
    // the code path that wrote the file did not record its provenance.
    internal static readonly HashSet<string> KnownArtSources =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ovhcloud", "pollinations", "ai-horde", "pexels-stock",
            "pollinations-keyed", "cloudflare-flux", "cloudflare-sdxl",
            "remotion-visual", "manual", "wikimedia-commons",
        };

    // Bake-off 2026-11-24: Pollinations anonymous (Sana 768, both
    // images dark empty halls, 2/10) and AI Horde (SD 1.5 512px, melty
    // faces, fake "CIME" text despite "no text" in the prompt, 3-4/10)
    // are DISCARDED — no prompt tweak saves them. Art from either leg
    // is a hard error (image-discarded-leg): at generation time the
    // file is rejected and the next provider is tried; at batch-check
    // time the job cannot assemble. This is the gate side of the quota
    // rule — when FLUX's daily quota runs out, the prep must fill with
    // Pexels + passing OVH images or pause for the reset, never fall
    // through to these legs. "pollinations-keyed" is NOT discarded
    // (dormant until Pollen exists); "cloudflare-flux",
    // "cloudflare-sdxl", "ovhcloud", "pexels-stock" and
    // "wikimedia-commons" are the passing legs. (Old jobs' art is mostly these legs — COP31 44/51
    // scenes, Bisleri 37/54, video #4 39/57 — so --check-quality on
    // an old job now fails on images. That is the gate doing its job
    // on old art; it does NOT mean re-render — the no-redo rule
    // stands, and the Uploader path does not re-run the gate.)
    internal static readonly HashSet<string> DiscardedArtSources =
        new(StringComparer.OrdinalIgnoreCase) { "pollinations", "ai-horde" };

    internal static readonly HashSet<string> AiArtSources =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ovhcloud", "pollinations", "ai-horde",
            "pollinations-keyed", "cloudflare-flux", "cloudflare-sdxl",
        };

    // ------------------------------------------------------------------
    // Image-prompt text ban (the main fix for video #4).
    // ------------------------------------------------------------------

    // Clauses that explicitly ask for NO text — these are allowed (and are
    // what the sanitiser appends). They are stripped before the ban runs,
    // so "no text" never trips the ban itself.
    private static readonly string[] NoTextClauses =
    [
        "no text", "without text", "without any text", "no letters",
        "no words", "no lettering", "no watermark", "no captions",
        "text-free", "without words", "no signage",
    ];

    // Words/phrases that ask an image model to render text. Free models
    // cannot spell: every one of these in a prompt is a garbled-text image
    // waiting to happen (video #4 scenes 0, 5, 7). Matched on word
    // boundaries after the no-text clauses are removed. "signage" joined
    // 2026-11-24 (bake-off prompt fix): a prompt asking for signage is
    // asking for rendered text.
    private static readonly string[] TextRequestTerms =
    [
        "lettering", "headline", "tweet", "quote", "bubble", "caption",
        "title card", "the word", "text", "letters", "written",
        "handwritten", "typography", "newspaper", "question mark",
        "signage",
    ];

    // Subjects that are real-world and photographable: for a NEWS video a
    // real Pexels photo of these beats AI art every time. An AI image for
    // one of these earns a "prefer stock" warning (the pool-order change —
    // Pexels first for news scenes — is a follow-up; see the design note).
    private static readonly string[] PhotoSubjectTerms =
    [
        "ship", "warship", "port", "harbor", "harbour", "sea", "ocean",
        "city", "street", "crowd", "person", "people", "anchor",
        "building", "beach", "coast", "mountain", "river", "bridge",
        "market", "stadium", "airport", "train", "soldier", "police",
        "protest", "flag", "skyline", "village", "town", "desert",
    ];

    // Bake-off prompt fix (2026-11-24): appended to every image
    // prompt. Fixes the close-up problem on capable models (wide shot,
    // headroom, full scene visible). The old suffix was only "no text,
    // no letters, no words, no watermark" — the "no watermark" clause
    // is dropped: the leg that watermarked (Pollinations anonymous) is
    // discarded, and FLUX/OVH do not watermark.
    //
    // Eye-catching direction (Rishi 2026-11-25): images must stop the
    // scroll — dramatic cinematic lighting, high contrast, expressive
    // faces, dynamic composition, vibrant colour, rule of thirds, a
    // clear focal subject, no dull flat frames. The wide-shot /
    // headroom / no-text guards stay verbatim: they fixed the
    // over-zoom problem, so the new direction is layered in FRONT of
    // them, never instead of them.
    internal const string NoTextSuffix =
        "dramatic cinematic lighting, high contrast, expressive faces, strong emotion, dynamic composition, vibrant colour, rule of thirds, clear focal subject, no dull flat frames, wide shot, subject with headroom, full scene visible, no close-up, no cropped faces, no text, no words, no letters, no signage";

    internal static bool ImagePromptHasTextRequest(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var text = StripNoTextClauses(prompt.ToLowerInvariant());
        return TextRequestTerms.Any(t =>
            Regex.IsMatch(text, $@"\b{Regex.Escape(t)}\b"));
    }

    // Returns the terms that tripped the ban (for the finding message).
    internal static List<string> ImagePromptTextRequestTerms(string? prompt)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(prompt)) return found;
        var text = StripNoTextClauses(prompt.ToLowerInvariant());
        foreach (var t in TextRequestTerms)
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(t)}\b"))
                found.Add(t);
        return found;
    }

    private static string StripNoTextClauses(string lower)
    {
        var text = lower;
        foreach (var clause in NoTextClauses)
            text = text.Replace(clause, " ");
        return text;
    }

    // Removes text requests from a prompt and appends the no-text suffix.
    // Quoted spans are dropped first (title-card lettering is always
    // quoted: "TRENDLINE NOW"), then whole sentences containing a banned
    // term are dropped. When nothing usable survives — the whole prompt
    // WAS a title card — falls back to a clean abstract news background:
    // the Remotion intro bumper draws the real channel text over it, so
    // the image itself must carry no words at all.
    internal static (string Prompt, List<string> Removed) SanitizeImagePrompt(string? prompt)
    {
        var removed = new List<string>();
        var text = (prompt ?? "").Trim();
        if (text.Length == 0)
            return (FallbackArtPrompt, removed);

        // Drop quoted spans ("TRENDLINE NOW", "stealth", ...).
        var quoted = Regex.Matches(text, "\"[^\"]+\"");
        foreach (Match m in quoted) removed.Add(m.Value);
        text = Regex.Replace(text, "\"[^\"]+\"", " ");

        // Drop sentences containing a banned term.
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+");
        var kept = new List<string>();
        foreach (var sentence in sentences)
        {
            var s = sentence.Trim();
            if (s.Length == 0) continue;
            var terms = ImagePromptTextRequestTerms(s);
            if (terms.Count > 0)
            {
                removed.Add(s);
                continue;
            }
            kept.Add(s);
        }
        text = string.Join(" ", kept).Trim().TrimEnd(',', ';', '.');

        if (text.Length < 15)
        {
            if (text.Length > 0) removed.Add(text);
            text = FallbackArtPrompt;
        }
        // Bake-off fix: EVERY prompt carries the wide-shot wording, not
        // just prompts that lacked "no text" — a prompt that already
        // says "no text" (e.g. from an earlier sanitize) still needs
        // the wide shot / headroom / full-scene clauses. The guard is
        // the FULL suffix, not just "wide shot": a prompt that already
        // says "wide shot" but none of the rest (e.g. "Wide shot of
        // delegates at the summit") would otherwise reach the model
        // without "no text, no words, no letters, no signage". The
        // full-suffix guard keeps sanitize idempotent (sanitize twice
        // = sanitize once) while closing that under-append hole.
        else if (!text.Contains(NoTextSuffix, StringComparison.OrdinalIgnoreCase))
        {
            text += ", " + NoTextSuffix;
        }

        return (text, removed);
    }

    internal const string FallbackArtPrompt =
        "Cinematic editorial news illustration, dark navy and red tones, dramatic cinematic lighting, high contrast, expressive faces, strong emotion, dynamic composition, vibrant colour, rule of thirds, clear focal subject, no dull flat frames, wide shot, subject with headroom, full scene visible, no close-up, no cropped faces, no text, no words, no letters, no signage";

    // Prompt improver for selective repair (Rishi 2026-11-23 ~23:37):
    // a failed image is NEVER regenerated from the same prompt — the
    // improver rewrites it clearer and more concrete, text-free, and
    // grounded in the scene's own narration (the "expert" step the
    // overnight manual repair did by hand for COP31 scenes 6/47/49 and
    // Bisleri scenes 0/10/21/26/32/41). Deterministic, no model call.
    //   1. Sanitize first (drops text requests, quoted spans) — the
    //      sanitizer is the floor.
    //   2. If the whole prompt was a text request (sanitize fell back
    //      to the generic fallback), rebuild from the narration's
    //      concrete nouns: mid-sentence capitalised entities +
    //      photographable subjects.
    //   3. Otherwise keep the surviving prompt and add a composition
    //      directive from the narration's subject (person -> medium
    //      shot with an expressive face; place/ship/city -> wide
    //      establishing shot).
    //   4. Never return the input unchanged: when the rewrite equals
    //      the sanitized original, append a variation clause (the same
    //      "different angle" wording prep already uses for duplicate
    //      visuals).
    internal static (string Prompt, List<string> Changes) ImproveImagePrompt(
        string? prompt, string? narration, IReadOnlyList<string>? failingChecks = null)
    {
        var changes = new List<string>();
        var (clean, removed) = SanitizeImagePrompt(prompt);
        if (removed.Count > 0)
            changes.Add($"removed text request ({string.Join("; ", removed.Select(r => r.Length > 40 ? r[..40] + "…" : r))})");

        var isFallback = clean == FallbackArtPrompt;
        string improved;
        if (isFallback)
        {
            var subjects = ConcreteSubjects(narration);
            if (subjects.Count > 0)
            {
                improved = $"Cinematic editorial news illustration of {string.Join(", ", subjects)}, dark navy and red tones, {NoTextSuffix}";
                changes.Add($"rebuilt from the narration's concrete subjects ({string.Join(", ", subjects)}) — the original prompt was only a text request");
            }
            else
            {
                improved = FallbackArtPrompt;
                changes.Add("original prompt was only a text request and the narration names no concrete subject — fell back to clean editorial art");
            }
        }
        else
        {
            improved = clean;
            var directive = CompositionDirective(narration);
            if (directive is not null &&
                !improved.Contains(directive, StringComparison.OrdinalIgnoreCase))
            {
                improved += ", " + directive;
                changes.Add($"added composition ({directive})");
            }
        }

        if (failingChecks is not null &&
            failingChecks.Any(c => c.Equals("image-duplicate", StringComparison.OrdinalIgnoreCase) ||
                                   c.Equals("image-near-duplicate", StringComparison.OrdinalIgnoreCase)) &&
            !improved.Contains("different angle", StringComparison.OrdinalIgnoreCase))
        {
            improved += ", different angle and composition from the previous scene";
            changes.Add("added variation clause (duplicate art)");
        }

        // Never hand the generator the same prompt again.
        var original = (prompt ?? "").Trim();
        if (string.Equals(improved, original, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(improved, clean, StringComparison.OrdinalIgnoreCase))
        {
            improved += ", different angle and composition from the previous attempt";
            changes.Add("added variation clause (prompt would otherwise be unchanged)");
        }

        if (!improved.Contains("no text", StringComparison.OrdinalIgnoreCase))
            improved += ", " + NoTextSuffix;

        return (improved, changes);
    }

    // Concrete, drawable subjects from a narration: mid-sentence
    // capitalised entities (people, places, organisations) plus
    // photographable subjects (ship, port, city, …). Sentence-initial
    // capitals are skipped — every sentence starts capitalised.
    internal static List<string> ConcreteSubjects(string? narration)
    {
        var subjects = new List<string>();
        if (string.IsNullOrWhiteSpace(narration)) return subjects;
        foreach (var sentence in Regex.Split(narration, @"(?<=[.!?])\s+"))
        {
            var words = Regex.Matches(sentence, @"[\p{L}]+")
                .Select(m => m.Value).ToList();
            for (var i = 1; i < words.Count; i++)
            {
                var w = words[i];
                if (w.Length < 3) continue;
                if (!char.IsUpper(w[0])) continue;
                if (w.All(char.IsUpper)) continue;
                if (subjects.Count(s => s.Equals(w, StringComparison.OrdinalIgnoreCase)) == 0)
                    subjects.Add(w);
            }
        }
        var lower = narration.ToLowerInvariant();
        foreach (var term in PhotoSubjectTerms)
        {
            if (Regex.IsMatch(lower, $@"\b{Regex.Escape(term)}\b") &&
                subjects.Count(s => s.Equals(term, StringComparison.OrdinalIgnoreCase)) == 0)
                subjects.Add(term);
        }
        return subjects.Take(4).ToList();
    }

    private static string? CompositionDirective(string? narration)
    {
        if (string.IsNullOrWhiteSpace(narration)) return null;
        var lower = narration.ToLowerInvariant();
        string[] personTerms =
        [
            "person", "people", "man", "woman", "leader", "minister",
            "president", "anchor", "soldier", "police", "officer",
            "spokesperson", "official", "worker", "resident", "crowd",
        ];
        if (personTerms.Any(t => Regex.IsMatch(lower, $@"\b{Regex.Escape(t)}\b")))
            return "medium shot, eye-level, subject in focus, expressive face, strong emotion";
        string[] placeTerms =
        [
            "ship", "warship", "port", "harbor", "harbour", "city",
            "street", "building", "skyline", "bridge", "stadium",
            "airport", "market", "village", "town", "coast", "beach",
            "mountain", "river", "sea", "ocean",
        ];
        if (placeTerms.Any(t => Regex.IsMatch(lower, $@"\b{Regex.Escape(t)}\b")))
            return "wide establishing shot";
        return null;
    }

    internal static bool ImagePromptIsPhotoSubject(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var text = prompt.ToLowerInvariant();
        return PhotoSubjectTerms.Any(t =>
            Regex.IsMatch(text, $@"\b{Regex.Escape(t)}\b"));
    }

    // ------------------------------------------------------------------
    // Script check.
    // ------------------------------------------------------------------

    private static readonly string[] GreetingStarts =
    [
        "welcome", "hello", "hey ", "hi there", "good morning",
        "good afternoon", "good evening", "thanks for watching",
        "in this video", "today we're", "today we are",
    ];

    private static readonly string[] PlaceholderMarkers =
    [
        "lorem", "todo", "tbd", "as an ai", "placeholder", "[todo]",
    ];

    private static readonly string[] KnownAcronyms =
    [
        "USA", "UK", "EU", "NATO", "UN", "FBI", "CIA", "GTA", "AI", "TV",
        "US", "UAE", "IDF", "ICC", "WHO", "WTO", "G7", "G20", "BRICS",
        "ASEAN", "OPEC", "NASA", "BBC", "CNN", "MP", "PM", "CEO", "UFO",
        "GPS", "DNA", "ATM",
    ];

    private static readonly string[] Stopwords =
    [
        "the", "and", "with", "from", "what", "that", "this", "when",
        "after", "before", "about", "into", "over", "under", "more",
        "than", "then", "says", "said", "amid", "among",
    ];

    // Words-per-minute the pipeline targets (TTS +10% ≈ 165-175 wpm).
    internal const double WordsPerMinute = 170;

    internal static List<QualityFinding> CheckScript(
        VideoScript script, string? topic = null,
        IReadOnlyList<string>? outroLines = null)
    {
        var findings = new List<QualityFinding>();
        const string gate = "script";
        void Warn(string check, string message, string? scene = null) =>
            findings.Add(new(gate, check, QualitySeverity.Warning, message, scene));
        void Error(string check, string message, string? scene = null) =>
            findings.Add(new(gate, check, QualitySeverity.Error, message, scene));

        var story = script.Scenes ?? [];
        var brief = script.BriefScenes ?? [];

        // The intro bumper (scene 0, narration == script.IntroText) is
        // channel furniture, not story content: a title card + hook line,
        // short by design. It must not trip the scene-length, duplicate,
        // or question rules — the hook IS scene 0, the first thing the
        // viewer hears. (Video #4's bumper is its hook line, 91 chars.)
        bool IsBumper(int i) => IsBumperScene(script, i);

        // --- Hook: the first spoken line must be the story's most
        // entertaining peak moment, landing inside the first 10-20 seconds.
        var hookLine = FirstHookLine(script);
        if (hookLine is null)
        {
            Error("hook", "No hook line found: scenes are empty.");
        }
        else
        {
            var lower = hookLine.Trim().ToLowerInvariant();
            if (GreetingStarts.Any(g => lower.StartsWith(g, StringComparison.Ordinal)))
                Error("hook", $"Hook opens with a greeting (\"{Trunc(hookLine, 60)}\") — the first line must deliver the story's most entertaining peak moment inside the first 10-20 seconds, no greeting.");
            else if (IsVagueHook(hookLine, topic))
                Error("hook", $"Hook is vague (\"{Trunc(hookLine, 80)}\") — no number, name, or concrete claim. The first line must carry a specific entertaining peak moment.");
            if (hookLine.Length > 200)
                Warn("hook", $"Hook line is {hookLine.Length} chars (>200) — a hook that long loses the scroll before the moment lands.");
        }

        // --- Questions: max 2 in the whole spoken script.
        var questionCount = brief.Concat(story.Where((s, i) => !IsBumper(i)))
            .Sum(s => (s.Narration ?? "").Count(c => c == '?'));
        if (questionCount > 2)
            Error("questions", $"{questionCount} question sentences in the script (max 2) — questions are rationed to 1-2 per video; statements carry the story.");

        // --- Exact-duplicate narration (video #2 shipped a duplicated scene).
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < story.Count; i++)
        {
            if (IsBumper(i)) continue;
            var norm = NormalizeNarration(story[i].Narration);
            if (norm.Length == 0) continue;
            if (seen.TryGetValue(norm, out var first))
                Error("duplicate-scene", $"Scenes {first} and {i} have identical narration — a duplicated scene shipped in video #2; one of them must be rewritten.", $"scene{i}");
            else
                seen[norm] = i;
        }

        // --- Scene length + placeholder text (story scenes only; brief
        // scenes are short teasers by design).
        for (var i = 0; i < story.Count; i++)
        {
            if (IsBumper(i)) continue;
            var narration = story[i].Narration ?? "";
            var label = $"scene{i}";
            if (narration.Length < 40)
                Error("scene-length", $"Scene {i} narration is {narration.Length} chars (<40) — too thin to carry a visual.", label);
            else if (narration.Length > 260)
                Error("scene-length", $"Scene {i} narration is {narration.Length} chars (>260) — one scene cannot hold that much; split it.", label);
            else if (narration.Length < 120 || narration.Length > 200)
                Warn("scene-length", $"Scene {i} narration is {narration.Length} chars (target 120-200).", label);

            var low = narration.ToLowerInvariant();
            foreach (var marker in PlaceholderMarkers)
            {
                if (low.Contains(marker, StringComparison.Ordinal))
                {
                    Error("placeholder", $"Scene {i} narration contains placeholder text (\"{marker}\").", label);
                    break;
                }
            }
        }

        // --- Speculation: capped, and every speculative scene must carry a
        // recognised attribution phrase (the fact gate's list).
        var speculative = story
            .Select((s, i) => (Scene: s, Index: i))
            .Where(x => x.Scene.IsSpeculative)
            .ToList();
        if (speculative.Count > 4)
            Error("speculation", $"{speculative.Count} speculative scenes (>4) — the video leans on unconfirmed claims; cut or verify some.");
        else if (speculative.Count > 2)
            Warn("speculation", $"{speculative.Count} speculative scenes (>2) — keep speculation to 1-2 clearly-labelled beats.");
        foreach (var (scene, i) in speculative)
        {
            var narration = scene.Narration ?? "";
            if (!FactGateRewrite.AttributionPhrases.Any(p =>
                    narration.Contains(p, StringComparison.OrdinalIgnoreCase)))
                Error("speculation-attribution", $"Speculative scene {i} carries no recognised attribution phrase (\"reports suggest…\", \"the theory going around is…\", …) — speculation must be labelled in the narration, not just on screen.", $"scene{i}");
        }

        // --- Structure: outro/CTA, title, description, tags. (No brief
        // any more — removed 2026-11-25, Rishi: no sting, no brief. The
        // gate used to error when BriefScenes was not exactly 2 teaser
        // scenes; new scripts have none and that is correct.)
        // The CTA is usually NOT in a scene's narration: for Spotlight
        // Now it is the profile's outro card ("Follow @spotlightnowofficial so
        // you don't miss it."), rendered by the assembler after the last
        // scene. Checking narration alone false-positives on every real
        // script (video #4), so the caller passes the card lines and the
        // check accepts the CTA in either place.
        var hasOutro = story.Concat(brief).Any(s =>
            (s.Narration ?? "").Contains("Follow @spotlightnowofficial", StringComparison.OrdinalIgnoreCase)) ||
            (outroLines ?? []).Any(l =>
                (l ?? "").Contains("Follow @spotlightnowofficial", StringComparison.OrdinalIgnoreCase));
        if (!hasOutro)
            Error("outro", "No outro/CTA: neither the script nor the outro card asks viewers to \"Follow @spotlightnowofficial\".");
        if (string.IsNullOrWhiteSpace(script.Title))
            Error("title", "Title is empty.");
        else if (script.Title.Length > 100)
            Error("title", $"Title is {script.Title.Length} chars (>100) — it truncates in search and on phones.");
        if (string.IsNullOrWhiteSpace(script.Description))
            Error("description", "Description is empty.");
        else if (script.Description.Length < 100)
            Warn("description", $"Description is {script.Description.Length} chars (<100) — too thin for search; it should summarise the story.");
        if ((script.Tags ?? []).Count < 5)
            Error("tags", $"Only {(script.Tags ?? []).Count} tags (>=5 required).");

        // --- Pace: implied length at ~170 wpm (bumper excluded — it is
        // a title card, not story time).
        var totalChars = story.Where((s, i) => !IsBumper(i)).Concat(brief).Sum(s => (s.Narration ?? "").Length);
        var minutes = totalChars / 5.0 / WordsPerMinute;
        if (minutes < 6)
            Warn("pace", $"Implied length is {minutes:0.0} min (<6) at ~170 wpm — below the 8-12 min target; the video will feel thin.");
        else if (minutes > 13)
            Warn("pace", $"Implied length is {minutes:0.0} min (>13) at ~170 wpm — above the 8-12 min target; retention falls off a cliff past 12.");

        // --- Title quality (social-media rules).
        if (!string.IsNullOrWhiteSpace(script.Title) && !string.IsNullOrWhiteSpace(topic))
        {
            var keywords = TopicKeywords(topic);
            var titleLow = script.Title.ToLowerInvariant();
            if (keywords.Count > 0 && keywords.All(k => !titleLow.Contains(k, StringComparison.Ordinal)))
                Warn("title-keywords", $"Title contains none of the topic keywords ({string.Join(", ", keywords)}) — the title should carry the words people search for.");
        }
        if (!string.IsNullOrWhiteSpace(script.Title))
        {
            var capsWords = Regex.Matches(script.Title, @"\b[A-Z]{3,}\b")
                .Select(m => m.Value)
                .Where(w => !KnownAcronyms.Contains(w, StringComparer.Ordinal))
                .ToList();
            if (capsWords.Count > 0)
                Warn("title-caps", $"Title has ALL-CAPS word(s) ({string.Join(", ", capsWords)}) — reads as shouting; keep caps for real acronyms only.");
            var hasDigit = script.Title.Any(char.IsDigit);
            var hasCapitalBeyondFirst = Regex.Matches(script.Title, @"\b[A-Z][a-z]")
                .Count > 1;
            var hasTopicKeyword = !string.IsNullOrWhiteSpace(topic) &&
                TopicKeywords(topic).Any(k => script.Title.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (!hasDigit && !hasCapitalBeyondFirst && !hasTopicKeyword)
                Warn("title-clickbait", "Title has no number, no named entity, and no topic keyword — pure clickbait with nothing concrete to rank on.");
        }

        // --- Description footer/disclaimer: the uploader appends both at
        // upload time, so the script description is only warned on when it
        // carries neither — the video then relies entirely on the append.
        if (!string.IsNullOrWhiteSpace(script.Description))
        {
            var descLow = script.Description.ToLowerInvariant();
            var hasChannelRef = descLow.Contains("spotlightnow");
            var hasDisclaimerMarker = descLow.Contains("disclaimer") ||
                descLow.Contains("speculat") || descLow.Contains("alleged") ||
                descLow.Contains("unconfirmed") || descLow.Contains("opinion");
            if (!hasChannelRef && !hasDisclaimerMarker)
                Warn("description-links", "Description carries no channel cross-link and no disclaimer marker — it relies entirely on the uploader-appended footer/disclaimer; add a link to the channel in the description.");
        }

        return findings;
    }

    // Shared bumper test (see CheckScript): scene 0 is the intro bumper
    // when its narration is the script's IntroText.
    private static bool IsBumperScene(VideoScript script, int i)
    {
        var story = script.Scenes ?? [];
        return i == 0 && story.Count > 0 &&
            !string.IsNullOrWhiteSpace(script.IntroText) &&
            string.Equals(story[0].Narration, script.IntroText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Script craft checks (Rishi 2026-11-23 ~23:45): correctness of
    // words and statements, every scene fitting perfectly, retention +
    // SEO alignment — judged with social-media-trend, SEO-expert and
    // senior-architect eyes.
    //
    // Severity policy (calibrated 2026-11-24 on the two approved
    // scripts, COP31 + Bisleri): WARN by default. Only clear-cut
    // defects ERROR — a sentence that does not end, a doubled word, a
    // name spelled two ways, a near-duplicate scene pair, a title none
    // of whose words the story delivers. Everything else warns, because
    // a hard fail there would block legitimate scripts. The calibration
    // numbers are in the design note; the headline result: COP31 gains
    // 0 new findings, Bisleri gains 5 near-duplicate errors — all five
    // verified genuine restatements, which is exactly what this check
    // exists to catch before a render.
    //
    // Deliberately NOT implemented: per-scene consecutive-disconnect.
    // 62-64% of consecutive scene pairs in the approved scripts share
    // zero content words (synonym shifts like fossil-fuel -> oil and gas
    // defeat token matching), so a per-scene rule would false-positive
    // on every real script. Only the script-level tripwire (>80% of
    // consecutive pairs disconnected) survives, as a warning.
    // ------------------------------------------------------------------

    // Content words for overlap/similarity: tokens that carry meaning.
    // Stopwords + short tokens are dropped; numbers are kept (a shared
    // "1969" is real evidence two scenes say the same thing).
    private static readonly HashSet<string> CraftStopwords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "with", "from", "what", "that", "this", "when",
            "after", "before", "about", "into", "over", "under", "more",
            "than", "then", "says", "said", "amid", "among",
            "is", "are", "was", "were", "be", "been", "has", "have", "had",
            "will", "would", "can", "could", "may", "might", "its", "it",
            "they", "them", "their", "he", "she", "his", "her", "we", "you",
            "an", "of", "to", "in", "on", "for", "by", "at", "as", "or",
            "but", "not", "no", "so", "if", "there", "here", "these",
            "those", "while", "a", "i",
        };

    internal static HashSet<string> ContentWords(string? text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return words;
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+"))
        {
            var w = m.Value;
            if (w.Length < 2) continue;
            if (CraftStopwords.Contains(w)) continue;
            words.Add(w);
        }
        return words;
    }

    internal static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(w => b.Contains(w));
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    // Porter-lite stem for title-story matching: "collapses" and
    // "collapsed" must match, "bottle" and "bottled" must match.
    // Strip -ing/-ed/-es/-s (never stripping "ss" — "glass" is not
    // "glas"), then a trailing 'e'.
    internal static string StemWord(string word)
    {
        var w = word.ToLowerInvariant();
        if (w.EndsWith("ing", StringComparison.Ordinal) && w.Length > 5)
            w = w[..^3];
        else if (w.EndsWith("ed", StringComparison.Ordinal) && w.Length > 4)
            w = w[..^2];
        else if (w.EndsWith("es", StringComparison.Ordinal) && w.Length > 4)
            w = w[..^2];
        else if (w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && w.Length > 3)
            w = w[..^1];
        if (w.EndsWith('e') && w.Length > 3)
            w = w[..^1];
        return w;
    }

    internal static int LevenshteinDistance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static string StripProseHashtags(string text) =>
        Regex.Replace(text ?? "", @"#[\p{L}\p{N}_]+", " ");

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text ?? "", $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);

    // A scene is "dead" for the dead-stretch check when it carries no
    // digit, no non-sentence-initial capitalised entity or acronym, and
    // (when a topic is known) no topic keyword. Sentence-initial
    // capitals do NOT count as entities — every scene starts capitalised,
    // so counting them would make the check never fire.
    private static bool SceneIsDead(string narration, List<string> topicKeywords)
    {
        if (string.IsNullOrWhiteSpace(narration)) return true;
        if (narration.Any(char.IsDigit)) return false;
        if (topicKeywords.Any(k => narration.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return false;
        foreach (var sentence in Regex.Split(narration, @"(?<=[.!?])\s+"))
        {
            var words = Regex.Matches(sentence, @"[\p{L}\p{N}]+")
                .Select(m => m.Value).ToList();
            for (var i = 1; i < words.Count; i++)
            {
                var w = words[i];
                if (w.Length >= 2 && char.IsUpper(w[0]))
                    return false;
                if (w.Length >= 2 && w.All(char.IsUpper) && w.Any(char.IsLetter))
                    return false;
            }
        }
        return true;
    }

    internal static List<QualityFinding> CheckScriptCraft(
        VideoScript script, string? topic = null)
    {
        var findings = new List<QualityFinding>();
        const string gate = "script";
        void Warn(string check, string message, string? scene = null) =>
            findings.Add(new(gate, check, QualitySeverity.Warning, message, scene));
        void Error(string check, string message, string? scene = null) =>
            findings.Add(new(gate, check, QualitySeverity.Error, message, scene));

        var story = script.Scenes ?? [];
        var brief = script.BriefScenes ?? [];
        var topicKeywords = string.IsNullOrWhiteSpace(topic)
            ? new List<string>()
            : TopicKeywords(topic);
        var topicKeywordSet = new HashSet<string>(topicKeywords, StringComparer.OrdinalIgnoreCase);

        // --- (a) Correctness of words and statements.
        // Sentence completeness: a scene's narration must end with
        // terminal punctuation (trailing closers — quotes, parens —
        // stripped first; COP31 scene 35 ends "…technology.”"). A scene
        // that trails off mid-sentence is a clear defect: error.
        var spoken = story.Select((s, i) => (Narration: s.Narration ?? "", Index: i, Label: $"scene{i}", IsStory: true))
            .Concat(brief.Select((s, i) => (Narration: s.Narration ?? "", Index: i, Label: $"brief{i}", IsStory: false)))
            .ToList();
        foreach (var (narration, _, label, isStory) in spoken)
        {
            if (isStory && label == "scene0" && IsBumperScene(script, 0)) continue;
            if (string.IsNullOrWhiteSpace(narration)) continue;
            var trimmed = narration.Trim().TrimEnd('”', '"', '\'', ')', ']', ' ');
            if (trimmed.Length == 0) continue;
            var last = trimmed[^1];
            if (last is not ('.' or '!' or '?' or '…'))
                Error("sentence-complete", $"Narration does not end with terminal punctuation (ends \"…{(trimmed.Length > 40 ? trimmed[^40..] : trimmed)}\") — a scene that trails off mid-sentence reads as a cut-off take.", label);
        }

        // Immediate repeated word ("the the") — a typo, not a style
        // choice: error. Content-word repetition (4+ times in one scene)
        // warns: a scene leaning on one word reads as filler. Stopwords
        // are excluded (COP31 scene 14 says "the" 4x and is fine) and
        // topic keywords are exempt (the story's own subject words
        // legitimately recur).
        foreach (var (narration, _, label, isStory) in spoken)
        {
            if (isStory && label == "scene0" && IsBumperScene(script, 0)) continue;
            if (string.IsNullOrWhiteSpace(narration)) continue;
            var words = Regex.Matches(narration.ToLowerInvariant(), @"[\p{L}\p{N}]+")
                .Select(m => m.Value).ToList();
            for (var i = 1; i < words.Count; i++)
            {
                if (words[i] == words[i - 1])
                {
                    Error("repeated-word", $"Narration repeats the word \"{words[i]}\" back-to-back — a typo that survives into the voiceover.", label);
                    break;
                }
            }
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in words)
            {
                if (w.Length < 3) continue;
                if (CraftStopwords.Contains(w)) continue;
                if (topicKeywordSet.Contains(w)) continue;
                counts[w] = counts.TryGetValue(w, out var c) ? c + 1 : 1;
            }
            foreach (var (w, c) in counts.Where(kv => kv.Value >= 4).OrderByDescending(kv => kv.Value))
                Warn("word-stuffing", $"The word \"{w}\" appears {c} times in one scene — the scene leans on a single word; vary the wording.", label);
        }

        // Name-spelling consistency: the same person/place spelled two
        // ways ("Zelenskiy" in scene 3, "Zelensky" in scene 20) is a
        // contradiction the audience notices. Only mid-sentence
        // capitalised words count, same initial letter, length diff ≤1,
        // edit distance ≤2 — and neither may be a prefix of the other
        // ("Africa"/"African" is a noun/adjective pair, not a spelling
        // error; "Local"/"Social" differ in the first letter anyway).
        var midSentenceNames = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        void CollectNames(string narration, int sceneIndex)
        {
            if (string.IsNullOrWhiteSpace(narration)) return;
            foreach (var sentence in Regex.Split(narration, @"(?<=[.!?])\s+"))
            {
                var words = Regex.Matches(sentence, @"[\p{L}]+")
                    .Select(m => m.Value).ToList();
                for (var i = 1; i < words.Count; i++)
                {
                    var w = words[i];
                    if (w.Length < 5) continue;
                    if (!char.IsUpper(w[0])) continue;
                    if (w.All(char.IsUpper)) continue; // acronym, not a name
                    if (KnownAcronyms.Contains(w, StringComparer.Ordinal)) continue;
                    if (!midSentenceNames.TryGetValue(w, out var scenes))
                        midSentenceNames[w] = scenes = [];
                    if (!scenes.Contains(sceneIndex)) scenes.Add(sceneIndex);
                }
            }
        }
        for (var i = 0; i < story.Count; i++)
        {
            if (IsBumperScene(script, i)) continue;
            CollectNames(story[i].Narration, i);
        }
        var nameList = midSentenceNames.Keys.ToList();
        var reportedNamePairs = new HashSet<string>(StringComparer.Ordinal);
        for (var a = 0; a < nameList.Count; a++)
        {
            for (var b = a + 1; b < nameList.Count; b++)
            {
                var x = nameList[a];
                var y = nameList[b];
                if (char.ToLowerInvariant(x[0]) != char.ToLowerInvariant(y[0])) continue;
                if (Math.Abs(x.Length - y.Length) > 1) continue;
                if (x.StartsWith(y, StringComparison.OrdinalIgnoreCase) ||
                    y.StartsWith(x, StringComparison.OrdinalIgnoreCase)) continue;
                if (LevenshteinDistance(x.ToLowerInvariant(), y.ToLowerInvariant()) > 2) continue;
                var key = string.CompareOrdinal(x, y) < 0 ? $"{x}|{y}" : $"{y}|{x}";
                if (!reportedNamePairs.Add(key)) continue;
                Error("name-spelling", $"The name \"{x}\" (scene {midSentenceNames[x][0]}) and \"{y}\" (scene {midSentenceNames[y][0]}) look like the same person/place spelled two ways — pick one spelling and use it everywhere.");
            }
        }

        // Measurement consistency: the same hyphenated measurement with
        // two different numbers ("250-foot" in one scene, "200-foot" in
        // another) contradicts itself. Hyphenated compounds only — bare
        // units must NOT be compared (COP31 correctly says both 1.5°C
        // and 2°C: target vs goal).
        var unitNumbers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        for (var si = 0; si < story.Count; si++)
        {
            if (IsBumperScene(script, si)) continue;
            foreach (Match m in Regex.Matches(story[si].Narration ?? "",
                         @"(\d+(?:\.\d+)?)\s*-\s*(foot|feet|mile|miles|km|kilometres?|kilometers?|metres?|meters?|storeys?|stories|years?|days?|months?|weeks?|hours?|minutes?|seconds?)",
                         RegexOptions.IgnoreCase))
            {
                var unit = m.Groups[2].Value.ToLowerInvariant();
                if (!unitNumbers.TryGetValue(unit, out var nums))
                    unitNumbers[unit] = nums = new(StringComparer.OrdinalIgnoreCase);
                nums.Add(m.Groups[1].Value);
            }
        }
        foreach (var (unit, nums) in unitNumbers.Where(kv => kv.Value.Count > 1))
            Warn("measurement-consistency", $"The same measurement is given as {string.Join(" and ", nums.Select(n => $"{n}-{unit}"))} in different scenes — one of them is wrong; reconcile the number.");

        // --- (b) Every scene fits perfectly.
        // Near-duplicate scenes: content-word Jaccard ≥0.50 is a
        // restatement, not a new scene (Bisleri 13–27 score 0.611:
        // "founded in 1969 by Ramesh Chauhan… bottled water and soft
        // drinks" near-verbatim, twice). ≥0.40 warns. Both sets need ≥5
        // content words, so short scenes can't trip it on a shared topic
        // word alone.
        var contentSets = story.Select(s => ContentWords(s.Narration)).ToList();
        for (var i = 0; i < story.Count; i++)
        {
            if (IsBumperScene(script, i)) continue;
            if (contentSets[i].Count < 5) continue;
            for (var j = i + 1; j < story.Count; j++)
            {
                if (contentSets[j].Count < 5) continue;
                var sim = JaccardSimilarity(contentSets[i], contentSets[j]);
                if (sim >= 0.50)
                    Error("near-duplicate-scene", $"Scenes {i} and {j} are near-duplicates (content-word overlap {sim:0.00}) — one of them restates the other instead of advancing the story; rewrite or cut one.", $"scene{j}");
                else if (sim >= 0.40)
                    Warn("near-duplicate-scene", $"Scenes {i} and {j} overlap heavily (content-word overlap {sim:0.00}) — check they each earn their place.", $"scene{j}");
            }
        }

        // Consecutive-scene disconnect: script-level tripwire only (see
        // the header note — per-scene would false-positive on 62-64% of
        // consecutive pairs in approved scripts).
        var consecutivePairs = 0;
        var disconnectedPairs = 0;
        for (var i = 1; i < story.Count; i++)
        {
            if (IsBumperScene(script, i - 1) || IsBumperScene(script, i)) continue;
            consecutivePairs++;
            if (!contentSets[i - 1].Overlaps(contentSets[i])) disconnectedPairs++;
        }
        if (consecutivePairs > 0 && (double)disconnectedPairs / consecutivePairs > 0.80)
            Warn("scene-disconnect", $"{disconnectedPairs}/{consecutivePairs} consecutive scene pairs share no content words — the story jumps topic scene to scene instead of flowing; check the act order.");

        // Rhythm: scene lengths should vary (short punchy scenes against
        // longer explanatory ones). A near-zero spread is monotonous;
        // a huge spread means the pacing lurches. Calibrated: approved
        // scripts sit at stdev ~17-18 chars.
        var storyLengths = story.Where((_, i) => !IsBumperScene(script, i))
            .Select(s => (s.Narration ?? "").Length).ToList();
        if (storyLengths.Count >= 3)
        {
            var mean = storyLengths.Average();
            var variance = storyLengths.Sum(l => (l - mean) * (l - mean)) / (storyLengths.Count - 1);
            var stdev = Math.Sqrt(variance);
            if (stdev < 10)
                Warn("rhythm", $"Scene lengths barely vary (stdev {stdev:0.0} chars) — every scene lands the same length, which reads as monotonous; vary short punchy scenes against longer ones.");
            else if (stdev > 45)
                Warn("rhythm", $"Scene lengths vary wildly (stdev {stdev:0.0} chars) — the pacing lurches between one-liners and monologues; even the lengths out.");
        }

        // Dead stretch: ≥3 consecutive scenes with no digit, no entity,
        // and no topic keyword — a stretch where nothing concrete
        // happens and viewers drift.
        var deadRunStart = -1;
        for (var i = 0; i < story.Count; i++)
        {
            if (IsBumperScene(script, i))
            {
                if (deadRunStart >= 0 && i - deadRunStart >= 3)
                    Warn("dead-stretch", $"Scenes {deadRunStart}–{i - 1} are a dead stretch ({i - deadRunStart} scenes with no number, no named entity, and no topic keyword) — nothing concrete happens here; cut or sharpen them.");
                deadRunStart = -1;
                continue;
            }
            if (SceneIsDead(story[i].Narration ?? "", topicKeywords))
            {
                if (deadRunStart < 0) deadRunStart = i;
            }
            else
            {
                if (deadRunStart >= 0 && i - deadRunStart >= 3)
                    Warn("dead-stretch", $"Scenes {deadRunStart}–{i - 1} are a dead stretch ({i - deadRunStart} scenes with no number, no named entity, and no topic keyword) — nothing concrete happens here; cut or sharpen them.");
                deadRunStart = -1;
            }
        }
        if (deadRunStart >= 0 && story.Count - deadRunStart >= 3)
            Warn("dead-stretch", $"Scenes {deadRunStart}–{story.Count - 1} are a dead stretch ({story.Count - deadRunStart} scenes with no number, no named entity, and no topic keyword) — nothing concrete happens here; cut or sharpen them.");

        // Act roles (thirds approximation — act boundaries are not stored
        // in script.json): the middle third should carry backstory /
        // development markers, the last third forward-looking language.
        // Both are warnings: a story can legitimately spend its middle
        // on the present, but a middle with no "after/when/years" and a
        // close with no "next/will" is usually a flat structure.
        if (story.Count >= 9)
        {
            var body = story.Where((_, i) => !IsBumperScene(script, i)).ToList();
            var third = body.Count / 3;
            if (third > 0)
            {
                var middle = body.Skip(third).Take(third);
                var last = body.Skip(2 * third);
                var backstoryMarkers = new[] { "after", "before", "when", "years", "history", "began", "started", "earlier", "decades", "since", "originally" };
                var forwardMarkers = new[] { "next", "will", "future", "ahead", "comes next", "watch", "follow", "what happens", "going forward" };
                var middleText = string.Join(" ", middle.Select(s => s.Narration ?? ""));
                var lastText = string.Join(" ", last.Select(s => s.Narration ?? ""));
                if (!backstoryMarkers.Any(m => ContainsWord(middleText, m)))
                    Warn("act2-backstory", "The middle third carries no backstory/development language (after, before, when, years, history, began…) — Act 2 should develop the story, not just repeat Act 1.");
                if (!forwardMarkers.Any(m => ContainsWord(lastText, m)))
                    Warn("act3-forward", "The last third carries no forward-looking language (next, will, future, what happens…) — Act 3 should land the stakes and what happens next.");
            }
        }

        // --- (c) Retention + SEO alignment.
        // Title-story match: a title whose words the story never
        // delivers is undelivered clickbait. Stemmed matching
        // ("collapses" vs "collapsed", "bottle" vs "bottled"). Error
        // when NO stemmed title word appears in the body; warning when
        // fewer than half do.
        if (!string.IsNullOrWhiteSpace(script.Title))
        {
            var titleWords = Regex.Matches(script.Title.ToLowerInvariant(), @"[\p{L}]+")
                .Select(m => m.Value)
                .Where(w => w.Length > 3 && !CraftStopwords.Contains(w))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (titleWords.Count > 0)
            {
                var bodyStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in story.Concat(brief))
                    foreach (Match m in Regex.Matches(s.Narration ?? "", @"[\p{L}]+"))
                        if (m.Value.Length > 2)
                            bodyStems.Add(StemWord(m.Value));
                var present = titleWords.Count(w => bodyStems.Contains(StemWord(w)));
                if (present == 0)
                    Error("title-story", $"The title (\"{Trunc(script.Title, 80)}\") shares no stemmed word with the story — the title promises something the video never delivers (undelivered clickbait).");
                else if ((double)present / titleWords.Count < 0.5)
                    Warn("title-story", $"Only {present}/{titleWords.Count} title words appear in the story (stemmed) — the title overpromises relative to what the video delivers.");
            }
        }

        // Description first lines + tags must carry the topic keywords
        // (search + feed truncation show the first ~200 chars). Hashtags
        // are stripped before counting — a tag does not count as prose.
        // Keyword stuffing: one keyword ≥6 times, or >10% of the
        // title+description tokens.
        if (topicKeywords.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(script.Description))
            {
                var prose = StripProseHashtags(script.Description);
                var first200 = prose.Length > 200 ? prose[..200] : prose;
                if (!topicKeywords.Any(k => first200.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    Warn("description-keywords", "The description's first 200 characters contain no topic keyword — search and feed truncation show the opening lines first; lead with the story's words.");
            }
            if ((script.Tags ?? []).Count > 0)
            {
                var tagsText = string.Join(" ", script.Tags ?? []);
                if (!topicKeywords.Any(k => tagsText.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    Warn("tag-coverage", "No tag contains a topic keyword — the tags don't cover the story's subject; add the words people search for.");
            }
            if (!string.IsNullOrWhiteSpace(script.Description) && !string.IsNullOrWhiteSpace(script.Title))
            {
                var prose = StripProseHashtags(script.Title + " " + script.Description);
                var tokens = Regex.Matches(prose.ToLowerInvariant(), @"[\p{L}\p{N}]+").Count;
                foreach (var k in topicKeywords)
                {
                    var count = Regex.Matches(prose.ToLowerInvariant(), $@"\b{Regex.Escape(k)}\b").Count;
                    if (count >= 6 || (tokens > 0 && (double)count / tokens > 0.10))
                    {
                        Warn("keyword-stuffing", $"The keyword \"{k}\" appears {count} times in the title + description — stuffing reads as spam to search and to viewers; say it once or twice and let the story carry the rest.");
                        break;
                    }
                }
            }
        }

        return findings;
    }

    internal static string? FirstHookLine(VideoScript script)
    {
        // The hook is the first spoken line: the first story scene (scene 0,
        // the bumper — the hook title card). Old scripts may still carry a
        // brief; its first sentence still counts as the hook when present.
        // A greeting HERE is the failure the hook rule exists to catch, so greeting
        // lines are returned, not skipped (the intro bumper is a
        // separate scene inserted later and is not in this list at
        // prep time).
        return (script.BriefScenes ?? []).Concat(script.Scenes ?? [])
            .Select(s => (s.Narration ?? "").Trim())
            .FirstOrDefault(l => l.Length > 0);
    }

    private static bool IsVagueHook(string line, string? topic)
    {
        var hasDigit = line.Any(char.IsDigit);
        var hasCapitalBeyondFirst = Regex.Matches(line, @"\b[A-Z][a-z]").Count > 1;
        var hasTopicKeyword = !string.IsNullOrWhiteSpace(topic) &&
            TopicKeywords(topic).Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));
        return !hasDigit && !hasCapitalBeyondFirst && !hasTopicKeyword;
    }

    internal static List<string> TopicKeywords(string topic)
    {
        return topic.ToLowerInvariant()
            .Split([' ', ',', '.', ':', ';', '-', '–', '—', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\'', '"', '.', ',', ':', ';'))
            .Where(w => w.Length > 3 && !Stopwords.Contains(w, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeNarration(string? narration)
    {
        if (string.IsNullOrWhiteSpace(narration)) return "";
        return Regex.Replace(narration.Trim().ToLowerInvariant(), @"\s+", " ");
    }

    private static string Trunc(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    // ------------------------------------------------------------------
    // Image-file checks.
    // ------------------------------------------------------------------

    // Sharpness threshold, calibrated 2026-11-23 on the variance of the
    // Laplacian (256x256 grey): video #4's worst images score 115-156
    // (Pollinations 115, the duplicated Pexels pair 122, AI Horde 139);
    // shipped videos #1/#3 bottom out at 230/273. 200 separates the
    // two groups with margin on both sides.
    internal const double SharpnessWarnThreshold = 200.0;

    // A real 1024x1024 image never has a grey stddev this low; a broken
    // or blank render does. Calibrated: the lowest real scene in videos
    // #1/#3/#4 is ~24.
    internal const double BlankStdDevThreshold = 5.0;

    internal const long MinSaneFileBytes = 20 * 1024;

    // Pure pixel math over a 256x256 grey buffer (row-major, 0-255):
    // grey stddev (blank/uniform detection) and variance of the
    // Laplacian (sharpness — an upscaled 512px image is soft by
    // construction and scores low). Testable without ffmpeg.
    internal static (double StdDev, double VarLaplacian) AnalyzePixels(
        byte[] gray, int width, int height)
    {
        if (gray.Length < width * height)
            throw new ArgumentException(
                $"Grey buffer too short: {gray.Length} bytes for {width}x{height}.");
        var n = (double)(width * height);
        double sum = 0, sumSq = 0;
        for (var i = 0; i < width * height; i++)
        {
            var v = gray[i];
            sum += v;
            sumSq += (double)v * v;
        }
        var mean = sum / n;
        var variance = Math.Max(0, sumSq / n - mean * mean);
        var stdDev = Math.Sqrt(variance);

        // Variance of the 3x3 Laplacian, interior pixels only.
        double lapSum = 0, lapSumSq = 0;
        var count = 0;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var c = gray[y * width + x];
                var lap = -4 * c
                    + gray[(y - 1) * width + x] + gray[(y + 1) * width + x]
                    + gray[y * width + x - 1] + gray[y * width + x + 1];
                lapSum += lap;
                lapSumSq += (double)lap * lap;
                count++;
            }
        }
        var lapMean = lapSum / count;
        var varLap = Math.Max(0, lapSumSq / count - lapMean * lapMean);
        return (stdDev, varLap);
    }

    // ------------------------------------------------------------------
    // Perceptual duplicate detection (Ronaldo 2026-11-25 — the checker
    // missed near-identical statue photos scenes 5+6 and a byte-identical
    // clip scenes 26+31 because it never looked at image content).
    // Two signals over the 256x256 grey buffer LoadGray256 returns:
    //   aHash — 64-bit average hash: block-average to 8x8, one bit per
    //     cell above the mean. Structure only, colour-blind.
    //   grey-histogram cosine — 256-bin luminance histogram, cosine
    //     similarity. Palette only, structure-blind.
    // Neither separates the Ronaldo defects alone. Calibration on the
    // Ronaldo job (job-20261125-234227, measured 2026-11-26 in C# on the
    // files the batch check actually hashes — square sources for art,
    // mid-frames for clips):
    //   statue photos 5 vs 6 (same statue, seconds apart): aHash 7, hist 0.998
    //   same pair as portraits (05 vs 06-bad backup):     aHash 12, hist 0.998
    //   byte-identical clip 26 vs 31 (mid-frames):        aHash 0, hist 1.000
    //   different images 17 vs 18 (silhouette vs boy):    aHash 14, hist 0.329
    //   unrelated art 5 vs 8:                             aHash 37, hist 0.531
    //   unrelated clips 7 vs 14 (mid-frames):             aHash 24, hist 0.239
    // Plain aHash cannot separate the statue pair from the 17/18 pair on
    // its own — the gap is 12 vs 14, one bit. The histogram separates
    // cleanly, so a pair counts as a near-duplicate only when BOTH hold:
    // structure close (aHash <= 12) AND palette close (hist cosine
    // >= 0.90). The 0.90 floor is calibrated, not guessed: the true
    // defects sit at 0.998-1.000, while the nearest same-subject
    // different-pose pair (scenes 10/16, two FLUX Ronaldo portraits in
    // red on dark backgrounds) scores 0.83 — the same aHash distance
    // (12) as the statue portraits, but a different picture, and it must
    // not flag. 0.90 sits between 0.83 and 0.998.
    // ------------------------------------------------------------------
    internal const int NearDuplicateMaxHashDistance = 12;
    internal const double NearDuplicateMinHistCosine = 0.90;

    internal static ulong ComputeAverageHash(byte[] gray256)
    {
        if (gray256.Length < 256 * 256)
            throw new ArgumentException(
                $"Grey buffer too short: {gray256.Length} bytes for 256x256.");
        // Block-average 32x32 cells to 8x8 (the ffmpeg scale to 256x256
        // already anti-aliased; averaging again is the downsample).
        var cells = new double[64];
        for (var by = 0; by < 8; by++)
            for (var bx = 0; bx < 8; bx++)
            {
                double sum = 0;
                for (var y = by * 32; y < by * 32 + 32; y++)
                    for (var x = bx * 32; x < bx * 32 + 32; x++)
                        sum += gray256[y * 256 + x];
                cells[by * 8 + bx] = sum / (32 * 32);
            }
        var mean = cells.Sum() / cells.Length;
        ulong hash = 0;
        for (var i = 0; i < 64; i++)
            if (cells[i] > mean)
                hash |= 1UL << i;
        return hash;
    }

    internal static int HammingDistance(ulong a, ulong b) =>
        System.Numerics.BitOperations.PopCount(a ^ b);

    internal static double GrayHistogramCosine(byte[] grayA, byte[] grayB)
    {
        var histA = new long[256];
        var histB = new long[256];
        foreach (var v in grayA) histA[v]++;
        foreach (var v in grayB) histB[v]++;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < 256; i++)
        {
            dot += (double)histA[i] * histB[i];
            normA += (double)histA[i] * histA[i];
            normB += (double)histB[i] * histB[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    internal static bool AreNearDuplicates(ulong hashA, ulong hashB, double histCosine) =>
        HammingDistance(hashA, hashB) <= NearDuplicateMaxHashDistance &&
        histCosine >= NearDuplicateMinHistCosine;

    // Reuse distance bands (Rishi 2026-11-25 23:49): the same visual may
    // be reused, but NEVER in consecutive scenes — only 4-7+ scenes
    // later. Distance < 4 is an error, 4-7 a warning, 8+ allowed.
    internal static QualitySeverity? ReuseDistanceSeverity(int sceneDistance) =>
        sceneDistance < 4 ? QualitySeverity.Error :
        sceneDistance <= 7 ? QualitySeverity.Warning :
        null;

    // Decodes an image to a 256x256 grey buffer via ffmpeg (no
    // System.Drawing in this sandbox). Returns null when ffmpeg cannot
    // decode the file at all (corrupt image — the caller errors).
    internal static byte[]? LoadGray256(string imagePath)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"qg-gray-{Guid.NewGuid():N}.raw");
        try
        {
            var result = RunProcessSync("ffmpeg",
            [
                "-v", "error", "-i", imagePath,
                "-vf", "scale=256:256",
                "-pix_fmt", "gray",
                "-f", "rawvideo", tmp,
            ], timeoutSeconds: 60);
            if (result.ExitCode != 0 || !File.Exists(tmp)) return null;
            var bytes = File.ReadAllBytes(tmp);
            return bytes.Length == 256 * 256 ? bytes : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    internal static (int Width, int Height)? ProbeImageDimensions(string imagePath)
    {
        var result = RunProcessSync("ffprobe",
        [
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height",
            "-of", "csv=p=0", imagePath,
        ], timeoutSeconds: 30);
        if (result.ExitCode != 0) return null;
        var parts = result.Stdout.Trim().Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) ||
            !int.TryParse(parts[1], out var h))
            return null;
        return (w, h);
    }

    // Container duration in seconds (ffprobe format duration — works
    // for video and audio alike). 0 when it cannot be probed.
    internal static double ProbeMediaDuration(string path)
    {
        var result = RunProcessSync("ffprobe",
        [
            "-v", "error", "-show_entries", "format=duration",
            "-of", "csv=p=0", path,
        ], timeoutSeconds: 30);
        if (result.ExitCode != 0) return 0;
        return double.TryParse(result.Stdout.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    // One still from the middle of a stock clip, for the near-duplicate
    // check — a clip's subject and palette are what the viewer sees,
    // and the mid-frame is the least likely to be a fade or a cut.
    // Returns the temp PNG path (the caller deletes it) or null when
    // the clip cannot be probed or framed; a clip that cannot be framed
    // is skipped by the check, never an error on its own.
    internal static string? ExtractClipMidFrame(string clipPath)
    {
        var duration = ProbeMediaDuration(clipPath);
        if (duration <= 0) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"qg-midframe-{Guid.NewGuid():N}.png");
        var result = RunProcessSync("ffmpeg",
        [
            "-v", "error",
            "-ss", (duration / 2).ToString("0.###", CultureInfo.InvariantCulture),
            "-i", clipPath,
            "-frames:v", "1", tmp,
        ], timeoutSeconds: 60);
        if (result.ExitCode != 0 || !File.Exists(tmp)) return null;
        return tmp;
    }

    // ------------------------------------------------------------------
    // Stock-subject check (Ronaldo scene 4: Pexels returned an ice-hockey
    // clip for "cristiano ronaldo celebrating goal portugal national
    // team" — the search ranks loosely and the client picks by file
    // quality, not relevance). Pexels video search returns no tags and
    // no alt text; the only subject signal is the page-URL slug
    // ("exciting-sweden-vs-norway-ice-hockey-match-highlights-32159115").
    // Photo search returns alt text. Both are recorded on the scene at
    // download time (Scene.PexelsUrl / PexelsAlt); without them the
    // check has nothing to judge and skips the scene.
    //
    // Each group is one subject with its synonyms; a group "names" a
    // text when any of its terms appears in it (word-boundary match on
    // the hyphen/underscore-normalised text). When the metadata names a
    // group the scene's own text (stock query + narration + title)
    // never mentions, the clip is about something else — warn, never
    // error: the slug is a heuristic, and a warning keeps the job
    // moving while flagging the clip for review. Only sports are
    // listed: that is the observed failure mode, and sport terms are
    // unambiguous in a slug. A clip whose slug names no listed subject
    // (e.g. "sevilla-20537082") is not judged — no signal, no finding.
    // ------------------------------------------------------------------
    internal static readonly (string Name, string[] Terms)[] StockSubjectGroups =
    [
        ("american football", ["american football", "nfl"]),
        ("football", ["football", "soccer"]),
        ("ice hockey", ["ice hockey", "hockey"]),
        ("basketball", ["basketball"]),
        ("tennis", ["tennis"]),
        ("golf", ["golf"]),
        ("baseball", ["baseball"]),
        ("rugby", ["rugby"]),
        ("cricket", ["cricket"]),
        ("volleyball", ["volleyball"]),
        ("badminton", ["badminton"]),
        ("boxing", ["boxing"]),
        ("mma", ["mma", "ufc"]),
        ("skiing", ["skiing", "snowboard", "snowboarding"]),
        ("surfing", ["surfing"]),
        ("skateboarding", ["skateboard", "skateboarding"]),
        ("swimming", ["swimming"]),
        ("gymnastics", ["gymnastics"]),
    ];

    // Returns the mismatching subject's name, or null when the metadata
    // names no listed subject or names one the scene text also mentions.
    // The most specific group wins: "american football" is checked before
    // "football", so an NFL clip in a soccer story is not excused by the
    // shared word "football".
    internal static string? StockSubjectMismatch(string? metadataText, string sceneText)
    {
        if (string.IsNullOrWhiteSpace(metadataText) || string.IsNullOrWhiteSpace(sceneText))
            return null;
        var meta = NormalizeSubjectText(metadataText);
        var scene = NormalizeSubjectText(sceneText);
        string? matchedSpecific = null;
        foreach (var (name, terms) in StockSubjectGroups)
        {
            var inMeta = terms.Any(t => SubjectTermPresent(meta, t));
            if (!inMeta) continue;
            // "american football" contains "football": once the specific
            // group has matched the metadata, the generic group must not
            // also match and excuse (or double-report) the same clip.
            if (name == "american football") matchedSpecific = name;
            if (name == "football" && matchedSpecific is not null) continue;
            var inScene = terms.Any(t => SubjectTermPresent(scene, t));
            if (!inScene) return name;
        }
        return null;
    }

    internal static string NormalizeSubjectText(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    internal static bool SubjectTermPresent(string normalizedText, string term) =>
        Regex.IsMatch(normalizedText, $@"\b{Regex.Escape(term)}\b");


    // Full per-file check for one scene's square art. Errors are the
    // hard-bad cases (missing, corrupt, wrong size, blank, no provenance);
    // soft images warn. The caller decides whether an error rejects the
    // image (generation time: delete + next provider) or blocks the
    // pipeline (batch check before render).
    internal static List<QualityFinding> CheckImageFile(
        string path, string? artSource, string? imagePrompt, int sceneIndex)
    {
        const string gate = "images";
        var label = $"scene{sceneIndex}";
        var findings = new List<QualityFinding>();
        void Warn(string check, string message) =>
            findings.Add(new(gate, check, QualitySeverity.Warning, message, label));
        void Error(string check, string message) =>
            findings.Add(new(gate, check, QualitySeverity.Error, message, label));

        var fileName = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            Error("image-exists", $"Art file missing: {fileName}.");
            return findings;
        }

        // Bake-off 2026-11-24: art from a discarded leg is a hard
        // error on its own — the file is rejected at generation time
        // (delete + next provider) and blocks the batch check. One
        // finding only: no point reporting softness on a file that
        // must be regenerated anyway.
        if (artSource is not null && DiscardedArtSources.Contains(artSource))
        {
            Error("image-discarded-leg", $"Art came from {artSource} — a leg the 2026-11-24 visual bake-off discarded (Pollinations anonymous 2/10: dark empty halls; AI Horde 3-4/10: melty faces, fake text). It must not ship; regenerate from a passing leg (Cloudflare FLUX, Pexels, OVHcloud).");
            return findings;
        }

        var info = new FileInfo(path);
        if (info.Length == 0)
        {
            Error("image-size", $"Art file is 0 bytes: {fileName} — nothing was written.");
            return findings;
        }
        if (info.Length < MinSaneFileBytes)
            Warn("image-size", $"Art file is {info.Length / 1024} KB (<20 KB) — likely blank or broken: {fileName}.");

        var dims = ProbeImageDimensions(path);
        if (dims is null)
        {
            Error("image-decode", $"Art file does not parse as an image: {fileName} — corrupt download or wrong format.");
            return findings;
        }
        if (dims.Value.Width != 1024 || dims.Value.Height != 1024)
            Error("image-dimensions", $"Art is {dims.Value.Width}x{dims.Value.Height}, expected 1024x1024: {fileName} — a smaller native image upscaled.");

        if (string.IsNullOrWhiteSpace(artSource))
            Error("image-source", $"No ArtSource recorded for {fileName} — provenance unknown; the code path that wrote the file must record its provider.");
        else if (!KnownArtSources.Contains(artSource))
            Error("image-source", $"Unknown ArtSource \"{artSource}\" for {fileName} — not a provider the pool recognises.");

        var gray = LoadGray256(path);
        if (gray is null)
        {
            Error("image-decode", $"Could not decode {fileName} to pixels — corrupt image.");
            return findings;
        }
        var (stdDev, varLap) = AnalyzePixels(gray, 256, 256);
        if (stdDev < BlankStdDevThreshold)
            Error("image-blank", $"Art is nearly uniform (grey stddev {stdDev:0.1} < {BlankStdDevThreshold}) — blank or broken render: {fileName}.");
        else if (varLap < SharpnessWarnThreshold)
            Warn("image-sharpness", $"Art is soft (Laplacian variance {varLap:0} < {SharpnessWarnThreshold}) — likely an upscaled low-res source: {fileName}.");

        if (ImagePromptIsPhotoSubject(imagePrompt) &&
            artSource is not null && AiArtSources.Contains(artSource))
            Warn("image-photo-preference", $"Scene {sceneIndex} depicts a real-world subject ({Trunc(imagePrompt ?? "", 70)}) but the art is AI-generated ({artSource}) — a real Pexels photo would look like news, not an illustration. Pexels is the #2 leg for news scenes (query: topic/entity keywords); if it missed, check the query first.");

        return findings;
    }

    // Batch check for a whole job: prompt bans, per-file checks,
    // duplicate art across scenes. Skips data-visual scenes (Remotion
    // draws them, no image) and context scenes (stock clip, not art).
    // Returns findings; the caller throws on errors.
    internal static List<QualityFinding> CheckImagesBatch(string jobDir, VideoScript script)
    {
        var findings = new List<QualityFinding>();
        const string gate = "images";
        var story = script.Scenes ?? [];
        var seenHashes = new Dictionary<string, int>(StringComparer.Ordinal);
        // Pairs already reported as byte-identical in step (2): the
        // near-duplicate step (4) must not report them a second time.
        var exactDuplicatePairs = new HashSet<(int First, int Second)>();

        for (var i = 0; i < story.Count; i++)
        {
            var scene = story[i];
            var label = $"scene{i}";

            // (1) Prompt ban — the main video #4 fix. A prompt that asks
            // for rendered text can never produce a good image.
            var terms = ImagePromptTextRequestTerms(scene.ImagePrompt);
            if (terms.Count > 0)
                findings.Add(new(gate, "image-prompt-text", QualitySeverity.Error,
                    $"Scene {i} image prompt asks for rendered text ({string.Join(", ", terms)}): \"{Trunc(scene.ImagePrompt ?? "", 100)}\" — image models cannot spell; the text comes out garbled.",
                    label));

            if (scene.SceneKind == "visual")
                continue; // Remotion-drawn data visual; no image file.
            // NOTE: the skip keys on SceneKind ONLY, not on VisualSpec
            // nullness. The model emits "visualSpec": {} on art/context
            // scenes (Ronaldo job: scenes 1-12), and an empty object
            // deserializes to a non-null VisualSpec with Kind null — so
            // nullness cannot tell a real visual from a stray empty one,
            // and the old `|| VisualSpec is not null` skip silently
            // exempted 12 scenes (including the defective 5/6 and the
            // hockey clip 4) from every image check. The renderer keys
            // on SceneKind too (VideoAssembler.BuildAssetAsync), so the
            // gate now matches it.
            if (scene.SceneKind == "context")
            {
                if (string.IsNullOrWhiteSpace(scene.ClipPath) &&
                    string.IsNullOrWhiteSpace(scene.ImagePath))
                    findings.Add(new(gate, "image-exists", QualitySeverity.Warning,
                        $"Context scene {i} has neither a stock clip nor an image.", label));
                continue;
            }
            if (scene.VisualMissing)
            {
                findings.Add(new(gate, "image-exists", QualitySeverity.Warning,
                    $"Scene {i} is flagged VisualMissing — it will render from a placeholder.", label));
                continue;
            }

            var artPath = scene.LandscapeImagePath ?? scene.ImagePath;
            if (string.IsNullOrWhiteSpace(artPath))
            {
                findings.Add(new(gate, "image-exists", QualitySeverity.Error,
                    $"Scene {i} has no art path recorded.", label));
                continue;
            }
            var fullPath = Path.IsPathRooted(artPath)
                ? artPath
                : Path.Combine(jobDir, artPath);
            findings.AddRange(CheckImageFile(fullPath, scene.ArtSource, scene.ImagePrompt, i));

            // (2) Duplicate art: the same image for two scenes (video #4
            // scenes 31 + 41 are byte-identical) breaks the one-distinct-
            // visual-per-scene rule.
            if (File.Exists(fullPath))
            {
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        File.ReadAllBytes(fullPath)));
                if (seenHashes.TryGetValue(hash, out var first))
                {
                    findings.Add(new(gate, "image-duplicate", QualitySeverity.Error,
                        $"Scenes {first} and {i} share byte-identical art ({Path.GetFileName(fullPath)}) — each scene needs its own visual.",
                        label));
                    exactDuplicatePairs.Add((first, i));
                }
                else
                    seenHashes[hash] = i;
            }
        }

        // (3) Provider mix (bake-off 2026-11-24 pool order: Cloudflare
        // FLUX primary, Pexels #2 for news scenes, OVHcloud fallback
        // filler ~10-15 scenes/video; pollinations-keyed counts as
        // passing — same FLUX model, dormant until Pollen exists;
        // wikimedia-commons counts as passing too — a real freely-
        // licensed photo of the star (Rishi 2026-11-24 ~23:14), tried
        // before FLUX when the scene names a ranked star).
        // The passing legs should draw the large majority. When they
        // draw under half, the primary leg under-delivered — usually
        // the FLUX daily quota (10,000 neurons/day, resets 00:00 UTC)
        // ran out mid-prep — and the job is mostly filler. Warn; the
        // discarded-leg error above is the hard backstop, and
        // Cloudflare SDXL is not counted either way (untested in the
        // bake-off). The old rule (OVH <half and Pollinations/AI Horde
        // >half) is gone with the legs it named.
        var passing = story.Count(s =>
            s.ArtSource is not null &&
            (s.ArtSource.Equals("cloudflare-flux", StringComparison.OrdinalIgnoreCase) ||
             s.ArtSource.Equals("pollinations-keyed", StringComparison.OrdinalIgnoreCase) ||
             s.ArtSource.Equals("pexels-stock", StringComparison.OrdinalIgnoreCase) ||
             s.ArtSource.Equals("wikimedia-commons", StringComparison.OrdinalIgnoreCase)));
        if (story.Count > 0 && passing * 2 < story.Count)
            findings.Add(new(gate, "image-provider-mix", QualitySeverity.Warning,
                $"Only {passing}/{story.Count} scenes came from the passing legs (Cloudflare FLUX + Pexels + Wikimedia Commons) — the primary leg under-delivered, usually the FLUX daily quota (10,000 neurons/day, resets 00:00 UTC) running out mid-prep. Check the quota before rendering; fill the gap with Pexels + passing OVH images, never with discarded legs."));

        // (4) Near-duplicate visuals (Ronaldo 2026-11-25: scenes 5+6
        // near-identical statue photos, scenes 26+31 a byte-identical
        // night-stadium clip — the byte-hash check in (2) catches only
        // exact copies, and it does not cover stock clips at all).
        // Pairwise aHash + grey-histogram over every scene's resolved
        // visual: the art square source for art scenes, the mid-frame
        // for stock clips, the photo itself for stock photos. Remotion
        // visuals and VisualMissing scenes have no file and are skipped.
        // The finding lands on the LATER scene — the one to replace —
        // so --repair-images regenerates it (art scenes) or the operator
        // hand-swaps it (stock clips, which --repair-images skips).
        // Severity follows Rishi's reuse rule (2026-11-25 23:49): the
        // same visual may return only 4-7+ scenes later — distance < 4
        // is an error, 4-7 a warning, 8+ allowed. That is why the
        // Ronaldo 26/31 clip (5 apart) warns instead of erroring.
        findings.AddRange(CheckVisualNearDuplicates(jobDir, script, exactDuplicatePairs));

        // (5) Stock-subject mismatch (Ronaldo scene 4: an ice-hockey
        // clip for a football story). Reads the Pexels page-URL slug /
        // photo alt text recorded on the scene at download time; scenes
        // without that metadata (older jobs, hand-placed files) are
        // skipped — no signal, no finding.
        for (var i = 0; i < story.Count; i++)
        {
            var scene = story[i];
            if (scene.SceneKind != "context") continue;
            var metadata = scene.PexelsUrl ?? scene.PexelsAlt;
            if (string.IsNullOrWhiteSpace(metadata)) continue;
            // The scene text is the query + narration + title + tags:
            // the tags carry the topic's subject words ("portugal
            // football") that the narration often never spells out —
            // without them a correct football clip would flag as a
            // mismatch on a Ronaldo story that never says "football".
            var sceneText = $"{scene.StockQuery} {scene.Narration} {script.Title} {string.Join(" ", script.Tags ?? [])}";
            var mismatch = StockSubjectMismatch(metadata, sceneText);
            if (mismatch is not null)
                findings.Add(new(gate, "stock-subject-mismatch", QualitySeverity.Warning,
                    $"Scene {i} stock clip's Pexels page is about {mismatch} (\"{Trunc(metadata, 80)}\") but the scene is about \"{Trunc(scene.StockQuery ?? scene.Narration, 60)}\" — the search ranks loosely and the client picks by file quality, not relevance. Check the clip before rendering; swap it if it shows the wrong subject.",
                    $"scene{i}"));
        }

        return findings;
    }

    // Pairwise near-duplicate check over a job's resolved visuals.
    // `exactDuplicatePairs` (from the byte-hash step) are skipped here
    // — they already have an image-duplicate error each.
    internal static List<QualityFinding> CheckVisualNearDuplicates(
        string jobDir, VideoScript script, HashSet<(int First, int Second)>? exactDuplicatePairs = null)
    {
        const string gate = "images";
        var findings = new List<QualityFinding>();
        var story = script.Scenes ?? [];

        // (index, isClip, file, grey buffer). The grey buffer is kept
        // so the pairwise histogram cosine needs no second decode.
        var visuals = new List<(int Index, bool IsClip, string File, byte[] Gray)>();
        var midFrameTemps = new List<string>();
        try
        {
            for (var i = 0; i < story.Count; i++)
            {
                var scene = story[i];
                // Remotion visuals have no file (see the note in
                // CheckImagesBatch — the skip keys on SceneKind only,
                // because the model emits empty "visualSpec": {} objects
                // on art/context scenes).
                if (scene.SceneKind == "visual") continue;
                if (scene.VisualMissing) continue;

                string? path = null;
                var isClip = false;
                if (scene.SceneKind == "context")
                {
                    if (!string.IsNullOrWhiteSpace(scene.ClipPath))
                    {
                        path = scene.ClipPath;
                        isClip = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(scene.ImagePath))
                        path = scene.ImagePath;
                }
                else
                {
                    path = scene.LandscapeImagePath ?? scene.ImagePath;
                }
                if (string.IsNullOrWhiteSpace(path)) continue;
                var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(jobDir, path);
                if (!File.Exists(fullPath)) continue;

                string framePath = fullPath;
                if (isClip)
                {
                    var mid = ExtractClipMidFrame(fullPath);
                    if (mid is null) continue;
                    midFrameTemps.Add(mid);
                    framePath = mid;
                }
                var gray = LoadGray256(framePath);
                if (gray is null) continue;
                visuals.Add((i, isClip, fullPath, gray));
            }

            var hashes = visuals.Select(v => ComputeAverageHash(v.Gray)).ToList();
            for (var a = 0; a < visuals.Count; a++)
            {
                for (var b = a + 1; b < visuals.Count; b++)
                {
                    var (first, second) = (visuals[a].Index, visuals[b].Index);
                    if (exactDuplicatePairs is not null &&
                        exactDuplicatePairs.Contains((first, second)))
                        continue;
                    var histCos = GrayHistogramCosine(visuals[a].Gray, visuals[b].Gray);
                    if (!AreNearDuplicates(hashes[a], hashes[b], histCos)) continue;
                    var distance = second - first;
                    var severity = ReuseDistanceSeverity(distance);
                    if (severity is null) continue;
                    var check = visuals[a].IsClip || visuals[b].IsClip
                        ? "clip-near-duplicate" : "image-near-duplicate";
                    var kind = check == "clip-near-duplicate" ? "clip" : "image";
                    findings.Add(new(gate, check, severity.Value,
                        $"Scenes {first} and {second} show near-identical {kind}s ({Path.GetFileName(visuals[a].File)} / {Path.GetFileName(visuals[b].File)}; aHash distance {HammingDistance(hashes[a], hashes[b])}, colour similarity {histCos:0.00}) only {distance} scene(s) apart — Rishi's rule allows reuse only 4-7+ scenes later. Replace the scene {second} visual" +
                        (check == "clip-near-duplicate"
                            ? " by hand (delete the stock file and re-run --prep, or hand-place a different clip)"
                            : " (VideoGen --repair-images --job " + Path.GetFileName(jobDir) + " regenerates it)") +
                        ".",
                        $"scene{second}"));
                }
            }
        }
        finally
        {
            foreach (var tmp in midFrameTemps)
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        return findings;
    }

    // ------------------------------------------------------------------
    // Music check.
    // ------------------------------------------------------------------

    internal static (bool Ok, string? Error) ProbeAudio(string path)
    {
        var result = RunProcessSync("ffprobe",
        [
            "-v", "error",
            "-select_streams", "a:0",
            "-show_entries", "stream=codec_name,sample_rate,channels",
            "-show_entries", "format=duration",
            "-of", "json", path,
        ], timeoutSeconds: 30);
        if (result.ExitCode != 0) return (false, $"ffprobe failed (exit {result.ExitCode})");
        try
        {
            using var doc = JsonDocument.Parse(result.Stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
                return (false, "no audio stream");
            var stream = streams[0];
            var sampleRate = stream.TryGetProperty("sample_rate", out var sr) &&
                int.TryParse(sr.GetString(), out var s) ? s : 0;
            if (sampleRate is < 8000 or > 192000)
                return (false, $"sample rate {sampleRate} Hz is outside 8000-192000");
            var channels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0;
            if (channels < 1)
                return (false, "audio stream has no channels");
            var duration = 0.0;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                duration = dur;
            if (duration <= 0)
                return (false, "duration is 0");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"ffprobe output did not parse: {ex.Message}");
        }
    }

    internal static double ProbeAudioDuration(string path)
    {
        var result = RunProcessSync("ffprobe",
        [
            "-v", "error", "-show_entries", "format=duration",
            "-of", "csv=p=0", path,
        ], timeoutSeconds: 30);
        if (result.ExitCode != 0) return 0;
        return double.TryParse(result.Stdout.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    // ffmpeg volumedetect: mean_volume + max_volume, for the clip/level
    // warnings. Returns nulls when the measurement fails.
    internal static (double? MeanVolume, double? MaxVolume) MeasureTrackLevel(string path)
    {
        var result = RunProcessSync("ffmpeg",
        [
            "-i", path, "-af", "volumedetect", "-f", "null", "-",
        ], timeoutSeconds: 120);
        var stderr = result.Stderr ?? "";
        double? mean = null, max = null;
        var meanMatch = Regex.Match(stderr, @"mean_volume:\s*(-?[\d.]+)\s*dB");
        if (meanMatch.Success && double.TryParse(meanMatch.Groups[1].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var mv))
            mean = mv;
        var maxMatch = Regex.Match(stderr, @"max_volume:\s*(-?[\d.]+)\s*dB");
        if (maxMatch.Success && double.TryParse(maxMatch.Groups[1].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var xv))
            max = xv;
        return (mean, max);
    }

    // Full check for a selected bed. `description` is the description
    // that will ship (script description at assemble time; sidecar
    // description at finish time) — the CC BY credit must be in it.
    // `checkCredit: false` skips the credit check: at track-selection
    // time there is no shipped description yet (the credit is verified
    // at finish time against the sidecar description instead).
    internal static List<QualityFinding> CheckMusicTrack(
        string? trackPath, string? mood, double videoSeconds, string? description,
        bool checkCredit = true)
    {
        const string gate = "music";
        var findings = new List<QualityFinding>();
        void Warn(string check, string message) =>
            findings.Add(new(gate, check, QualitySeverity.Warning, message));
        void Error(string check, string message) =>
            findings.Add(new(gate, check, QualitySeverity.Error, message));

        if (string.IsNullOrWhiteSpace(trackPath))
        {
            Warn("music-missing", "No background-music track selected — the video renders narration-only.");
            return findings;
        }

        var fileName = Path.GetFileName(trackPath);
        if (BlockedTracks.Contains(fileName))
        {
            Error("music-blocked", $"Track \"{fileName}\" is BLOCKED (copyright-claimed bed — video #1's Short was blocked worldwide by an Elite Alliance Music claim). It must never be selected.");
            return findings;
        }
        if (!ApprovedTracks.TryGetValue(fileName, out var info))
        {
            Error("music-pool", $"Track \"{fileName}\" is not in the approved pool (5 FMA tracks) — unknown provenance, unknown licence; refusing to render under it.");
            return findings;
        }
        if (!File.Exists(trackPath))
        {
            Error("music-file", $"Selected track does not exist: {trackPath}.");
            return findings;
        }

        var (ok, error) = ProbeAudio(trackPath);
        if (!ok)
        {
            Error("music-file", $"Selected track is not valid audio: {error} ({fileName}).");
            return findings;
        }

        if (!string.IsNullOrWhiteSpace(mood))
        {
            var expected = MoodKeywords(mood);
            if (expected.Length > 0 &&
                !info.Moods.Any(m => expected.Contains(m, StringComparer.OrdinalIgnoreCase)))
                Warn("music-mood", $"Track \"{fileName}\" (moods: {string.Join(", ", info.Moods)}) does not match the video's mood \"{mood}\" — the bed will feel wrong for the story.");
        }

        if (videoSeconds > 0)
        {
            var trackSeconds = ProbeAudioDuration(trackPath);
            if (trackSeconds > 0)
            {
                var loops = Math.Ceiling(videoSeconds / trackSeconds);
                if (loops > 3)
                    Warn("music-duration", $"Track \"{fileName}\" is {trackSeconds:0}s; a {videoSeconds:0}s video loops it {loops}x (>3) — the loop point will be audible. Pick a longer bed.");
            }
        }

        var (mean, max) = MeasureTrackLevel(trackPath);
        if (max is not null && max > -1.0)
            // The FILE is mastered hot (samples pinned near 0 dBFS) —
            // the mix itself does not clip from the bed: the bed is
            // re-gained down to BackgroundMusicLufs and the
            // peak-headroom trim (ComputePeakTrimDb) caps its peak at
            // MusicPeakCeilingDb in the mix. The warning stays because
            // a hot master has less dynamic range and a future path
            // that plays the bed near unity would clip; prefer a clean
            // same-mood track when one exists (FindCleanSameMoodTrack).
            Warn("music-clipping", $"Track \"{fileName}\" peaks at {max:0.0} dB (max_volume > -1 dB) — the file is mastered hot. The mix re-gains it down and caps the bed peak at {MusicPeakCeilingDb:0.#} dBFS (peak-headroom trim); prefer a clean same-mood track when one exists.");
        if (mean is not null && (mean < -30 || mean > -8))
            Warn("music-level", $"Track \"{fileName}\" mean level is {mean:0.0} dB (outside -30..-8) — an extreme master; the mixer re-gains it, but verify the bed sits right under the narration.");

        if (checkCredit && info.License == "CC BY")
        {
            if (description is null)
            {
                Warn("music-credit", $"Track \"{fileName}\" is CC BY — its credit line must be in the shipped description; no description was supplied to verify against.");
            }
            else if (info.Credit is not null &&
                !description.Contains(info.Credit, StringComparison.Ordinal))
            {
                Error("music-credit", $"Track \"{fileName}\" is CC BY but the description has no credit line. The licence requires: {info.Credit}");
            }
        }

        return findings;
    }

    // ------------------------------------------------------------------
    // Music — bed peak handling (Rishi 2026-11-23 ~23:37).
    // Measured 2026-11-24 (ffmpeg volumedetect): 4 of the 5 approved
    // FMA tracks peak at -0.7..-0.0 dB — tense-newsroom-01.mp3
    // (mean -16.6, max -0.0, 329 samples at 0 dBFS), tense-newsroom-02
    // (mean -18.2, max -0.0), cinematic-epic-01 (mean -17.6, max -0.7),
    // uplifting-tech-01 (mean -16.0, max -0.2). Only calm-neutral-01
    // (mean -18.2, max -4.5) is clean. So a same-mood non-clipping
    // alternative usually does NOT exist in this pool — the fallback
    // below is a mechanism for when clean tracks are added, and the
    // peak-headroom trim is the guarantee that does the work today.
    //
    // Why the mix does not clip today: the bed is re-gained to
    // BackgroundMusicLufs (-34), about -15..-18 dB for this pool, so a
    // 0 dBFS peak lands near -17 dBFS in the mix — and the ffmpeg path
    // ends in alimiter=0.95 anyway. But that gain is computed from
    // INTEGRATED loudness only, so nothing guaranteed the peak. The
    // trim is that guarantee, applied in both mix paths (Remotion
    // props volume + ffmpeg mix). It only ever REDUCES the gain, so
    // the final-mix loudness check (-14 ±1 LUFS, narration-dominated)
    // is unaffected.
    // ------------------------------------------------------------------

    // Bed peak ceiling in the mix. -3 dBFS leaves margin for
    // inter-sample peaks, which volumedetect's sample peak understates
    // by ~0.3-1 dB.
    internal const double MusicPeakCeilingDb = -3.0;

    // Extra attenuation (≤ 0 dB) needed so a bed with the given true
    // peak, after the given gain, never peaks above the ceiling.
    internal static double ComputePeakTrimDb(double peakDb, double gainDb)
    {
        var peakAfter = peakDb + gainDb;
        return peakAfter > MusicPeakCeilingDb ? MusicPeakCeilingDb - peakAfter : 0;
    }

    // Safer-track fallback at selection time: when the picked track
    // clips (peak > -1 dB), prefer a clean same-mood track (peak
    // ≤ -1 dB) from the same folder, excluding the picked file. Only
    // approved-pool tracks count. Returns null when no clean same-mood
    // track exists — the usual outcome in today's pool — in which case
    // the caller keeps the pick and the peak-headroom trim does the
    // work instead.
    private static readonly string[] MusicFileExtensions = [".mp3", ".wav", ".ogg", ".m4a", ".flac"];

    internal static string? FindCleanSameMoodTrack(string folder, string? mood, string excludeFile)
    {
        if (string.IsNullOrWhiteSpace(mood) || !Directory.Exists(folder)) return null;
        var expected = MoodKeywords(mood);
        if (expected.Length == 0) return null;
        var candidates = Directory.EnumerateFiles(folder)
            .Where(f => MusicFileExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f))
            .Where(name => !name.Equals(excludeFile, StringComparison.OrdinalIgnoreCase))
            .Where(name => ApprovedTracks.TryGetValue(name, out var info) &&
                info.Moods.Any(m => expected.Contains(m, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var name in candidates)
        {
            var (_, max) = MeasureTrackLevel(Path.Combine(folder, name));
            if (max is not null && max <= -1.0)
                return Path.Combine(folder, name);
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Final-mix check (finished mp4).
    // ------------------------------------------------------------------

    // Parses ffmpeg ebur128 summary output ("  I:         -14.0 LUFS").
    internal static double? ParseEbur128Integrated(string stderr)
    {
        var match = Regex.Match(stderr ?? "", @"^\s*I:\s*(-?[\d.]+)\s*LUFS",
            RegexOptions.Multiline);
        return match.Success && double.TryParse(match.Groups[1].Value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // Parses ffmpeg silencedetect output into (start, duration) pairs.
    internal static List<(double Start, double Duration)> ParseSilence(string stderr)
    {
        // Pair each silence_start with the silence_duration that follows
        // it (silencedetect prints "silence_start: 0" then later
        // "silence_end: 2.34 | silence_duration: 2.34").
        var result = new List<(double Start, double Duration)>();
        var starts = Regex.Matches(stderr ?? "", @"silence_start:\s*(-?[\d.]+)");
        var durations = Regex.Matches(stderr ?? "", @"silence_duration:\s*([\d.]+)");
        for (var i = 0; i < Math.Min(starts.Count, durations.Count); i++)
        {
            if (double.TryParse(starts[i].Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var s) &&
                double.TryParse(durations[i].Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var d))
                result.Add((s, d));
        }
        return result;
    }

    internal static List<QualityFinding> CheckFinalMix(
        string mp4Path, double expectedSeconds)
    {
        const string gate = "music";
        var findings = new List<QualityFinding>();
        void Warn(string check, string message) =>
            findings.Add(new(gate, check, QualitySeverity.Warning, message));
        void Error(string check, string message) =>
            findings.Add(new(gate, check, QualitySeverity.Error, message));

        if (!File.Exists(mp4Path))
        {
            Error("finalmix-exists", $"Finished file missing: {mp4Path}.");
            return findings;
        }

        var duration = ProbeAudioDuration(mp4Path);
        if (duration <= 0)
        {
            Error("finalmix-audio", $"Finished file has no measurable duration — no audio stream or corrupt file: {Path.GetFileName(mp4Path)}.");
            return findings;
        }
        if (expectedSeconds > 0 && Math.Abs(duration - expectedSeconds) > 2)
            Error("finalmix-duration", $"Finished file is {duration:0.0}s, expected {expectedSeconds:0.0}s (>2s off) — the mix is truncated or padded.");

        var ebur = RunProcessSync("ffmpeg",
        [
            "-i", mp4Path, "-af", "ebur128=framelog=quiet", "-f", "null", "-",
        ], timeoutSeconds: 300);
        var lufs = ParseEbur128Integrated(ebur.Stderr);
        if (lufs is null)
        {
            Warn("finalmix-loudness", "Could not measure the finished file's loudness (ebur128 gave no summary) — verify the mix by ear.");
        }
        else if (Math.Abs(lufs.Value + 14) > 1)
        {
            Error("finalmix-loudness", $"Finished mix measures {lufs.Value:0.00} LUFS (target -14 ±1) — the loudnorm step did not land; the video will sound noticeably quieter/louder than the channel's other videos.");
        }

        var silence = RunProcessSync("ffmpeg",
        [
            "-i", mp4Path, "-af", "silencedetect=noise=-50dB:d=0.5", "-f", "null", "-",
        ], timeoutSeconds: 300);
        foreach (var (start, dur) in ParseSilence(silence.Stderr))
        {
            if (start <= 0.1 && dur >= 2)
                Error("finalmix-leading-silence", $"Finished file opens with {dur:0.1}s of silence — the video starts dead; viewers swipe away before the hook.");
            else if (dur >= 5)
                Warn("finalmix-silence", $"Finished file has a {dur:0.1}s silent gap at {start:0.1}s — dead air mid-video.");
        }

        return findings;
    }

    // ------------------------------------------------------------------
    // Report.
    // ------------------------------------------------------------------

    internal static QualityReport BuildReport(string job, IEnumerable<QualityFinding> findings)
    {
        return new QualityReport
        {
            Job = job,
            GeneratedAtUtc = DateTime.UtcNow,
            Findings = findings.ToList(),
        };
    }

    // Appends one gate's findings to the per-job report (replacing that
    // gate's previous findings), so prep, assemble and finish all write
    // into the same quality-report.json.
    internal static QualityReport AppendReport(
        string reportPath, string job, string gate, IEnumerable<QualityFinding> findings)
    {
        QualityReport report;
        if (File.Exists(reportPath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<QualityReport>(
                    File.ReadAllText(reportPath));
                report = existing ?? BuildReport(job, []);
                report.Findings.RemoveAll(f => f.Gate == gate);
            }
            catch
            {
                report = BuildReport(job, []);
            }
        }
        else
        {
            report = BuildReport(job, []);
        }
        report.Job = job;
        report.GeneratedAtUtc = DateTime.UtcNow;
        report.Findings.AddRange(findings);
        var dir = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }

    internal static string OneLineSummary(string gate, IEnumerable<QualityFinding> findings)
    {
        var list = findings.ToList();
        var errors = list.Count(f => f.Severity == QualitySeverity.Error);
        var warnings = list.Count(f => f.Severity == QualitySeverity.Warning);
        var status = errors > 0 ? "FAIL" : warnings > 0 ? "WARN" : "PASS";
        return $"quality[{gate}]: {status} ({errors} error(s), {warnings} warning(s))";
    }

    internal static void ThrowOnErrors(
        IEnumerable<QualityFinding> findings, string gate, string jobDir)
    {
        var errors = findings.Where(f => f.Severity == QualitySeverity.Error).ToList();
        if (errors.Count == 0) return;
        var detail = string.Join("\n  ", errors.Select(e =>
            $"[{e.Check}] {(e.Scene is not null ? e.Scene + ": " : "")}{e.Message}"));
        throw new InvalidOperationException(
            $"Quality gate FAILED [{gate}] ({errors.Count} error(s)):\n  {detail}\n" +
            $"Fix the findings above and re-run; the full report is in {Path.Combine(jobDir, "quality-report.json")}.");
    }

    // Synchronous process runner (the gate runs from both sync and async
    // callers; each call is seconds-long ffmpeg/ffprobe work).
    internal static (int ExitCode, string Stdout, string Stderr) RunProcessSync(
        string exe, IEnumerable<string> args, int timeoutSeconds = 120)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)
            ?? throw new Exception($"Could not start {exe}.");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSeconds * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{exe} did not finish within {timeoutSeconds}s.");
        }
        return (p.ExitCode, stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult());
    }
}
