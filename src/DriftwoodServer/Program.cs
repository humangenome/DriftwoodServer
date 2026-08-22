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
            throw new ArgumentException("Usage: DriftwoodServer --config <appsettings.json> | --verify-build <appsettings.json>");
        }
        return Path.GetFullPath(args[1]);
    }
}
