using System.Diagnostics;

namespace DriftwoodServer;

// Owns the game process: launch, log capture, and a stop that gives the game a chance to save.
internal sealed class GameProcess : IAsyncDisposable
{
    private const long MaxGameLogBytes = 32L * 1024 * 1024;
    private readonly HostOptions _options;
    private readonly Process _process;
    private readonly TextWriter _log;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;

    private GameProcess(HostOptions options, Process process, TextWriter log)
    {
        _options = options;
        _process = process;
        _log = log;
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public Task<int> ExitTask => _exit.Task;

    public static GameProcess Start(HostOptions options, IChildProcessContainer childProcesses)
    {
        string executable = Path.Combine(options.GameRoot, options.GameExecutable);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The game executable is missing from this server's folder.", executable);
        }
        Directory.CreateDirectory(options.StateRoot);

        // The game will not run outside Steam without steam_appid.txt next to the executable:
        // SteamAPI.RestartAppIfNecessary fires and the Heathen wrapper calls Application.Quit()
        // - exit code 0, clean log, nothing to debug. The fleet's installer writes this file; a
        // self-host install built from the README's self-hosting guide has nobody else to do it, so the
        // supervisor owns it the same way it owns the host mod's configuration. Proven on the
        // first executed walk of that doc: without this file every start died loading the world,
        // with it the same install hosted.
        string steamAppIdPath = Path.Combine(options.GameRoot, "steam_appid.txt");
        string steamAppId = options.PinnedBuild.AppId.ToString();
        try
        {
            if (!File.Exists(steamAppIdPath)
                || !string.Equals(File.ReadAllText(steamAppIdPath).Trim(), steamAppId, StringComparison.Ordinal))
            {
                File.WriteAllText(steamAppIdPath, steamAppId);
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not write steam_appid.txt into the game folder ({steamAppIdPath}). Without it the game exits during boot with a clean log and no error.",
                exception);
        }

        TextWriter log = TextWriter.Synchronized(
            new RotatingFileTextWriter(Path.Combine(options.StateRoot, "driftwood-supervisor-game.log"), MaxGameLogBytes));

        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            // steam_appid.txt is read from the process WORKING DIRECTORY, not the executable's
            // directory. Launched without this, SteamAPI.RestartAppIfNecessary fires and the
            // Heathen wrapper calls Application.Quit() - the game exits code 0 with a clean log
            // and no obvious cause. That trap cost a full cycle during feasibility.
            WorkingDirectory = options.GameRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-batchmode");
        startInfo.ArgumentList.Add("-nographics");
        startInfo.ArgumentList.Add("-logFile");
        startInfo.ArgumentList.Add(Path.Combine(options.StateRoot, "unity.log"));

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        GameProcess game = new(options, process, log);
        process.OutputDataReceived += (_, e) => game.OnLine("OUT", e.Data);
        process.ErrorDataReceived += (_, e) => game.OnLine("ERR", e.Data);
        process.Exited += (_, _) => game.OnExited();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The game process did not start.");
            try
            {
                // Contain the child so a supervisor crash cannot leave an orphan holding the port.
                childProcesses.Assign(process);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw;
            }
            log.WriteLine($"[{DateTimeOffset.UtcNow:O}] START pid={process.Id} port={options.GamePort}");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return game;
        }
        catch
        {
            process.Dispose();
            log.Dispose();
            throw;
        }
    }

    // The graceful path is: write stop.requested, let the host mod save and call Application.Quit,
    // then wait for the save tree to settle before any force kill. The game itself saves three
    // times on a clean exit (the host mod's explicit save, Server.OnStopServer, and
    // SaveManager.OnApplicationQuit), and only one of those depends on us.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process.HasExited) return;
        _stopping = true;
        try
        {
            AtomicFile.WriteText(_options.StopFilePath, DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine);
        }
        catch (IOException exception)
        {
            _log.WriteLine($"[{DateTimeOffset.UtcNow:O}] STOP_FILE_FAILED {exception.Message}");
        }

        using CancellationTokenSource graceful = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        graceful.CancelAfter(TimeSpan.FromSeconds(_options.GracefulStopSeconds));
        try
        {
            await _process.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
            _log.WriteLine($"[{DateTimeOffset.UtcNow:O}] STOP graceful");
            return;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        // It did not go quietly. Do not kill into the middle of a save: wait until the save tree
        // stops changing, or until the budget runs out, and record which happened.
        bool quiescent = false;
        try
        {
            quiescent = await SaveTreeQuiescence.WaitAsync(
                _options.SaveRoot,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(2),
                3,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        try
        {
            if (!_process.HasExited)
            {
                _log.WriteLine($"[{DateTimeOffset.UtcNow:O}] FORCE_KILL saveTreeQuiescent={quiescent}");
                _process.Kill(true);
            }
        }
        catch
        {
        }
        try
        {
            using CancellationTokenSource killWait = new(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void OnLine(string stream, string? line)
    {
        if (line is null) return;
        lock (_log) _log.WriteLine($"[{DateTimeOffset.UtcNow:O}] {stream} {line}");
    }

    private void OnExited()
    {
        int exitCode;
        try { exitCode = _process.ExitCode; } catch { exitCode = -1; }
        _exit.TrySetResult(exitCode);
        lock (_log) _log.WriteLine($"[{DateTimeOffset.UtcNow:O}] EXIT code={exitCode} expected={_stopping}");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _process.Dispose();
        _log.Dispose();
    }
}
