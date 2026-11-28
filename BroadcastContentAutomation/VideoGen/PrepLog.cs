// L10 — per-job prep log. VideoGen used to log only to stdout, so a prep
// launched manually (or whose redirect was lost, or that died with the VM)
// left no failure record. PrepLog mirrors every Console line into
// <job>/prep.log with the same append-with-lock shape as Uploader/Logger.
// Failure lines already go to Console.Error (top-level FATAL catch,
// config errors, SIGTERM/SIGINT handlers) and stderr is mirrored too,
// so a failure survives even a stdout-only capture.

using System.Text;

public static class PrepLog
{
    private static readonly object _gate = new();
    private static string? _file;

    /// <summary>Path of the installed log file, or null if not installed.</summary>
    public static string? FilePath => _file;

    /// <summary>
    /// Install the prep.log mirror for this job. One log per process: a
    /// second call is a no-op (a prep run has exactly one job dir).
    /// </summary>
    public static void Install(string jobDir)
    {
        lock (_gate)
        {
            if (_file is not null)
                return;
            _file = Path.Combine(jobDir, "prep.log");
            Console.SetOut(new TeeWriter(Console.Out, _file, _gate));
            Console.SetError(new TeeWriter(Console.Error, _file, _gate));
        }
    }

    /// <summary>
    /// A TextWriter that passes every line through to the wrapped writer
    /// and appends it to the prep log file. Best-effort: a file failure
    /// must never kill the prep.
    /// </summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly string _file;
        private readonly object _gate;
        private readonly StringBuilder _pending = new();

        public TeeWriter(TextWriter inner, string file, object gate)
        {
            _inner = inner;
            _file = file;
            _gate = gate;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            lock (_gate)
            {
                _inner.Write(value);
                if (value == '\n')
                    FlushPendingLineLocked();
                else
                    _pending.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;
            lock (_gate)
            {
                _inner.Write(value);
                _pending.Append(value);
                FlushCompleteLinesLocked();
            }
        }

        public override void WriteLine()
        {
            lock (_gate)
            {
                _inner.WriteLine();
                FlushPendingLineLocked();
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _inner.WriteLine(value);
                if (value is not null)
                    _pending.Append(value);
                FlushPendingLineLocked();
            }
        }

        public override void Flush()
        {
            lock (_gate)
            {
                if (_pending.Length > 0)
                    FlushPendingLineLocked();
                _inner.Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Flush();
            base.Dispose(disposing);
        }

        private void FlushCompleteLinesLocked()
        {
            var text = _pending.ToString();
            var idx = text.LastIndexOf('\n');
            if (idx < 0)
                return;
            var complete = text[..idx];
            _pending.Clear();
            _pending.Append(text[(idx + 1)..]);
            foreach (var line in complete.Split('\n'))
                AppendLine(line);
        }

        private void FlushPendingLineLocked()
        {
            var line = _pending.ToString();
            _pending.Clear();
            AppendLine(line);
        }

        private void AppendLine(string line)
        {
            if (line.EndsWith('\r'))
                line = line[..^1];
            try
            {
                File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Best-effort mirror: a log-file failure must never fail the prep.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
