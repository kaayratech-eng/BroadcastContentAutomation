using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GiggleGarden.Shared;

// Pre-render fact-traceability check (fix sequence #2 ITEM 2, review #2 §E).
//
// Video #3 shipped invented facts — a "HydroFlow" engine, "LifePulse" AI,
// $2.5B revenue, charts sourced to dates after its own Nov 22 prep —
// because nothing ever verified the script against the facts it was given.
// The per-act validator (ScriptGenerator.ValidateNewsScenes) only demotes
// a bad visual to art, and only against the act's own facts; the finished
// script was never checked as a whole. This is the whole-script gate that
// runs in FinishPrepAsync BEFORE script.json is saved, so a failing prep
// can never reach TTS/render unchecked. Since the fact-gate rewrite
// (2026-11-23, FactGateRewrite), a finding no longer kills the prep: the
// caller rewrites the offending claim into attributed speculation (or
// drops it, for named persons) and re-runs this check on the rewritten
// script. This file still only FINDS the untraceable claims; the rewrite
// lives in FactGateRewrite.
//
// What it checks, on every scene's narration and visualSpec (charts,
// tables, callouts) plus the brief:
//   * UntraceableNumber — a multi-digit number (currency, percent, count,
//     year, chart value) that does not appear in the supplied context.
//     Single digits are ignored ("GTA 6"), as are "24-hour"/"24/7" and
//     clock times. Speculative scenes are exempt: a labelled rumour may
//     cite speculative numbers.
//   * UntraceableEntity — a CamelCase proper noun ("HydroFlow",
//     "LifePulse") that appears nowhere in the context and is not a known
//     brand (YouTube, iPhone, PlayStation, ...). Applies to speculative
//     scenes too: a rumour about a real thing is fine, an invented name
//     is still fabrication.
//   * UntraceableAttribution — "According to X" / "X said" where X is a
//     named source (not a generic "analysts"/"critics") that does not
//     appear in the context.
//   * UntraceableQuote — a quoted line in a visual attributed to an outlet
//     ("– GameSpot review") that is not in the context.
//   * FutureDatedSource — a source dated after the prep date (ISO dates,
//     "Month D, YYYY", or a bare year past the prep year).
//   * InternalSource — a source containing "internal" ("internal report",
//     "internal data") that was not supplied verbatim in the context.
//   * UntraceableSource — a chart source whose outlet (or "press release"
//     wording) does not appear in the context at all.
//
// Traceability is deliberately literal: the source string, the quoted
// entity, or the numeric claim must appear in the supplied context (the
// trend headlines + full article text + the picked story's guarded
// Wikipedia background(s), see BuildCheckContext). It is practical, not
// perfect — it cannot tell a true claim from a false one that happens to
// share a number with the context — but it catches exactly the failure
// that shipped: specifics with no source behind them at all.
//
// Findings are plain strings, "scene {n} [{Kind}]: {detail}", with brief
// scenes numbered 0. An empty context returns no findings; the caller logs
// a loud skip warning instead — the gate only bites when a real trend
// context exists, so hand-written/manual scripts are never blocked by it.
static class FactTraceability
{
    public static List<string> Check(VideoScript script, string checkContext, DateTime prepDate) =>
        CheckJson(JsonSerializer.Serialize(script, Sidecar.Options), checkContext, prepDate);

