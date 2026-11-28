using System.Text.RegularExpressions;

// Fact-gate rewrite pass (Rishi 2026-11-23): the fact-traceability gate
// (FactTraceability) used to FATAL a whole prep on any untraceable claim —
// no script.json was saved and the topic was dropped after repeated
// failures. Rishi's instruction: we do NOT need a complete fact check;
// unverified claims should be presented as rumours / "people are
// discussing" instead, and the code should always make content work rather
// than dropping it. So the gate still RUNS (it must still find the
// untraceable claims) but FinishPrepAsync now rewrites instead of throwing.
//
// Handling per finding kind (deterministic template reframing — never a
// free-form model rewrite, which could invent new claims):
//   * UntraceableNumber / date — the flagged number is replaced with a
//     vague equivalent ("2022 meeting" -> "recent meeting", "targets for
//     2050" -> "targets for the years ahead", "early 2000s" -> "earlier
//     decades", "19th-century" -> "historic", "52 hours" -> "several
//     hours") and the sentence is prefixed with an attribution phrase
//     ("Reports suggest ..."). The scene is marked IsSpeculative, so the
//     red SPECULATION badge and the narration attribution fire.
//   * UntraceableEntity — an invented CamelCase name is removed from the
//     sentence ("called 'HydroFlow'" is deleted) and the sentence is
//     reframed as speculation the same way.
//   * UntraceableAttribution — reframed as speculation ("X said Y" ->
//     "Reports suggest Y"; "X never confirmed Y" -> "It remains unclear
//     whether Y took place") UNLESS the attributed source is a named
//     person, in which case the sentence is DROPPED, never reframed:
//     "people are saying X about [person]" still repeats an unverified
//     claim about them (Rishi's rule 3, hard line — never softened).
//   * UntraceableQuote — a visual quote attributed to an untraceable
//     outlet is re-attributed to "reports"; a quote from a named person
//     is removed with its visual (demoted to art).
//   * FutureDatedSource — the future date is stripped from the visual's
//     source; if the outlet still does not trace, the source is stripped.
//   * InternalSource / UntraceableSource — the untraceable source string
//     is stripped from the visual (the callout/chart stays, the
//     narration is untouched) UNLESS the visual's content is a
//     named-person quote resting on that source: then rule 3 applies and
//     the scene is dropped when the narration restates the quote, or the
//     visual is demoted to art when it does not.
//
// Escalation: the rewrite runs in passes (max 3) with a re-check after
// each pass. Pass 1 applies the smart handling above; pass 2 drops the
// offending sentence (narration kinds) or demotes the visual (visual
// kinds); pass 3 drops the scene. Anything still flagged after that is
// dropped outright. A rewrite therefore always ends with a gate-clean
// script or a loud failure (RemainingFindings non-empty) — never a
// half-rewritten script that still fails the gate.
//
// Re-check rule (see also FactTraceability.CheckNarrationText): after
// rewriting, the gate re-runs on the rewritten narration, and a scene
// marked IsSpeculative whose narration carries a recognised attribution
// phrase (see AttributionPhrases — "reports suggest", "people are
// discussing", "the theory going around is", "it remains unclear", ...)
// PASSES the attribution and number checks: the attribution + the
// on-screen SPECULATION label IS the traceability — the claim is no
// longer presented as a verified fact. Two things are NOT exempt, by
// design: invented entities (a rumour about a real thing is fine, an
// invented name is still fabrication) and attributions to a NAMED PERSON
// (rule 3 — a labelled rumour about a person is still an unverified
// claim about them, so it still fails and the rewrite drops it).
//
// Floor: dropping scenes can push a long-form script below the floors
// (50 scenes / 5,500 chars). The rewrite reports BelowFloor; the caller
// (FinishPrepAsync) regenerates the missing scenes through the existing
// RepairThinActAsync path. The "never drop" rule covers gate findings,
// not a script that cannot reach the floor.
internal static class FactGateRewrite
{
    // The exact attribution-phrase set the gate recognises for the
    // re-check exemption (see the re-check rule above). The rewrite only
    // ever introduces phrases from this set, so a rewritten scene always
    // passes its own re-check. Keep the two in sync: if a phrase is
    // added here, it is automatically recognised by the gate, and vice
    // versa (FactTraceability calls ContainsAttributionPhrase).
    internal static readonly string[] AttributionPhrases =
    [
        "reports suggest",
        "reports indicate",
        "unconfirmed reports",
        "people are saying",
        "people are discussing",
        "the theory going around is",
        "the theory",
        "theory circulating",
        "rumour",
        "rumor",
        "it remains unclear",
        "what if",
        "imagine if",
        "online speculation",
    ];

    internal static bool ContainsAttributionPhrase(string narration)
    {
        var text = narration.ToLowerInvariant();
        return AttributionPhrases.Any(p => text.Contains(p, StringComparison.Ordinal));
    }

    internal sealed record RewriteResult(
        List<int> DroppedScenes,
        List<int> ReframedScenes,
        List<int> SourceFixedScenes,
        List<int> DemotedScenes,
        List<string> Actions,
        List<(int SceneNumber, string Before, string After)> Changes,
        List<string> RemainingFindings,
        int SceneCount,
        int TotalChars,
        bool BelowFloor);

    // --- named-person classification (rule 3) -------------------------------
    //
    // The attributed source is a named person when, after stripping the
    // gate's filler words (leading prepositions/conjunctions, trailing
    // auxiliaries/adverbs, leading role words like "president"/"minister"),
    // 1-3 words remain, none of them a generic/org/country/outlet word,
    // none of them a collective plural ("insurers", "investors"), none of
    // them an all-caps acronym (NATO, UN, EU, UNFCCC) or a CamelCase
    // brand (GameSpot, HydroFlow), and every remaining word is capitalised
    // in the original ("Fossil fuel lobby" and "Maritime insurers" fail
    // here — their continuations are lowercase). When in doubt the
    // classifier says "not a person" EXCEPT for the capitalisation test,
    // which is the conservative direction for the re-check exemption
    // (a misclassified person stays flagged and gets dropped, never
    // shipped as rumour).

    private static readonly HashSet<string> LeadingFiller = new(StringComparer.Ordinal)
    {
        "as", "in", "on", "for", "with", "by", "of", "at", "to", "from", "and", "but", "so", "if", "when", "while",
        "the", "a", "an",
    };

    private static readonly HashSet<string> TrailingFiller = new(StringComparer.Ordinal)
    {
        "has", "have", "had", "is", "are", "was", "were", "will", "would", "can", "could",
        "repeatedly", "already", "first", "also", "just", "often", "now", "today", "recently", "still", "even",
        "never", "not",
    };

    private static readonly HashSet<string> RoleWords = new(StringComparer.Ordinal)
    {
        "president", "minister", "secretary", "general", "ceo", "chief", "director", "ambassador",
        "senator", "governor", "mayor", "chairman", "chair", "spokesperson", "spokesman", "spokeswoman",
        "leader", "founder", "head", "envoy", "adviser", "advisor", "premier", "chancellor",
        "judge", "justice", "professor", "doctor", "dr", "pope", "king", "queen", "prince", "princess",
    };

