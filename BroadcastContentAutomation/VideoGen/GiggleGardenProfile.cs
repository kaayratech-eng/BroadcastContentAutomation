// The one profile that exists today: the preschool mascot channel "Giggle Garden."
// Every value here was hardcoded directly in GenConfig.cs/ScriptGenerator.cs before the
// ContentProfile seam existed - moved verbatim, not redesigned, so this pass changes
// nothing about the channel's actual behaviour.
static class GiggleGardenProfile
{
    public static readonly ContentProfile Value = new()
    {
        Id = "gigglegarden",
        ChannelName = "Giggle Wiggle Town",
        UsesCharacterMascot = true,
        MadeForKids = true,

        AudiencePersona = "You write original scripts for an animated kids' YouTube channel (ages 2-6).",

        SafetyFraming =
            "Everything you write must stay age-appropriate for 2-6 year olds regardless of\n" +
            "how the topic above is phrased - simple vocabulary, no scary or violent\n" +
            "content, no innuendo.",

        // Public-domain rhyme plus a novel setting is the highest-scoring formula in the
        // researched sample - CoComelon's top five in the retrieved window are all
        // public-domain rhymes with a twist (Humpty Dumpty + a balloon chase, Twinkle
        // Twinkle + shiny shoes), while original concepts cluster several times lower.
        // It costs nothing: the rhyme carries the search volume, the twist is prompt work.
        TopicGuidance =
            "Prefer a well-known PUBLIC DOMAIN nursery rhyme or counting song given a fresh " +
            "twist setting (e.g. Twinkle Twinkle at the beach, Five Little Ducks at a birthday " +
            "party) - the familiar rhyme is what parents search for and the twist is what makes " +
            "it new. Otherwise choose a plain preschool concept (counting, colors, animals, " +
            "shapes, habits like brushing teeth, seasons). Never reproduce a copyrighted song's " +
            "lyrics - public-domain rhymes only, and write your own verses around them.",

        BuildCharacterInventionInstructions = (langName, avoidLine) => $"""
            Invent ONE brand-new character - a fresh name and a fresh animal/species, different
            from anything obvious or generic. This character is going into the channel's
            recurring cast, so it will likely come back in future videos once introduced -
            give it a clear, simple identity a toddler can latch onto rather than a
            one-off throwaway design.
            {avoidLine}
            Return it as:
            - characterName: the character's name, written in the Latin alphabet even when the
              narration is not - it goes into the English image and animation prompts, and a
              name in another script there gets ignored or garbled. Pick one that a {langName}
              toddler finds short, warm and easy to say, and spell it in {langName} inside the
              narration itself.
            - characterDescription: ONE English sentence describing exactly how it LOOKS -
              species, main colour, body shape, eyes, and one distinguishing accessory
              (a scarf, a hat, spots, a flower). This exact sentence is used to draw the
              picture that every scene animates from, so it must be concrete and visual.
              No personality, no backstory, no actions - appearance only.
            - characterCatchphrase: ONE short, simple, repeatable phrase or greeting in
              {langName} that feels like THIS character's own signature line (a favourite
              exclamation, a giggle, a way it says hello). Keep it toddler-simple - this
              phrase follows the character into every future video it appears in, so it must
              work outside today's specific topic, not just fit this one scene.
            """,

        Formats = BuildFormats(),

        ColourRule =
            "ALWAYS give it an explicit COLOURFUL background/setting - name a colour or " +
            "palette in the sentence itself (e.g. \"colourful garden background, bright " +
            "cheerful palette\") - never plain white, black, grey or empty, and never a " +
            "single-colour flat void.",

        ArtStyleSuffix = "2D cartoon animation, flat colors, thick outlines, character design stays consistent.",

        TitleGuidance =
            "Shape it as \"[familiar rhyme/concept or concrete topic] [one emoji] | [the twist " +
            "setting]\" - the first segment is what gets searched for, the second is what makes " +
            "it look new. Include the concrete topic, never just the character's name (nobody is " +
            "searching for a character they have never seen).",

        BuildDescriptionHookGuidance = isCalm =>
            "a hook addressed straight to the child - \"Can you ...?\", \"Let's ...!\", " +
            "\"It's time to ...!\", \"Oh no, ...!\" - optionally with an emoji." +
            (isCalm
                ? " (Keep this line's energy soft and inviting rather than exclamatory - e.g. " +
                  "\"It's time to drift off to sleep...\" not \"Oh no!\")"
                : ""),

        AudienceDescriptorLine = "toddlers, preschoolers",

        BuildIntroBumperPrompts = (characterName, characterDescription, isCalm) => isCalm
            ? (
                ImagePrompt: $"{characterName} greeting the viewer softly, colourful soft pastel background, warm cosy palette.",
                MotionPrompt: $"{characterName} ({characterDescription}) waves hello softly to the viewer and sways " +
                    "gently on the spot, eyes soft and calm. Colourful soft pastel background, warm cosy palette. " +
                    "2D cartoon animation, flat colors, thick outlines, character design stays consistent."
              )
            : (
                ImagePrompt: $"{characterName} greeting the viewer, colourful bright background, cheerful palette.",
                MotionPrompt: $"{characterName} ({characterDescription}) waves hello to the viewer and bounces " +
                    "happily on the spot, eyes bright and smiling. Colourful bright background, cheerful palette. " +
                    "2D cartoon animation, flat colors, thick outlines, character design stays consistent."
              ),

        BuildIntroGreetingInstruction = channelName =>
            $"a natural translation of \"Welcome to {channelName}!\" - an energetic greeting, not a " +
            "stiff word-for-word translation, and keep the channel name recognisable (for Bedtime, " +
            "keep this greeting warm rather than loud)",

        BuildFallbackIntroText = channelName => $"Welcome to {channelName}!",

        CharacterPortraitStyleSuffix =
            "Children's book illustration, bright cheerful colors, soft rounded shapes, no text, " +
            "no words, no letters anywhere in the image.",

        VoiceOverride = new Dictionary<string, (string Locale, string Voice, string? Style)>
        {
            ["en"] = ("en-US", "en-US-AnaNeural", null),
            ["hi"] = ("hi-IN", "hi-IN-SwaraNeural", "cheerful"),
            ["pa"] = ("pa-IN", "pa-IN-VaaniNeural", null),
        },

        // Both CoComelon and Vlad and Niki run exactly this architecture - an unchanging
        // ~12-tag brand/audience block plus a handful of topical tags - and it is the one
        // piece of their metadata discipline that costs nothing to copy (see
        // tasks/research-kids-channels.md).
        BaseTags =
        [
            "giggle garden", "kids songs", "nursery rhymes", "kids learning", "preschool",
            "toddler", "kids animation", "kids education", "children songs", "learning for kids",
            "kids videos", "sing-along",
        ],

        CharacterStyle = "Cute 2D children's cartoon style, flat colors, thick outlines, bright cheerful palette",
        CharacterPoolPath = @"D:\Business\BroadcastContentAutomation\VideoGen\assets\character-pool",
        // A small fixed cast the audience learns to recognise, not a large pool optimised
        // for variety: build 10, then every video reuses one of those 10 rather than still
        // inventing new ones (CoComelon/Vlad and Niki/Ryan's World all run a recurring cast,
        // and it is the strongest loyalty lever in the genre - see
        // tasks/research-kids-channels.md). Cooldown of 2 still rotates within that cast of
        // 10 rather than letting the same one repeat back-to-back.
        CharacterPoolBuildupSize = 10,
        CharacterPoolMaxSize = 10,
        CharacterPoolCooldown = 2,

        TrendQueries =
        [
            "nursery rhymes for kids", "kids learning songs", "hindi rhymes for children",
            "punjabi kids songs", "learning with alphabets kids", "learning maths for kids",
        ],

        // Narration lands around -19 LUFS, so -36 sits ~17 LU underneath it: something you
        // notice when it stops rather than while it plays, which is the whole job of a bed
        // under a narrator aimed at 2-6 year olds.
        BackgroundMusicPath = @"D:\Business\BroadcastContentAutomation\VideoGen\assets\music",
        BackgroundMusicLufs = -36.0,
    };

