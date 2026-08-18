// Splits a single script's acts across two providers instead of one - e.g. Groq
// for acts 1-2, Gemini for acts 3-4 - so each provider's daily quota only has to
// cover half a script's requests. ScriptGenerator always calls CompleteAsync
// exactly once per act, in fixed sequential order (main call, then continuation
// acts 2-4), and each continuation prompt already carries a recap of the prior
// act's scenes regardless of who wrote them - so a plain call-count switch is
// enough here; no changes needed in ScriptGenerator itself.
class HybridScriptProvider(IScriptProvider first, IScriptProvider second, int firstProviderCalls) : IScriptProvider
{
    int calls;

    public Task<string> CompleteAsync(string prompt)
    {
        calls++;
        var useFirst = calls <= firstProviderCalls;
        Console.WriteLine($"  [hybrid] call {calls} -> {(useFirst ? first : second).GetType().Name}");
        return (useFirst ? first : second).CompleteAsync(prompt);
    }
}