    // Words that make an attribution source a generic group, an
    // organisation, a place, or an outlet — never a named person.
    private static readonly HashSet<string> NonPersonWords = new(StringComparer.Ordinal)
    {
        // Generic attribution words (same set as the gate's).
        "analysts", "experts", "critics", "fans", "players", "reports", "sources", "officials",
        "researchers", "developers", "insiders", "observers", "commentators", "users", "gamers",
        "streamers", "industry", "people", "many", "some", "others",
        // Organisation / collective nouns.
        "nations", "united", "games", "studio", "studios", "news", "times", "post", "review", "media",
        "statement", "rules", "report", "network", "channel", "company", "companies", "government",
        "parliament", "senate", "congress", "court", "ministry", "department", "agency", "administration",
        "council", "committee", "alliance", "union", "party", "bank", "fund", "organization", "organisation",
        "institute", "university", "school", "hospital", "group", "team", "club", "league", "association",
        "federation", "coalition", "bloc", "command", "forces", "army", "navy", "lobby", "market", "markets",
        "investors", "traders", "producers", "states", "nation", "country", "countries", "region", "regions",
        "city", "world", "internet", "press", "television", "radio", "magazine", "journal", "paper",
        "website", "blog", "forum", "platform", "app", "service", "system", "program", "programme",
        "project", "mission", "operation", "campaign", "movement", "summit", "conference", "panel",
        "board", "office", "bureau", "division", "unit", "branch", "wing", "faction", "regime", "cabinet",
        "assembly", "legislature", "judiciary", "tribunal", "commission", "authority", "authorities",
        "regulators", "police", "military", "rebels", "militants", "activists", "protesters", "demonstrators",
        "workers", "employees", "staff", "members", "delegates", "representatives", "negotiators",
        "diplomats", "voters", "citizens", "residents", "customers", "clients", "viewers", "readers",
        "listeners", "subscribers", "followers", "supporters", "opponents", "rivals", "competitors",
        "partners", "allies", "adversaries", "enemies", "environment", "climate", "foreign", "defence",
        "defense", "finance", "health", "education", "energy", "trade", "interior", "justice", "culture",
        "transport", "housing",
        // Countries.
        "ukraine", "russia", "ireland", "china", "india", "france", "germany", "britain", "england",
        "scotland", "wales", "america", "usa", "us", "uk", "israel", "palestine", "iran", "iraq", "libya",
        "turkey", "turkiye", "brazil", "canada", "australia", "japan", "korea", "poland", "italy", "spain",
        "egypt", "syria", "yemen", "sudan", "nigeria", "kenya", "mexico", "argentina", "chile", "peru",
        "colombia", "venezuela", "afghanistan", "pakistan", "bangladesh", "indonesia", "vietnam", "thailand",
        "philippines", "malaysia", "singapore", "norway", "sweden", "denmark", "finland", "netherlands",
        "belgium", "switzerland", "austria", "greece", "portugal", "hungary", "romania", "bulgaria",
        "croatia", "serbia", "lithuania", "latvia", "estonia", "georgia", "armenia", "azerbaijan",
        "kazakhstan", "uzbekistan", "mongolia", "nepal", "myanmar", "cambodia", "laos", "taiwan", "iceland",
        "luxembourg", "malta", "cyprus", "lebanon", "jordan", "qatar", "kuwait", "bahrain", "oman",
        "emirates", "arabia", "morocco", "algeria", "tunisia", "ethiopia", "somalia", "ghana", "senegal",
        "zimbabwe", "zambia", "angola", "mozambique", "madagascar", "mauritius", "fiji", "zealand",
        // Outlets.
        "axios", "bbc", "cnn", "reuters", "bloomberg", "guardian", "verge", "wired", "forbes", "nyt",
        "npr", "abc", "nbc", "cbs", "fox", "sky", "aljazeera", "euronews", "politico", "ign",
        "gamespot", "polygon", "kotaku", "ap", "afp",
        // Acronym organisations (lowercased: the all-caps check above
        // catches "NATO", this catches a title-cased "Nato").
        "nato", "un", "eu", "fbi", "cia", "nsa", "unfccc", "opec", "wto", "who", "imf",
        "apec", "asean", "g7", "g20",
    };

    private static readonly Regex CamelCaseRegex = new(@"\b[A-Za-z]*[a-z][A-Z][A-Za-z0-9]*\b", RegexOptions.Compiled);

    internal static bool IsNamedPerson(string? rawSource)
    {
        if (string.IsNullOrWhiteSpace(rawSource)) return false;
        var trimmed = rawSource.Trim().Trim('"', '\'', '“', '”', '‘', '’');
        var words = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        // An all-caps word (NATO, UN, EU, UNFCCC, FBI) is an organisation.
        if (words.Any(w => w.Length >= 2 && w.Any(char.IsLetter) && w.All(c => !char.IsLetter(c) || char.IsUpper(c))))
            return false;
        // A CamelCase token (GameSpot, HydroFlow) is a brand/outlet.
        if (words.Any(w => CamelCaseRegex.IsMatch(w))) return false;
        var (cleaned, original) = CleanedSourceWords(trimmed);
        if (cleaned.Count == 0 || cleaned.Count > 3) return false;
        // The gate's SaidRegex sweeps preceding words into the captured
        // subject ("Reports suggest Zelenskiy confirmed" captures "Reports
        // suggest Zelenskiy", not "Zelenskiy"): the actual entity is the
        // TRAILING name, so try the full phrase first, then its trailing
        // 2-word and 1-word suffixes. A suffix that is a generic word, a
        // collective, or lowercase in the original still fails — so
        // "Maritime insurers" stays a group and "Rockstar Games" stays a
        // company, while "Reports suggest Zelenskiy" resolves to the person.
        for (var take = cleaned.Count; take >= 1; take--)
        {
            if (IsNameWords(
                cleaned.Skip(cleaned.Count - take).ToList(),
                original.Skip(original.Count - take).ToList()))
                return true;
        }
        return false;
    }

    // The per-phrase person test: 1-3 words, none generic/org/country/
    // outlet/role, none a collective plural, every word capitalised in
    // the original.
    private static bool IsNameWords(List<string> cleaned, List<string> original)
    {
        if (cleaned.Count == 0 || cleaned.Count > 3) return false;
        if (cleaned.Any(w => NonPersonWords.Contains(w) || RoleWords.Contains(w))) return false;
        if (cleaned.Any(w => w.Length > 4 &&
            (w.EndsWith("ers", StringComparison.Ordinal) ||
             w.EndsWith("ors", StringComparison.Ordinal) ||
             w.EndsWith("ists", StringComparison.Ordinal))))
            return false;
        // Every remaining word must be capitalised in the original.
        if (original.Any(w => w.Length == 0 || !char.IsUpper(w[0]))) return false;
        return true;
    }

    // The gate's attribution finding quotes the raw captured subject
    // ("Zelenskiy", "NATO never", "Darragh O'Brien"); an unparseable
    // finding is treated as a named person — the conservative direction.
    internal static bool IsNamedPersonAttribution(string finding)
    {
        var m = Regex.Match(finding, @"attributes a claim to ""([^""]+)""");
        return !m.Success || IsNamedPerson(m.Groups[1].Value);
    }

    // Strip the gate's filler words (leading prepositions/conjunctions and
    // leading role words, trailing auxiliaries/adverbs) from an attribution
    // source. Returns the cleaned words alongside the corresponding
    // original-cased words (same positions, so the capitalisation test in
    // IsNamedPerson can run on them).
    private static (List<string> Cleaned, List<string> Original) CleanedSourceWords(string trimmed)
    {
        var original = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        var cleaned = original.Select(w => w.ToLowerInvariant()
            .Replace("’", "'").Replace("‘", "'")
            .Replace("'s", "").Replace("'", "")).ToList();
        while (original.Count > 1 && (LeadingFiller.Contains(cleaned[0]) || RoleWords.Contains(cleaned[0])))
        {
            original.RemoveAt(0);
            cleaned.RemoveAt(0);
        }
        while (original.Count > 1 && TrailingFiller.Contains(cleaned[^1]))
        {
            original.RemoveAt(original.Count - 1);
            cleaned.RemoveAt(cleaned.Count - 1);
        }
        return (cleaned, original);
    }

