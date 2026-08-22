using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

public class HostConfigTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "driftwood-cfg-" + Guid.NewGuid().ToString("N") + ".cfg");

    private HostConfig Load(string body)
    {
        File.WriteAllText(_path, body);
        return HostConfig.Load(_path);
    }

    private const string Minimal =
        "[Server]\nPort = 22003\nMaxPlayers = 4\nSaveRoot = C:\\srv\\Saves\n\n[World]\nWorldName = Driftwood\n";

    [Fact]
    public void ReadsTheKeysThePanelWrites()
    {
        HostConfig config = Load(Minimal);
        Assert.Null(config.Validate());
        Assert.Equal(22003, config.Port);
        Assert.Equal(4, config.MaxPlayers);
        Assert.Equal(22004, config.EffectiveHttpPort);
        Assert.Empty(config.UnrecognisedKeys);
    }

    [Fact]
    public void AcceptsTheOtherLanesNamesForTheSameSettings()
    {
        // The two lanes disagreed about Slots vs MaxPlayers and SaveDirectory vs SaveRoot. A
        // disagreement here means the panel writes a slot limit the mod never sees, and both
        // halves look healthy.
        HostConfig config = Load(
            "[Server]\nGamePort = 22013\nSlots = 6\nSaveDirectory = C:\\srv\\Saves\nworld_name = Elsewhere\nFps = 30\n");
        Assert.Equal(22013, config.Port);
        Assert.Equal(6, config.MaxPlayers);
        Assert.Equal(30, config.TargetFrameRate);
        Assert.Equal("Elsewhere", config.WorldName);
        Assert.Empty(config.UnrecognisedKeys);
    }

    [Fact]
    public void AKeyNobodyReadsIsReportedRatherThanIgnored()
    {
        HostConfig config = Load(Minimal + "MaxPlayerz = 12\n");
        Assert.Contains(config.UnrecognisedKeys, k => k.EndsWith("MaxPlayerz", StringComparison.Ordinal));
        // ...and the real setting is untouched, so the typo cannot silently become the answer.
        Assert.Equal(4, config.MaxPlayers);
    }

    [Fact]
    public void AMissingSaveRootIsARefusal()
    {
        // No safe default exists: persistentDataPath is per Windows user, so an unset SaveRoot
        // pools every server's world into one directory.
        HostConfig config = Load("[Server]\nPort = 22003\n\n[World]\nWorldName = Driftwood\n");
        string? reason = config.Validate();
        Assert.NotNull(reason);
        Assert.Contains("overwrite", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARelativeSaveRootIsARefusal()
    {
        HostConfig config = Load("[Server]\nPort = 22003\nSaveRoot = Saves\n\n[World]\nWorldName = Driftwood\n");
        Assert.NotNull(config.Validate());
    }

    [Fact]
    public void TheReservedWorldNameIsARefusal()
    {
        HostConfig config = Load(Minimal.Replace("WorldName = Driftwood", "WorldName = local"));
        string? reason = config.Validate();
        Assert.NotNull(reason);
        Assert.Contains("local", reason);
    }

    [Fact]
    public void AStatusPortCollidingWithTheGamePortIsARefusal()
    {
        HostConfig config = Load(Minimal + "\n[Http]\nPort = 22003\n");
        Assert.NotNull(config.Validate());
    }

    [Fact]
    public void TheDefaultPortIsTheFleetBandNotTheGamesOwn()
    {
        // 7777 is the game's default and is already taken by nine games on this fleet; a first
        // boot there is a bind failure on a shared host.
        HostConfig config = Load("[Server]\nSaveRoot = C:\\srv\\Saves\n\n[World]\nWorldName = Driftwood\n");
        Assert.Equal(22003, config.Port);
    }

    [Fact]
    public void AnOutOfRangePhysicsStepIsARefusal()
    {
        HostConfig config = Load(Minimal + "\n[Performance]\nPhysicsStepSeconds = 0.5\n");
        string? reason = config.Validate();
        Assert.NotNull(reason);
        Assert.Contains("tunnel", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZeroMeansLeaveTheGamesOwnValueAlone()
    {
        HostConfig config = Load(Minimal);
        Assert.Equal(0f, config.PhysicsStepSeconds);
        Assert.Equal(0, config.NetworkTickRate);
        Assert.Null(config.Validate());
    }

    // --- the three breaks a cross-repo contract test caught, each now pinned -------------------

    [Fact]
    public void TheHttpPasswordIsTheApiTokenAndNotTheJoinPassword()
    {
        // The break: "Password" was an alias of JoinPassword and sections were ignored, so
        // "[Http] Password" landed on the join password and the API token was always empty -
        // rejecting every authenticated call to the surface the panel depends on.
        HostConfig config = Load(
            "[Server]\nPort = 22003\nSaveRoot = C:\\srv\\Saves\nPassword = joinsecret\n\n[Http]\nPassword = apitoken\n\n[World]\nName = Driftwood\n");
        Assert.Equal("apitoken", config.AuthToken);
        Assert.Equal("joinsecret", config.JoinPassword);
    }

    [Fact]
    public void PortsInDifferentSectionsDoNotCollapse()
    {
        // The break: Port appears under [Server] and [Http], and a flat reader took whichever came
        // first - so the game port and the status port silently became the same value.
        HostConfig config = Load(
            "[Server]\nPort = 22003\nSaveRoot = C:\\srv\\Saves\n\n[Http]\nPort = 22004\n\n[World]\nName = Driftwood\n");
        Assert.Equal(22003, config.Port);
        Assert.Equal(22004, config.EffectiveHttpPort);
        Assert.Null(config.Validate());
    }

    [Fact]
    public void WorldNameIsReadFromTheSectionedFormThePanelWrites()
    {
        // The break: "[World] Name" was never read at all.
        HostConfig config = Load("[Server]\nPort = 22003\nSaveRoot = C:\\srv\\Saves\n\n[World]\nName = Archipelago\n");
        Assert.Equal("Archipelago", config.WorldName);
    }

    [Fact]
    public void BindAddressInAnotherSectionDoesNotOverrideTheServersOwn()
    {
        HostConfig config = Load(
            "[Server]\nPort = 22003\nBindAddress = 0.0.0.0\nSaveRoot = C:\\srv\\Saves\n\n[Http]\nBindAddress = 127.0.0.1\n\n[World]\nName = Driftwood\n");
        Assert.Equal("0.0.0.0", config.BindAddress);
    }

    [Fact]
    public void InstanceRootIsReadAndTheGameDirIsItsChild()
    {
        // The install nests: <instance root>\How to Fish\How to Fish.exe. Saves and the boot
        // markers live under the instance root, OUTSIDE the game dir, so a SteamCMD validate
        // cannot own them.
        HostConfig config = Load(
            "[Server]\nPort = 22003\nSaveRoot = C:\\ss\\941353\\Saves\n\n[World]\nName = Driftwood\n\n[Paths]\nInstanceRoot = C:\\ss\\941353\n");
        Assert.Equal("C:\\ss\\941353", config.InstanceRoot);
        Assert.Equal(Path.Combine("C:\\ss\\941353", "Logs"), config.ResolveLogsDirectory("C:\\ss\\941353\\How to Fish"));
    }

    [Fact]
    public void WithoutAnInstanceRootTheParentOfTheGameDirIsUsed()
    {
        // Without this the mod cannot locate Logs\, never writes the markers the panel asserts on,
        // and every server reports Stopped.
        HostConfig config = Load(Minimal);
        Assert.Equal("/srv/941353", config.ResolveInstanceRoot("/srv/941353/How to Fish"));
        Assert.Equal(Path.Combine("/srv/941353", "Logs"), config.ResolveLogsDirectory("/srv/941353/How to Fish"));
    }

    [Fact]
    public void ABareKeyStillWorksButNeverOutranksASectionedOne()
    {
        HostConfig config = Load(
            "MaxPlayers = 3\n\n[Server]\nPort = 22003\nSaveRoot = C:\\srv\\Saves\nMaxPlayers = 6\n\n[World]\nName = Driftwood\n");
        Assert.Equal(6, config.MaxPlayers);
    }

    // --- the review findings of 2026-08-22, each pinned ---------------------------------------

    [Fact]
    public void AConfiguredJoinPasswordRefusesTheStart()
    {
        // The panel wrote the customer's real passlock here, decrypted, on every start. The mod
        // read it into a field with ZERO consumers - v1 servers are raw UDP with no join-time
        // check - and because the key is read it never showed up as unrecognised either, so the
        // product's own tripwire for a dead key could not see it.
        //
        // The panel no longer emits it. This is the other half: a re-emit must fail CLOSED and
        // loudly rather than come up open while the panel believes the server is locked.
        HostConfig config = Load(Minimal.Replace("[World]", "JoinPassword = hunter2\n\n[World]"));
        Assert.Equal("hunter2", config.JoinPassword);
        string? reason = config.Validate();
        Assert.NotNull(reason);
        Assert.Contains("join password", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoJoinPasswordIsTheNormalCase()
    {
        // An EMPTY value must not refuse: the panel clearing a stale key is exactly how a
        // passwordless server is expressed everywhere else in this family.
        HostConfig config = Load(Minimal.Replace("[World]", "JoinPassword = \n\n[World]"));
        Assert.Null(config.Validate());
    }

    [Fact]
    public void AFrameCapFarBelowTheNetworkTickIsRefused()
    {
        // This floor lived only in the supervisor's HostOptions, and the supervisor is not what
        // runs in production - the panel-written cfg is. So the production path had no below-tick
        // check at all.
        HostConfig config = Load(Minimal + "\n[Performance]\nTargetFrameRate = 10\n");
        string? reason = config.Validate();
        Assert.NotNull(reason);
        Assert.Contains("tick", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCapTheEndpointActuallyShipsIsAccepted()
    {
        // 60 is what the hosting endpoint emits. If this ever fails, every
        // server on the fleet refuses to start.
        HostConfig config = Load(Minimal + "\n[Performance]\nTargetFrameRate = 60\nIdleFrameRate = 5\nPauseWorldWhenEmpty = false\n");
        Assert.Null(config.Validate());
        Assert.Equal(60, config.TargetFrameRate);
        Assert.Equal(5, config.IdleFrameRate);
        Assert.False(config.PauseWorldWhenEmpty);
    }

    [Fact]
    public void TheDensityLeversAreReadFromThePerformanceSectionTheEndpointWrites()
    {
        // These three had no path into production at all until 2026-08-22: the endpoint emitted an
        // uncapped frame rate and neither of the other two. The section name is load-bearing -
        // "Performance.IdleFrameRate" is the first alias in the group.
        HostConfig config = Load(Minimal + "\n[Performance]\nTargetFrameRate = 60\nIdleFrameRate = 5\nPauseWorldWhenEmpty = true\n");
        Assert.Empty(config.UnrecognisedKeys);
        Assert.True(config.PauseWorldWhenEmpty);
    }

    [Fact]
    public void AnIdleFrameRateOfZeroMeansOffAndAnythingElseMustStillServiceTheNetcode()
    {
        Assert.Null(Load(Minimal + "\n[Performance]\nIdleFrameRate = 0\n").Validate());
        // Negative is not "off", it is a loop that never runs.
        string? reason = Load(Minimal + "\n[Performance]\nIdleFrameRate = -1\n").Validate();
        Assert.NotNull(reason);
        Assert.Contains("IdleFrameRate", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReservedWorldNameIsRefused()
    {
        // Saves\local.txt is the game's own per-machine settings file. The endpoint's sanitizer
        // used to pass "local" because it is plain letters, so a customer who renamed their world
        // to it got a server that refused every start for a reason they never saw.
        foreach (string name in new[] { "local", "LOCAL", "Local" })
        {
            HostConfig config = Load(Minimal.Replace("WorldName = Driftwood", "WorldName = " + name));
            string? reason = config.Validate();
            Assert.NotNull(reason);
            Assert.Contains("local", reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        GC.SuppressFinalize(this);
    }
}
