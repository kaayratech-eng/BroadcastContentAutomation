// Single-instance lock (review #2 B28): two concurrent uploader runs can
// both read Pending and both publish (the ExistingId check cannot help —
// neither has saved an id yet). A FileStream opened with FileShare.None is
// held for the whole run; a second process gets an IOException and exits
// without touching anything. The lock is kernel-held (flock on Unix), so a
// killed run releases it automatically; the lock file itself is left
// behind and re-used by the next run.
namespace GiggleGarden.Uploader;

sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLock(FileStream stream) => _stream = stream;

    // Returns null when another process already holds the lock.
    public static SingleInstanceLock? TryAcquire(string lockPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(lockPath));
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstanceLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
