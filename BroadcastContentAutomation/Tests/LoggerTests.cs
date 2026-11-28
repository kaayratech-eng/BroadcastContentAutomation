// L24 (failure-logging plan): the Logger had no exception/stack support,
// so every caller logged ex.Message only and the type/stack was lost; and a
// log-write failure threw out of the logger itself, masking the original
// failure. The Error(string, Exception) overload records type + message +
// first stack frame, and a failed file write falls back to console-only
// with a one-time warning instead of throwing.
using GiggleGarden.Uploader;

namespace BroadcastContentAutomation.Tests;

public class LoggerTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tn-logger-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Thrower() => throw new InvalidOperationException("boom-test-message");

    [Fact]
    public void Error_WithException_LogsTypeMessageAndStack()
    {
        var dir = TempDir("ex");
        try
        {
            var logDir = Path.Combine(dir, "logs");
            var logger = new Logger(logDir);
            Exception caught;
            try
            {
                Thrower();
                throw new Exception("unreachable");
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            logger.Error("something failed", caught);

            var logFile = Directory.GetFiles(logDir, "upload-*.log").Single();
            var text = File.ReadAllText(logFile);
            Assert.Contains("something failed", text);
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("boom-test-message", text);
            // The first stack frame names the throw site.
            Assert.Contains("at ", text);
            Assert.Contains("Thrower", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LogFileUnwritable_DoesNotThrow()
    {
        // A directory sitting where the log file should be makes every
        // append fail. Logging must still return and the console must
        // still get the lines, with exactly one warning about the fallback.
        var dir = TempDir("nowrite");
        try
        {
            var logDir = Path.Combine(dir, "logs");
            Directory.CreateDirectory(logDir);
            Directory.CreateDirectory(Path.Combine(logDir, $"upload-{DateTime.Now:yyyy-MM-dd}.log"));
            var logger = new Logger(logDir);

            var originalOut = Console.Out;
            var capture = new StringWriter();
            Console.SetOut(capture);
            try
            {
                logger.Info("first line");
                logger.Info("second line");
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var console = capture.ToString();
            Assert.Contains("first line", console);
            Assert.Contains("second line", console);
            Assert.Contains("unwritable", console);
            Assert.Contains("logging to console only", console);
            Assert.Equal(1, CountOccurrences(console, "unwritable"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
