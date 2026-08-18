namespace GiggleGarden.Uploader;

public sealed class Logger(string dir)
{
    private readonly string _file = Path.Combine(
        Directory.CreateDirectory(dir).FullName,
        $"upload-{DateTime.Now:yyyy-MM-dd}.log");

    private readonly object _gate = new();

    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg) => Write("ERROR", msg);

    private void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {msg}";
        lock (_gate)
        {
            Console.WriteLine(line);
            File.AppendAllText(_file, line + Environment.NewLine);
        }
    }
}