    // --- finding parsing ----------------------------------------------------

    private sealed record Finding(int SceneNumber, string Kind, string Detail);

    private static readonly Regex FindingRegex = new(@"^scene (\d+) \[(\w+)\]: (.*)$", RegexOptions.Compiled);

    private static List<Finding> ParseFindings(IEnumerable<string> findings)
    {
        var parsed = new List<Finding>();
        foreach (var f in findings)
        {
            var m = FindingRegex.Match(f);
            if (m.Success)
                parsed.Add(new Finding(int.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value));
        }
        return parsed;
    }

    private static string? GroupValue(string detail, string pattern)
    {
        var m = Regex.Match(detail, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? AttributionSource(string detail) => GroupValue(detail, @"attributes a claim to ""([^""]+)""");
    private static string? NumberToken(string detail) =>
        GroupValue(detail, @"number ""([^""]+)""") ?? GroupValue(detail, @"value ""([^""]+)""");
    private static string? EntityToken(string detail) => GroupValue(detail, @"names ""([^""]+)""");
    private static string? QuotedOutlet(string detail) => GroupValue(detail, @"attributed to ""([^""]+)""");
    private static string? VisualSource(string detail) => GroupValue(detail, @"source ""([^""]+)""");

    // Narration findings say 'narration number "19"'; visual findings say
    // 'visual "callout" value "84"' / 'visual "callout" number "30"'.
    private static bool IsVisualFinding(string detail) =>
        detail.StartsWith("visual ", StringComparison.Ordinal);

    // --- rewrite entry point ------------------------------------------------

    private sealed class RewriteState
    {
        public required VideoScript Script { get; init; }
        public List<string> Actions { get; } = [];
        public List<int> DroppedScenes { get; } = [];
        public List<int> ReframedScenes { get; } = [];
        public List<int> SourceFixedScenes { get; } = [];
        public List<int> DemotedScenes { get; } = [];
        // Scene references for the before/after report: dropping a scene
        // shifts every later scene's index, so the "after" text is read off
        // the scene OBJECT, not off the (shifted) list position.
        public List<(int Number, Scene? Scene, string Before)> Captured { get; } = [];

        public void CaptureBefore(int sceneNumber)
        {
            if (Captured.Any(c => c.Number == sceneNumber)) return;
            if (sceneNumber == 0)
            {
                Captured.Add((0, null, Script.Brief));
                return;
            }
            if (sceneNumber >= 1 && sceneNumber <= Script.Scenes.Count)
            {
                var scene = Script.Scenes[sceneNumber - 1];
                Captured.Add((sceneNumber, scene, scene.Narration));
            }
        }
    }

    internal static RewriteResult Rewrite(VideoScript script, List<string> findings, string checkContext, DateTime prepDate,
        // L72: Rewrite() itself never logged — every action depended on the
        // caller iterating result.Actions, so a future caller that forgot
        // the loop got silent rewrites/drops. Rewrite logs each action
        // itself now, with the job id; callers pass their identity (and
        // the repair path tags its lines "rewrite (repair)").
        PrepIdentity? context = null, string actionTag = "rewrite")
    {
        var state = new RewriteState { Script = script };
        var current = ParseFindings(findings);
        foreach (var n in current.Select(f => f.SceneNumber).Distinct())
            state.CaptureBefore(n);

        for (var pass = 1; pass <= 3 && current.Count > 0; pass++)
        {
            ApplyPass(state, current, pass);
            current = ParseFindings(FactTraceability.Check(script, checkContext, prepDate));
        }

        if (current.Count > 0)
        {
            // Last resort: drop every scene the gate still flags. A scene
            // that cannot be reframed into traceability must not ship —
            // but the script as a whole still proceeds (the floor check
            // below decides whether it needs repair, not a fatal).
            foreach (var n in current.Select(f => f.SceneNumber).Distinct().OrderByDescending(n => n))
            {
                if (n == 0)
                {
                    state.Actions.Add("brief: dropped — still untraceable after 3 rewrite passes");
                    script.Brief = "";
                    script.BriefScenes.Clear();
                    continue;
                }
                DropScene(state, n, "still untraceable after 3 rewrite passes");
            }
            current = ParseFindings(FactTraceability.Check(script, checkContext, prepDate));
        }

        var totalChars = script.Scenes.Sum(s => s.Narration.Length);
        var belowFloor = script.IsLongForm &&
            (script.Scenes.Count < ScriptGenerator.LongFormMinScenes ||
             totalChars < ScriptGenerator.LongFormHardFailChars);
        var changes = state.Captured
            .OrderBy(c => c.Number)
            .Select(c => (c.Number,
                Before: c.Before,
                After: c.Number == 0 ? script.Brief
                    : state.DroppedScenes.Contains(c.Number) ? "(scene dropped)"
                    : c.Scene is null ? "(scene dropped)"
                    : c.Scene.Narration))
            .ToList();
        var result = new RewriteResult(
            state.DroppedScenes, state.ReframedScenes, state.SourceFixedScenes, state.DemotedScenes,
            state.Actions, changes, current.Select(f => $"scene {f.SceneNumber} [{f.Kind}]: {f.Detail}").ToList(),
            script.Scenes.Count, totalChars, belowFloor);
        // L72: log each action here (job id + scene number ride in the
        // action text itself) instead of relying on the caller.
        foreach (var a in result.Actions)
            Console.WriteLine($"  {PrepContext.PrefixFor(context)}[fact-check] {actionTag}: {a}");
        return result;
    }

    // L72: the one-line OK summary — shared by FinishPrepAsync and the
    // --rewrite-gate path so the demoted count cannot drift out of one of
    // them again. (The old summaries printed reframed/dropped/source-fixed
    // only, so visual→art demotions were invisible in the summary.)
    internal static string OkSummary(RewriteResult result, int sceneCount) =>
        $"[fact-check] OK after rewrite: {result.ReframedScenes.Count} scene(s) reframed as speculation, {result.DroppedScenes.Count} dropped, {result.SourceFixedScenes.Count} visual source(s) fixed, {result.DemotedScenes.Count} visual(s) demoted to art — re-check clean, {sceneCount} scenes.";

    // Descending scene order, so a dropped scene never shifts the index of
    // a finding still to process in this pass. Findings are always fresh
    // from the re-check at the start of the next pass.
    private static void ApplyPass(RewriteState state, List<Finding> findings, int pass)
    {
        foreach (var f in findings.OrderByDescending(f => f.SceneNumber))
        {
            if (f.SceneNumber == 0)
            {
                ApplyBriefFinding(state, f, pass);
                continue;
            }
            if (SceneAt(state.Script, f.SceneNumber) is null) continue;
            ApplySceneFinding(state, f, pass);
        }
    }

    private static Scene? SceneAt(VideoScript script, int sceneNumber) =>
        sceneNumber >= 1 && sceneNumber <= script.Scenes.Count ? script.Scenes[sceneNumber - 1] : null;

    // --- per-finding handling ----------------------------------------------
    //
    // Pass 1 applies the smart handling per kind (reframe / strip source /
    // drop named-person sentences). Pass 2 escalates: drop the offending
    // sentence (narration kinds) or demote the visual (visual kinds).
    // Pass 3 drops the scene. A finding the rewrite cannot parse is
    // treated as fatal to its scene, never ignored.

    private static void ApplySceneFinding(RewriteState state, Finding f, int pass)
    {
        var n = f.SceneNumber;
        switch (f.Kind)
        {
            case "UntraceableAttribution":
            {
                var source = AttributionSource(f.Detail);
                if (source is null)
                {
                    DropScene(state, n, $"unparseable attribution finding: {f.Detail}");
                    return;
                }
                if (IsNamedPerson(source))
                {
                    // Rule 3 (Rishi, hard line): never reframe a claim
                    // about a named person as rumour — drop the sentence,
                    // or the scene when the sentence IS the scene.
                    if (!DropAttributionSentence(state, n, source))
                        DropScene(state, n, $"named-person attribution to \"{source}\" (rule 3)");
                }
                else if (pass == 1) ReframeAttributionScene(state, n, source);
                else if (pass == 2)
                {
                    if (!DropSentenceWithName(state, n, source))
                        DropScene(state, n, $"attribution to \"{source}\" could not be reframed");
                }
                else DropScene(state, n, $"attribution to \"{source}\" still untraceable after rewrite");
                return;
            }
            case "UntraceableNumber":
            {
                var token = NumberToken(f.Detail);
                if (token is null)
                {
                    DropScene(state, n, $"unparseable number finding: {f.Detail}");
                    return;
                }
                if (IsVisualFinding(f.Detail))
                {
                    if (pass <= 2) DemoteVisual(state, n, $"untraceable visual number \"{token}\"");
                    else DropScene(state, n, $"untraceable visual number \"{token}\"");
                }
                else if (pass == 1) ReframeNumberScene(state, n, token);
                else if (pass == 2)
                {
                    if (!DropSentenceWithToken(state, n, token))
                        DropScene(state, n, $"untraceable number \"{token}\" could not be reframed");
                }
                else DropScene(state, n, $"number \"{token}\" still untraceable after rewrite");
                return;
            }
            case "UntraceableEntity":
            {
                var entity = EntityToken(f.Detail);
                if (entity is null)
                {
                    DropScene(state, n, $"unparseable entity finding: {f.Detail}");
                    return;
                }
                if (IsVisualFinding(f.Detail))
                {
                    if (pass <= 2) DemoteVisual(state, n, $"untraceable visual entity \"{entity}\"");
                    else DropScene(state, n, $"untraceable visual entity \"{entity}\"");
                }
                else if (pass == 1) ReframeEntityScene(state, n, entity);
                else if (pass == 2)
                {
                    if (!DropSentenceWithToken(state, n, entity))
                        DropScene(state, n, $"untraceable entity \"{entity}\" could not be reframed");
                }
                else DropScene(state, n, $"entity \"{entity}\" still untraceable after rewrite");
                return;
            }
            case "UntraceableQuote":
            {
                var outlet = QuotedOutlet(f.Detail);
                if (pass == 1 && outlet is not null && !IsNamedPerson(outlet))
                    ReframeVisualQuote(state, n, outlet);
                else if (pass <= 2) DemoteVisual(state, n, $"quote attributed to untraceable outlet \"{outlet}\"");
                else DropScene(state, n, $"quote attributed to \"{outlet}\" still untraceable after rewrite");
                return;
            }
            case "FutureDatedSource":
            {
                if (pass == 1) StripFutureDateFromSource(state, n, f.Detail);
                else if (pass == 2) DemoteVisual(state, n, "future-dated visual source");
                else DropScene(state, n, "future-dated visual source still untraceable after rewrite");
                return;
            }
            case "InternalSource":
            case "UntraceableSource":
            {
                var scene = SceneAt(state.Script, n);
                if (pass == 1 && scene is not null)
                {
                    if (VisualQuotesNamedPerson(scene, out var quoted))
                    {
                        // The scene's content IS a named person's quote
                        // resting on this untraceable source: rule 3. Drop
                        // the scene when the narration restates the quote;
                        // otherwise just demote the visual (the quote goes
                        // away with it) and keep the narration.
                        if (NarrationRestsOnQuote(scene.Narration, quoted))
                            DropScene(state, n, $"named-person quote (\"{quoted}\") resting on untraceable source (rule 3)");
                        else
                            DemoteVisual(state, n, $"visual quotes named person \"{quoted}\" on an untraceable source");
                    }
                    else StripVisualSource(state, n);
                }
                else if (pass == 2) DemoteVisual(state, n, "untraceable visual source");
                else DropScene(state, n, "visual source still untraceable after rewrite");
                return;
            }
            default:
                // Unknown finding kind: never let it through. Pass 3 drops
                // the scene; earlier passes leave it for the escalation.
                if (pass >= 3) DropScene(state, n, $"unhandled finding kind {f.Kind}");
                return;
        }
    }

    // The brief is scene 0 in gate findings. BriefScenes (when built) are
    // rewritten scene by scene; otherwise the raw Brief text is rewritten
    // sentence by sentence. A brief sentence that cannot be reframed is
    // dropped from the text; a brief scene emptied the same way is removed.
    private static void ApplyBriefFinding(RewriteState state, Finding f, int pass)
    {
        var script = state.Script;
        if (script.BriefScenes.Count > 0)
        {
            foreach (var briefScene in script.BriefScenes.ToList())
            {
                var text = briefScene.Narration;
                var rewritten = ReframeText(text, f, pass);
                if (rewritten is null)
                {
                    script.BriefScenes.Remove(briefScene);
                    state.Actions.Add($"brief: dropped a brief scene — {f.Kind} could not be reframed");
                }
                else if (rewritten != text)
                {
                    briefScene.Narration = rewritten;
                    briefScene.IsSpeculative = true;
                    state.Actions.Add($"brief: reframed ({f.Kind}) as speculation");
                }
            }
            return;
        }
        if (string.IsNullOrWhiteSpace(script.Brief)) return;
        var result = ReframeText(script.Brief, f, pass);
        if (result is null)
        {
            script.Brief = "";
            state.Actions.Add($"brief: dropped — {f.Kind} could not be reframed");
        }
        else if (result != script.Brief)
        {
            script.Brief = result;
            state.Actions.Add($"brief: reframed ({f.Kind}) as speculation");
        }
    }

    // Sentence-level rewrite of a free text (the Brief) for one finding.
    // Returns the rewritten text, or null when the offending sentence must
    // be dropped and nothing meaningful remains.
    private static string? ReframeText(string text, Finding f, int pass)
    {
        var sentences = SplitSentences(text);
        string? token = f.Kind switch
        {
            "UntraceableAttribution" => AttributionSource(f.Detail),
            "UntraceableNumber" => NumberToken(f.Detail),
            "UntraceableEntity" => EntityToken(f.Detail),
            _ => null,
        };
        if (token is null) return pass >= 3 ? null : text;
        if (f.Kind == "UntraceableAttribution" && IsNamedPerson(token))
        {
            if (FindSentenceWithAttribution(sentences, token) < 0) return text;
            if (!DropSentenceWithName(sentences, token)) return null;
            var kept = JoinSentences(sentences);
            return Meaningful(kept) ? kept : null;
        }
        var idx = f.Kind == "UntraceableAttribution"
            ? FindSentenceWithAttribution(sentences, token)
            : FindSentenceWithToken(sentences, token);
        if (idx < 0) return text;
        string? reframed = f.Kind switch
        {
            "UntraceableAttribution" => ReframeAttribution(sentences[idx], token),
            "UntraceableNumber" => ReframeNumber(sentences[idx], token),
            "UntraceableEntity" => ReframeEntity(sentences[idx], token),
            _ => null,
        };
        if (reframed is null)
        {
            sentences.RemoveAt(idx);
            var kept = JoinSentences(sentences);
            return Meaningful(kept) ? kept : null;
        }
        sentences[idx] = reframed;
        return JoinSentences(sentences);
    }

    // --- scene mutations ----------------------------------------------------

    private static void DropScene(RewriteState state, int sceneNumber, string reason)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene is null) return;
        state.Script.Scenes.RemoveAt(sceneNumber - 1);
        if (!state.DroppedScenes.Contains(sceneNumber)) state.DroppedScenes.Add(sceneNumber);
        state.Actions.Add($"scene {sceneNumber}: dropped — {reason}");
    }

