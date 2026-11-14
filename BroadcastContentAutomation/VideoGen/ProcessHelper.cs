// Shared process runner for the helper-based scene-art clients.
using System.Diagnostics;
//
// Why helpers instead of direct HTTP: the Cloudflare, Gemini and
// Pollinations keys live in Muse's secure vault (custom.cloudflare /
// custom.gemini / custom.pollinations). VideoGen cannot read vault values,
// so the API calls go through the small Python helpers in
// ~/workspace/custom-connectors/custom.<provider>/bin/, which obtain the
// credential through the authd surrogate exchange at call time. No key,
// token or secret ever appears in config, arguments, environment variables
// or logs here — only the account id (a non-secret identifier) and the
// prompt text are passed on the command line, via ArgumentList (no shell),
// so prompt text cannot inject shell commands.
static class ProcessHelper
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    public static async Task<Result> RunAsync(
        string exe, IEnumerable<string> args, int timeoutMinutes = 10)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi)
            ?? throw new Exception($"Could not start {exe}.");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"{exe} did not finish within {timeoutMinutes} minutes.");
        }
        return new Result(p.ExitCode, await stdoutTask, await stderrTask);
    }

    // Guards the "already on disk" contract shared by every art client:
    // a resumed job never regenerates an image it already has.
    public static bool AlreadyOnDisk(string outputPath) =>
        File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;

    // Deterministic seed per output file, so a retry of the same beat
    // reproduces the same image instead of a new random one.
    public static int SeedFor(string outputPath) =>
        Math.Abs(outputPath.GetHashCode(StringComparison.Ordinal));

    public static void RequireFile(string outputPath, string what)
    {
        if (!AlreadyOnDisk(outputPath))
            throw new Exception($"{what} did not produce {outputPath}.");
    }
}
