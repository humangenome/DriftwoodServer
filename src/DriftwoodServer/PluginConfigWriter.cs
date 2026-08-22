using System.Globalization;
using System.Text;

namespace DriftwoodServer;

// The supervisor owns the host mod's configuration, because the panel owns the supervisor's.
//
// Playbook 1d, "silently-ignored config": a config file the consumer deserialises by shape will
// silently ignore a plausible-but-wrong one. Lodestone measured 763 fps on a server whose config
// said 30, because a hand-typed root element was written instead of the grouped one the game
// expects - and it came up unmuted and uncapped while the file on disk said otherwise.
//
// So this writes the file AND the supervisor afterwards asserts, from the readiness document, that
// the values actually took effect at runtime. Writing is not configuring.
internal static class PluginConfigWriter
{
    public static void Write(HostOptions options)
    {
        string directory = Path.GetDirectoryName(options.PluginConfigPath)!;
        Directory.CreateDirectory(directory);

        StringBuilder text = new();
        text.AppendLine("## Written by DriftwoodServer. Hand edits are overwritten on every start.");
        text.AppendLine();
        text.AppendLine("[Server]");
        text.AppendLine("Enabled = true");
        // An EMPTY bind address resolves to loopback and the server is unreachable from outside
        // the box, so it is always written explicitly.
        text.AppendLine("BindAddress = 0.0.0.0");
        text.AppendLine(Line("Port", options.GamePort));
        text.AppendLine(Line("Slots", options.Slots));
        text.AppendLine("StartDelaySeconds = 10");
        text.AppendLine(Line("WorldReadyTimeoutSeconds", options.WorldReadyTimeoutSeconds));
        text.AppendLine($"ServerName = {Sanitise(options.ServerName)}");
        text.AppendLine($"SaveRoot = {options.SaveRoot}");
        text.AppendLine();
        // The API token lives under [Http] and is NOT the join password. They are different
        // secrets in different sections, and a reader that flattens sections turns "[Http] Password"
        // into the join password - leaving the token empty and rejecting every authenticated call.
        text.AppendLine("[Http]");
        text.AppendLine(Line("Port", options.HttpPort > 0 ? options.HttpPort : options.GamePort + 1));
        text.AppendLine($"Password = {Sanitise(options.AuthToken)}");
        text.AppendLine();
        text.AppendLine("[World]");
        text.AppendLine($"Name = {Sanitise(options.WorldName)}");
        text.AppendLine(Line("AutoSaveMinutes", options.AutoSaveMinutes));
        text.AppendLine();
        text.AppendLine("[Gameplay]");
        text.AppendLine($"FriendlyFire = {Bool(options.FriendlyFire)}");
        text.AppendLine($"OneShotKills = {Bool(options.OneShotKills)}");
        text.AppendLine();
        text.AppendLine("[Host]");
        text.AppendLine($"SuppressGhostHost = {Bool(options.SuppressGhostHost)}");
        text.AppendLine();
        text.AppendLine("[Performance]");
        text.AppendLine($"PauseWorldWhenEmpty = {Bool(options.PauseWorldWhenEmpty)}");
        text.AppendLine(Line("IdleFrameRate", options.IdleFrameRate));
        text.AppendLine(Line("TargetFrameRate", options.TargetFrameRate));
        text.AppendLine();
        text.AppendLine("[Paths]");
        text.AppendLine($"StateDirectory = {options.StateRoot}");
        // The INSTANCE root, not the game dir. The install nests as
        // <instance root>\How to Fish\How to Fish.exe, and Logs\ plus the boot markers live under
        // the instance root - outside the game dir, so a SteamCMD validate cannot own them.
        text.AppendLine($"InstanceRoot = {options.InstanceRoot}");

        AtomicFile.WriteText(options.PluginConfigPath, text.ToString());
    }

    // Returns null when the running server matches the config, otherwise one plain sentence.
    // This is the half that makes the write mean something.
    public static string? AssertTookEffect(HostOptions options, ReadinessDocument readiness)
    {
        if (readiness.Port != options.GamePort)
            return $"The server is listening on port {readiness.Port} but was configured for {options.GamePort}, so its configuration did not take effect.";
        if (readiness.Slots != options.Slots)
            return $"The server is enforcing {readiness.Slots} slots but was sold {options.Slots}, so its configuration did not take effect.";
        if (options.TargetFrameRate > 0 && readiness.EffectiveTargetFrameRate != options.TargetFrameRate)
            return $"The server is running at a frame cap of {readiness.EffectiveTargetFrameRate} but was configured for {options.TargetFrameRate}, so its configuration did not take effect.";
        if (!string.Equals(readiness.WorldName, options.WorldName, StringComparison.Ordinal))
            return $"The server loaded the world \"{readiness.WorldName}\" but was configured for \"{options.WorldName}\", so its configuration did not take effect.";
        // A configured cap with no limiter means the server is UNCAPPED, whatever the engine says.
        // This is the field that exists because the limiter once shipped as dead code and three
        // "capped" measurement runs were silently uncapped.
        if (options.TargetFrameRate > 0 && !readiness.FrameLimiterActive)
        {
            return $"The server was configured with a {options.TargetFrameRate} fps cap but its frame limiter is not running, so it is uncapped and will use far more CPU than expected.";
        }
        if (options.IdleFrameRate > 0 && !readiness.FrameLimiterActive)
        {
            return "The server was configured to slow down while empty but its frame limiter is not running, so it will run at full speed with nobody connected.";
        }
        if (options.SuppressGhostHost && !readiness.GhostHostSuppressed)
            return "The server is running a placeholder host character that should have been suppressed, so it would appear as a phantom player.";
        if (!SameDirectory(readiness.SaveDirectory, options.SaveRoot))
            return $"The server is saving to \"{readiness.SaveDirectory}\" instead of this server's own folder, so its world could be overwritten by another server on this machine.";
        return null;
    }

    private static bool SameDirectory(string left, string right)
    {
        static string Normalise(string value) =>
            value.Replace('\\', '/').TrimEnd('/');
        return string.Equals(Normalise(left), Normalise(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string Line(string key, int value) =>
        $"{key} = {value.ToString(CultureInfo.InvariantCulture)}";

    private static string Line(string key, double value) =>
        $"{key} = {value.ToString("0.###", CultureInfo.InvariantCulture)}";

    private static string Bool(bool value) => value ? "true" : "false";

    // BepInEx config values are line-terminated, so a newline in a customer-supplied name would
    // inject a key. Strip control characters rather than trusting the caller.
    private static string Sanitise(string value) =>
        new(( value ?? string.Empty).Where(c => !char.IsControl(c)).Take(80).ToArray());
}
