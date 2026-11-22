using System;

// The few game and loader members SnapshotStore touches, standing in so the real store runs in a test.
// The world flush always succeeds and nothing here starts or ends a game.
namespace DriftwoodHost
{
    internal static class Plugin
    {
        internal sealed class StandInLog
        {
            internal void LogInfo(object message) { }
            internal void LogWarning(object message) { }
            internal void LogError(object message) { }
        }

        internal static StandInLog Log { get; } = new StandInLog();
    }

    internal static class WorldLifecycle
    {
        internal static bool FlushForSnapshot() => true;
    }
}

namespace UnityEngine
{
    internal static class Application
    {
        internal static void Quit(int exitCode) => throw new InvalidOperationException("no test ends a game");
    }
}
