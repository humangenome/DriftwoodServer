using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriftwoodServer;

// The host mod's side of the contract. The supervisor NEVER infers hosting from the process being
// alive or the port being bound - it reads this, and refuses to report Hosting on anything else.
//
// That is the whole point of playbook 1d requirement 3, and specifically of its near-miss warning:
// Lodestone computed this signal correctly and shipped it behind a disabled admin gate, so nothing
// read it. A correct signal nothing consumes is worth exactly zero. This one is consumed here, and
// the health endpoint below is the proof.
internal sealed class ReadinessDocument
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("pluginVersion")] public string PluginVersion { get; set; } = string.Empty;
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = string.Empty;
    [JsonPropertyName("timestampUtc")] public DateTimeOffset TimestampUtc { get; set; }
    [JsonPropertyName("phase")] public string Phase { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("worldRunning")] public bool WorldRunning { get; set; }
    [JsonPropertyName("serverStarted")] public bool ServerStarted { get; set; }
    [JsonPropertyName("islandLoaded")] public bool IslandLoaded { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("slots")] public int Slots { get; set; }
    [JsonPropertyName("transportMaxClients")] public int TransportMaxClients { get; set; }
    [JsonPropertyName("connectedTransportClients")] public int ConnectedTransportClients { get; set; }
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("worldName")] public string WorldName { get; set; } = string.Empty;
    [JsonPropertyName("saveDirectory")] public string SaveDirectory { get; set; } = string.Empty;
    [JsonPropertyName("ghostHostSuppressed")] public bool GhostHostSuppressed { get; set; }
    [JsonPropertyName("displayNamesResolved")] public bool DisplayNamesResolved { get; set; }
    [JsonPropertyName("effectiveTargetFrameRate")] public int EffectiveTargetFrameRate { get; set; }
    [JsonPropertyName("swallowedTotal")] public long SwallowedTotal { get; set; }
    [JsonPropertyName("patchesApplied")] public string[] PatchesApplied { get; set; } = [];
    [JsonPropertyName("patchesMissing")] public string[] PatchesMissing { get; set; } = [];
    [JsonPropertyName("patchesFailed")] public string[] PatchesFailed { get; set; } = [];
    [JsonPropertyName("featuresStoodDown")] public string[] FeaturesStoodDown { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    // A stale file is not a healthy server: if the game process wedges, the last document it wrote
    // stays on disk saying "Hosting" forever. Treat anything older than the staleness window as
    // unknown rather than as its last known good value.
    public bool IsFresh(TimeSpan window) => DateTimeOffset.UtcNow - TimestampUtc <= window;

    public static ReadinessDocument? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ReadinessDocument>(File.ReadAllText(path), Options);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
