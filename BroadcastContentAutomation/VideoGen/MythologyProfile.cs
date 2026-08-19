// "Chronicle & Chaos" - an adult-directed (18-34) animated mythology, history and folklore
// storytelling channel. Deliberately NOT "Made for Kids": no cute mascot energy, no
// sing-along choruses, no nursery-rhyme cadence. Runs alongside GiggleGardenProfile, which
// stays untouched - see ContentProfileRegistry for how --profile picks one.
static class MythologyProfile
{
    public static readonly ContentProfile Value = new()
    {
        Id = "chronicleandchaos",
        ChannelName = "Chronicle & Chaos",
        OutroDestinationText = "@chronicleandchaoshq",
        OutroDestinationSpoken = "Chronicle and Chaos",
        UsesCharacterMascot = true,
        MadeForKids = false,

        AudiencePersona =
            "You write original narrated scripts for \"Chronicle & Chaos\", an animated YouTube " +
            "channel retelling myths, legends, folklore and true historical origin stories for an " +
            "adult audience (18-34), in a cinematic documentary storytelling voice.",

        SafetyFraming =
            "This channel is written for adults, not children, and is NOT eligible for YouTube's " +
            "\"Made for Kids\" classification - do not soften content into a kids' tone, and do not " +
            "use any of the child-directed appeal signals (bright primary-colour cartoon mascots, " +
            "sing-along choruses, nursery-rhyme cadence, a narrator addressing \"boys and girls\") " +
            "YouTube's kid-content classifier looks for. Mature themes - death, war, betrayal, " +
            "sacrifice, tragedy, the supernatural - are allowed and often the point of the story, " +
            "but keep depictions of violence and death non-graphic: describe consequence and " +
            "emotion, not gore, wounds or torture in clinical detail. No sexual content. If a " +
            "myth's original source material includes something YouTube would flag (incest, " +
            "extreme violence, explicit sexuality), tell the story around it rather than depicting " +
            "it directly - the historical or mythological fact can be stated, it does not need to " +
            "be shown.",

        TopicGuidance =
            "Choose ONE myth, legend, folk tale, or true historical origin story - Greek, Norse, " +
            "Egyptian, Mesopotamian, Celtic, African, Asian, Indigenous American or other world " +
            "mythology/folklore, or a pivotal moment of real history told as a story (an empire's " +
            "fall, a legendary figure's rise, the origin of a famous custom or place). Prefer a " +
            "story with a clear dramatic arc - a want, an obstacle, a turning point, a consequence " +
            "- over a dry list of facts. A well-known myth retold with a genuinely fresh angle (an " +
            "overlooked character's point of view, the version of the story most retellings leave " +
            "out, what modern historians now think really happened) beats a generic retelling.",

        BuildCharacterInventionInstructions = (langName, avoidLine) => $"""
            This video is about ONE central figure from the myth, legend, or history you choose -
            a god, hero, monarch, warrior, trickster, or ordinary person swept up in extraordinary
            events. Do NOT invent a fictional character standing outside the story to narrate it;
            the figure IS the story's actual protagonist or central subject.
            {avoidLine}
            Return it as:
            - characterName: the figure's name as it is actually known (e.g. "Anubis", "Boudica",
              "Anansi"), written in the Latin alphabet even when the narration is not - it goes
              into the English image and animation prompts, and a name in another script there
              gets ignored or garbled. Spell it in {langName} inside the narration itself.
            - characterDescription: ONE English sentence describing exactly how it LOOKS - build,
              face, clothing/armour/regalia appropriate to their culture and era, and one or two
              distinguishing features (a weapon, a headdress, a mark, an animal companion). This
              exact sentence is used to draw the picture that every scene animates from, so it
              must be concrete, visual, and historically/culturally grounded rather than generic
              fantasy armour. No personality, no backstory, no actions - appearance only.
            """,

        Formats = BuildFormats(),

        ColourRule =
            "The palette should match the story's mood rather than always being cheerful - name a " +
            "specific colour palette in the sentence itself (e.g. \"desaturated bronze and shadow " +
            "palette\", \"cold moonlit blues\", \"warm firelit ochre and red\") - avoid plain flat " +
            "white, black or grey voids, but do not force bright cheerful colour where the scene " +
            "calls for something darker or moodier.",

        ArtStyleSuffix = "painterly illustrated animation, dramatic lighting, semi-realistic proportions, character design stays consistent.",

        TitleGuidance =
            "Shape it as a genuine hook, not a dry label - name the figure or event, then the " +
            "angle that makes THIS telling worth clicking (e.g. \"Anubis: The God Egypt Was Too " +
            "Afraid to Paint\", \"The Real Trojan Horse - What Actually Happened\"). Include the " +
            "concrete subject, never a vague tease alone (nobody searches for a mystery with no name).",

        BuildDescriptionHookGuidance = isCalm =>
            "a hook addressed straight to the viewer, in a cinematic documentary voice - a bold " +
            "claim, a striking question, or the story's central tension stated plainly (\"The god " +
            "even the pharaohs refused to name...\", \"What really happened the night Troy fell?\")." +
            (isCalm
                ? " Keep this line's energy grave and atmospheric rather than punchy - this format " +
                  "is a slower, more cinematic telling."
                : ""),

        AudienceDescriptorLine = "adults who enjoy mythology, history and folklore storytelling",

        BuildIntroBumperPrompts = (characterName, characterDescription, isCalm) => isCalm
            ? (
                ImagePrompt: $"{characterName} standing in shadow and firelight, looking directly toward the viewer, painterly dramatic lighting, muted moody palette.",
                MotionPrompt: $"{characterName} ({characterDescription}) turns slowly to face the viewer, still and imposing, eyes locking onto camera as embers or dust drift past. Painterly dramatic lighting, muted moody palette. " +
                    "painterly illustrated animation, dramatic lighting, semi-realistic proportions, character design stays consistent."
              )
            : (
                ImagePrompt: $"{characterName} standing in dramatic light, looking directly toward the viewer, painterly cinematic palette.",
                MotionPrompt: $"{characterName} ({characterDescription}) steps forward and turns to face the viewer, commanding and still, a subtle gust moving cloth or hair. Painterly cinematic palette. " +
                    "painterly illustrated animation, dramatic lighting, semi-realistic proportions, character design stays consistent."
              ),

        BuildIntroGreetingInstruction = channelName =>
            $"a cold-open hook line naming tonight's story in one dramatic sentence, in the voice " +
            $"of a documentary narrator - NOT a \"Welcome to {channelName}!\" greeting; the " +
            "channel's name is spoken once, briefly, folded into the hook rather than as its own " +
            $"separate greeting (e.g. \"Tonight on {channelName}: the god the Egyptians feared to name.\")",

        BuildFallbackIntroText = channelName => $"Tonight on {channelName}: a story history almost forgot.",

        CharacterPortraitStyleSuffix =
            "digital painting, dramatic chiaroscuro lighting, semi-realistic anatomy, period-accurate " +
            "attire, no text, no words, no letters.",

        VoiceOverride = new Dictionary<string, (string Locale, string Voice, string? Style)>
        {
            // Verified live against Azure's /cognitiveservices/voices/list: en-US-DavisNeural
            // supports express-as styles (angry, sad, excited, terrified, shouting, whispering,
            // etc.), unlike EricNeural which had none - picked after sampling both for a deeper,
            // more cinematic "documentary narrator" read matching this channel's biggest comps
            // (The Why Files, The Infographics Show, both male-narrated), and because the style
            // list is what per-scene mood modulation will need. Only "en" is populated - this
            // channel is US/UK-weighted English, unlike GiggleGarden's hi/pa reach.
            ["en"] = ("en-US", "en-US-DavisNeural", null),
        },

        // Dramatic subset of Davis's verified live style list (angry, cheerful, excited,
        // friendly, hopeful, sad, shouting, terrified, unfriendly, whispering) - drops "chat"
        // and "unfriendly" as not useful for documentary/mythology narration. Drives
        // ScriptGenerator's per-scene mood tagging (see Scene.Mood) so narration style actually
        // shifts with what's happening in the scene, instead of one flat rate/pitch for the
        // whole video.
        NarrationMoodStyles =
        [
            "angry", "sad", "excited", "hopeful", "terrified", "shouting", "whispering", "cheerful", "friendly",
        ],

        BaseTags =
        [
            "mythology", "history", "folklore", "ancient history", "mythology explained",
            "legends", "ancient civilizations", "storytelling", "world history", "chronicle and chaos",
        ],

        CharacterStyle =
            "Painterly illustrated adult storytelling style, semi-realistic proportions, dramatic " +
            "lighting, period-appropriate clothing and architecture - not cute, not cartoon, not " +
            "flat-color-thick-outline.",

        // Empty on purpose, unlike GiggleGarden's pool: GiggleGarden's mascot is interchangeable
        // (any cute animal works for any garden story), so a stable-hash pool pick that ignores
        // the video's topic is fine. Here the character IS the myth's actual protagonist - a
        // video about Anubis cannot be illustrated with whatever figure a hash pick happened to
        // land on for a different video's story. Every video invents and draws its own figure.
        CharacterPoolPath = "",

        // This channel's videos mix drawn character moments with real stock footage/photos
        // of the myth's actual setting (ruins, landscapes, storms, temples) - see
        // StockFootageClient and tasks/todo.md's Deliverable 6. GiggleGarden leaves this
        // false and stays fully illustrated.
        AllowsContextScenes = true,

        TrendQueries =
        [
            "mythology explained", "greek mythology story", "norse mythology animated",
            "history storytelling shorts", "ancient history documentary", "folklore stories",
        ],

        BackgroundMusicPath = @"D:\Business\BroadcastContentAutomation\VideoGen\assets\music-mythology",

        // Was -30.0, tuned against nothing (see the now-fixed bug in
        // VideoAssembler.AssembleWithRemotionAsync where this value was declared but never
        // actually read - the Remotion mix used a flat 0.15 linear multiplier regardless).
        // Lowered to -34.0, closer to GiggleGarden's -36.0, in direct response to a "the
        // background music is loud" report on real rendered output. Still a placeholder to
        // check by ear against more videos, not a final measurement.
        BackgroundMusicLufs = -34.0,
    };

