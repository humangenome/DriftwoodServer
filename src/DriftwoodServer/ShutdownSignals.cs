using System.Runtime.InteropServices;

namespace DriftwoodServer;

// Since .NET 10 a SIGTERM (systemctl stop, and on Windows a shutdown or a closed console) no longer
// raises ProcessExit by default, so the old ProcessExit handler alone would let the process die with
// the game still running and the save not flushed. These turn each of them into the same clean stop
// Ctrl+C gets.
internal static class ShutdownSignals
{
    public static IDisposable Register(CancellationTokenSource shutdown)
    {
        void Stop(PosixSignalContext context)
        {
            // Cancel = true: the process stays up until the host has finished stopping.
            context.Cancel = true;
            try { shutdown.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        return new Registrations(
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop),
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop));
    }

    private sealed class Registrations(params PosixSignalRegistration[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (PosixSignalRegistration item in items) item.Dispose();
        }
    }
}
