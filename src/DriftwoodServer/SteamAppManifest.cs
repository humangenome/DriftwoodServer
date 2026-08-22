using System.Globalization;

namespace DriftwoodServer;

// Steam's appmanifest_<appid>.acf is a Valve KeyValues text file. Only a handful of fields matter
// here and they are all scalars at the top level of the "AppState" block, so a small tolerant
// reader beats a full KeyValues implementation - and it can be unit tested without Steam.
internal sealed class SteamAppManifest
{
    public required int AppId { get; init; }
    public required string BuildId { get; init; }
    public required string TargetBuildId { get; init; }
    public required string StateFlags { get; init; }
    public required string UpdateResult { get; init; }
    public required string LastUpdated { get; init; }

    // StateFlags is a bitfield. 4 = StateFullyInstalled. Anything with an update, validation or
    // download bit set means Steam is midway through changing the files under us.
    public const int StateFullyInstalled = 4;

    public bool TryGetStateFlags(out int value) =>
        int.TryParse(StateFlags, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static SteamAppManifest Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '"') continue;
            // "key"<whitespace>"value"
            int keyEnd = line.IndexOf('"', 1);
            if (keyEnd <= 1) continue;
            string key = line.Substring(1, keyEnd - 1);
            int valueStart = line.IndexOf('"', keyEnd + 1);
            if (valueStart < 0) continue;
            int valueEnd = line.IndexOf('"', valueStart + 1);
            if (valueEnd < 0) continue;
            string value = line.Substring(valueStart + 1, valueEnd - valueStart - 1);
            // First occurrence wins: the top-level AppState scalars come before the nested
            // "InstalledDepots" / "UserConfig" blocks, which reuse names like "manifest".
            if (!values.ContainsKey(key)) values[key] = value;
        }

        return new SteamAppManifest
        {
            AppId = int.TryParse(values.GetValueOrDefault("appid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int appId) ? appId : 0,
            BuildId = values.GetValueOrDefault("buildid", string.Empty),
            TargetBuildId = values.GetValueOrDefault("TargetBuildID", string.Empty),
            StateFlags = values.GetValueOrDefault("StateFlags", string.Empty),
            UpdateResult = values.GetValueOrDefault("UpdateResult", string.Empty),
            LastUpdated = values.GetValueOrDefault("LastUpdated", string.Empty)
        };
    }
}
