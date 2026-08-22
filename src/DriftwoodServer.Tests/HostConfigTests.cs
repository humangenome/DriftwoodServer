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

    private const string Minimal = """
[Server]
Port = 22003
MaxPlayers = 4
SaveRoot = C:\srv\Saves

[World]
WorldName = Driftwood
""";

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
        HostConfig config = Load("""
[Server]
GamePort = 22013
Slots = 6
SaveDirectory = C:\srv\Saves
world_name = Elsewhere
Fps = 30
""");
        Assert.Equal(22013, config.Port);
        Assert.Equal(6, config.MaxPlayers);
        Assert.Equal(30, config.TargetFrameRate);
        Assert.Equal("Elsewhere", config.WorldName);
        Assert.Empty(config.UnrecognisedKeys);
    }

    [Fact]
    public void SectionsDoNotChangeWhereAValueLands()
    {
        // TargetFrameRate has lived under [Server] and under [Performance] in different drafts.
        HostConfig config = Load(Minimal + "\n[Performance]\nTargetFrameRate = 45\n");
        Assert.Equal(45, config.TargetFrameRate);
    }

    [Fact]
    public void AKeyNobodyReadsIsReportedRatherThanIgnored()
    {
        HostConfig config = Load(Minimal + "\nMaxPlayerz = 12\n");
        Assert.Contains("MaxPlayerz", config.UnrecognisedKeys);
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
        HostConfig config = Load(Minimal + "\nHttpPort = 22003\n");
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
        HostConfig config = Load(Minimal + "\nPhysicsStepSeconds = 0.5\n");
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

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        GC.SuppressFinalize(this);
    }
}
