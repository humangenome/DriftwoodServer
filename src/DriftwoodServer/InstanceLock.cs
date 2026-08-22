namespace DriftwoodServer;

internal sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    public static InstanceLock Acquire(string stateRoot)
    {
        Directory.CreateDirectory(stateRoot);
        string path = Path.Combine(stateRoot, "host.lock");
        try
        {
            FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using StreamWriter writer = new(stream, leaveOpen: true);
            writer.WriteLine(Environment.ProcessId);
            writer.WriteLine(DateTimeOffset.UtcNow.ToString("O"));
            writer.Flush();
            stream.Flush(true);
            return new InstanceLock(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Another Driftwood host owns this instance state directory", exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