    public static List<string> CheckJson(string scriptJson, string? checkContext, DateTime prepDate)
    {
        var findings = new List<string>();
        if (string.IsNullOrWhiteSpace(checkContext)) return findings;
        var context = WikipediaClient.NormalizeForMatch(checkContext);
        var contextNoCommas = context.Replace(",", "");

        using var doc = JsonDocument.Parse(scriptJson);
        var root = doc.RootElement;

        // The brief is checked too (as scene 0): it is spoken narration,
        // and a teaser that asserts an invented number is the same failure.
        // BriefScenes win when present; otherwise the raw Brief text.
        var briefScenes = GetArray(root, "BriefScenes");
        if (briefScenes is { Count: > 0 })
        {
            for (var i = 0; i < briefScenes.Count; i++)
                CheckNarration(briefScenes[i], 0, $"brief scene {i + 1}", findings, context, contextNoCommas);
        }
        else if (GetString(root, "Brief") is { Length: > 0 } brief)
        {
            CheckNarrationText(brief, 0, "brief", isSpeculative: false, findings, context, contextNoCommas);
        }

        var scenes = GetArray(root, "Scenes");
        if (scenes is null) return findings.Distinct().ToList();
        for (var i = 0; i < scenes.Count; i++)
        {
            var scene = scenes[i];
            var n = i + 1;
            CheckNarration(scene, n, $"scene {n}", findings, context, contextNoCommas);
            if (GetObject(scene, "VisualSpec") is { } spec)
                CheckVisualSpec(spec, n, prepDate, findings, context, contextNoCommas);
        }
        return findings.Distinct().ToList();
    }

    // The context the gate checks against: the trend context with the
    // top-story Wikipedia block stripped (it belongs to news[0], usually a
    // different story than the one picked — leaving it in would let a
    // script "trace" 2019 to the Russian-interference timeline) plus the
    // picked story's guarded background. When no picked background was
    // fetched, a forced-topic (--topic) block is kept: it IS the picked
    // topic's background, not the top story's.
    internal static string BuildCheckContext(string? trendContext, string? pickedTopicBackground)
    {
        var text = trendContext ?? "";
        string? forcedTopicBlock = null;
        var forcedIdx = text.IndexOf("Wikipedia context for this topic", StringComparison.Ordinal);
        if (forcedIdx >= 0)
            forcedTopicBlock = text[forcedIdx..].Trim();
        text = StripWikipediaBlocks(text);
        // Include BOTH (2026-11-23): the forced-topic block is what the
        // MODEL saw before generation (the backstory articles fetched for
        // the forced topic), while the picked background is fetched after
        // generation, on the script title, and may be a different article.
        // The old else-if dropped the forced-topic block whenever a picked
        // background existed — so a number the model took from the
        // forced-topic backstory failed the gate even though it was
        // supplied. The gate must check against everything the model was
        // given. (Duplicate text when both fetches return the same article
        // is harmless — matching is by presence.)
        if (!string.IsNullOrWhiteSpace(pickedTopicBackground))
            text = (text + "\n" + pickedTopicBackground.Trim()).Trim();
        if (forcedTopicBlock is not null)
            text = (text + "\n" + forcedTopicBlock).Trim();
        return text;
    }

    private static string StripWikipediaBlocks(string text)
    {
        foreach (var marker in new[] { "Background facts (Wikipedia)", "Wikipedia context for this topic" })
        {
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            while (idx >= 0)
            {
                var end = FindBlockEnd(text, idx + marker.Length);
                text = (text[..idx] + text[end..]).TrimEnd();
                idx = text.IndexOf(marker, StringComparison.Ordinal);
            }
        }
        return text;
    }

    private static int FindBlockEnd(string text, int from)
    {
        // A Wikipedia block runs to the next top-level section header or EOF.
        // "Full article text" is a header too (multi-source context
        // 2026-11-23): the article section follows the Wikipedia block in
        // the trend text, and stripping the top-story Wikipedia block must
        // not swallow it — the gate checks the script against that text.
        var headers = new[]
        {
            "\nWhat worked on this channel before:",
            "\nTop global news stories",
            "\nTrending YouTube titles",
            "\nBackground facts (Wikipedia)",
            "\nWikipedia context for this topic",
            "\nFull article text",
        };
        var end = text.Length;
        foreach (var h in headers)
        {
            var i = text.IndexOf(h, from, StringComparison.Ordinal);
            if (i >= 0 && i < end) end = i;
        }
        return end;
    }

