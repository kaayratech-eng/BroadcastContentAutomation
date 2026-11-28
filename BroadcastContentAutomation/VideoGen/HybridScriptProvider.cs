// Splits a single script's acts across two providers instead of one - e.g. Groq
// for acts 1-2, Gemini for acts 3-4 - so each provider's daily quota only has to
// cover half a script's requests. ScriptGenerator always calls CompleteAsync
// exactly once per act, in fixed sequential order (main call, then continuation
// acts 2-4), and each continuation prompt already carries a recap of the prior
// act's scenes regardless of who wrote them - so a plain call-count switch is
// enough here; no changes needed in ScriptGenerator itself.
using GiggleGarden.Shared;

class HybridScriptProvider(IScriptProvider first, IScriptProvider second, int firstProviderCalls) : IScriptProvider
{
    int calls;

    public async Task<string> CompleteAsync(string prompt, PrepIdentity? context = null)
    {
        calls++;
        var useFirst = calls <= firstProviderCalls;
        var chosen = useFirst ? first : second;
        var other = useFirst ? second : first;
        Console.WriteLine($"  {PrepContext.PrefixFor(context)}[hybrid] call {calls} -> {chosen.GetType().Name}");
        try
        {
            return await chosen.CompleteAsync(prompt, context);
        }
        catch (Exception ex)
        {
            // L67: a failed call used to consume its slot silently — the
            // exception propagated with no hybrid-level log, so nothing
            // said which leg failed or which leg the next call would use.
            // The switch still advances on failure (unchanged behaviour);
            // the log says so, so the consumed slot is visible.
            Console.WriteLine($"  {PrepContext.PrefixFor(context)}[hybrid] call {calls} -> {chosen.GetType().Name} failed: {Text.Tail(ex.Message.Replace('\n', ' ').Replace('\r', ' '), 200)} — next call will use {other.GetType().Name} (the failed call still advanced the switch, slot {calls} consumed)");
            throw;
        }
    }
}
