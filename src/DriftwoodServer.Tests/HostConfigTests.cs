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

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        GC.SuppressFinalize(this);
    }
}
