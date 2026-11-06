using System.Diagnostics;
using Xunit;

namespace DriftwoodServer.Tests;

public sealed class ShutdownSignalsTests
{
    [Theory]
    [InlineData("TERM")]
    [InlineData("HUP")]
    public async Task A_stop_signal_cancels_the_host_and_the_process_stays_up(string signal)
    {
        if (OperatingSystem.IsWindows()) return;

        using CancellationTokenSource shutdown = new();
        using IDisposable registration = ShutdownSignals.Register(shutdown);

        using Process kill = Process.Start("kill", $"-{signal} {Environment.ProcessId}");
        await kill.WaitForExitAsync();

        await Task.Delay(TimeSpan.FromSeconds(20), shutdown.Token)
            .ContinueWith(_ => { }, TaskScheduler.Default);
        Assert.True(shutdown.IsCancellationRequested, $"SIG{signal} did not cancel the host");
    }
}