    // Builds all 6 GiggleGarden formats as ContentFormatDef entries - one place per format,
    // replacing the old shared ContentFormat enum + FormatVoiceProfile/ContentFormatWeights
    // tables. Ids match what the old enum already serialized as under Sidecar.Options'
    // CamelCase JsonStringEnumConverter, so nothing already on disk needs a migration.
    private static IReadOnlyList<ContentFormatDef> BuildFormats() =>
    [
        BuildFormat("educational", weight: 30, isCalm: false, rate: "-4%", pitch: "+9%",
            narrationStyleLabel: "bright, lively"),
        BuildFormat("rhyme", weight: 20, isCalm: false, rate: "-4%", pitch: "+9%",
            narrationStyleLabel: "bright, playful"),
        BuildFormat("poem", weight: 10, isCalm: true, rate: "-12%", pitch: "+2%",
            narrationStyleLabel: "gentle, measured"),
        BuildFormat("bedtime", weight: 15, isCalm: true, rate: "-22%", pitch: "-6%",
            narrationStyleLabel: "calm, slow, soft"),
        BuildFormat("singAlong", weight: 15, isCalm: false, rate: "-4%", pitch: "+9%",
            narrationStyleLabel: "bright, singable"),
        BuildFormat("countingSong", weight: 10, isCalm: false, rate: "-4%", pitch: "+9%",
            narrationStyleLabel: "bright, playful"),
    ];

