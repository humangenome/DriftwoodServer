namespace DriftwoodServer;

internal sealed class HostApplication
{
    private readonly HostOptions _options;
    private readonly StatusStore _status;

    public HostApplication(HostOptions options)
    {
        _options = options;
        _status = new StatusStore(options);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.StateRoot);
        Directory.CreateDirectory(_options.SaveRoot);
        using InstanceLock instanceLock = InstanceLock.Acquire(_options.StateRoot);
        using HostLogSession log = HostLogSession.Start(_options.StateRoot);
        using CancellationTokenSource background = new();

        _status.Write(HostPhase.Starting, "Verifying the pinned game build");

        GameProcess? game = null;
        WindowsKillOnCloseJob? childJob = null;
        HealthEndpoint? health = null;
        Task? healthTask = null;
        string pinnedBuildId = string.Empty;
        bool requestedStop = false;

        try
        {
            // GATE 1 - the build pin. Refuse before anything else so a Steam update can never
            // silently move a customer onto an unvalidated build.
            BuildPinResult pin = BuildPin.Verify(
                _options.GameRoot,
                _options.PinnedBuild.AssemblyRelativePath,
                _options.PinnedBuild.AssemblySha256,
                _options.PinnedBuild.AppId,
                string.IsNullOrWhiteSpace(_options.PinnedBuild.BuildId) ? null : _options.PinnedBuild.BuildId,
                string.IsNullOrWhiteSpace(_options.SteamAppsDirectory) ? null : _options.SteamAppsDirectory);
            pinnedBuildId = pin.ActualBuildId;
            if (!pin.Ok)
            {
                Refuse(pin.Reason, pinnedBuildId);
                Console.Error.WriteLine($"ACTUAL_ASSEMBLY_SHA256={pin.ActualAssemblyHash}");
                return;
            }
            Console.WriteLine($"BUILD_PIN_OK assembly={pin.ActualAssemblyHash} steamBuild={pin.ActualBuildId}");

            // GATE 2 - the host mod is present, and is the one we pinned.
            string hostModPath = Path.Combine(_options.GameRoot, "BepInEx", "plugins", "DriftwoodHost.dll");
            if (!File.Exists(hostModPath))
            {
                Refuse("This server will not start because the Driftwood host mod is missing from its game folder. Without it the game would run as a single-player client and nobody could join.", pinnedBuildId);
                return;
            }
            if (_options.PinnedBuild.HostModSha256.Length == 64)
            {
                try
                {
                    FileHashVerifier.Verify(hostModPath, _options.PinnedBuild.HostModSha256, "The Driftwood host mod");
                }
                catch (InvalidDataException)
                {
                    Refuse("This server will not start because the Driftwood host mod in its game folder is not the validated build.", pinnedBuildId);
                    return;
                }
            }

            // The supervisor owns the host mod's configuration. Written before every start, and
            // asserted against the running server afterwards.
            PluginConfigWriter.Write(_options);
            TryDelete(_options.StopFilePath);
            TryDelete(_options.ReadinessPath);

            childJob = WindowsKillOnCloseJob.Create();
            game = GameProcess.Start(_options, childJob);
            _status.Write(HostPhase.Starting, "Loading the world", game.Id, pinnedBuildId: pinnedBuildId);

            health = new HealthEndpoint(_options.HttpPort, _status);
            healthTask = health.RunAsync(background.Token);

            ReadinessDocument? readiness = await WaitForWorldAsync(game, pinnedBuildId, cancellationToken).ConfigureAwait(false);
            if (readiness is null)
            {
                requestedStop = true;
                return;
            }

            // Writing the config is not configuring it. Assert the values are in force on the
            // RUNNING server before telling anyone this server is up.
            string? mismatch = PluginConfigWriter.AssertTookEffect(_options, readiness);
            if (mismatch is not null)
            {
                Refuse(mismatch, pinnedBuildId, game.Id, readiness);
                return;
            }

            _status.Write(HostPhase.Hosting,
                $"Hosting \"{readiness.WorldName}\" on port {readiness.Port}",
                game.Id, readiness, pinnedBuildId);
            Console.WriteLine($"DRIFTWOOD_HOSTING port={readiness.Port} slots={readiness.Slots} world={readiness.WorldName} pid={game.Id}");

            long lastSwallowed = readiness.SwallowedTotal;
            while (!cancellationToken.IsCancellationRequested)
            {
                Task completed = await Task.WhenAny(
                    game.ExitTask,
                    Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), cancellationToken)).ConfigureAwait(false);
                if (completed == game.ExitTask)
                {
                    int exitCode = await game.ExitTask.ConfigureAwait(false);
                    if (File.Exists(_options.StopFilePath) || cancellationToken.IsCancellationRequested)
                    {
                        requestedStop = true;
                        break;
                    }
                    // A stop file is normally consumed by the host mod: it saves, writes a final
                    // "Stopped" readiness document, deletes the file, and quits. By the time the
                    // exit lands here the file is already gone - so without this check every
                    // clean requested stop is reported as "stopped unexpectedly", which reads
                    // like a crash to exactly the operator who just followed the manual. The
                    // readiness document is deleted before every start, so a "Stopped" phase can
                    // only have been written by this run's own deliberate shutdown.
                    ReadinessDocument? finalReadiness = ReadinessDocument.TryRead(_options.ReadinessPath);
                    if (finalReadiness is not null
                        && string.Equals(finalReadiness.Phase, "Stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        requestedStop = true;
                        break;
                    }
                    Refuse($"This server stopped unexpectedly (exit code {exitCode}).", pinnedBuildId);
                    return;
                }

                ReadinessDocument? current = ReadinessDocument.TryRead(_options.ReadinessPath);
                if (current is null || !current.IsFresh(TimeSpan.FromSeconds(_options.ReadinessStaleSeconds)))
                {
                    // A stale readiness file is not a healthy server. The game process can wedge
                    // with the port still bound and the last document still saying "Hosting".
                    _status.Write(HostPhase.Starting,
                        "This server has stopped reporting its state; it may be wedged.",
                        game.Id, current, pinnedBuildId);
                    continue;
                }

                if (current.SwallowedTotal - lastSwallowed > 0)
                {
                    Console.Error.WriteLine(
                        $"SWALLOWED_EXCEPTIONS +{current.SwallowedTotal - lastSwallowed} (total {current.SwallowedTotal}). Catching is not fixing - investigate.");
                    lastSwallowed = current.SwallowedTotal;
                }

                if (!current.WorldRunning)
                {
                    _status.Write(HostPhase.Starting, current.Reason, game.Id, current, pinnedBuildId);
                    continue;
                }
                _status.Write(HostPhase.Hosting,
                    $"Hosting \"{current.WorldName}\" on port {current.Port}",
                    game.Id, current, pinnedBuildId);
            }
            requestedStop = true;
        }
        catch (OperationCanceledException)
        {
            requestedStop = true;
        }
        catch (Exception exception)
        {
            Refuse(exception.Message, pinnedBuildId, game?.Id ?? 0);
            throw;
        }
        finally
        {
            if (requestedStop)
            {
                _status.Write(HostPhase.Stopping, "Saving and shutting down", game?.Id ?? 0, pinnedBuildId: pinnedBuildId);
            }
            if (game is not null)
            {
                try { await game.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { Console.Error.WriteLine($"Game shutdown failed: {exception.Message}"); }

                // Save-then-snapshot, in that order and AFTER the process is down, so the archive
                // contains the flush rather than the file the flush replaced. A forced kill skips
                // the game's own quit-time save entirely, which is exactly when a stale snapshot
                // would be most damaging.
                if (!string.IsNullOrWhiteSpace(_options.BackupRoot))
                {
                    try
                    {
                        using CancellationTokenSource snapshotCancel = new(TimeSpan.FromMinutes(3));
                        SaveSnapshot.Result snapshot = await new SaveSnapshot(_options)
                            .CaptureAsync(_options.AuthToken, snapshotCancel.Token).ConfigureAwait(false);
                        Console.WriteLine(snapshot.Ok
                            ? $"SHUTDOWN_SNAPSHOT_OK {snapshot.Path} ({snapshot.Bytes} bytes)"
                            : $"SHUTDOWN_SNAPSHOT_FAILED {snapshot.Reason}");
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine($"Shutdown snapshot failed: {exception.Message}");
                    }
                }
            }
            childJob?.Dispose();
            background.Cancel();
            if (healthTask is not null)
            {
                try { await healthTask.ConfigureAwait(false); } catch { }
            }
            health?.Dispose();
            TryDelete(_options.StopFilePath);
            if (requestedStop) _status.Write(HostPhase.Stopped, "Stopped cleanly", pinnedBuildId: pinnedBuildId);
        }
    }

    private async Task<ReadinessDocument?> WaitForWorldAsync(GameProcess game, string pinnedBuildId, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(_options.WorldReadyTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (game.HasExited)
            {
                Refuse($"This server stopped while loading its world (exit code {await game.ExitTask.ConfigureAwait(false)}).", pinnedBuildId, game.Id);
                return null;
            }
            ReadinessDocument? readiness = ReadinessDocument.TryRead(_options.ReadinessPath);
            if (readiness is not null)
            {
                if (string.Equals(readiness.Phase, "WillNotHost", StringComparison.OrdinalIgnoreCase))
                {
                    // The host mod refused. Its sentence is the useful one - pass it through
                    // rather than replacing it with a generic timeout message.
                    Refuse(readiness.Reason, pinnedBuildId, game.Id, readiness);
                    return null;
                }
                if (readiness.WorldRunning) return readiness;
            }
            await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), cancellationToken).ConfigureAwait(false);
        }
        Refuse($"This server's world did not finish loading within {_options.WorldReadyTimeoutSeconds} seconds, so it reports as down rather than as a healthy server with nothing behind it.", pinnedBuildId, game.Id);
        return null;
    }

    private void Refuse(string reason, string pinnedBuildId, int gamePid = 0, ReadinessDocument? readiness = null)
    {
        Console.Error.WriteLine($"DRIFTWOOD WILL NOT HOST: {reason}");
        _status.Write(HostPhase.WillNotHost, reason, gamePid, readiness, pinnedBuildId);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
