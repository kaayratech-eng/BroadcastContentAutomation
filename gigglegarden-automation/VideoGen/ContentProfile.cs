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

    // Everything about a ContentFormat the prompt needs - see FormatInstructionSet.
    public required Func<ContentFormat, FormatInstructionSet> FormatInstructions { get; init; }

    // Weighted random draw for ScriptGenerator.PickFormat - keys are ContentFormat member
    // names (case-insensitive). A --format flag/override always wins over this draw.
    public required Dictionary<string, int> ContentFormatWeights { get; init; }

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
