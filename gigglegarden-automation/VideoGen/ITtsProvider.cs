// Lets the narration stage swap providers (Azure, Google Cloud TTS, ...) without
// touching the call sites in Program.cs. All providers speak the same shape:
// SSML built from ScriptGenerator.FormatVoiceProfile, one MP3 written to outputPath.
interface ITtsProvider
{
    Task SynthesizeAsync(string text, string language, string outputPath, ContentFormat format = ContentFormat.Educational);
}