    // Demote a data visual to plain art (the existing demotion from
    // ValidateNewsScenes): the chart/callout and its untraceable source or
    // numbers go away, the narration stays. Idempotent.
    private static bool DemoteVisual(RewriteState state, int sceneNumber, string reason)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene is null) return false;
        if (scene.VisualSpec is null && scene.SceneKind == "art") return false;
        scene.SceneKind = "art";
        scene.VisualSpec = null;
        if (string.IsNullOrWhiteSpace(scene.ImagePrompt))
            scene.ImagePrompt = $"Documentary news illustration for this beat: {scene.Narration}";
        if (!state.DemotedScenes.Contains(sceneNumber)) state.DemotedScenes.Add(sceneNumber);
        state.Actions.Add($"scene {sceneNumber}: visual demoted to art — {reason}");
        return true;
    }

    // Strip the untraceable source string from a visual (callout/chart
    // stays, narration untouched). The gate skips an empty source, and
    // ValidateNewsScenes never requires one.
    private static void StripVisualSource(RewriteState state, int sceneNumber)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene?.VisualSpec is null) return;
        var old = scene.VisualSpec.Source ?? "";
        scene.VisualSpec = scene.VisualSpec with { Source = "" };
        if (!state.SourceFixedScenes.Contains(sceneNumber)) state.SourceFixedScenes.Add(sceneNumber);
        state.Actions.Add($"scene {sceneNumber}: stripped untraceable visual source \"{old}\" (narration unchanged)");
    }

    private static void StripFutureDateFromSource(RewriteState state, int sceneNumber, string detail)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene is null) return;
        var spec = scene.VisualSpec;
        if (spec?.Source is not { Length: > 0 } source) return;
        var stripped = Regex.Replace(source, @"\b20\d\d-\d\d-\d\d\b", "").Trim();
        stripped = Regex.Replace(stripped,
            @"\b(january|february|march|april|may|june|july|august|september|october|november|december|jan\.?|feb\.?|mar\.?|apr\.?|jun\.?|jul\.?|aug\.?|sep\.?|sept\.?|oct\.?|nov\.?|dec\.?)\s+\d{1,2},?\s+20\d\d\b",
            "", RegexOptions.IgnoreCase).Trim();
        stripped = Regex.Replace(stripped, @"[,\s]+$", "").Trim();
        if (stripped.Length == 0)
        {
            StripVisualSource(state, sceneNumber);
            return;
        }
        scene.VisualSpec = spec with { Source = stripped };
        if (!state.SourceFixedScenes.Contains(sceneNumber)) state.SourceFixedScenes.Add(sceneNumber);
        state.Actions.Add($"scene {sceneNumber}: stripped future date from visual source \"{source}\" -> \"{stripped}\"");
    }

    // Re-attribute a visual quote to "reports" instead of the untraceable
    // outlet, and mark the scene speculative. Only for non-person outlets;
    // a named-person quote is demoted away by the caller.
    private static void ReframeVisualQuote(RewriteState state, int sceneNumber, string outlet)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene?.VisualSpec is null) return;
        var spec = scene.VisualSpec;
        var rewritten = RewriteFirstQuotedAttribution(spec, outlet);
        if (rewritten is null) return;
        scene.VisualSpec = rewritten;
        scene.IsSpeculative = true;
        if (!state.ReframedScenes.Contains(sceneNumber)) state.ReframedScenes.Add(sceneNumber);
        state.Actions.Add($"scene {sceneNumber}: visual quote re-attributed from \"{outlet}\" to reports");
    }

    // Does this scene's visual quote a line attributed to a named person?
    // Returns the quoted text when it does.
    private static bool VisualQuotesNamedPerson(Scene scene, out string quoted)
    {
        quoted = "";
        if (scene.VisualSpec is null) return false;
        foreach (var text in VisualTexts(scene.VisualSpec))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = QuotedAttributionRegex.Match(text);
            if (!m.Success) continue;
            if (!IsNamedPerson(m.Groups[2].Value.Trim())) continue;
            quoted = m.Groups[1].Value;
            return true;
        }
        return false;
    }

    // The narration restates the quote (at least half its significant
    // words), so the scene's content IS the named person's quote — rule 3
    // drops the scene rather than just the visual.
    private static bool NarrationRestsOnQuote(string narration, string quoted)
    {
        var quoteWords = SignificantWords(quoted);
        if (quoteWords.Count == 0) return false;
        var narrationLower = narration.ToLowerInvariant();
        var hits = quoteWords.Count(w => narrationLower.Contains(w, StringComparison.Ordinal));
        return hits * 2 >= quoteWords.Count;
    }

    private static List<string> SignificantWords(string text)
    {
        var stop = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "with", "is", "was", "are", "were",
            "that", "this", "it", "its", "as", "at", "by", "from", "but", "not", "no",
        };
        return Regex.Matches(text.ToLowerInvariant(), @"[a-z][a-z\-']+")
            .Select(m => m.Value.Trim('\''))
            .Where(w => w.Length >= 4 && !stop.Contains(w))
            .Distinct()
            .ToList();
    }

    private static IEnumerable<string?> VisualTexts(VisualSpec spec)
    {
        yield return spec.Title;
        yield return spec.Text;
        yield return spec.Subtext;
        yield return spec.Unit;
        if (spec.Labels is not null) foreach (var t in spec.Labels) yield return t;
        if (spec.Headers is not null) foreach (var t in spec.Headers) yield return t;
        if (spec.Rows is not null)
            foreach (var row in spec.Rows)
                if (row is not null)
                    foreach (var cell in row)
                        yield return cell;
    }

    // The quoted-attribution shape, identical to the gate's: a quoted line
    // followed by a dash and an outlet.
    private static readonly Regex QuotedAttributionRegex = new(
        @"([""“”‘’'][^""“”‘’']{3,200}[""“”‘’']),?\s*[–—-]\s*([^,.;\n]{2,60})",
        RegexOptions.Compiled);

    // Rewrite the first quoted attribution in any visual text field to
    // "– reports". Returns the new spec, or null when no field matches.
    private static VisualSpec? RewriteFirstQuotedAttribution(VisualSpec spec, string outlet)
    {
        VisualSpec? With(string? field, Func<VisualSpec, string?, VisualSpec> set)
        {
            if (string.IsNullOrWhiteSpace(field)) return null;
            var m = QuotedAttributionRegex.Match(field);
            if (!m.Success) return null;
            var replacement = m.Groups[1].Value + " – reports";
            return set(spec, field[..m.Index] + replacement + field[(m.Index + m.Length)..]);
        }
        return With(spec.Text, (s, v) => s with { Text = v })
            ?? With(spec.Title, (s, v) => s with { Title = v })
            ?? With(spec.Subtext, (s, v) => s with { Subtext = v })
            ?? With(spec.Unit, (s, v) => s with { Unit = v });
    }

    // --- narration sentence operations --------------------------------------
    //
    // Every reframe works on ONE sentence: the sentence containing the
    // flagged token/name. The rest of the scene's narration is untouched,
    // so a rewrite can never smuggle in a new claim — it can only soften
    // or delete the flagged one. A reframed scene is marked IsSpeculative,
    // and a scene that turns speculative loses any data visual (the
    // existing rule: a theory never gets a chart or callout).

    private static bool ReframeAttributionScene(RewriteState state, int sceneNumber, string source)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene is null) return false;
        var sentences = SplitSentences(scene.Narration);
        var idx = FindSentenceWithAttribution(sentences, source);
        if (idx < 0) return false;
        var reframed = ReframeAttribution(sentences[idx], source);
        if (reframed is null)
            return DropSentenceAt(state, sceneNumber, sentences, idx, $"reframe failed for attribution to \"{source}\" — sentence dropped");
        sentences[idx] = reframed;
        scene.Narration = JoinSentences(sentences);
        MarkReframed(state, scene, sceneNumber, $"attribution to \"{source}\" reframed as speculation");
        return true;
    }

    private static bool ReframeNumberScene(RewriteState state, int sceneNumber, string token)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene is null) return false;
        var sentences = SplitSentences(scene.Narration);
        var idx = FindSentenceWithToken(sentences, token);
        if (idx < 0) return false;
        var reframed = ReframeNumber(sentences[idx], token);
        if (reframed is null)
            return DropSentenceAt(state, sceneNumber, sentences, idx, $"reframe failed for number \"{token}\" — sentence dropped");
        sentences[idx] = reframed;
        scene.Narration = JoinSentences(sentences);
        MarkReframed(state, scene, sceneNumber, $"number \"{token}\" reframed as speculation");
        return true;
    }

    private static bool ReframeEntityScene(RewriteState state, int sceneNumber, string entity)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene is null) return false;
        var sentences = SplitSentences(scene.Narration);
        var idx = FindSentenceWithToken(sentences, entity);
        if (idx < 0) return false;
        var reframed = ReframeEntity(sentences[idx], entity);
        if (reframed is null)
            return DropSentenceAt(state, sceneNumber, sentences, idx, $"reframe failed for entity \"{entity}\" — sentence dropped");
        sentences[idx] = reframed;
        scene.Narration = JoinSentences(sentences);
        MarkReframed(state, scene, sceneNumber, $"entity \"{entity}\" reframed as speculation");
        return true;
    }

    private static void MarkReframed(RewriteState state, Scene scene, int sceneNumber, string what)
    {
        scene.IsSpeculative = true;
        if (!state.ReframedScenes.Contains(sceneNumber)) state.ReframedScenes.Add(sceneNumber);
        state.Actions.Add($"scene {sceneNumber}: {what}");
        // A speculative scene never keeps a data visual (legal/spice rules
        // Rishi 2026-11-21, same demotion ValidateNewsScenes applies).
        if (scene.SceneKind == "visual" && scene.VisualSpec is not null)
            DemoteVisual(state, sceneNumber, "speculative scene never gets a chart or callout");
    }

    // Drop the sentence that attributes a claim to a named person. Returns
    // false when the scene would be left empty or meaningless — the caller
    // then drops the scene instead.
    private static bool DropAttributionSentence(RewriteState state, int sceneNumber, string source)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene is null) return false;
        var sentences = SplitSentences(scene.Narration);
        var idx = FindSentenceWithAttribution(sentences, source);
        if (idx < 0) return false;
        return DropSentenceAt(state, sceneNumber, sentences, idx,
            $"named-person attribution to \"{source}\" dropped (rule 3)");
    }

    private static bool DropSentenceWithName(RewriteState state, int sceneNumber, string source)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene is null) return false;
        var sentences = SplitSentences(scene.Narration);
        var idx = FindSentenceWithAttribution(sentences, source);
        if (idx < 0) return false;
        return DropSentenceAt(state, sceneNumber, sentences, idx, $"sentence attributing a claim to \"{source}\" dropped");
    }

    private static bool DropSentenceWithToken(RewriteState state, int sceneNumber, string token)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene is null) return false;
        var sentences = SplitSentences(scene.Narration);
        var idx = FindSentenceWithToken(sentences, token);
        if (idx < 0) return false;
        return DropSentenceAt(state, sceneNumber, sentences, idx, $"sentence with untraceable \"{token}\" dropped");
    }

    // Remove sentences[idx] from the scene's narration. Returns false when
    // nothing meaningful remains (the caller drops the scene).
    private static bool DropSentenceAt(RewriteState state, int sceneNumber, List<string> sentences, int idx, string what)
    {
        var scene = SceneAt(state.Script, sceneNumber);
        if (scene is null) return false;
        sentences.RemoveAt(idx);
        var kept = JoinSentences(sentences);
        if (!Meaningful(kept)) return false;
        scene.Narration = kept;
        state.Actions.Add($"scene {sceneNumber}: {what}");
        return true;
    }

    // Drop the sentence containing `name` from a sentence list (brief path).
    private static bool DropSentenceWithName(List<string> sentences, string name)
    {
        var idx = FindSentenceWithName(sentences, name);
        if (idx < 0) return false;
        sentences.RemoveAt(idx);
        return Meaningful(JoinSentences(sentences));
    }

    private static bool Meaningful(string narration)
    {
        var letters = narration.Count(char.IsLetterOrDigit);
        return letters >= 20;
    }

    // --- sentence splitting / lookup ----------------------------------------

    // Split on sentence-ending punctuation followed by a capitalised start.
    // Delimiters stay attached to their sentence. A narration with no
    // sentence end is one sentence.
    private static List<string> SplitSentences(string narration)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < narration.Length; i++)
        {
            if (narration[i] is not ('.' or '!' or '?')) continue;
            var j = i + 1;
            if (j < narration.Length && narration[j] is '"' or '\'' or '”' or '’') j++;
            if (j >= narration.Length) continue;
            if (narration[j] != ' ') continue;
            var k = j + 1;
            if (k < narration.Length && (char.IsUpper(narration[k]) || narration[k] is '“' or '"' or '('))
            {
                sentences.Add(narration[start..(j)].Trim());
                start = k;
                i = k - 1;
            }
        }
        var tail = narration[start..].Trim();
        if (tail.Length > 0) sentences.Add(tail);
        return sentences.Count == 0 ? [narration.Trim()] : sentences;
    }

    private static string JoinSentences(List<string> sentences) =>
        string.Join(" ", sentences.Where(s => s.Length > 0));

    // Boundary-aware token search: "19" must not match "2019", "30" must
    // not match "300". Unicode-normalised on both sides (the model writes
    // non-breaking hyphens inside "19th-century").
    private static int FindSentenceWithToken(List<string> sentences, string token)
    {
        var pattern = $@"(?<!\d){Regex.Escape(NormalizeText(token))}(?!\d)";
        for (var i = 0; i < sentences.Count; i++)
            if (Regex.IsMatch(NormalizeText(sentences[i]), pattern))
                return i;
        return -1;
    }

    // The sentence that actually ATTRIBUTES to the source: the name plus
    // its reporting verb ("X said", "X never confirmed", "According to X").
    // A plain name search would pick the wrong sentence — in the Ukraine
    // scene 23 the name "NATO" also appears in two earlier questions
    // ("Was Ukraine operating inside NATO territory?"), and reframing one
    // of those would mangle a question into "Reports suggest was Ukraine
    // operating...". Falls back to any sentence containing the name.
    private static int FindSentenceWithAttribution(List<string> sentences, string source)
    {
        var words = AttributionPatternWords(source);
        if (words.Count == 0) return -1;
        var namePat = string.Join(@"\s+", words.Select(WordPattern));
        var verbPat = @"(said|says|told|warned|noted|notes|note|announced|confirmed|reported|reports|claimed|claims|argued|argues)";
        var patterns = new[]
        {
            $@"according to\s+{namePat}\b",
            $@"\b{namePat}\s+(has\s+|have\s+|had\s+)?{verbPat}\b",
            $@"\b{namePat}\s+never\s+(confirmed|denied|acknowledged|admitted|said|announced|claimed|stated)\b",
        };
        foreach (var pattern in patterns)
            for (var i = 0; i < sentences.Count; i++)
                if (Regex.IsMatch(NormalizeText(sentences[i]), pattern, RegexOptions.IgnoreCase))
                    return i;
        return FindSentenceWithName(sentences, source);
    }

    private static int FindSentenceWithName(List<string> sentences, string source)
    {
        var words = AttributionPatternWords(source);
        if (words.Count == 0) return -1;
        var pattern = $@"\b{string.Join(@"\s+", words.Select(WordPattern))}\b";
        for (var i = 0; i < sentences.Count; i++)
            if (Regex.IsMatch(NormalizeText(sentences[i]), pattern, RegexOptions.IgnoreCase))
                return i;
        // Fallback: any single name word (the sentence may carry the name
        // with a possessive or a title the pattern missed).
        foreach (var w in words)
        {
            var single = $@"\b{WordPattern(w)}\b";
            for (var i = 0; i < sentences.Count; i++)
                if (Regex.IsMatch(NormalizeText(sentences[i]), single, RegexOptions.IgnoreCase))
                    return i;
        }
        return -1;
    }

    // The attribution subject as the gate captured it ("NATO never", "As
    // Guterres", "President Zelenskiy") reduced to the name words, with
    // filler/role words stripped. Used to build the sentence regex.
    private static List<string> AttributionPatternWords(string rawSource)
    {
        var (cleaned, _) = CleanedSourceWords(rawSource.Trim().Trim('"', '\'', '“', '”', '‘', '’'));
        // CleanedSourceWords lowercases; the sentence regex is
        // case-insensitive, so lowercased words match the original.
        return cleaned;
    }

    // One name word as a regex: apostrophes are optional ("O'Brien"
    // matches "OBrien" and vice versa), a trailing "'s" is optional
    // ("Ireland's" matches "Ireland").
    private static string WordPattern(string word)
    {
        var core = word;
        var optionalS = false;
        if (core.EndsWith("'s", StringComparison.Ordinal))
        {
            core = core[..^2];
            optionalS = true;
        }
        var pattern = Regex.Escape(core).Replace("'", "'?", StringComparison.Ordinal);
        return optionalS ? pattern + "'?s?" : pattern;
    }

    // --- normalisation ------------------------------------------------------

    // The model writes non-breaking hyphens (U+2011) and narrow no-break
    // spaces (U+202F) inside dates and names ("19th-century", "Sharm
    // El-Sheikh"); the gate normalises the same way for matching, so the
    // rewrite does too — on the sentence being edited only. Apostrophes
    // are KEPT ("Russia's fleet" must not become "Russias fleet").
    private static string NormalizeText(string s) => s
        .Replace('\u2011', '-').Replace('\u2010', '-').Replace('\u2013', '-').Replace('\u2014', '-')
        .Replace('\u202F', ' ').Replace('\u00A0', ' ').Replace('\u2009', ' ')
        .Replace('\u2019', '\'').Replace('\u2018', '\'')
        .Replace('\u201C', '"').Replace('\u201D', '"');

    private static string CleanSpaces(string s)
    {
        s = Regex.Replace(s, @"\s+", " ");
        s = Regex.Replace(s, @"\s+([,.;:!?%])", "$1");
        s = Regex.Replace(s, @"([(])\s+", "$1");
        return s.Trim();
    }

    private static string LowerFirst(string s)
    {
        if (s.Length >= 2 && char.IsUpper(s[0]) && char.IsLower(s[1]))
            return char.ToLowerInvariant(s[0]) + s[1..];
        return s;
    }

    private static readonly Regex IntensifierRegex = new(
        @"\s+\b(undeniably|clearly|obviously|certainly|definitely)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string StripIntensifiers(string s) => CleanSpaces(IntensifierRegex.Replace(s, ""));

    // Prefix a reframed sentence with a recognised attribution phrase,
    // unless it already carries one. The prefix is what the re-check
    // recognises (see AttributionPhrases).
    private static string PrefixSpeculative(string sentence)
    {
        var s = CleanSpaces(sentence);
        if (s.Length == 0) return s;
        if (ContainsAttributionPhrase(s)) return s;
        return "Reports suggest " + LowerFirst(s);
    }

    // --- template reframes (pure string functions) --------------------------
    //
    // Each returns the reframed sentence (attribution phrase included) or
    // null when the sentence cannot be reframed — the caller then drops
    // the sentence instead. Nothing here calls a provider: the templates
    // only delete or soften the flagged span, so no new claim can appear.

    // "The 2022 meeting in Sharm El-Sheikh ..." -> "Reports suggest the
    // recent meeting in Sharm El-Sheikh ...". "targets for 2050" -> "targets
    // for the years ahead". "the early 2000s" -> "earlier decades".
    // "historic 19th-century ship" -> "historic ship". "52 hours" ->
    // "several hours". "$2.5 billion revenue" -> "a substantial sum".
    private static string? ReframeNumber(string sentence, string token)
    {
        var s = NormalizeText(sentence);
        var esc = Regex.Escape(NormalizeText(token));
        var changed = false;
        string Replace(string pattern, string replacement)
        {
            var next = Regex.Replace(s, pattern, replacement, RegexOptions.IgnoreCase);
            if (next != s) changed = true;
            return next;
        }
        string ReplaceEval(string pattern, MatchEvaluator eval)
        {
            var next = Regex.Replace(s, pattern, eval, RegexOptions.IgnoreCase);
            if (next != s) changed = true;
            return next;
        }

        // "the early 2000s" -> "earlier decades".
        s = Replace($@"\bearly\s+{esc}s\b", "earlier decades");
        // "19th-century" -> "historic" (or delete it when "historic" is
        // already there: "a historic historic ship" is not an option).
        s = ReplaceEval($@"\b{esc}(st|nd|rd|th)-century\b",
            _ => s.Contains("historic", StringComparison.OrdinalIgnoreCase) ? "" : "historic");
        // Bare ordinal: "the 19th meeting" -> "the latest meeting".
        s = Replace($@"\b{esc}(st|nd|rd|th)\b", "latest");
        // A 4-digit year, with the replacement chosen by its neighbours.
        if (token.Length == 4 && (token.StartsWith("19") || token.StartsWith("20")))
        {
            s = ReplaceEval($@"\b{esc}\b", m =>
            {
                var before = s[..m.Index];
                var after = s[(m.Index + m.Length)..];
                var prevWord = LastWord(before);
                var nextWord = NextWord(after);
                if (prevWord is "for" or "by" or "until" or "through" or "to") return "the years ahead";
                if (nextWord is not null && YearNouns.Contains(nextWord)) return "recent";
                if (prevWord is "in" or "since" or "from") return "recent years";
                return "recent years";
            });
        }
        // Percent: "a 40% increase" -> "a significant increase";
        // "rose 40%" -> "rose significantly".
        s = ReplaceEval($@"\b{esc}%",
            m =>
            {
                var after = s[(m.Index + m.Length)..];
                var nextWord = NextWord(after);
                return nextWord is not null && char.IsLower(nextWord[0]) ? "a significant" : "significantly";
            });
        // Currency / big unit: "$2.5 billion" -> "a substantial sum".
        s = Replace($@"[$£€]\s?{esc}(\s?(billion|million|trillion|bn|m)\b)?", "a substantial sum");
        s = Replace($@"\b{esc}\s+(billion|million|trillion)\b", "a substantial sum");
        // Plain count: "52 hours" -> "several hours"; anything else loses
        // the bare number.
        s = ReplaceEval($@"(?<!\d){esc}(?!\d)",
            m =>
            {
                var after = s[(m.Index + m.Length)..];
                var nextWord = NextWord(after);
                return nextWord is not null && char.IsLower(nextWord[0]) ? "several" : "";
            });

        s = CleanSpaces(s);
        s = Regex.Replace(s, @"\ba several\b", "several", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bthe historic historic\b", "the historic", RegexOptions.IgnoreCase);
        if (!changed) return null;
        // The flagged number must actually be gone (boundary-aware).
        if (Regex.IsMatch(s, $@"(?<!\d){esc}(?!\d)")) return null;
        s = StripIntensifiers(s);
        if (!Meaningful(s)) return null;
        return PrefixSpeculative(s);
    }

    // Nouns a year can directly precede ("The 2022 meeting" -> "The recent
    // meeting"). Anything else takes the "recent years" fallback.
    private static readonly HashSet<string> YearNouns = new(StringComparer.Ordinal)
    {
        "meeting", "summit", "conference", "talks", "deal", "war", "election", "elections", "report",
        "attack", "attacks", "invasion", "crisis", "season", "session", "round", "vote", "treaty",
        "agreement", "pact", "forum", "expo", "games", "olympics", "cup", "championship", "era",
        "period", "phase", "edition", "version", "release", "launch", "debut", "premiere", "trial",
        "hearing", "ruling", "verdict", "strike", "protest", "crash", "spill", "outbreak",
    };

    private static string? LastWord(string before)
    {
        var m = Regex.Match(before, @"([A-Za-z]+)[^A-Za-z]*$");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
    }

    private static string? NextWord(string after)
    {
        var m = Regex.Match(after, @"^\s+([A-Za-z]+)");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    // "Zelenskiy said X" is never reframed (named person — dropped by the
    // caller). For a non-person source: "According to X, Y" / "X said Y" ->
    // "Reports suggest Y"; "X never confirmed Y" -> "It remains unclear
    // whether Y took place. Reports suggest ...".
    private static string? ReframeAttribution(string sentence, string rawSource)
    {
        var s = StripIntensifiers(NormalizeText(sentence));
        var words = AttributionPatternWords(rawSource);
        if (words.Count == 0) return null;
        var namePat = string.Join(@"\s+", words.Select(WordPattern));
        var verbPat = @"(said|says|told|warned|noted|notes|note|announced|confirmed|reported|reports|claimed|claims|argued|argues)";

        Match m;
        // (a) "According to X, <rest>"
        m = Regex.Match(s, $@"^\s*according to\s+{namePat}\s*[,;:—-]?\s*(.*)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var rest = CleanSpaces(m.Groups[1].Value);
            return rest.Length == 0 ? null : PrefixSpeculative(rest);
        }
        // (b) "X never confirmed/denied <rest>" — a negated confirmation
        // becomes an open question, never a flat denial.
        m = Regex.Match(s, $@"^\s*{namePat}\s+never\s+(confirmed|denied|acknowledged|admitted|said|announced|claimed|stated)\s+(.*)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var rest = CleanSpaces(m.Groups[2].Value);
            if (rest.Length == 0) return null;
            var butMatch = Regex.Match(rest, @"\s*[,;]\s+but\s+", RegexOptions.IgnoreCase);
            if (butMatch.Success)
            {
                var head = CleanSpaces(rest[..butMatch.Index]);
                var tail = CleanSpaces(rest[(butMatch.Index + butMatch.Length)..]);
                if (!HeadHasVerb(head)) head += " took place";
                var tailReframed = tail.Length == 0 ? "" : PrefixSpeculative(StripIntensifiers(tail));
                var first = "It remains unclear whether " + LowerFirst(head) + ".";
                return tailReframed.Length == 0 ? first : first + " " + tailReframed;
            }
            if (!HeadHasVerb(rest)) rest += " took place";
            return "It remains unclear whether " + LowerFirst(rest) + " — reports suggest no confirmation has emerged.";
        }
        // (c) "X said/says (that) <rest>", with an optional auxiliary.
        m = Regex.Match(s, $@"^\s*{namePat}\s+(has\s+|have\s+|had\s+)?{verbPat}\b\s*(that\s+)?(.*)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var rest = CleanSpaces(m.Groups[4].Value);
            return rest.Length == 0 ? null : PrefixSpeculative(rest);
        }
        // (e) Mid-sentence: "The deal, X said, collapsed." -> the
        // attribution clause is removed and the sentence is reframed.
        m = Regex.Match(s, $@"^(.*?),\s*{namePat}\s+{verbPat}\b[^,.;!?]*,?\s*(.*)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var rest = CleanSpaces(m.Groups[1].Value + " " + m.Groups[3].Value);
            return rest.Length == 0 ? null : PrefixSpeculative(rest);
        }
        // (f) Fallback: delete the name itself and reframe what remains.
        var removed = CleanSpaces(Regex.Replace(s, $@"\b{namePat}\b", "", RegexOptions.IgnoreCase));
        if (removed == s || !Meaningful(removed)) return null;
        return PrefixSpeculative(removed);
    }

    // "any joint operations" has no verb, so "whether ..." needs one
    // ("whether any joint operations took place"); a head that already
    // carries a verb is left alone.
    private static bool HeadHasVerb(string head)
    {
        var words = Regex.Matches(head.ToLowerInvariant(), @"[a-z]+").Select(m => m.Value).ToList();
        return words.Any(w =>
            w.EndsWith("ed", StringComparison.Ordinal) ||
            w.EndsWith("ing", StringComparison.Ordinal) ||
            w is "is" or "are" or "was" or "were" or "has" or "have" or "had" or "happened" or "occurred" or "began" or "took" or "emerged");
    }

    // An invented name is deleted from the sentence ("a new physics engine
    // called 'HydroFlow'" -> "a new physics engine") and the sentence is
    // reframed as speculation.
    private static string? ReframeEntity(string sentence, string entity)
    {
        var s = StripIntensifiers(NormalizeText(sentence));
        var esc = Regex.Escape(NormalizeText(entity));
        var changed = false;
        string Replace(string pattern, string replacement)
        {
            var next = Regex.Replace(s, pattern, replacement, RegexOptions.IgnoreCase);
            if (next != s) changed = true;
            return next;
        }
        // "called/dubbed/named 'HydroFlow'" (with an optional article).
        s = Replace($@"\b(called|dubbed|named|termed)\s+(a\s+|an\s+|the\s+)?[""“']?{esc}[""”']?\s*,?\s*", "");
        // A bare quoted or unquoted mention.
        s = Replace($@"[""“']{esc}[""”']", "");
        s = Replace($@"\b{esc}\b", "");
        s = CleanSpaces(s);
        // Deleting the name must not leave a dangling "called" or a double space.
        s = Regex.Replace(s, @"\b(called|dubbed|named|termed)\s+([,.;])", "$2", RegexOptions.IgnoreCase);
        if (!changed) return null;
        if (!Meaningful(s)) return null;
        return PrefixSpeculative(s);
    }
}
