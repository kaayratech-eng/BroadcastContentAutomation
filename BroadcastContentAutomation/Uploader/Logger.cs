namespace GiggleGarden.Uploader;

public sealed class Logger(string dir)
{
    private readonly string _file = Path.Combine(
        Directory.CreateDirectory(dir).FullName,
        $"upload-{DateTime.Now:yyyy-MM-dd}.log");

    private readonly object _gate = new();
    private bool _fileWarningShown;

    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg) => Write("ERROR", msg);

    // L24: callers used to log ex.Message only, losing the exception type
    // and stack (only the top-level FATAL kept them). This overload records
    // the type + message + the first stack frame, so the throw site is
    // visible in the log file.
    public void Error(string msg, Exception ex)
    {
        var firstFrame = ex.StackTrace?.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("at ", StringComparison.Ordinal));
        Write("ERROR", firstFrame is null
            ? $"{msg} — {ex.GetType().Name}: {ex.Message}"
            : $"{msg} — {ex.GetType().Name}: {ex.Message} ({firstFrame})");
    }

    private void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {msg}";
        lock (_gate)
        {
            Console.WriteLine(line);
            try
            {
                File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // L24: a log-write failure used to throw out of the logger
                // itself and mask the original failure. Fall back to
                // console-only and warn once, so logging never throws.
                if (!_fileWarningShown)
                {
                    _fileWarningShown = true;
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} [WARN] log file {_file} is unwritable " +
                        $"({ex.GetType().Name}: {ex.Message}); logging to console only");
                }
            }
        }
    }
}
