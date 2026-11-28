// Lets script generation swap LLM providers (Claude, Groq, ...) without touching
// the call sites in Program.cs. Providers just turn a prompt into raw text -
// ScriptGenerator owns building the prompt and parsing/validating the response.
//
// L21: the optional context carries the prep identity (job folder name,
// topic, act) so a leg's own log lines — including its RetryHandler
// callback — can prefix [jobId]. Callers that pass none get the ambient
// PrepContext.Current instead (see PrepContext.PrefixFor).
interface IScriptProvider
{
    Task<string> CompleteAsync(string prompt, PrepIdentity? context = null);
}
