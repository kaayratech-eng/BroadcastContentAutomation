// "Spotlight Now" - a daily entertainment channel (English, audience
// 14-45): fashion, games and entertainment - Bollywood, Hollywood, top
// singers and actors. Scripts tell their success stories: what's new, what
// they've been doing, praising and celebrating them, because people admire
// success stories. Renamed from "Trendline Now" 2026-11-24 (Rishi) - the
// class/file name and the internal profile Id "trendlinenow" stay as
// internal identifiers; only user-visible strings changed. Faceless by
// design: no mascot, no invented character - the story is the star.
// UsesCharacterMascot = false, so ScriptGenerator takes the
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
        ChannelName = "Spotlight Now",
        OutroDestinationText = "@spotlightnowofficial",
        OutroDestinationSpoken = "Spotlight Now",

        // NO intro sting, NO brief (Rishi 2026-11-25 13:55 EST: "we do not
        // need to use the sting at all", "just the story and direction and
        // script"). The 3.5s animated logo sting and the 10-20s teaser brief
        // that led into it are both gone — every video opens directly with
        // the hook (scene 0, the bumper scene's IntroText) and closes on the
        // "next big story" end card. The sting asset file stays on disk
        // (VideoGen/assets/spotlight-now-logo.png) but nothing renders it.
        // Shorts end the same way they always did, except the closing line
        // now sends viewers to the full story on YouTube (Rishi 2026-11-25
        // 13:56 EST) — spoken AND on the end card, vertical only.
        ShortOutroLine = "🎬 Want the full story? Watch it on YouTube — @spotlightnowofficial",
        ShortOutroSpoken = "Want the full story? Watch it on YouTube — Spotlight Now.",
        OutroLines =
        [
            "The next big story is already brewing.",
            "Follow @spotlightnowofficial so you don't miss it.",
        ],
        OutroSpoken = "The next big story is already brewing. Follow Spotlight Now so you don't miss it.",
        OutroHandles = ["YouTube", "Instagram", "TikTok", "Facebook"],
        UsesCharacterMascot = false,
        MadeForKids = false,

        AudiencePersona =
            "You write original narrated scripts for \"Spotlight Now\", a daily YouTube " +
            "entertainment channel that tells the success stories of the stars everyone " +
            "is talking about - Bollywood, Hollywood, top singers and actors, gaming " +
            "and fashion - in English, for a global audience aged 14-45. Every video " +
            "tells one star's story: where they started, how they broke through, " +
            "what's new with them, and what they're doing next - told with warmth " +
            "and admiration, because people love a success story.",

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
            "Cover the ONE biggest entertainment story today - a star's new move, a " +
            "breakthrough, a comeback, a record, a new film, album, tour or game. " +
            "Tell it as a SUCCESS STORY: the rise, the moment that changed " +
            "everything, what's new, what they're doing now, and what's next. " +
            "Praise and celebrate the star - their hard work, their talent, the " +
            "journey that got them here. The video must make a fan feel proud of " +
            "their favourite star, and make a stranger want to know them. Prefer a " +
            "story with a real human arc - humble beginnings, struggle, breakthrough " +
            "- over a dry announcement with no heart. Remember why the star is the " +
            "story: the biggest stars bring their own audience, and a video that " +
            "makes their fans feel seen is what turns those fans into OUR followers - " +
            "speak to the fans, not past them.",

        // Never called for a faceless profile (ScriptGenerator's news path never invents
        // a character) - throwing here is a tripwire if that ever changes, not a path
        // anyone is expected to hit.
        BuildCharacterInventionInstructions = (_, _) => throw new NotSupportedException(
            "Spotlight Now is faceless - it never invents a character. This should never be called."),

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

        ArtStyleSuffix = "bold editorial entertainment illustration, glamorous red-carpet and stage lighting, high contrast, photorealistic where the scene is real-world.",

        TitleGuidance =
            "Shape it as a genuine hook, not a dry label - lead with the STAR'S NAME, " +
            "then the moment or record that makes THIS telling worth clicking (e.g. " +
            "\"Shah Rukh Khan's Comeback Broke Every Record - Here's How He Did It\", " +
            "\"Taylor Swift Just Did What No Singer Has Ever Done\"). Include the " +
            "concrete subject, never a vague tease alone. The title must be " +
            "true to the facts - no clickbait claims the video cannot back up. " +
            "Write it as a title a FAN would tap to celebrate their favourite star - " +
            "the star's name first, the achievement second, the curiosity gap third.",

        // Catchy, emoji-rich description (Rishi 2026-11-25 13:42 EST: video
        // descriptions must ALWAYS be catchy and carry emojis wherever
        // possible). This guidance is slotted into ScriptGenerator's
        // description instruction as "line 1: ..." — the model writes the
        // whole description body from it, so the full body template lives
        // here, not just the hook line. Layout the model must follow
        // (ScriptGenerator already fixes line 2 as the hashtag line, and
        // Program.CanonicalHashtags reads that line-2 position as the
        // single source of truth for the Short's captions — so the hashtag
        // line MUST stay line 2, channel first):
        //   line 1: emoji hook — 1-2 emojis up front, then the star's most
        //     amazing true moment as an exclamation / bold claim. This is
        //     what shows before YouTube's "…more", so it must sell the
        //     video in one line.
        //   line 2: 7-12 hashtags on ONE line, channel first.
        //   then: "🔥 In this video:" followed by 3-4 one-line emoji
        //     bullets, one real fact per bullet — what happened, the
        //     backstory / breakthrough, why it matters, what happens next —
        //     each bullet starting with a DIFFERENT emoji that fits its fact.
        //   then: a short paragraph naming what this video is and who it is
        //     for, then the closing line "👇 Watch the full story, then tell
        //     us your favourite moment below!".
        // Every emoji sits next to a real fact from the supplied context —
        // never an emoji-only line, never invent a fact to justify an emoji,
        // and the hook stays true to the facts (SafetyFraming).
        BuildDescriptionHookGuidance = _ =>
            "a hook addressed straight to the viewer, in the channel's voice - the star's " +
            "most amazing true moment stated as an exclamation or a bold claim (\"Nobody " +
            "believed she would make it. Then she broke every record in the book.\"). " +
            "Start line 1 with 1-2 emojis that match the story (🔥 ⭐ 🚀 🎬 👑 💥) so the " +
            "hook pops before YouTube's \"…more\". After the line-2 hashtag line, add a " +
            "\"🔥 In this video:\" section with 3-4 one-line emoji bullets — one real fact " +
            "per bullet (what happened, the backstory / breakthrough, why it matters, " +
            "what happens next), each bullet starting with a DIFFERENT fitting emoji — " +
            "then the short paragraph about what the video is and who it is for, then the " +
            "closing line \"👇 Watch the full story, then tell us your favourite moment below!\". " +
            "Every emoji sits next to a real fact from the supplied context — never invent " +
            "a fact to justify an emoji.",

        AudienceDescriptorLine = "fans aged 14-45 who love Bollywood, Hollywood, music, gaming and fashion and want the stories behind their favourite stars",

        // The intro bumper is a branded title card, not a character greeting - the
        // character name/description arguments are ignored (they are empty strings for
        // a faceless profile). Scene 0 is the channel's title card with the hook.
        // TEXT-FREE (2026-11-26): the old prompt asked for large "SPOTLIGHT NOW"
        // lettering, which image models cannot spell cleanly - every prep hit the
        // image gate on scene 0 and needed a manual fix. The hook title is drawn
        // by Remotion as an animated title card, so the art itself carries no text.
        BuildIntroBumperPrompts = (_, _, _) => (
            ImagePrompt: "Dramatic entertainment opening shot: a golden spotlight " +
                "beam sweeping down across a dark navy stage, red-carpet glamour, " +
                "sparkling crowd lights in the distance, cinematic high contrast, " +
                "no text, no words, no letters.",
            MotionPrompt: "The spotlight beam sweeps slowly across the frame as the " +
                "crowd lights shimmer. Bold editorial entertainment " +
                "illustration, glamorous lighting, high contrast."
        ),

        // The introText is the video's opening hook, not a greeting - see
        // ScriptGenerator.GenerateNewsAsync, which asks for 1-2 short hook sentences.
        BuildIntroGreetingInstruction = channelName =>
            $"the video's opening hook in one or two short sentences - the star's most " +
            $"amazing true moment, stated so the viewer has to keep watching (e.g. " +
            $"\"She was rejected by every studio. Then she broke every record in the " +
            $"book. This is {channelName}.\")",

        BuildFallbackIntroText = channelName => $"The biggest star story of the day, explained. This is {channelName}.",

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

        // Trimmed (review 2026-11-22, SEO #6): 4 tags, not 7. The old list
        // burned 7 of the ~13 tag slots on near-worthless generics
        // ("what happened", "explained", "world news"), leaving room for
        // only ~5 topic-specific tags in a Short. The survivors are the
        // brand plus the three strongest generic search phrases.
        // SPOTLIGHT NOW pivot (2026-11-24): brand + Bollywood + Hollywood +
        // celebrity news - the phrases this audience actually types.
        BaseTags =
        [
            "spotlight now", "bollywood", "hollywood", "celebrity news",
        ],

        CharacterStyle =
            "Bold editorial entertainment illustration, glamorous, high contrast",

        CharacterPoolPath = "",

        // Spotlight visuals mix AI scene art with real stock footage/photos of the actual
        // star, event or place - and with data visuals (graphs/tables/callouts)
        // for the numbers. Renders through the no-Vidu Remotion stills pipeline.
        AllowsContextScenes = true,
        UsesRemotionStills = true,

        // The per-category style table - data, not code. Add a category by adding
        // one TopicStyleProfile entry in TopicCategoryStyles.cs.
        CategoryStyles = TopicCategoryStyles.All,

        // Entertainment topic strategy (SPOTLIGHT NOW pivot 2026-11-24, Rishi):
        // the YouTube trending titles are the views evidence the entertainment
        // candidates are ranked against, so the queries must cover the channel's
        // beats - Bollywood, Hollywood, celebrity news, music/singers, movies,
        // gaming, fashion - not generic news. 11 queries x 100 units + 1
        // videos.list unit = ~1,101 units per video, well inside the
        // 10,000/day free quota.
        TrendQueries =
        [
            "bollywood news", "hollywood news", "celebrity news", "entertainment news",
            "gaming news", "singer new album", "new movie trailer", "box office",
            "fashion week", "music news", "celebrity interview",
        ],

        // Copyright-free FMA pool (Rishi 2026-11-21): the old Chronicle library
        // (music-mythology) held the claimed Alex Morgan bed, so Spotlight Now
        // uses music-trendline instead. Same Windows path shape so
        // ApplyAssetMapping re-roots it identically on Linux. The pool is flat
        // (no mood subfolders), but the script's MusicMood now picks the bed:
        // VideoAssembler.PickTrackForMood matches the mood against the track
        // filenames (tense -> tense-newsroom-01/02, driving epic -> uplifting /
        // cinematic-epic, curious ambient -> calm-neutral), so the music fits
        // the story instead of being pure hash rotation. The 3 CC BY tracks
        // need a description credit when used (see assets/music-trendline/README.md).
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
        // SPOTLIGHT NOW pivot (2026-11-24, Rishi): the channel tells SUCCESS
        // STORIES - Bollywood, Hollywood, top singers and actors, gaming and
        // fashion. The structure below is modelled on the top cover channels
        // (Entertainment Tonight, TMZ, Clevver News, Bollywood Hungama,
        // Pinkvilla, Zoom): cold open at the star's peak moment, then rewind
        // to humble beginnings; fan-energy celebration, never mean gossip;
        // a re-hook at the end of every section so people keep watching.
        const string hookFirstRules = """
            - CELEBRATE THE STAR. This channel is a fan of the people it covers.
              Praise the hard work, the talent, the comeback, the record. The tone
              is warm and admiring - a fan telling a friend why this star is amazing.
              Never mean, never gossipy about anyone's private life, never tearing
              a person down for clicks. A star's struggle is told with respect, as
              the hard part of the journey, not as gossip.
            - ENTERTAINMENT FIRST. This is not a news bulletin - it is the story told
              the way fans actually want to hear it. Lead with the moment the audience
              is curious about, not the driest fact. Every beat should answer "why
              should I care?" before it answers "what happened?".
            - HOOK FIRST, always. Open with the star's biggest moment - the record,
              the comeback, the "wait, what?" moment. Never open with "today in the
              news", "welcome back", a date, or a plain summary. The first line must
              make a scroller stop.
            - FIRST 10-20 SECONDS — THE HOOK WINDOW (Rishi 2026-11-25 13:58 EST,
              refining the 2026-11-21 "most shocking TRUE fact within 5 seconds"
              rule). The story OPENS with its most entertaining part — the single
              most dramatic, entertaining peak moment of the story — and that moment
              must land inside the first 10-20 seconds (roughly the first 2-3
              scenes), not just the first sentence: front-load the entertainment
              across the whole opening window — the peak moment, the most dramatic
              image, the biggest number — before any setup or backstory. No greeting,
              no throat-clearing, no "let me explain", no "today in the news", no
              date, no plain summary. The very first sentence IS the hook, and the
              next 1-2 scenes keep the energy up — the viewer must feel the most
              entertaining thing in the first 10-20 seconds or the opening has
              failed. The opening visual must be the star at their most dramatic —
              a red-carpet moment, a stage, a record-breaking scene — not a generic
              title card. The rewind to humble beginnings starts AFTER the window,
              never inside it.
            - SOMETHING BIGGER (Rishi 2026-11-25 13:58 EST): the viewer must feel
              something BIGGER is being shared through this story — a larger
              significance unfolding, not just a headline. The opening frames the
              bigger promise: "This isn't just about [the headline] — it's about
              [the larger meaning]". The middle builds toward it WITHOUT giving it
              away: each beat adds one piece of the bigger picture, and every
              section ends on a re-hook that promises the reveal is still coming.
              The payoff lands at the end (Act 3 / "what's next"): the larger
              meaning stated plainly, so the viewer leaves feeling the story
              mattered beyond the headline. The "bigger" thing must be REAL — a
              genuine larger significance of the true story (a record that stood
              for decades, a career rewritten at an age nobody plays at, a star
              changing how their whole field works) — never an invented tease,
              never "you won't believe what happens next" without a real payoff
              behind it. If the story has no larger significance, make the peak
              moment itself the promise — do not manufacture one.
            - KEEP THEM WATCHING. Re-hook at the end of every section - a line that
              promises something bigger is coming ("...and the best part hasn't
              happened yet.", "...but nobody saw what came next."). Tease the
              breakthrough early, deliver it late. Never let a section end flat.
            - WIN THE FANS (Rishi 2026-11-24 ~23:14: the point of covering the
              biggest stars is to bring THEIR followers to our channels). Speak
              directly to the star's fans - "If you love [star], this one's for
              you" energy, not a detached report. Make a fan feel proud to be a
              fan, and make them want MORE stories like this one - that is what
              turns a viewer into a follower. End the video by inviting the
              fans in: follow Spotlight Now for a new star story every day,
              and a closing comment question that asks fans for THEIR favourite
              moment ("What is your favourite [star] moment? Tell us below.").
              The outro already says "Follow @spotlightnowofficial so you don't miss
              it." - the script must earn that follow before it is asked for.
            - Write bold and punchy: exclamations, direct address ("you"),
              vivid analogies that make the journey concrete, short punchy
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
              (a) the most amazing REAL moment of the star's story, played up
              dramatically; (b) a clearly-attributed rumor or theory ("the theory
              blowing up online is...", "people are saying...", "the wildest take
              going around..."); (c) a dramatic "what if" ("what if this actually
              happens..."). A stunned beat is ONE beat, not the whole video - the
              facts carry it.
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
            - Keep the audience in mind (14-45, global, English): fans of Bollywood,
              Hollywood, music, gaming and fashion. Explain jargon the moment it
              appears, use comparisons a teenager and a 40-year-old both get, and
              never assume the viewer already follows the star.
            """;

        // Statement-driven structure (Rishi 2026-11-21): video #1's Q&A beats
        // did not fit - too many questions. The hook is an exclamation, then
        // statements carry the story; questions at most 1-2 per video.
        const string narrativeStructure = """
            - Structure every video as a SUCCESS STORY in this order: the peak
              moment (the star at the top, right now - the record, the comeback,
              the moment everyone is talking about) -> where they started (humble
              beginnings - the small town, the first audition, the years nobody
              noticed) -> the grind (the struggle, the rejections, the years of
              hard work) -> the breakthrough (the one role, song, game or moment
              that changed everything) -> what's new (what they're doing right
              now) -> what's next.
            - Tell it NON-LINEAR: open at the PEAK, then rewind to the beginning.
              The viewer meets the star at their biggest moment first, then learns
              how they got there - that rewind is what keeps people watching. The
              first 10-20 seconds (the hook window, the first 2-3 scenes) stay on
              the peak and frame the bigger promise; the rewind starts after the
              window, never inside it.
            - Each beat is one or more scenes of STATEMENTS - declarative narration
              that carries the story forward. A beat never opens with a question and
              the video is never a question-answer chain. The final beat ends with a
              forward-looking line and the video's comment question.
            - QUESTIONS ARE RARE: at most 1-2 in the whole video, including the
              closing comment question, and only where a question genuinely hits
              harder than a statement. Never a question per beat, never a question
              opening a beat.
            - The hook itself is the peak moment. The theory / "what if" beat, if the
              video has one, lands in "what's next" - never in "what happened", which
              stays strictly factual.
            """;

        const string visualRules = """
            - Every scene is one of three kinds, and you must pick the kind that fits
              the beat:
              - "art": an AI-generated editorial illustration of the moment (imagePrompt
                in English, vivid and specific, no text in the image). The default for
                most beats.
              - "context": a real place, person, or event with no illustration - give
                a stockQuery (2-6 plain English words for a stock photo/video search,
                e.g. "tokyo street night rain"). Use this when real footage of the
                actual thing beats an illustration.
              - "visual": a data visual - a graph, a table, or a big on-screen text
                callout - with a visualSpec (see the JSON schema below). Use this ONLY
                where the beat has a real number, list, or quote worth showing.
            - DATA HONESTY (hard rule): a graph or table may ONLY chart numbers that
              appear in the news/trend context supplied with this prompt, and its
              "source" must name where the number came from. Never invent a value,
              never estimate a series, never chart a number you were not given. If the
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
            - Scene 1 opens on the hook - the star at their peak moment - not a setup.
              Ground the viewer in a concrete image or moment within the first line.
              The first 10-20 seconds are the hook window: the peak moment and the
              bigger promise land there, before any backstory.
            - Structure the 8 scenes as a tight statement-driven success story: peak
              moment, humble beginnings, the grind, the breakthrough, what's new,
              what's next - not a flat list of facts in chronological order.
            - End on the star's actual achievement and what happens next - not a
              vague "and that is the story" non-ending - then the comment question.
            """;

        var longFormContentRules = narrativeStructure + "\n" + hookFirstRules + "\n" + visualRules + """
            - Scene 1 opens on the hook - the star at their peak moment - not a setup.
              Ground the viewer in a concrete image or moment within the first line,
              since these opening scenes also double as the short teaser. The first
              10-20 seconds (the hook window, the first 2-3 scenes) stay on the peak
              moment and frame the bigger promise — the rewind to the beginning
              starts after the window, never inside it.
            - Structure the full runtime as a success story in three acts: Act 1 the
              peak moment and what happened; Act 2 the humble beginnings, the grind,
              and the breakthrough that changed everything; Act 3 what's new, why it
              matters, and what's next. The extra length exists to develop the
              journey properly, not to pad the same three beats slower.
            - End each act on a cliffhanger line that pulls the viewer into the next act.
            - The video has 1-2 stunned beats TOTAL across all three acts - Act 1 usually
              carries one; a later act only adds another if the story earns it.
            - End on the star's actual achievement and what happens next, then the
              comment question. Act 3 is where the bigger promise pays off: the larger
              meaning of the story, stated plainly.
            - Where the record is genuinely disputed or unknown, say so in the narration
              itself ("some say...", "nobody knows yet...") rather than presenting
              invention as settled fact.
            """;

        return new FormatInstructionSet(
            ContentRules: contentRules,
            ColourGuidance:
                "Bold, vivid, high-contrast colour where the story calls for it - the " +
                "category palette in the prompt leads; glamorous and warm, red-carpet " +
                "energy rather than washed out, but can still lean darker or moodier " +
                "than a cheerful palette when the story calls for it.",
            ThumbnailGuidance:
                "Name the star, give them ONE large, dramatically lit presence filling " +
                "much of the frame - a red-carpet look, a stage moment, a triumphant " +
                "expression - plus one instantly recognisable object or symbol beside " +
                "them (a trophy, a microphone, a film camera, a game controller). Bold, " +
                "saturated, high-contrast colours and lighting, cinematic composition, " +
                "no text in the image.",
            IntroThird: "a one-line hook naming the star's most amazing true moment " +
                "(\"She was rejected by every studio - then she broke every record in the book.\")",
            MusicMenu: "\"tense atmospheric\", \"curious ambient\", \"driving epic\", \"mysterious tense\"",
            HashtagExamples: "#SpotlightNow #bollywood #hollywood #entertainment",
            LongFormContentRules: longFormContentRules);
    }
}