    // --- narration -----------------------------------------------------------
    // Checks run on the RAW text (findings quote the original wording);
    // only the context lookups go through the normalised (lowercase) form.

    private static void CheckNarration(JsonElement scene, int n, string label, List<string> findings, string context, string contextNoCommas)
    {
        var narration = GetString(scene, "Narration") ?? "";
        CheckNarrationText(narration, n, label, GetBool(scene, "IsSpeculative"), findings, context, contextNoCommas);
    }

    private static void CheckNarrationText(string narration, int n, string label, bool isSpeculative, List<string> findings, string context, string contextNoCommas)
    {
        if (string.IsNullOrWhiteSpace(narration)) return;
        if (!isSpeculative)
            CheckNumberTokens(narration, n, label, "narration number", findings, contextNoCommas);
        CheckEntities(narration, n, label, "narration names", findings, context);
        // Re-check exemption (fact-gate rewrite, 2026-11-23): a scene marked
        // IsSpeculative whose narration carries a recognised attribution
        // phrase ("reports suggest", "people are discussing", "the theory
        // going around is", "it remains unclear", ... — see
        // FactGateRewrite.AttributionPhrases) passes the attribution check:
        // the attribution + the on-screen SPECULATION label IS the
        // traceability, because the claim is no longer presented as a
        // verified fact. The exemption is deliberately narrow — it does NOT
        // cover invented entities (checked above: a rumour about a real
        // thing is fine, an invented name is still fabrication) and it does
        // NOT cover attributions to a NAMED PERSON (rule 3: a labelled
        // rumour about a person is still an unverified claim about them,
        // so that finding is kept and the rewrite drops the sentence).
        var attrFindings = new List<string>();
        CheckAttributions(narration, n, label, attrFindings, context);
        if (isSpeculative && FactGateRewrite.ContainsAttributionPhrase(narration))
            attrFindings.RemoveAll(f => !FactGateRewrite.IsNamedPersonAttribution(f));
        findings.AddRange(attrFindings);
    }

    // --- visualSpec ----------------------------------------------------------

