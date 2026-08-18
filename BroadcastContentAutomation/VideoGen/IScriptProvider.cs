// Lets script generation swap LLM providers (Claude, Groq, ...) without touching
// the call sites in Program.cs. Providers just turn a prompt into raw text -
// ScriptGenerator owns building the prompt and parsing/validating the response.
interface IScriptProvider
{
    Task<string> CompleteAsync(string prompt);
}