    // Builds all 5 Chronicle & Chaos formats as ContentFormatDef entries, mirroring
    // GiggleGardenProfile's BuildFormats pattern. Unlike GiggleGarden, DefaultMusicMood is
    // passed explicitly per format rather than derived from isCalm alone, since 4 of these 5
    // formats share IsCalm = false but each still wants its own distinct default mood.
    private static IReadOnlyList<ContentFormatDef> BuildFormats() =>
    [
        BuildFormat("retellingArc", weight: 35, isCalm: true, rate: "-8%", pitch: "-4%",
            narrationStyleLabel: "grave, cinematic", defaultMusicMood: "somber orchestral"),
        BuildFormat("whatIf", weight: 15, isCalm: false, rate: "0%", pitch: "0%",
            narrationStyleLabel: "urgent, speculative", defaultMusicMood: "tense atmospheric"),
        BuildFormat("topFive", weight: 20, isCalm: false, rate: "+4%", pitch: "+2%",
            narrationStyleLabel: "brisk, punchy", defaultMusicMood: "driving epic"),
        BuildFormat("explainer", weight: 20, isCalm: false, rate: "0%", pitch: "0%",
            narrationStyleLabel: "clear, documentary", defaultMusicMood: "curious ambient"),
        BuildFormat("mythBust", weight: 10, isCalm: false, rate: "+2%", pitch: "0%",
            narrationStyleLabel: "wry, contrarian", defaultMusicMood: "mysterious tense"),
    ];

