using DriftwoodServer;
using Xunit;

namespace DriftwoodServer.Tests;

public class PluginConfigTests
{
    private static HostOptions Options() => new()
    {
        InstanceId = "gs123",
        GameRoot = "/tmp/game",
        StateRoot = "/tmp/state",
        SaveRoot = "/tmp/saves",
        GamePort = 7801,
        Slots = 4,
        TargetFrameRate = 30,
        WorldName = "Driftwood",
        SuppressGhostHost = true
    };

    private static ReadinessDocument Matching() => new()
    {
        Port = 7801,
        Slots = 4,
        EffectiveTargetFrameRate = 30,
        FrameLimiterActive = true,
        WorldName = "Driftwood",
        GhostHostSuppressed = true,
        SaveDirectory = "/tmp/saves/"
    };

    [Fact]
    public void AcceptsAServerThatMatchesItsConfig()
    {
        Assert.Null(PluginConfigWriter.AssertTookEffect(Options(), Matching()));
    }

    [Fact]
    public void CatchesASlotLimitThatDidNotTakeEffect()
    {
        // The exact Lodestone bug: the number was written, and the running server enforces
        // something else.
        ReadinessDocument readiness = Matching();
        readiness.Slots = 8;
        string? reason = PluginConfigWriter.AssertTookEffect(Options(), readiness);
        Assert.NotNull(reason);
        Assert.Contains("slots", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatchesAFrameCapThatDidNotTakeEffect()
    {
        ReadinessDocument readiness = Matching();
        readiness.EffectiveTargetFrameRate = -1;
        Assert.NotNull(PluginConfigWriter.AssertTookEffect(Options(), readiness));
    }

    [Fact]
    public void CatchesSavesLandingSomewhereShared()
    {
        ReadinessDocument readiness = Matching();
        readiness.SaveDirectory = "C:/Users/Administrator/AppData/LocalLow/Dazed Games/How to Fish/Saves/";
        string? reason = PluginConfigWriter.AssertTookEffect(Options(), readiness);
        Assert.NotNull(reason);
        Assert.Contains("overwritten", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatchesAGhostHostThatWasNotSuppressed()
    {
        ReadinessDocument readiness = Matching();
        readiness.GhostHostSuppressed = false;
        Assert.NotNull(PluginConfigWriter.AssertTookEffect(Options(), readiness));
    }

    [Fact]
    public void CatchesACapConfiguredWithNoLimiterRunning()
    {
        // The engine reports targetFrameRate back as though it took even though batch mode ignores
        // it, so the ONLY evidence a cap is in force is the limiter being installed. This shipped
        // as dead code once and three measurement runs were silently uncapped.
        ReadinessDocument readiness = Matching();
        readiness.FrameLimiterActive = false;
        string? reason = PluginConfigWriter.AssertTookEffect(Options(), readiness);
        Assert.NotNull(reason);
        Assert.Contains("uncapped", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatchesAnIdleRateConfiguredWithNoLimiterRunning()
    {
        HostOptions options = Options();
        options.TargetFrameRate = 0;
        options.IdleFrameRate = 5;
        ReadinessDocument readiness = Matching();
        readiness.EffectiveTargetFrameRate = 0;
        readiness.FrameLimiterActive = false;
        string? reason = PluginConfigWriter.AssertTookEffect(options, readiness);
        Assert.NotNull(reason);
        Assert.Contains("full speed", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatchesTheWrongWorld()
    {
        ReadinessDocument readiness = Matching();
        readiness.WorldName = "SomeoneElsesWorld";
        Assert.NotNull(PluginConfigWriter.AssertTookEffect(Options(), readiness));
    }

    // The host mod reads its Steam Web API key file from [Identity] SteamWebApiKeyFile, and the
    // supervisor rewrites the host's config on every start. Before 2026-11-25 the supervisor had
    // no such setting and wrote no [Identity] section, so a self-hosted server could never turn on
    // the Steam check its README offers. Written by the supervisor, read back by the host's own
    // config loader: the path arrives, and only the path.
    [Fact]
    public void TheSteamKeyFileReachesTheHostsConfig()
    {
        string root = Path.Combine(Path.GetTempPath(), "driftwood-steamkey-" + Guid.NewGuid().ToString("N"));
        try
        {
            HostOptions options = Options();
            options.GameRoot = Path.Combine(root, "How to Fish");
            options.SteamWebApiKeyFile = Path.Combine(root, "secrets", "steam web api key.txt");
            PluginConfigWriter.Write(options);

            string written = File.ReadAllText(options.PluginConfigPath);
            Assert.Contains("[Identity]", written);
            DriftwoodHost.HostConfig host = DriftwoodHost.HostConfig.Load(options.PluginConfigPath);
            Assert.Equal(options.SteamWebApiKeyFile, host.SteamWebApiKeyFile);
            Assert.Equal(string.Empty, host.SteamWebApiKey);
            Assert.Empty(host.UnrecognisedKeys);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void NoSteamKeyFileWritesNoIdentitySection()
    {
        string root = Path.Combine(Path.GetTempPath(), "driftwood-steamkey-" + Guid.NewGuid().ToString("N"));
        try
        {
            HostOptions options = Options();
            options.GameRoot = Path.Combine(root, "How to Fish");
            PluginConfigWriter.Write(options);

            Assert.DoesNotContain("[Identity]", File.ReadAllText(options.PluginConfigPath));
            Assert.Equal(string.Empty, DriftwoodHost.HostConfig.Load(options.PluginConfigPath).SteamWebApiKeyFile);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void TheSteamKeyFileLoadsFromAppsettingsAndResolvesBesideIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "driftwood-steamkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "appsettings.json");
            File.WriteAllText(config,
                "{ \"instanceId\": \"my-server\", \"gameRoot\": \"How to Fish\", \"stateRoot\": \"host-state\", " +
                "\"saveRoot\": \"Saves\", \"steamWebApiKeyFile\": \"steam-key.txt\", " +
                "\"pinnedBuild\": { \"assemblySha256\": \"" + new string('a', 64) + "\" } }");
            HostOptions options = HostOptions.Load(config);
            Assert.Equal(Path.Combine(root, "steam-key.txt"), options.SteamWebApiKeyFile);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