    private static void CheckVisualSpec(JsonElement spec, int n, DateTime prepDate, List<string> findings, string context, string contextNoCommas)
    {
        // Art scenes carry an empty VisualSpec object from serialisation
        // (Kind ""); that is not a visual and has nothing to check.
        var kind = GetString(spec, "Kind");
        if (string.IsNullOrWhiteSpace(kind)) return;

        var source = GetString(spec, "Source") ?? "";
        if (!string.IsNullOrWhiteSpace(source))
            CheckSource(source, n, kind, prepDate, findings, context);

        // Numbers: chart values plus every number token in the titles,
        // labels, headers, rows and callout text.
        if (GetArray(spec, "Values") is { } values)
            foreach (var v in values)
            {
                if (v.ValueKind != JsonValueKind.Number) continue;
                var token = v.GetRawText().Replace(",", "").TrimEnd('%');
                if (token.Length <= 1) continue;
                if (!NumberInContext(token, contextNoCommas))
                    findings.Add($"scene {n} [UntraceableNumber]: visual \"{kind}\" value \"{v.GetRawText()}\" does not appear in the supplied trend context");
            }

        var texts = new List<string?>();
        texts.Add(GetString(spec, "Title"));
        texts.Add(GetString(spec, "Text"));
        texts.Add(GetString(spec, "Subtext"));
        texts.Add(GetString(spec, "Unit"));
        texts.AddRange(GetStrings(spec, "Labels"));
        texts.AddRange(GetStrings(spec, "Headers"));
        if (GetArray(spec, "Rows") is { } rows)
            foreach (var row in rows)
                if (row.ValueKind == JsonValueKind.Array)
                    foreach (var cell in row.EnumerateArray())
                        texts.Add(cell.ValueKind == JsonValueKind.String ? cell.GetString() : null);
        foreach (var t in texts)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            CheckNumberTokens(t, n, $"scene {n}", $"visual \"{kind}\" number", findings, contextNoCommas);
            CheckEntities(t, n, $"scene {n}", $"visual \"{kind}\" names", findings, context);
            CheckQuotedAttribution(t, n, kind, findings, context);
        }
    }

    private static void CheckSource(string source, int n, string kind, DateTime prepDate, List<string> findings, string context)
    {
        var norm = WikipediaClient.NormalizeForMatch(source);
        var label = $"scene {n}";
        var verbatimInContext = context.Contains(norm);

        CheckSourceDates(norm, n, kind, prepDate, findings);

        // "Internal" sources are never traceable unless the exact source
        // string was supplied in the context (it never is).
        if (norm.Contains("internal") && !verbatimInContext)
        {
            findings.Add($"{label} [InternalSource]: visual \"{kind}\" source \"{source}\" is an internal source that was not in the supplied trend context");
            return; // already flagged; the outlet check below would only repeat it
        }
        if (norm.Contains("press release") && !verbatimInContext)
        {
            findings.Add($"{label} [UntraceableSource]: visual \"{kind}\" source \"{source}\" is a press release that was not in the supplied trend context");
            return;
        }

        // Outlet traceability: the outlet (source up to the first comma,
        // trailing date stripped) must appear in the context, or the full
        // source string must.
        var outlet = norm.Split(',')[0].Trim();
        outlet = Regex.Replace(outlet, @"\s*(20\d\d-\d\d-\d\d|q[1-4]\s*20\d\d|,?\s*20\d\d)\s*$", "");
        if (outlet.Length >= 3 && !context.Contains(outlet) && !verbatimInContext)
            findings.Add($"{label} [UntraceableSource]: visual \"{kind}\" source \"{source}\" names outlet \"{outlet}\", which does not appear in the supplied trend context");
    }

    private static readonly Regex IsoDateRegex = new(@"\b20\d\d-\d\d-\d\d\b", RegexOptions.Compiled);
    private static readonly Regex MonthDateRegex = new(
        @"\b(january|february|march|april|may|june|july|august|september|october|november|december|jan\.?|feb\.?|mar\.?|apr\.?|jun\.?|jul\.?|aug\.?|sep\.?|sept\.?|oct\.?|nov\.?|dec\.?)\s+(\d{1,2}),?\s+(20\d\d)\b",
        RegexOptions.Compiled);
    private static readonly Regex BareYearRegex = new(@"\b(20\d\d)\b", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> Months = new(StringComparer.Ordinal)
    {
        ["january"] = 1, ["jan"] = 1, ["february"] = 2, ["feb"] = 2, ["march"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4, ["may"] = 5, ["june"] = 6, ["jun"] = 6, ["july"] = 7, ["jul"] = 7,
        ["august"] = 8, ["aug"] = 8, ["september"] = 9, ["sep"] = 9, ["sept"] = 9, ["october"] = 10,
        ["oct"] = 10, ["november"] = 11, ["nov"] = 11, ["december"] = 12, ["dec"] = 12,
    };

    private static void CheckSourceDates(string normSource, int n, string kind, DateTime prepDate, List<string> findings)
    {
        var label = $"scene {n}";
        var prep = DateOnly.FromDateTime(prepDate);
        foreach (Match m in IsoDateRegex.Matches(normSource))
        {
            if (DateOnly.TryParse(m.Value, out var d) && d > prep)
                findings.Add($"{label} [FutureDatedSource]: visual \"{kind}\" source is dated {m.Value}, after the prep date {prep:yyyy-MM-dd}");
        }
        foreach (Match m in MonthDateRegex.Matches(normSource))
        {
            var monthName = m.Groups[1].Value.TrimEnd('.');
            if (!Months.TryGetValue(monthName, out var month)) continue;
            var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var day = Math.Min(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), 28);
            if (new DateOnly(year, month, day) > prep)
                findings.Add($"{label} [FutureDatedSource]: visual \"{kind}\" source is dated {m.Value}, after the prep date {prep:yyyy-MM-dd}");
        }
        foreach (Match m in BareYearRegex.Matches(normSource))
        {
            if (int.TryParse(m.Value, out var year) && year > prep.Year)
                findings.Add($"{label} [FutureDatedSource]: visual \"{kind}\" source is dated {m.Value}, after the prep date {prep:yyyy-MM-dd}");
        }
    }

    // --- shared token checks -------------------------------------------------

    // Ends with a digit (or a decimal/percent), so "2019," in "back in
    // 2019, long before" matches as "2019", not "2019," — while "1,000"
    // still matches whole.
    private static readonly Regex NumberTokenRegex = new(@"\d(?:[\d,]*\d)?(?:\.\d+)?%?", RegexOptions.Compiled);

    private static void CheckNumberTokens(string text, int n, string label, string what, List<string> findings, string contextNoCommas)
    {
        foreach (Match m in NumberTokenRegex.Matches(text))
        {
            var raw = m.Value;
            // Clock times ("23:19") and durations ("24-hour", "24/7") are
            // not factual claims.
            var before = m.Index > 0 ? text[m.Index - 1] : '\0';
            var afterIdx = m.Index + m.Length;
            var after = afterIdx < text.Length ? text[afterIdx] : '\0';
            if (before == ':' || after == ':') continue;
            if (afterIdx + 5 <= text.Length && text.AsSpan(afterIdx, 5).Equals("-hour", StringComparison.OrdinalIgnoreCase)) continue;
            if (after == '/' && afterIdx + 1 < text.Length && text[afterIdx + 1] == '7') continue;
            var token = raw.Replace(",", "").TrimEnd('%');
            if (token.Length <= 1) continue; // single digits ("GTA 6") are not claims
            if (!NumberInContext(token, contextNoCommas))
                findings.Add($"scene {n} [UntraceableNumber]: {what} \"{raw}\" does not appear in the supplied trend context ({label})");
        }
    }

    // Boundary-aware: "30" must not match "300", "2024" must not match
    // "20240". Commas are stripped on both sides ("1,000" matches "1000");
    // a "%" is ignored on both sides.
    //
    // A "." is a boundary ONLY when it is a decimal separator (i.e. it
    // sits between two digits, as in "1.50"). A sentence-ending period
    // must not block the match: the old (?![\d.]) rejected a year that
    // ended a sentence in the context ("...on 24 February 2022.") — the
    // Russian-ship dry run (2026-11-23) flagged "2022" twice even though
    // the picked Wikipedia background contains "24 February 2022", because
    // that is the only occurrence and it is followed by a period. So the
    // lookarounds block a digit, and a dot only when a digit follows it
    // (decimal) / a digit precedes it (fractional part, e.g. the "5" in
    // "3.5").
    private static bool NumberInContext(string token, string contextNoCommas) =>
        Regex.IsMatch(contextNoCommas, $@"(?<!\d)(?<!\d\.){Regex.Escape(token)}(?!\d)(?!\.\d)");

    // A word with a lowercase-to-uppercase transition inside it
    // ("HydroFlow", "LifePulse") — an invented product/engine/AI name
    // looks exactly like this, while ordinary entities ("Vice City",
    // "Rockstar Games") do not. Known brands are allowlisted; anything
    // else must appear in the context.
    private static readonly Regex CamelCaseRegex = new(@"\b[A-Za-z]*[a-z][A-Z][A-Za-z0-9]*\b", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownBrands = new(StringComparer.Ordinal)
    {
        "youtube", "tiktok", "iphone", "ipad", "macbook", "playstation", "openai", "chatgpt",
        "ebay", "whatsapp", "mcdonald", "mcdonalds", "spacex", "netflix", "spotify", "airbnb", "starbucks",
    };

    private static void CheckEntities(string text, int n, string label, string what, List<string> findings, string context)
    {
        foreach (Match m in CamelCaseRegex.Matches(text))
        {
            var token = m.Value.ToLowerInvariant();
            if (KnownBrands.Contains(token)) continue;
            if (context.Contains(token)) continue;
            findings.Add($"scene {n} [UntraceableEntity]: {what} \"{m.Value}\" appears nowhere in the supplied trend context ({label})");
        }
    }

    private static readonly Regex AccordingToRegex = new(@"according to ([^,.;:!?]{3,80})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // The subject is a short noun phrase (1-4 words, no clause punctuation):
    // a longer span would swallow a whole clause — "But the launch wasn't
    // flawless – dozens of players reported" is not an attribution to
    // "But the launch...", it is a sentence that happens to contain
    // "reported".
    private static readonly Regex SaidRegex = new(
        @"\b([A-Z][A-Za-z'’]*(?:\s+[A-Za-z'’]+){0,3})\s+(said|says|told|warned|noted|notes|note|announced|confirmed|reported|reports|claimed|claims|argued|argues)\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> GenericAttributionWords = new(StringComparer.Ordinal)
    {
        "analysts", "experts", "critics", "fans", "players", "reports", "sources", "officials",
        "researchers", "developers", "insiders", "observers", "commentators", "users", "gamers",
        "streamers", "industry", "people", "many", "some", "others", "the", "a", "an",
    };

    private static void CheckAttributions(string text, int n, string label, List<string> findings, string context)
    {
        // Apostrophes: CleanAttribution strips them from the name
        // ("O'Brien" -> "obrien") but the normalised context keeps them
        // ("o'brien"), so a name with an apostrophe could never match and
        // every O'Brien / O'Connor / D'Angelo attribution false-positived
        // (found by the multi-source COP31 dry run: Darragh O'Brien IS in
        // the Guardian article text). Check against an apostrophe-stripped
        // context too.
        var contextNoApos = context.Replace("'", "");
        foreach (Match m in AccordingToRegex.Matches(text))
        {
            var who = CleanAttribution(m.Groups[1].Value);
            if (who.Length < 3 || IsGenericAttribution(who)) continue;
            if (!context.Contains(who) && !contextNoApos.Contains(who))
                findings.Add($"scene {n} [UntraceableAttribution]: narration attributes a claim to \"{m.Groups[1].Value.Trim()}\", which does not appear in the supplied trend context ({label})");
        }
        foreach (Match m in SaidRegex.Matches(text))
        {
            var who = CleanAttribution(m.Groups[1].Value);
            if (who.Length < 3 || IsGenericAttribution(who)) continue;
            if (!context.Contains(who) && !contextNoApos.Contains(who))
                findings.Add($"scene {n} [UntraceableAttribution]: narration attributes a claim to \"{m.Groups[1].Value.Trim()}\", which does not appear in the supplied trend context ({label})");
        }
    }

    // Words the SaidRegex sweeps into the captured subject that are not
    // part of the entity name: a leading preposition/conjunction ("As
    // Guterres said" — the entity is Guterres, not "As Guterres"), and
    // trailing auxiliaries/adverbs ("United Nations has repeatedly
    // warned", "Maritime insurers have already warned", "Ukraine first
    // claimed" — the entities are United Nations / Maritime insurers /
    // Ukraine). Stripping them makes the gate check the actual entity
    // name, so an attribution to an entity that IS in the context traces
    // instead of false-positiving. This does NOT weaken the gate: an
    // invented entity still fails, because the stripped name is still not
    // in the context (e.g. "Maritime insurers" is not in the Russian-ship
    // articles, so it still fails after stripping "have already").
    private static readonly HashSet<string> AttributionLeadingFiller = new(StringComparer.Ordinal)
    {
        "as", "in", "on", "for", "with", "by", "of", "at", "to", "from", "and", "but", "so", "if", "when", "while",
    };

    private static readonly HashSet<string> AttributionTrailingFiller = new(StringComparer.Ordinal)
    {
        "has", "have", "had", "is", "are", "was", "were", "will", "would", "can", "could",
        "repeatedly", "already", "first", "also", "just", "often", "now", "today", "recently", "still", "even",
    };

    private static string CleanAttribution(string who)
    {
        var w = WikipediaClient.NormalizeForMatch(who).Replace("'s", "").Replace("'", "").Trim();
        if (w.StartsWith("the ", StringComparison.Ordinal)) w = w[4..];
        if (w.StartsWith("a ", StringComparison.Ordinal)) w = w[2..];
        var words = w.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && AttributionLeadingFiller.Contains(words[0]))
            words.RemoveAt(0);
        while (words.Count > 1 && AttributionTrailingFiller.Contains(words[^1]))
            words.RemoveAt(words.Count - 1);
        return string.Join(" ", words);
    }

    private static bool IsGenericAttribution(string who)
    {
        var words = who.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToList();
        return words.Count > 0 && words.All(w => GenericAttributionWords.Contains(w));
    }

    // A quoted line in a visual attributed to an outlet after a dash:
    // "\"The city feels alive,\" – GameSpot review". The outlet (trailing
    // "review"/"analysis"/"blog" stripped) must appear in the context.
    private static readonly Regex QuotedAttributionRegex = new(
        @"[""“”‘’'][^""“”‘’']{3,200}[""“”‘’'],?\s*[–—-]\s*([^,.;\n]{2,60})",
        RegexOptions.Compiled);

    private static readonly HashSet<string> AttributionTailWords = new(StringComparer.Ordinal)
    {
        "review", "analysis", "report", "blog", "article", "post", "coverage", "piece", "story",
    };

    private static void CheckQuotedAttribution(string text, int n, string kind, List<string> findings, string context)
    {
        // Apostrophes: same fix as CheckAttributions (2026-11-23) —
        // CleanAttribution strips them ("O'Brien" -> "obrien") but the
        // normalised context keeps them ("o'brien"), so a quoted line
        // attributed to Darragh O'Brien (who IS in the Guardian article
        // text) false-positived as an untraceable quote. Check against an
        // apostrophe-stripped context too.
        var contextNoApos = context.Replace("'", "");
        foreach (Match m in QuotedAttributionRegex.Matches(text))
        {
            var outlet = CleanAttribution(m.Groups[1].Value);
            var words = outlet.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 1 && AttributionTailWords.Contains(words[^1]))
                words.RemoveAt(words.Count - 1);
            outlet = string.Join(" ", words);
            if (outlet.Length < 3) continue;
            if (!context.Contains(outlet) && !contextNoApos.Contains(outlet))
                findings.Add($"scene {n} [UntraceableQuote]: visual \"{kind}\" quotes a line attributed to \"{m.Groups[1].Value.Trim()}\", which does not appear in the supplied trend context");
        }
    }

    // --- JSON helpers (case-insensitive: script.json is PascalCase, but the
    // check must not depend on it) -------------------------------------------

    private static JsonElement? GetObject(JsonElement el, string name)
    {
        foreach (var p in el.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object)
                return p.Value;
        return null;
    }

    private static List<JsonElement>? GetArray(JsonElement el, string name)
    {
        foreach (var p in el.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                return p.Value.EnumerateArray().ToList();
        return null;
    }

    private static string? GetString(JsonElement el, string name)
    {
        foreach (var p in el.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }

    private static bool GetBool(JsonElement el, string name)
    {
        foreach (var p in el.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True)
                return true;
        return false;
    }

    private static IEnumerable<string?> GetStrings(JsonElement el, string name)
    {
        var arr = GetArray(el, name);
        if (arr is null) return [];
        return arr.Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null);
    }
}
