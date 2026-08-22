using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriftwoodServer;

internal sealed class HostOptions
{
    private static readonly JsonSerializerOptions LoadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // An unknown key is a typo or a stale config, and silently ignoring it is exactly how a
        // setting ends up "configured" and not in force.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string InstanceId { get; set; } = string.Empty;
    // The per-customer game install. Never a shared baseline: each server gets its own copy.
    public string GameRoot { get; set; } = string.Empty;
    public string StateRoot { get; set; } = string.Empty;
    public string SaveRoot { get; set; } = string.Empty;
    public string BackupRoot { get; set; } = string.Empty;
    // Steam's steamapps folder for this install, so the build pin can read appmanifest_<appid>.acf.
    public string SteamAppsDirectory { get; set; } = string.Empty;

    public string GameExecutable { get; set; } = "How to Fish.exe";
    public string WorldName { get; set; } = "Driftwood";
    public string ServerName { get; set; } = string.Empty;
    public int GamePort { get; set; } = 7777;
    public int HttpPort { get; set; }
    public int Slots { get; set; } = 8;
    public int TargetFrameRate { get; set; } = 30;
    public bool SuppressGhostHost { get; set; } = true;
    public bool FriendlyFire { get; set; } = true;
    public bool OneShotKills { get; set; }
    public double AutoSaveMinutes { get; set; } = 5;

    public int WorldReadyTimeoutSeconds { get; set; } = 240;
    public int ReadinessStaleSeconds { get; set; } = 30;
    public int PollSeconds { get; set; } = 3;
    public int GracefulStopSeconds { get; set; } = 45;

    public PinnedBuildOptions PinnedBuild { get; set; } = new();

    [JsonIgnore] public string ConfigPath { get; private set; } = string.Empty;
    [JsonIgnore] public string ReadinessPath => Path.Combine(StateRoot, "host-ready.json");
    [JsonIgnore] public string StopFilePath => Path.Combine(StateRoot, "stop.requested");
    [JsonIgnore] public string PluginConfigPath =>
        Path.Combine(GameRoot, "BepInEx", "config", "com.humangenome.driftwood.host.cfg");

    public static HostOptions Load(string path)
    {
        string configPath = Path.GetFullPath(path);
        HostOptions options = JsonSerializer.Deserialize<HostOptions>(File.ReadAllText(configPath), LoadOptions)
            ?? throw new InvalidDataException("The Driftwood host config is empty.");
        options.ConfigPath = configPath;
        string baseDirectory = Path.GetDirectoryName(configPath)!;
        options.GameRoot = Resolve(baseDirectory, options.GameRoot, nameof(GameRoot));
        options.StateRoot = Resolve(baseDirectory, options.StateRoot, nameof(StateRoot));
        options.SaveRoot = Resolve(baseDirectory, options.SaveRoot, nameof(SaveRoot));
        if (!string.IsNullOrWhiteSpace(options.BackupRoot))
            options.BackupRoot = Resolve(baseDirectory, options.BackupRoot, nameof(BackupRoot));
        if (!string.IsNullOrWhiteSpace(options.SteamAppsDirectory))
            options.SteamAppsDirectory = Resolve(baseDirectory, options.SteamAppsDirectory, nameof(SteamAppsDirectory));
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(InstanceId) || InstanceId.Length > 80 ||
            InstanceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            throw new InvalidDataException("InstanceId must be 1-80 ASCII letters, digits, dashes or underscores.");
        }
        if (GamePort is < 1 or > 65535) throw new InvalidDataException("GamePort is not a usable port.");
        if (HttpPort is < 0 or > 65535) throw new InvalidDataException("HttpPort is not a usable port.");
        if (HttpPort == GamePort) throw new InvalidDataException("HttpPort must differ from GamePort.");
        if (Slots is < 1 or > 250) throw new InvalidDataException("Slots must be between 1 and 250.");
        if (TargetFrameRate is < 0 or > 1000) throw new InvalidDataException("TargetFrameRate must be between 0 and 1000.");
        if (AutoSaveMinutes is < 1 or > 60) throw new InvalidDataException("AutoSaveMinutes must be between 1 and 60.");
        if (WorldReadyTimeoutSeconds is < 60 or > 1800) throw new InvalidDataException("WorldReadyTimeoutSeconds must be between 60 and 1800.");
        if (ReadinessStaleSeconds is < 10 or > 300) throw new InvalidDataException("ReadinessStaleSeconds must be between 10 and 300.");
        if (PollSeconds is < 1 or > 60) throw new InvalidDataException("PollSeconds must be between 1 and 60.");
        if (GracefulStopSeconds is < 10 or > 600) throw new InvalidDataException("GracefulStopSeconds must be between 10 and 600.");
        if (string.IsNullOrWhiteSpace(WorldName) || WorldName.Equals("local", StringComparison.OrdinalIgnoreCase) ||
            WorldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("WorldName must be a usable file name and cannot be \"local\", which the game reserves.");
        }
        if (PathEquals(StateRoot, GameRoot)) throw new InvalidDataException("StateRoot must not be the GameRoot.");
        PinnedBuild.Validate();
    }

    private static string Resolve(string baseDirectory, string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{name} is required.");
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal sealed class PinnedBuildOptions
{
    public int AppId { get; set; } = 4001890;
    // ONLY Assembly-CSharp.dll identifies a build. The Unity launcher stub does not change between
    // versions, so pinning the .exe would wave a game update straight through.
    public string AssemblyRelativePath { get; set; } = Path.Combine("How to Fish_Data", "Managed", "Assembly-CSharp.dll");
    public string AssemblySha256 { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public string HostModSha256 { get; set; } = string.Empty;

    public void Validate()
    {
        // Fail closed by default: an EMPTY pin is not "no pin", it is "no validated build has been
        // published yet", and every start must refuse until one has been.
        if (AssemblySha256.Length != 64 || !AssemblySha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException(
                "PinnedBuild.AssemblySha256 must be the 64-character SHA-256 of the validated Assembly-CSharp.dll. Until a validated Driftwood build is published this is deliberately empty and every start refuses.");
        }
        if (AppId <= 0) throw new InvalidDataException("PinnedBuild.AppId is required.");
        if (HostModSha256.Length != 0 && (HostModSha256.Length != 64 || !HostModSha256.All(char.IsAsciiHexDigit)))
        {
            throw new InvalidDataException("PinnedBuild.HostModSha256 must be a 64-character SHA-256 when set.");
        }
    }
}