    private static ContentFormatDef BuildFormat(
        string id, int weight, bool isCalm, string rate, string pitch, string narrationStyleLabel, string defaultMusicMood) =>
        new(id, weight, isCalm, rate, pitch, narrationStyleLabel, defaultMusicMood, Instructions: FormatInstructions(id, isCalm));

    private static FormatInstructionSet FormatInstructions(string id, bool isCalm)
    {
        var colourGuidance = isCalm
            ? "Desaturated, shadowy, atmospheric palettes are welcome here (cold moonlit blues, " +
              "muted bronze, dim firelight amber) - moody and cinematic rather than bright, but " +
              "still with enough colour contrast to read clearly, never a flat grey or black void."
            : "Bold, dramatic, saturated colour where the story calls for it (deep reds, gold, " +
              "stormy blues) - cinematic and vivid rather than washed out, but can still lean " +
              "darker or moodier than a cheerful palette when the scene calls for it.";

        var thumbnailGuidance = isCalm
            ? "Name the figure and its appearance, give it ONE large, dramatically lit face or " +
              "silhouette filling much of the frame with an intense, mysterious or haunted " +
              "expression, looking toward or just past the lens, plus one instantly recognisable " +
              "object or symbol beside it (a weapon, a crown, a mask, a flame). Moody, " +
              "high-contrast lighting, desaturated or cold palette, no text in the image."
            : "Name the figure and its appearance, give it ONE large, dramatically lit face " +
              "filling much of the frame with an intense, striking expression, looking straight " +
              "down the lens, plus one instantly recognisable object or symbol beside it. Bold, " +
              "saturated, high-contrast colours and lighting, cinematic composition, no text in " +
              "the image.";

        var musicMenu = isCalm
            ? """"somber orchestral", "ancient drone", "tense atmospheric""""
            : """"driving epic", "curious ambient", "mysterious tense", "tense atmospheric"""";

        string contentRules, introThird, hashtagExamples, longFormContentRules;
        switch (id)
        {
            case "retellingArc":
                contentRules = """
                    - Scene 1 opens in the middle of the action or at a moment of tension (in
                      medias res) - not "Long ago, in a land far away." Ground the viewer in a
                      concrete image or moment within the first line.
                    - Structure the 8 scenes as a real dramatic arc: setup, rising stakes, a
                      turning point around scene 4-5, and a consequence or resolution in the
                      final 1-2 scenes - not a flat list of facts in chronological order.
                    - State the figure's want or motivation early, and let the story's tension
                      come from what stands in the way of it (a rival, a curse, a law, their own
                      flaw).
                    - End on the story's actual consequence or fate - what it explains, what
                      changed, what the figure became known for - not a vague "and that is the
                      legend of..." non-ending.
                    - Where the historical or mythological record is genuinely disputed or
                      unknown, say so once in the narration itself ("some say...") rather than
                      presenting invention as settled fact.
                    """;
                longFormContentRules = """
                    - Scene 1 opens in the middle of the action or at a moment of tension (in
                      medias res) - not "Long ago, in a land far away." Ground the viewer in a
                      concrete image or moment within the first line, since these opening scenes
                      also double as the short teaser.
                    - Structure the full runtime as a real multi-act dramatic arc: a real setup
                      (who they are, what they want), a rising middle with more than one genuine
                      complication or setback (not just one obstacle stretched thin), a clear
                      turning point roughly two-thirds through, and a consequence/resolution act
                      at the end - the extra length exists to develop the world, supporting
                      figures, and stakes properly, not to pad the same three beats slower.
                    - State the figure's want or motivation early, and let the story's tension
                      come from what stands in the way of it (a rival, a curse, a law, their own
                      flaw) - develop that obstacle across several scenes rather than resolving
                      it in one line.
                    - End on the story's actual consequence or fate - what it explains, what
                      changed, what the figure became known for - not a vague "and that is the
                      legend of..." non-ending.
                    - Where the historical or mythological record is genuinely disputed or
                      unknown, say so in the narration itself ("some say...") rather than
                      presenting invention as settled fact - a long-form telling has room to
                      briefly weigh competing versions where the short cannot.
                    """;
                introThird = "a one-line teaser of the story's central tension or twist, without " +
                             "giving away the ending (\"A god who could not be named - and the " +
                             "mortal who dared to.\")";
                hashtagExamples = "#mythology #ancienthistory #folklore";
                break;

            case "whatIf":
                contentRules = """
                    - Frame the whole video around one clear speculative premise stated in the
                      first line ("What if the Trojan War was never about Helen at all?", "What
                      if Rome had never fallen in the West?").
                    - Treat the premise as a genuine thought experiment grounded in real
                      historical or mythological detail, not pure fantasy - reference what we
                      actually know before departing from it.
                    - Build the 8 scenes as a chain of consequences: each scene should follow
                      logically from the premise and the scene before it - "and because of that,
                      this happened."
                    - Land on a genuinely thought-provoking final beat - what the world, the myth,
                      or history would look like now, or what it reveals about the real version.
                    - Keep the tone curious and speculative rather than definitive - use
                      "imagine", "perhaps", "what if" rather than asserting the counterfactual as
                      fact.
                    """;
                longFormContentRules = """
                    - Frame the whole video around one clear speculative premise stated in the
                      first line ("What if the Trojan War was never about Helen at all?", "What
                      if Rome had never fallen in the West?") - this opening also has to work as
                      the short teaser, so the premise must land immediately.
                    - Treat the premise as a genuine thought experiment grounded in real
                      historical or mythological detail - spend real scenes laying out what we
                      actually know before departing from it, so the departure has weight.
                    - Build the full runtime as a chain of consequences across several distinct
                      stages, not just 8 beats stretched out: each stage should follow logically
                      from the premise and the stage before it - "and because of that, this
                      happened" - with room to actually develop each consequence rather than
                      naming it in passing.
                    - Land on a genuinely thought-provoking final beat - what the world, the myth,
                      or history would look like now, or what it reveals about the real version -
                      and give it real space rather than a rushed closing line.
                    - Keep the tone curious and speculative rather than definitive throughout -
                      use "imagine", "perhaps", "what if" rather than asserting the counterfactual
                      as fact.
                    """;
                introThird = "a one-line statement of tonight's premise as a direct question " +
                             "(\"What if the underworld had a door - and someone left it open?\")";
                hashtagExamples = "#whatif #alternatehistory #mythology";
                break;

            case "topFive":
                contentRules = """
                    - Structure the video as a countdown or ranked list (5 to 1, or 1 to 5) of the
                      most dramatic, terrifying, strange or consequential examples within tonight's
                      topic - state the list's exact framing and count in the first line.
                    - Each of the (up to) 5 entries gets roughly 1-2 scenes: name the entry
                      clearly, then land the one detail that earns its place on the list.
                    - Keep pace brisk - short, punchy sentences, minimal setup per entry, move on
                      quickly.
                    - Rank with an actual point of view - state briefly why each entry ranks where
                      it does, not just a list of facts.
                    - The final entry (#1) should land as a genuine payoff, not just "and that's
                      the list."
                    """;
                longFormContentRules = """
                    - Structure the video as a countdown or ranked list (state the exact framing
                      and count in the first line) of the most dramatic, terrifying, strange or
                      consequential examples within tonight's topic - expand the list itself to
                      8-12 entries so the extra runtime goes into more entries, not padding.
                    - Give each entry a dedicated multi-scene segment: name it clearly, develop
                      the one story or detail that earns its place, and let it breathe as its own
                      mini-narrative rather than a single punchy line.
                    - Keep pace brisk within each entry even as the list runs longer - short,
                      punchy sentences, minimal throat-clearing - the extra length buys more
                      entries and more depth per entry, not slower pacing.
                    - Rank with an actual point of view - state why each entry ranks where it
                      does, and let stronger entries get visibly more development than weaker
                      ones.
                    - The final entry (#1) should land as a genuine payoff with real space to
                      develop it, not just "and that's the list."
                    """;
                introThird = "a one-line tease of the list itself, without revealing #1 (\"Five " +
                             "gods you really don't want to owe a favour.\")";
                hashtagExamples = "#top5 #mythology #ranked";
                break;

            case "explainer":
                contentRules = """
                    - Open by stating the myth or legend plainly, then pivot immediately to the
                      real question this video answers: what actually inspired it, what it was
                      really explaining, or what modern historians or archaeologists now think.
                    - Alternate between "the myth says..." and "but the evidence/history
                      suggests..." beats - the structure IS the content: legend, then reality,
                      repeated.
                    - Cite the kind of evidence in plain language (a real ruin, an old text, a
                      natural phenomenon, a real historical event) without needing exact
                      citations - "archaeologists found," "the earliest written version," "one
                      theory is."
                    - Stay honest about uncertainty - where scholars disagree, say so, rather than
                      presenting one theory as the final answer.
                    - Land on what the myth was really doing for the people who told it -
                      explaining a mystery, teaching a lesson, justifying power, remembering a
                      real disaster.
                    """;
                longFormContentRules = """
                    - Open by stating the myth or legend plainly, then pivot to the real question
                      this video answers: what actually inspired it, what it was really
                      explaining, or what modern historians or archaeologists now think.
                    - Alternate between "the myth says..." and "but the evidence/history
                      suggests..." beats across several full cycles - the extra runtime means
                      more evidence, more competing theories, and more of the historical/
                      archaeological detail behind each, not just a slower repeat of the same
                      two beats.
                    - Cite the kind of evidence in plain language (a real ruin, an old text, a
                      natural phenomenon, a real historical event) and give the strongest pieces
                      of evidence a real scene or two to actually explain, rather than a single
                      namedrop.
                    - Stay honest about uncertainty throughout - where scholars disagree, lay out
                      the competing views rather than presenting one theory as the final answer.
                    - Land on what the myth was really doing for the people who told it -
                      explaining a mystery, teaching a lesson, justifying power, remembering a
                      real disaster - with room to actually make that case, not just assert it.
                    """;
                introThird = "a one-line hook framing tonight's real question (\"Every culture " +
                             "has a flood myth. Here's what they might actually be remembering.\")";
                hashtagExamples = "#mythologyexplained #history #ancientmysteries";
                break;

            case "mythBust":
                contentRules = """
                    - Open by stating the popular version of the myth or history everyone thinks
                      they know, clearly and fairly - not a strawman.
                    - Devote the middle scenes to correcting it point by point: what's actually
                      true, what's exaggerated, what's a later invention (a Victorian
                      embellishment, a Hollywood addition, a mistranslation).
                    - Keep a wry, slightly contrarian energy throughout - the fun of this format
                      is the reveal, not a dry correction.
                    - Back each correction with the kind of source in plain language (the earliest
                      text, the archaeological record, a historian's consensus) without needing
                      exact citations.
                    - Land on why the popular version persists anyway - it's a better story, it
                      served a purpose, it got repeated until it stuck - not just "so now you
                      know."
                    """;
                longFormContentRules = """
                    - Open by stating the popular version of the myth or history everyone thinks
                      they know, clearly and fairly - not a strawman - since this opening also
                      has to work as the short teaser.
                    - Give each point being corrected its own dedicated segment: what's actually
                      true, what's exaggerated, what's a later invention (a Victorian
                      embellishment, a Hollywood addition, a mistranslation) - the extra runtime
                      means more points get busted, and each one gets real room to build its
                      case, not a rapid-fire list.
                    - Keep a wry, slightly contrarian energy throughout - the fun of this format
                      is the reveal, not a dry correction, even across a longer runtime.
                    - Back each correction with the kind of source in plain language (the earliest
                      text, the archaeological record, a historian's consensus) and let the
                      strongest corrections get a real scene or two to lay out the evidence.
                    - Land on why the popular version persists anyway - it's a better story, it
                      served a purpose, it got repeated until it stuck - and give that closing
                      idea real space rather than a one-line button.
                    """;
                introThird = "a one-line challenge to the popular version (\"Everything you think " +
                             "you know about the Trojan Horse is wrong.\")";
                hashtagExamples = "#mythbusting #mythology #actuallyhistory";
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown format id.");
        }

        return new FormatInstructionSet(contentRules, colourGuidance, thumbnailGuidance, introThird, musicMenu, hashtagExamples, longFormContentRules);
    }
}
