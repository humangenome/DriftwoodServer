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
}
