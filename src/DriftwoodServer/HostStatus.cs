namespace DriftwoodServer;

internal enum HostPhase
{
    Starting,
    // The world is running and a player can join. Never written from a bound port alone.
    Hosting,
    // A named, terminal refusal. One plain sentence, readable by a support person.
    WillNotHost,
    Stopping,
    Stopped
}

internal sealed record HostStatus(
    HostPhase Phase,
    string Reason,
    DateTimeOffset Timestamp,
    string InstanceId,
    string SupervisorVersion,
    string PluginVersion,
    string GameVersion,
    string PinnedBuildId,
    int SupervisorPid,
    int GamePid,
    int GamePort,
    int Slots,
    int Players,
    bool Full,
    bool WorldRunning,
    string WorldName,
    long SwallowedExceptions,
    IReadOnlyList<string> PatchesFailed);

internal sealed class StatusStore
{
    private readonly HostOptions _options;
    private readonly string _statusPath;
    private readonly object _sync = new();
    private HostStatus? _latest;

    public StatusStore(HostOptions options)
    {
        _options = options;
        _statusPath = Path.Combine(options.StateRoot, "status.json");
    }

    public HostStatus? Latest
    {
        get { lock (_sync) return _latest; }
    }

    public void Write(
        HostPhase phase,
        string reason,
        int gamePid = 0,
        ReadinessDocument? readiness = null,
        string pinnedBuildId = "")
    {
        // -1 is UNKNOWN and must survive every hop. Defaulting a missing reading to 0 is how a
        // server that has not reported yet gets mistaken for an empty one.
        int players = readiness?.Players ?? -1;
        int slots = readiness?.Slots ?? _options.Slots;
        HostStatus status = new(
            phase,
            reason,
            DateTimeOffset.UtcNow,
            _options.InstanceId,
            DriftwoodVersion.Value,
            readiness?.PluginVersion ?? string.Empty,
            readiness?.GameVersion ?? string.Empty,
            pinnedBuildId,
            Environment.ProcessId,
            gamePid,
            readiness?.Port ?? _options.GamePort,
            slots,
            players,
            players >= 0 && slots > 0 && players >= slots,
            phase == HostPhase.Hosting && (readiness?.WorldRunning ?? false),
            readiness?.WorldName ?? _options.WorldName,
            readiness?.SwallowedTotal ?? 0,
            readiness?.PatchesFailed ?? []);
        lock (_sync)
        {
            _latest = status;
            AtomicFile.WriteJson(_statusPath, status);
        }
    }
}

internal static class DriftwoodVersion
{
    public static string Value =>
        typeof(DriftwoodVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
