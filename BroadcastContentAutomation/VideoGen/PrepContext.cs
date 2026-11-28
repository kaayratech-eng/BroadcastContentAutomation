// L21 — prep identity threading. Before this, the only job identifier in
// a prep was the `Workspace:` line, so no downstream log line ([script-pool],
// [trends], [wikipedia], [articles], [governor], retry lines) could ever
// name the job it belonged to. A PrepIdentity (job folder name + topic +
// act label) is now threaded explicitly through the prep signatures —
// IScriptProvider.CompleteAsync, the Wikipedia/Article fetch clients,
// GovernorClient, ScriptGenerator — and carried ambiently (AsyncLocal) so
// log sites that take no context (RetryHandler callbacks, the TTS pool,
// the top-level FATAL catch) can still prefix their lines with [jobId].
// The ambient is set once at each prep entry point in Program.cs and
// refined per act by ScriptGenerator; explicit parameters win over the
// ambient wherever a caller passes one.
using System.Threading;

sealed record PrepIdentity(string JobId, string? Topic = null, string? Act = null)
{
    public PrepIdentity WithAct(string act) => this with { Act = act };
}

static class PrepContext
{
    private static readonly AsyncLocal<PrepIdentity?> _current = new();

    /// <summary>Identity of the prep currently running on this async flow, if any.</summary>
    public static PrepIdentity? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>
    /// The `[jobId] ` prefix for a prep log line: the explicit context when
    /// one is passed, else the ambient prep identity, else "" (no prep
    /// running — e.g. a unit test or a non-prep mode). Never null.
    /// </summary>
    public static string PrefixFor(PrepIdentity? context)
    {
        var id = context?.JobId ?? Current?.JobId;
        return string.IsNullOrWhiteSpace(id) ? "" : $"[{id}] ";
    }

    /// <summary>Ambient-only form, for log callbacks that take no context.</summary>
    public static string Prefix => PrefixFor(null);

    /// <summary>
    /// L42 — the scene phrase for a TTS log line: "scene 12/55" when the
    /// caller passed a "{i+1}/{n}" label, "this scene" when it did not, so
    /// unlabeled call sites (outro, --test-tts) keep their old wording.
    /// </summary>
    public static string ScenePhrase(string? sceneLabel) =>
        string.IsNullOrWhiteSpace(sceneLabel) ? "this scene" : $"scene {sceneLabel.Trim()}";
}
