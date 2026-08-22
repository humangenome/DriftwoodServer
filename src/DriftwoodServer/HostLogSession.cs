using System.Text;

namespace DriftwoodServer;

internal sealed class HostLogSession : IDisposable
{
    private const long DefaultMaxLogBytes = 16L * 1024 * 1024;
    private readonly TextWriter _originalOutput;
    private readonly TextWriter _originalError;
    private readonly TextWriter _log;

    private HostLogSession(TextWriter originalOutput, TextWriter originalError, TextWriter log)
    {
        _originalOutput = originalOutput;
        _originalError = originalError;
        _log = log;
    }

    public static HostLogSession Start(string stateRoot)
    {
        Directory.CreateDirectory(stateRoot);
        TextWriter log = new RotatingFileTextWriter(
            Path.Combine(stateRoot, "host.log"),
            DefaultMaxLogBytes);
        TextWriter synchronizedLog = TextWriter.Synchronized(log);
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        Console.SetOut(new TeeTextWriter(originalOutput, synchronizedLog));
        Console.SetError(new TeeTextWriter(originalError, synchronizedLog));
        log.WriteLine($"[{DateTimeOffset.UtcNow:O}] HOST_START pid={Environment.ProcessId}");
        return new HostLogSession(originalOutput, originalError, log);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOutput);
        Console.SetError(_originalError);
        _log.WriteLine($"[{DateTimeOffset.UtcNow:O}] HOST_END pid={Environment.ProcessId}");
        _log.Dispose();
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _first;
        private readonly TextWriter _second;

        public TeeTextWriter(TextWriter first, TextWriter second)
        {
            _first = first;
            _second = second;
        }

        public override Encoding Encoding => _first.Encoding;

        public override void Write(char value)
        {
            _first.Write(value);
            _second.Write(value);
        }

        public override void Write(string? value)
        {
            _first.Write(value);
            _second.Write(value);
        }

        public override void WriteLine(string? value)
        {
            _first.WriteLine(value);
            _second.WriteLine(value);
        }

        public override void Flush()
        {
            _first.Flush();
            _second.Flush();
        }
    }
}

internal sealed class RotatingFileTextWriter : TextWriter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly string _path;
    private readonly string _backupPath;
    private readonly long _maxBytes;
    private readonly object _sync = new();
    private FileStream _stream = null!;
    private StreamWriter _writer = null!;
    private bool _disposed;

    public RotatingFileTextWriter(string path, long maxBytes)
    {
        if (maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _path = path;
        _backupPath = path + ".1";
        _maxBytes = maxBytes;
        Open();
        if (File.Exists(_backupPath)) TrimBackup();
        RotateIfNeeded();
    }

    public override Encoding Encoding => Utf8;

    public override void Write(char value)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _writer.Write(value);
            RotateIfNeeded();
        }
    }

    public override void Write(string? value)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _writer.Write(value);
            RotateIfNeeded();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _writer.WriteLine(value);
            RotateIfNeeded();
        }
    }

    public override void Flush()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _writer.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Open()
    {
        _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_stream, Utf8) { AutoFlush = true };
    }

    private void RotateIfNeeded()
    {
        if (_stream.Length < _maxBytes) return;
        _writer.Dispose();
        File.Move(_path, _backupPath, true);
        try
        {
            TrimBackup();
        }
        finally
        {
            Open();
        }
        _writer.WriteLine($"[{DateTimeOffset.UtcNow:O}] HOST_LOG_ROTATED");
    }

    private void TrimBackup()
    {
        FileInfo backup = new(_backupPath);
        if (backup.Length <= _maxBytes) return;

        string temporaryPath = _backupPath + ".tmp";
        try
        {
            using (FileStream source = new(_backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream destination = new(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                source.Seek(-_maxBytes, SeekOrigin.End);
                source.CopyTo(destination);
                destination.Flush(true);
            }
            File.Move(temporaryPath, _backupPath, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
