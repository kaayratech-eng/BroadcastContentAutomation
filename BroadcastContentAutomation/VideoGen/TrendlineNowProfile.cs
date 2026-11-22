// "Trendline Now" - a daily global trending-news explainer channel (English,
// audience 14-45). Faceless by design: no mascot, no invented character - the
// story is the star. UsesCharacterMascot = false, so ScriptGenerator takes the
// faceless news path (GenerateNewsAsync) instead of the character-anchored prompt.
// Scenes are a mix of AI scene art ("art"), real stock footage/photos ("context")
// and data visuals ("visual": graphs, tables, callouts) - see Scene.VisualSpec.
//
// Style is topic-dependent (Rishi 2026-11-21): CategoryStyles is the per-category
// style table (TopicCategoryStyles.cs) - gaming is neon and fast, finance is
// data-heavy with charts, politics is newsroom-serious with timelines. The script
// prompt injects the resolved category's block; TTS pacing comes from the
// category too. Voice is DavisNeural (kept 2026-11-21 - already the deepest
// Azure English voice), with per-scene mood styles OFF (Rishi 2026-11-17:
// variations sound poor; the "interesting" quality comes from the script text,
// not mood tags - NarrationMoodStyles stays empty on purpose).
static class TrendlineNowProfile
{
    public static readonly ContentProfile Value = new()
    {
        Id = "trendlinenow",
        ChannelName = "Trendline Now",
        OutroDestinationText = "@trendlinenow",
        OutroDestinationSpoken = "Trendline Now",

        // Branded intro sting + outro card (Rishi 2026-11-21): every video opens
        // with the 3.5s animated logo sting and closes on the "next big story"
        // end card. The logo file lives in VideoGen/assets (copied from the
        // brand folder) so ApplyAssetMapping's last-segment re-root finds it.
        IntroLogoPath = "trendlinenow-logo.png",
        IntroSeconds = 3.5,
        OutroLines =
        [
            "The next big story is already brewing.",
            "Follow @trendlinenow so you don't miss it.",
        ],
        OutroSpoken = "The next big story is already brewing. Follow Trendline Now so you don't miss it.",
        OutroHandles = ["YouTube", "Instagram", "TikTok", "Facebook"],
        UsesCharacterMascot = false,
        MadeForKids = false,

        AudiencePersona =
            "You write original narrated scripts for \"Trendline Now\", a daily YouTube news " +
            "channel that explains the single biggest trending story in the world - in English, " +
            "for a global audience aged 14-45. Every video explains one thing: what " +
            "everyone is talking about today, and why it matters?",

        SafetyFraming =
            "Accuracy is the whole product. Use ONLY facts from the news/trend context " +
            "supplied with this prompt - real headlines, real numbers, real quotes. Never " +
            "invent a fact, a number, a date, a quote, or a source. If the context does not " +
            "give you a number, do not chart it and do not state it - say what IS known " +
            "instead. Attribute claims plainly (\"according to...\", \"reported by...\"). " +
            "No partisan spin: present what happened and what each side says. No graphic " +
            "descriptions of violence or death - state what happened and its consequence. " +
            "No sexual content. A hook must be true to the facts - interesting comes from " +
            "the angle and the writing, never from exaggeration or a misleading tease. " +
            "Never state an unverified fact about a named person as fact - you may " +
            "criticise a public figure's public actions, but do not invent private " +
            "misconduct, a crime, or a quote about any identifiable person. Speculation " +
            "is only ever an attributed theory or a question (\"the theory going around " +
            "is...\", \"people are saying...\", \"what if...\") and every such scene is " +
            "marked isSpeculative: true so the video labels it on screen.",

        TopicGuidance =
            "Cover the ONE biggest trending story in the world today - the story with the " +
            "most momentum across global trends and news, whichever country it comes from. " +
            "Prefer a story with a real twist, a surprising backstory, or a clear \"wait, " +
            "what?\" moment over a dry event with no angle. The video must explain the story " +
            "to someone who has only seen the headline and is confused about why everyone " +
            "is talking about it.",

        // Never called for a faceless profile (ScriptGenerator's news path never invents
        // a character) - throwing here is a tripwire if that ever changes, not a path
        // anyone is expected to hit.
        BuildCharacterInventionInstructions = (_, _) => throw new NotSupportedException(
            "Trendline Now is faceless - it never invents a character. This should never be called."),

        Formats = [BuildFormat()],

        // House palette; the resolved topic category's palette is layered on top by the
        // news prompt (see ScriptGenerator.GenerateNewsAsync), so a finance video reads
        // muted blues/greens while a gaming video goes neon - same channel, different
        // energy per story.
        ColourRule =
            "The palette is the channel's house look - dark navy with a red rising-trend " +
            "accent, white type - unless the topic category's palette (supplied in the " +
            "prompt) overrides it for this video. Avoid plain flat white, black or grey " +
            "voids; keep real contrast so the frame reads at phone size.",

        ArtStyleSuffix = "bold editorial news illustration, cinematic lighting, high contrast, photorealistic where the scene is real-world.",

        TitleGuidance =
            "Shape it as a genuine hook, not a dry label - name the story, then the angle " +
            "that makes THIS telling worth clicking (e.g. \"GTA 6's Map Is Bigger Than " +
            "You Think - Here's the Proof\", \"The Bank That Vanished Overnight\"). " +
            "Include the concrete subject, never a vague tease alone. The title must be " +
            "true to the facts - no clickbait claims the video cannot back up.",

        BuildDescriptionHookGuidance = _ =>
            "a hook addressed straight to the viewer, in the channel's voice - the story's " +
            "most surprising true angle stated as an exclamation or a bold claim (\"Everyone " +
            "is talking about this bank. Here's what actually happened to it.\").",

        AudienceDescriptorLine = "people aged 14-45 who want to understand what's trending and why it matters",

        // The intro bumper is a branded title card, not a character greeting - the
        // character name/description arguments are ignored (they are empty strings for
        // a faceless profile). Scene 0 is the channel's title card with the hook.
        BuildIntroBumperPrompts = (_, _, _) => (
            ImagePrompt: "Bold news title card for the channel \"Trendline Now\": large white " +
                "\"TRENDLINE NOW\" lettering on a dark navy background, a red rising trend " +
                "line sweeping diagonally across, clean modern broadcast-news design, " +
                "no other text.",
            MotionPrompt: "The title card holds steady as the red trend line draws itself " +
                "upward across the frame. Bold editorial news illustration, cinematic " +
                "lighting, high contrast."
        ),

        // The introText is the video's opening hook, not a greeting - see
        // ScriptGenerator.GenerateNewsAsync, which asks for 1-2 short hook sentences.
        BuildIntroGreetingInstruction = channelName =>
            $"the video's opening hook in one or two short sentences - the story's most " +
            $"surprising true angle, stated so the viewer has to keep watching (e.g. " +
            $"\"A bank vanished overnight. And nobody can agree on where the money went. " +
            $"This is {channelName}.\")",

        BuildFallbackIntroText = channelName => $"The biggest story of the day, explained. This is {channelName}.",

        CharacterPortraitStyleSuffix = "no text, no words, no letters.",

        VoiceOverride = new Dictionary<string, (string Locale, string Voice, string? Style)>
        {
            // DavisNeural, kept 2026-11-21: the deepest/heaviest Azure English voice
            // (the Chronicle narrator). Style is null on purpose - per-scene mood
            // variation is OFF for this channel, so narration stays plain and
            // consistent; the "interesting" quality comes from the script text.
            ["en"] = ("en-US", "en-US-DavisNeural", null),
        },

        // Deliberately empty: NarrationMoodStyles drives ScriptGenerator's per-scene
        // mood tagging, and Rishi disabled mood variation (2026-11-17) because the
        // express-as variations sound poor. Empty here means the model is never
        // asked for a mood tag at all.
        NarrationMoodStyles = new Dictionary<string, IReadOnlyList<string>>(),

        BaseTags =
        [
            "trendline now", "trending news", "news explained", "world news",
            "breaking news", "what happened", "explained",
        ],

        CharacterStyle =
            "Bold editorial news illustration, cinematic, high contrast",

        CharacterPoolPath = "",

        // News visuals mix AI scene art with real stock footage/photos of the actual
        // event, place or people - and with data visuals (graphs/tables/callouts)
        // for the numbers. Renders through the no-Vidu Remotion stills pipeline.
        AllowsContextScenes = true,
        UsesRemotionStills = true,

        // The per-category style table - data, not code. Add a category by adding
        // one TopicStyleProfile entry in TopicCategoryStyles.cs.
        CategoryStyles = TopicCategoryStyles.All,

        TrendQueries =
        [
            "breaking news explained", "world news today", "trending news story",
            "news explained", "viral news story", "what happened today",
        ],

        // Copyright-free FMA pool (Rishi 2026-11-21): the old Chronicle library
        // (music-mythology) held the claimed Alex Morgan bed, so Trendline Now
        // uses music-trendline instead. Same Windows path shape so
        // ApplyAssetMapping re-roots it identically on Linux. The pool is flat
        // (no mood subfolders), so any script mood falls back to the whole
        // pool and PickTrack chooses by job-folder hash — a different track
        // from the previous video's bed in practice, and always from this
        // copyright-free pool. The 3 CC BY tracks need a description credit
        // when used (see assets/music-trendline/README.md).
        BackgroundMusicPath = @"D:\Business\BroadcastContentAutomation\VideoGen\assets\music-trendline",
        BackgroundMusicLufs = -34.0,
    };

