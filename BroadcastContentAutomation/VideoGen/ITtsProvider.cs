// Lets the narration stage swap providers (Azure, Google Cloud TTS, ...) without
// touching the call sites in Program.cs. All providers speak the same shape: SSML built
// from the caller-supplied rate/pitch. The caller resolves those from the active
// ContentProfile's matching ContentFormatDef (see ScriptGenerator.PickFormat) - providers
// no longer know anything about ContentFormat/profiles themselves.
interface ITtsProvider
{
    Task SynthesizeAsync(string text, string language, string outputPath, string rate, string pitch);
}
