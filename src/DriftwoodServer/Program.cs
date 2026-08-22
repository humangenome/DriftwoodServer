namespace DriftwoodServer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0].Equals("--verify-build", StringComparison.OrdinalIgnoreCase))
            {
                // A support verb: answer "is this install the build we validated?" without
                // starting anything.
                HostOptions verifyOptions = HostOptions.Load(args[1]);
                BuildPinResult result = BuildPin.Verify(
                    verifyOptions.GameRoot,
                    verifyOptions.PinnedBuild.AssemblyRelativePath,
                    verifyOptions.PinnedBuild.AssemblySha256,
                    verifyOptions.PinnedBuild.AppId,
                    string.IsNullOrWhiteSpace(verifyOptions.PinnedBuild.BuildId) ? null : verifyOptions.PinnedBuild.BuildId,
                    string.IsNullOrWhiteSpace(verifyOptions.SteamAppsDirectory) ? null : verifyOptions.SteamAppsDirectory);
                Console.WriteLine(result.Ok ? "BUILD_PIN_OK" : "BUILD_PIN_FAILED");
                Console.WriteLine(result.Reason);
                Console.WriteLine($"assemblySha256={result.ActualAssemblyHash}");
                Console.WriteLine($"steamBuildId={result.ActualBuildId}");
                return result.Ok ? 0 : 2;
            }

            if (args.Length == 2 && args[0].Equals("--snapshot", StringComparison.OrdinalIgnoreCase))
            {
                // Panel-triggered backup of a RUNNING server. Save first, snapshot second - a
                // snapshot taken before the flush captures the stale file the flush was meant to
                // replace, and says nothing about it.
                HostOptions snapshotOptions = HostOptions.Load(args[1]);
                using CancellationTokenSource snapshotCancel = new(TimeSpan.FromMinutes(5));
                SaveSnapshot.Result snapshot = await new SaveSnapshot(snapshotOptions)
                    .CaptureAsync(snapshotOptions.AuthToken, snapshotCancel.Token)
                    .ConfigureAwait(false);
                Console.WriteLine(snapshot.Ok ? "SNAPSHOT_OK" : "SNAPSHOT_FAILED");
                Console.WriteLine(snapshot.Reason);
                if (snapshot.Ok) Console.WriteLine($"path={snapshot.Path} bytes={snapshot.Bytes}");
                return snapshot.Ok ? 0 : 3;
            }

            string configPath = ParseConfigPath(args);
            HostOptions options = HostOptions.Load(configPath);
            using CancellationTokenSource shutdown = new();
            ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; shutdown.Cancel(); };
            EventHandler exitHandler = (_, _) => shutdown.Cancel();
            Console.CancelKeyPress += cancelHandler;
            AppDomain.CurrentDomain.ProcessExit += exitHandler;
            try
            {
                await new HostApplication(options).RunAsync(shutdown.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"HOST_FAILED: {exception.Message}");
            return 1;
        }
    }

    private static string ParseConfigPath(string[] args)
    {
        if (args.Length != 2 || !args[0].Equals("--config", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Usage: DriftwoodServer --config <appsettings.json> | --verify-build <appsettings.json> | --snapshot <appsettings.json>");
        }
        return Path.GetFullPath(args[1]);
    }
}
