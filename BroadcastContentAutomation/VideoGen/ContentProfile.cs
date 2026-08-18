// A swappable content identity: channel name, audience/safety framing, per-format
// prompt instructions, tag list, trend queries, and (when UsesCharacterMascot) the
// character-mascot pool this profile's videos are built around. GiggleGardenProfile
// is the only implementation today; ContentProfileRegistry is how --profile picks one.
//
// Data as C#, not JSON - matches the existing provider-seam convention (see
// IScriptProvider/ScriptProviderFactory) rather than introducing a second way of
// configuring the app.
record ContentProfile
{
    public required string Id { get; init; }

    // The channel itself, not a character. Every video opens "Welcome to {ChannelName}!"
    // (translated), which is what stays constant across the channel even for profiles
    // whose cast changes video to video.
    public required string ChannelName { get; init; }

    // When false, ScriptGenerator.GenerateAsync throws immediately rather than building
    // a character-anchored prompt - a faceless-visual script/art pipeline is future work,
    // not implemented in this pass. Program.cs also gates every CharacterSource call on
    // this flag.
    //
    // This replaces GenConfig.ChannelName/CharacterName's old bug history: the script
    // used to free-invent a name and species per video ("Pip the Squirrel", then "Pip
    // the Penguin") while the art stayed one fixed duckling regardless, so narration and
    // picture described two different characters. Inventing a character is fine;
    // inventing one the art never saw is not - that is why the invented description
    // drives the picture too, for every profile that uses this flow.
    public required bool UsesCharacterMascot { get; init; }

    // Drives every video's YouTube Sidecar.MadeForKids (see YouTubePublisher). Required,
    // not defaulted, because guessing wrong here is a compliance/monetization problem,
    // not a cosmetic one - every profile has to say this on purpose.
    public required bool MadeForKids { get; init; }

    // Opening persona line of the script-generation prompt (e.g. "You write original
    // scripts for an animated kids' YouTube channel (ages 2-6).") - names who the writer
    // is and who the audience is.
    public required string AudiencePersona { get; init; }

    // Content-safety framing appended after the language line - what's off-limits and why.
    public required string SafetyFraming { get; init; }

    // Steers topic selection when no --topic is given - what kind of topic this profile's
    // videos should gravitate toward.
    public required string TopicGuidance { get; init; }

    // Builds the "invent a new character" block of the prompt, given the narration
    // language name and the avoid-these-names line (empty string if there's nothing to
    // avoid). Only called when UsesCharacterMascot is true and no pooled character was
    // supplied.
    public required Func<string, string, string> BuildCharacterInventionInstructions { get; init; }

    // This profile's own content-format catalog - replaces the old shared ContentFormat
    // enum (see ContentFormatDef below). A profile defines exactly the formats it wants;
    // nothing here is shared across profiles, so a mythology channel's formats don't have
    // to coexist with GiggleGarden's on one fixed list.
    public required IReadOnlyList<ContentFormatDef> Formats { get; init; }

    // The base "always give it a colourful background" rule appended to every image/motion
    // prompt regardless of format - profile-owned because a mood-driven adult channel may
    // legitimately want desaturated/shadowy palettes a preschool channel never would.
    public required string ColourRule { get; init; }

    // Fixed suffix every motionPrompt ends with, describing the animation's overall visual
    // style (e.g. flat 2D cartoon vs. painterly semi-realistic) - independent of CharacterStyle,
    // which only covers the character reference portrait.
    public required string ArtStyleSuffix { get; init; }

    // Replaces the "[familiar rhyme/concept] [twist setting]" title-shape instruction -
    // what makes a good title varies by audience (a search-optimised nursery-rhyme title
    // has nothing in common with a documentary-style mythology hook), so this is profile
    // prose rather than a shared template. Slotted into the surrounding "Title <=90 chars,
    // written for search" / "ALL-CAPS fine here" sentences, which stay generic in
    // ScriptGenerator.
    public required string TitleGuidance { get; init; }

    // Builds the description's opening hook-line instruction, given whether the resolved
    // format is calm. Separate from AudienceDescriptorLine because the hook is about tone
    // (exclamatory vs. soft vs. cinematic), not who the video is for.
    public required Func<bool, string> BuildDescriptionHookGuidance { get; init; }

    // Who this channel's videos are for, in a few words (e.g. "toddlers, preschoolers") -
    // slotted into "...and who it is for ({{AudienceDescriptorLine}})." at the end of the
    // description instruction.
    public required string AudienceDescriptorLine { get; init; }

    // Builds this profile's branded intro-bumper Scene content (image + motion prompts) -
    // scene zero, played before scene 1, the same channel greeting every video. Takes this
    // video's character name/description and whether the resolved ContentFormatDef is calm,
    // and returns the (ImagePrompt, MotionPrompt) pair - entirely profile-owned rather than
    // living in Program.cs, since the greeting's energy/tone is channel identity.
    public required Func<string, string, bool, (string ImagePrompt, string MotionPrompt)> BuildIntroBumperPrompts { get; init; }

    // The "First: ..." sentence of the introText prompt instruction, describing how the
    // model should write this profile's channel-greeting line - takes ChannelName so the
    // sentence can be built around it verbatim.
    public required Func<string, string> BuildIntroGreetingInstruction { get; init; }

