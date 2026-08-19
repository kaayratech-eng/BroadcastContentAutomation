// Lets the narration stage swap providers (Azure, Google Cloud TTS, ...) without
// touching the call sites in Program.cs. All providers speak the same shape: SSML built
// from the caller-supplied rate/pitch. The caller resolves those from the active
// ContentProfile's matching ContentFormatDef (see ScriptGenerator.PickFormat) - providers
// no longer know anything about ContentFormat/profiles themselves.
//
// styleOverride is this scene's mood tag (see Scene.Mood/ContentProfile.NarrationMoodStyles),
// optional so every pre-existing call site (outro line, --test-tts) keeps compiling
// unchanged. AzureTtsProvider applies it as an express-as style when given; GoogleTtsProvider
// has no equivalent and ignores it.
interface ITtsProvider
{
    Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch, string? styleOverride = null);
}