    private static ContentFormatDef BuildFormat() =>
        // Pacing (Rishi 2026-11-21 23:26): video #1 was too slow. TTS rate
        // -10% -> 0% then -> +10% (see below), PostNarrationHoldSeconds
        // 1.5 -> 0.75s (see VideoAssembler.cs). Applies from video #3 onward;
        // video #2 was already rendered at the old pace.
        // Rate calibrated to Rishi's reference (TikTok @untldfcts, Boriska
        // Mars boy, 82s, ~170 wpm rapid-fire, new visual every 4-5s, no dead
        // air): DavisNeural at +10% ≈ 165-175 wpm, matching the reference.
        new("newsExplainer", 100, false, "+10%", "-4%", "clear, punchy, news-anchor",
            "curious ambient", Instructions: FormatInstructions());

    private static FormatInstructionSet FormatInstructions()
    {
        // Hook-first, unusual, attention-grabbing writing (Rishi 2026-11-21 01:12)
        // + entertainment-first (Rishi 2026-11-21 01:25): the video must feel like
        // something you'd stop scrolling for - what PEOPLE WANT TO HEAR, not a dry
        // recitation. Real facts are the backbone; 1-2 stunned beats (a shocking
        // real angle, an attributed wild theory, a marked "what if") keep people
        // listening. The hook must be TRUE to the facts - interesting comes from
        // the angle and the writing, not exaggeration. Per-scene mood tags stay OFF;
        // the energy lives in the script text itself.
        const string hookFirstRules = """
            - ENTERTAINMENT FIRST. This is not a news bulletin - it is the story told
              the way people actually want to hear it. Lead with the angle the audience
              is curious about, not the driest fact. Every beat should answer "why
              should I care?" before it answers "what happened?".
            - HOOK FIRST, always. Open with the most surprising, curious, or dramatic
              TRUE angle of the story - the twist, the irony, the "wait, what?" moment.
              Never open with "today in the news", "welcome back", a date, or a plain
              summary. The first line must make a scroller stop.
            - FIRST 5 SECONDS (Rishi 2026-11-21: video #1's opening did not catch
              viewers' interest). The viewer must hear the single most shocking
              TRUE fact within the first 5 seconds - no setup, no greeting, no
              throat-clearing, no "let me explain". The very first sentence IS the
              hook: a bold statement of the most surprising thing that happened,
              phrased so the viewer has to keep watching to find out what it means.
              If the opening does not create a "wait, what?" reaction in 5 seconds,
              it has failed - rewrite it. The opening visual must be the story's
              most dramatic image, not a generic title card.
            - Write bold and punchy: exclamations, direct address ("you"),
              vivid analogies that make an abstract story concrete, short punchy
              sentences mixed with longer ones, dramatic reveals, and a cliffhanger
              at the end of each section that pulls the viewer into the next one
              ("...and that's when it gets weird.").
            - STATEMENTS carry the story, not questions (Rishi 2026-11-21: the
              Q&A format did not fit - too many questions). The narration is a
              chain of declarative statements that pull the viewer forward, not
              a lecture and not a question-answer chain. A question is used only
              where it genuinely hits harder than a statement - at most 1-2 in
              the whole video, including the closing comment question.
            - Find the unexpected framing where the story supports it: the irony, the
              reversal, the detail nobody is talking about - and lead with it. If the
              story has no twist, lead with the sharpest true stakes instead.
            - THE MIX (1-2 stunned beats per video, on a backbone of real facts):
              the core events, verified details and numbers are stated as facts and
              stay accurate. On top of that backbone, include 1-2 stunned beats -
              (a) the most shocking REAL angle of the story, played up dramatically;
              (b) a clearly-attributed rumor or theory ("the theory blowing up online
              is...", "people are saying...", "the wildest take going around...");
              (c) a dramatic "what if" ("what if this actually happens..."). A
              stunned beat is ONE beat, not the whole video - the facts carry it.
            - ATTRIBUTE EVERY THEORY, MARK EVERY SPECULATION. A rumor or theory is
              never stated as fact: always name who is saying it ("online", "fans",
              "critics", "some analysts") and say the word theory/rumor in the
              narration. Speculation is always marked ("what if...", "nobody knows
              yet, but imagine..."). Never present a guess as an established fact.
              Every speculative scene also sets "isSpeculative": true in its JSON -
              the renderer reads this flag and puts a SPECULATION label on screen
              for that scene - a theory/rumor/"what if" scene with the flag missing
              is a defect, not a style choice.
            - NAMED PEOPLE (hard rule): never assert an unverified fact about a
              named person. Criticising a public figure's public actions is fair
              game; inventing private misconduct, a crime, or a quote about them
              is not. If a claim about a person is not in the supplied facts, do
              not state it as fact - attribute it ("critics say...") or leave it out.
              The stunned effect only works if the audience believes the channel -
              one false claim stated as fact breaks trust, turns the comments
              against the video, and risks the platforms flagging or demonetising
              the channel, which defeats the whole monetisation goal.
            - Speculation beats are NARRATION ONLY: never a "visual" scene, never
              a charted number, never a callout presented as a figure. A theory
              gets words on screen at most as a quote of what people are saying,
              clearly attributed - it never gets a graph.
            - Credible, not clickbait: every hook, claim and number must be true to
              the facts supplied. Never invent, never exaggerate, never tease something
              the video does not deliver. If a detail is unconfirmed, say so in the
              narration ("nobody knows yet...") rather than presenting a guess as fact.
            - Keep the audience in mind (14-45, global, English): explain jargon the
              moment it appears, use comparisons a teenager and a 40-year-old both
              get, and never assume the viewer already follows the story.
            """;

        // Statement-driven structure (Rishi 2026-11-21): video #1's Q&A beats
        // did not fit - too many questions. The hook is an exclamation, then
        // statements carry the story; questions at most 1-2 per video.
        const string narrativeStructure = """
            - Structure every video as statement-driven beats in this order: the hook
              (the most surprising true angle, as an exclamation - "What happened!" -
              or a bold claim) -> what happened (the facts, plainly) -> the
              backstory (the history that explains how we got here) -> what's
              happening now (the latest development) -> why it matters (what it
              means for the viewer) -> what happens next.
            - Each beat is one or more scenes of STATEMENTS - declarative narration
              that carries the story forward. A beat never opens with a question and
              the video is never a question-answer chain. The final beat ends with a
              forward-looking line and the video's comment question.
            - QUESTIONS ARE RARE: at most 1-2 in the whole video, including the
              closing comment question, and only where a question genuinely hits
              harder than a statement. Never a question per beat, never a question
              opening a beat.
            - The hook itself is usually stunned beat (a) - the shocking real angle.
              The theory / "what if" beat, if the video has one, lands in "why it
              matters" or "what happens next" - never in "what happened", which stays
              strictly factual.
            """;

        const string visualRules = """
            - Every scene is one of three kinds, and you must pick the kind that fits
              the beat:
              - "art": an AI-generated editorial illustration of the moment (imagePrompt
                in English, vivid and specific, no text in the image). The default for
                most beats.
              - "context": a real place, person, or event with no illustration - give
                a stockQuery (2-6 plain English words for a stock photo/video search,
                e.g. "tokyo street night rain", "stock market trading floor screens").
                Use this when real footage of the actual thing beats an illustration.
              - "visual": a data visual - a graph, a table, or a big on-screen text
                callout - with a visualSpec (see below). Use this ONLY where the beat
                has a real number, list, or quote worth showing on screen.
            - visualSpec is a JSON object, one of:
              - {"kind":"line","title":"...","labels":["...","..."],"values":[1,2],
                "unit":"...","source":"..."} - a trend line (search interest, price,
                poll, count over time). labels and values must be the same length.
              - {"kind":"bar","title":"...","labels":["..."],"values":[1],"unit":"...",
                "source":"..."} - a comparison bar chart.
              - {"kind":"table","title":"...","headers":["...","..."],"rows":[["...","..."]],
                "source":"..."} - key figures, a comparison, or a timeline (headers
                like ["Date","What happened"]).
              - {"kind":"callout","text":"...","subtext":"..."} - one big on-screen
                line: a real quote, a key number, or the one sentence nobody should
                miss. subtext is a short attribution or context line.
            - DATA HONESTY (hard rule): a graph or table may ONLY chart numbers that
              appear in the news/trend context supplied with this prompt, and its
              "source" must name where the number came from (e.g. "Google Trends",
              "company earnings", the news outlet). Never invent a value, never
              estimate a series, never chart a number you were not given. If the
              context gives you no numbers, do not force a chart - use a callout or
              an art/context scene instead. A callout's text must be a real quote or
              a real reported figure, never a paraphrase presented as a quote.
              A theory, rumor, or "what if" is NEVER a visual scene - no chart, no
              table, no callout number for something unconfirmed.
            - Do not use "visual" for more than a third of the scenes, and never for
              scene 1 (the hook opens on a striking image, not a chart).
            - Keep the subject centred and clear of the top and bottom thirds so the
              same art works cropped to both 16:9 and 9:16, no text in the image.
            """;

        var contentRules = narrativeStructure + "\n" + hookFirstRules + "\n" + visualRules + """
            - Scene 1 opens on the hook - the most surprising true angle - not a setup.
              Ground the viewer in a concrete image or moment within the first line.
            - Structure the 8 scenes as a tight statement-driven arc: hook, what happened,
              backstory, what's happening now, why it matters, what happens next -
              not a flat list of facts in chronological order.
            - End on the story's actual consequence and what happens next - not a
              vague "and that is the story" non-ending - then the comment question.
            """;

        var longFormContentRules = narrativeStructure + "\n" + hookFirstRules + "\n" + visualRules + """
            - Scene 1 opens on the hook - the most surprising true angle - not a setup.
              Ground the viewer in a concrete image or moment within the first line,
              since these opening scenes also double as the short teaser.
            - Structure the full runtime as a statement-driven arc in three acts: Act 1 the
              hook and what happened; Act 2 the backstory and the context that
              explains it; Act 3 what's happening now, why it matters, and what
              happens next. The extra length exists to develop the story properly -
              more evidence, more of the backstory, the competing explanations where
              they exist - not to pad the same three beats slower.
            - End each act on a cliffhanger line that pulls the viewer into the next
              act ("...and that's when it gets weird.", "But the real story starts
              ten years earlier.").
            - The video has 1-2 stunned beats TOTAL across all three acts (see the
              mix rule above) - Act 1 usually carries one; a later act only adds
              another if the story earns it. Do not add a fresh theory beat in
              every act.
            - End on the story's actual consequence and what happens next - with real
              space to make the case, not a rushed closing line - then the comment
              question.
            - Where the record is genuinely disputed or unknown, say so in the
              narration itself ("some say...", "nobody knows yet...") rather than
              presenting invention as settled fact - a long-form telling has room to
              briefly weigh competing versions where the short cannot.
            """;

        return new FormatInstructionSet(
            ContentRules: contentRules,
            ColourGuidance:
                "Bold, vivid, high-contrast colour where the story calls for it - the " +
                "category palette in the prompt leads; cinematic and striking rather " +
                "than washed out, but can still lean darker or moodier than a cheerful " +
                "palette when the story calls for it.",
            ThumbnailGuidance:
                "Name the story's key figure, place, or moment, give it ONE large, " +
                "dramatically lit subject filling much of the frame with an intense, " +
                "striking expression or a jaw-dropping scene, plus one instantly " +
                "recognisable object or symbol beside it. Bold, saturated, high-contrast " +
                "colours and lighting, cinematic composition, no text in the image.",
            IntroThird: "a one-line hook naming the story's most surprising true angle " +
                "(\"A bank vanished overnight - and nobody can agree where the money went.\")",
            MusicMenu: "\"tense atmospheric\", \"curious ambient\", \"driving epic\", \"mysterious tense\"",
            HashtagExamples: "#trending #news #explained",
            LongFormContentRules: longFormContentRules);
    }
}