    // Fallback introText used when the model omits one - takes ChannelName. Kept separate
    // from BuildIntroGreetingInstruction (which only shapes the prompt asking for a greeting)
    // because a profile's fallback need not be phrased as a "Welcome to X!" greeting at all.
    public required Func<string, string> BuildFallbackIntroText { get; init; }

    // Suffix appended to every character reference-portrait prompt (see ImageClient.BuildPrompt)
    // describing the illustration medium/style - independent of CharacterStyle, which only
    // covers this specific character's design brief.
    public required string CharacterPortraitStyleSuffix { get; init; }

    // Per-language Azure voice/style table AzureTtsProvider draws from - Locale/Voice/Style
    // (Style is Azure's express-as tag, e.g. "cheerful"; null when the voice/profile combo
    // has none). Only meaningful when AzureTtsProvider is actually reached (TtsProvider =
    // "azure", or "auto" for a language Google has no free voice for - see TtsProviderFactory).
    public required IReadOnlyDictionary<string, (string Locale, string Voice, string? Style)> VoiceOverride { get; init; }

    // Fixed tag block appended to every video regardless of ContentFormat - channel
    // identity, not format identity, so format-specific tags layer on top of it.
    public required string[] BaseTags { get; init; }

    // Art style only - deliberately says nothing about who the character is, since that
    // changes every video. Applied when drawing an invented character reference. Only
    // meaningful when UsesCharacterMascot is true.
    public string CharacterStyle { get; init; } = "";

    // Where this profile's characters come from. Empty means the script invents a
    // brand-new one per video and it is drawn on the spot - see CharacterSource.cs.
    // Otherwise: a single image pins every video to that one character, or a folder
    // picks one per video. Only meaningful when UsesCharacterMascot is true.
    public string CharacterPoolPath { get; init; } = "";

    // When true, ScriptGenerator asks the model to tag each scene as a "character" moment
    // (this video's figure doing something, drawn via the manual Gemini art loop) or a
    // "context" moment (a real setting/object/era with no character in frame, fetched from
    // Pexels stock footage via StockFootageClient) - the no-Vidu hybrid pipeline for
    // Chronicle & Chaos (Deliverable 6, tasks/todo.md). False (the default) keeps a
    // profile's scenes 100% character-anchored with no prompt change at all - GiggleGarden's
    // visual style is fully illustrated throughout, no stock footage mixed in.
    public bool AllowsContextScenes { get; init; } = false;

    // The three below only matter when CharacterPoolPath is a folder - they drive
    // CharacterSource.Resolve's build-up/mix/reuse policy so the cast grows on its own
    // instead of staying a stranger every video.
    public int CharacterPoolBuildupSize { get; init; } = 15;
    public int CharacterPoolMaxSize { get; init; } = 40;
    public int CharacterPoolCooldown { get; init; } = 10;

    // YouTube search queries used to pull real trending titles as topic inspiration when
    // --topic is not given - see TrendResearch.cs.
    public required string[] TrendQueries { get; init; }

    // A single track, or a folder of them to pick from per video (see
    // VideoAssembler.ResolveBackgroundMusic). Empty means render without music on
    // purpose; a path that isn't there throws.
    public required string BackgroundMusicPath { get; init; }

    // Target loudness for the music bed, in LUFS - tied to this profile's narration tone
    // (e.g. -36 LUFS sits well under a narrator aimed at 2-6 year olds). Less negative =
    // louder music.
    public required double BackgroundMusicLufs { get; init; }

    public void Validate()
    {
        // Everything below only matters for profiles that actually use the character-pool
        // mechanism; a faceless profile has nothing here to check.
        if (!UsesCharacterMascot) return;

        // Empty is the normal case (draw a new character per video), so only a path that
        // was set and then pointed nowhere is an error.
        if (!string.IsNullOrWhiteSpace(CharacterPoolPath) &&
            !File.Exists(CharacterPoolPath) && !Directory.Exists(CharacterPoolPath))
            throw new FileNotFoundException(
                $"Profile \"{Id}\": CharacterPoolPath points at \"{CharacterPoolPath}\", which does not exist. " +
                "Point it at a character image, or at a folder of them, or clear it to draw a new character each video.",
                CharacterPoolPath);

        if (CharacterPoolBuildupSize > CharacterPoolMaxSize)
            throw new InvalidOperationException(
                $"Profile \"{Id}\": CharacterPoolBuildupSize ({CharacterPoolBuildupSize}) is greater than " +
                $"CharacterPoolMaxSize ({CharacterPoolMaxSize}) - the pool would never reach the mixing zone. " +
                "Lower the first or raise the second.");
    }
}

// One content format, entirely owned by whichever ContentProfile defines it. Replaces the
// old shared `enum ContentFormat` plus the separate FormatVoiceProfile/ContentFormatWeights
// tables that used to key off it - everything about a format now lives in one place, and a
// new profile adding a format never touches shared engine code.
//
// Id is the format's identity everywhere it is persisted (script.json's Format field,
// dry-script filenames, --format matching) - profile-chosen, unique within that profile,
// matched case-insensitively. GiggleGardenProfile's Ids are chosen to match what the old
// `ContentFormat` enum already serialized as under Sidecar.Options' CamelCase converter, so
// nothing already on disk needed a migration.
sealed record ContentFormatDef(
    string Id,
    int Weight,
    bool IsCalm,
    string Rate,
    string Pitch,
    string NarrationStyleLabel,
    string DefaultMusicMood,
    FormatInstructionSet Instructions);