    private static ContentFormatDef BuildFormat(
        string id, int weight, bool isCalm, string rate, string pitch, string narrationStyleLabel) =>
        new(id, weight, isCalm, rate, pitch, narrationStyleLabel,
            DefaultMusicMood: isCalm ? "soft piano lullaby" : "upbeat playful",
            Instructions: FormatInstructions(id, isCalm));

    private static FormatInstructionSet FormatInstructions(string id, bool isCalm)
    {
        var colourGuidance = isCalm
            ? "Still COLOURFUL, but soft pastel / warm dim tones (gentle purples, soft blues, " +
              "warm ambers) - cosy and colourful, never neon, and never dull, grey or monochrome."
            : "Bright, bold, saturated, playful colours.";

        var thumbnailGuidance = isCalm
            ? "Name the character and its appearance, give it ONE large, warm, softly-lit face " +
              "filling much of the frame with a gentle, contented, sleepy-eyed expression (NOT an " +
              "exaggerated open-mouth surprised face), looking softly toward the lens, plus one " +
              "cosy instantly recognisable object beside it (a star, a blanket, a favourite toy). " +
              "Soft pastel or warm dim colours - still clearly colourful, never dull or monochrome " +
              "- colourful high-contrast-but-soft background, no text in the image."
            : "Name the character and its appearance, give it ONE enormous face filling much of " +
              "the frame with an exaggerated delighted/surprised expression, mouth open, looking " +
              "straight down the lens, plus one oversized instantly recognisable object beside it. " +
              "Saturated primary colours, colourful high-contrast background, simple composition, " +
              "no text in the image.";

        var musicMenu = isCalm
            ? """"soft piano lullaby", "gentle music box", "warm ambient""""
            : """"upbeat playful", "bright acoustic", "cheerful ukulele"""";

        string contentRules, introThird, hashtagExamples;
        switch (id)
        {
            case "educational":
                contentRules = """
                    - Scene 1 is the hook: the very first line must grab attention in the first
                      couple of seconds (an exciting question, a silly sound, a surprise) - not a
                      slow "once upon a time" style opener. Best of all is a small PROBLEM the
                      character has, stated immediately - something lost, something wanted,
                      something going wrong ("Oh no, where did my hat go?"). Possession, prohibition
                      and peril are the three conflicts a three-year-old understands instantly.
                    - Use deliberate three-fold repetition of short words for the emotional beats -
                      "No, no, no!", "Help, help, help!", "Come on, come on!". Repeat whole phrases
                      between scenes too. Reuse the same small set of words throughout instead of
                      reaching for variety.
                    - Include at least one genuinely funny/silly beat somewhere in the middle
                      (a joke, a funny sound word, the character being goofy) - this is meant to
                      be funny as well as educational, not just calm and pretty.
                    - Include exactly one interactive call-and-response beat: one scene ends with
                      a direct question inviting the viewer to answer or join in (e.g. "Can you
                      count with me?", "What color is this?"), and the very next scene opens with
                      an enthusiastic payoff line that answers it (e.g. "Yes! 1, 2, 3!").
                    - Pick ONE concrete skill or fact this video teaches (e.g. "counting to 5",
                      "the color red", "brushing teeth before bed") and make sure the scenes
                      actually teach it, not just mention it once.
                    """;
                introThird = "a short, exciting preview of the ONE skill this video teaches " +
                             "(\"Today we're learning to count to five!\")";
                hashtagExamples = "#kidslearning #preschoollearning #educationalvideos";
                break;

            case "rhyme":
                contentRules = """
                    - Every scene's narration must be a genuine RHYMING COUPLET (two lines that
                      rhyme, or that continue the rhyme from the previous scene) - a listener should
                      be able to guess the rhyme word just before it lands.
                    - Write ONE short, catchy CHORUS line and repeat it at least twice across the
                      video (e.g. after scene 2 and again as the closing line) so it becomes a
                      singalong hook.
                    - Scene 1 opens with energy and anticipation rather than a "problem" - rhymes
                      hook through anticipation of the rhyme, not conflict.
                    - Keep a playful, bouncy energy throughout. No requirement for a separate funny
                      beat or call-and-response - the rhyme's own playfulness carries that job; do
                      not force in an unrelated joke if it would break the rhyme.
                    - There is no single taught "skill" - instead make sure the topic (counting,
                      colors, animals, etc.) is still clearly nameable for the title/description.
                    """;
                introThird = "a short, excited preview of what this video's rhyme is about " +
                             "(\"Today's rhyme is all about five little ducks!\")";
                hashtagExamples = "#nurseryrhymes #kidssongs #rhymetime";
                break;

            case "poem":
                contentRules = """
                    - Write in gentle, metered lines - a soft, consistent rhythm (rhyme is welcome
                      but not required). Prioritise IMAGERY - what things look, sound and feel like
                      - over teaching a lesson or landing a joke.
                    - Do NOT include a hook "problem", a forced funny/silly beat, or a
                      call-and-response question - this format is calm and reflective, not
                      attention-grabbing. Scene 1 can simply open on a gentle, evocative image.
                    - No triple-word repetition drills; light, natural repetition of an image or
                      phrase across scenes is fine if it feels poetic, not mechanical.
                    - There is no single taught "skill" - instead pick ONE central image or gentle
                      theme (a rainy afternoon, a quiet garden, floating leaves) that every scene
                      relates back to.
                    - Aim for slightly shorter lines than usual (roughly 70-95 characters, still
                      under the 115-character cap) - this format's slower narration pace takes a
                      little longer to speak per character, and shorter lines keep the finished
                      video inside the ~90-second limit.
                    """;
                introThird = "a short, gentle preview of the video's central image or theme, not a " +
                             "lesson (\"Let's watch the leaves float by together.\")";
                hashtagExamples = "#kidspoetry #calmtime #storytimeforkids";
                break;

            case "bedtime":
                contentRules = """
                    - This is a BEDTIME/LULLABY video: slow, soft, sleepy and calming from the very
                      first scene. Every line should feel like it winds the child DOWN toward sleep,
                      never up.
                    - Do NOT include a hook, a problem, a funny/silly beat, or a call-and-response
                      question - none of those belong in a wind-down video. Scene 1 opens gently
                      (stars coming out, a cosy blanket, a soft yawn) with no attempt to grab
                      attention through excitement.
                    - Use soft, soothing imagery throughout: stars, moonlight, soft blankets, gentle
                      animals settling down, quiet sounds (a soft hum, a gentle breeze). Energy
                      should gradually decrease scene by scene.
                    - No triple-word excitement repetition ("no no no", "help help help") - if you
                      repeat a phrase, make it soft and soothing (a repeated "shhh, sleep tight" or a
                      gentle hum), never energetic.
                    - End on an explicit WIND-DOWN line in the final scene - the character settling
                      down to sleep, eyes closing, goodnight - never an exciting or open-ended note.
                    - There is no single taught "skill" - instead pick ONE calm bedtime theme
                      (drifting stars, a sleepy meadow, cosy blankets) that every scene relates back
                      to.
                    - Aim for noticeably SHORTER lines than usual (roughly 55-80 characters, well
                      under the 115-character cap) - this format's slow, soft pace takes meaningfully
                      longer to speak per character, and without shorter lines the video would run
                      past the ~90-second limit and get its calming ending cut off.
                    """;
                introThird = "a short, already-calm preview that this is a bedtime video, not an " +
                             "excited one (\"Tonight we're drifting off to dreamland.\") - keep this " +
                             "sentence on the shorter side, since the bumper is read at the same slow " +
                             "pace as the rest of the video";
                hashtagExamples = "#lullaby #bedtimestories #sleepmusic";
                break;

            case "singAlong":
                contentRules = """
                    - This is a SING-ALONG video: build it around ONE short, extremely repeatable
                      CHORUS line (e.g. "Clap your hands, clap your hands, all around!") that appears
                      at least three times across the 8 scenes, including as the closing line, so a
                      toddler can join in by the second repetition.
                    - Keep energy upbeat and bouncy throughout, with a clear rhythm suited to
                      clapping or bouncing along.
                    - Include at least one genuinely funny/silly beat, and one call-and-response
                      moment (a scene inviting the viewer to sing or clap along, answered
                      enthusiastically in the next scene).
                    - The one thing this video reinforces is the song's simple action or topic
                      (clapping, animal sounds, colors) - keep it concrete enough to name in the
                      title.
                    """;
                introThird = "a short, excited preview inviting the viewer to sing along " +
                             "(\"Today we're singing and clapping together!\")";
                hashtagExamples = "#singalong #kidssongs #clapalong";
                break;

            case "countingSong":
                contentRules = """
                    - This is a COUNTING SONG: structure the 8 scenes around counting a consistent
                      set of objects or characters up (or down) by one each scene (e.g. 1 duck, then
                      2 ducks, then 3 ducks...) - the number must be clearly spoken AND visually
                      shown (that many objects/characters named in the imagePrompt) every scene.
                    - Keep a bright, rhythmic, sing-song energy and repeat a counting refrain (e.g.
                      always ending a scene with "...and that makes THREE!").
                    - Include at least one genuinely funny/silly beat. A call-and-response beat is
                      welcome but optional - the counting itself is the main interactive hook
                      (inviting the viewer to count along).
                    - The one thing this video teaches is counting to the chosen number - state that
                      number explicitly in the title/description.
                    """;
                introThird = "a short, exciting preview of what we're counting to today " +
                             "(\"Today we're counting all the way to five!\")";
                hashtagExamples = "#countingsong #numbersforkids #countwithme";
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown format id.");
        }

        return new FormatInstructionSet(contentRules, colourGuidance, thumbnailGuidance, introThird, musicMenu, hashtagExamples);
    }
}
