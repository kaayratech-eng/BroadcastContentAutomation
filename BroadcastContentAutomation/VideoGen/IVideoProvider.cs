// Type-level seam around ViduClient's submit/poll/download contract. Purely a seam
// for now - Program.cs constructs ViduClient through this interface instead of the
// concrete type, but ViduClient is still the only implementation and no behavior
// changes. Lets a future provider be dropped in without touching the orchestration
// logic in Program.cs/ScriptGenerator. See tasks/free-video-feasibility.md for why
// there is nothing free worth plugging in here yet.
interface IVideoProvider
{
    Task<ViduClient.Submission> SubmitAsync(
        string startFramePath, string prompt, double narrationSeconds, CancellationToken ct = default);

    Task<ViduClient.Result> PollAsync(string taskId, CancellationToken ct = default);

    Task DownloadAsync(string url, string outputPath, CancellationToken ct = default);
}
