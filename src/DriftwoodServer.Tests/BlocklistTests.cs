using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The block list is the store a ban's survival depends on: an entry that does not round-trip
// through disk un-bans somebody on the next restart, silently. Blocklist is static state, so
// every test in this class re-Initialises against its own directory - and stays in this one
// class, because two parallel test classes sharing the static would race.
public class BlocklistTests : IDisposable
{
    private const ulong RealId = 76561197960287930UL;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "driftwood-block-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ABlockSurvivesARestart()
    {
        Assert.Null(Blocklist.Initialise(_root, null));
        Assert.Null(Blocklist.Add(RealId, "Griefer", 1700000000L));
        Assert.True(Blocklist.IsBlocked(RealId));

        // "Restart": a fresh Initialise against the same instance root.
        Assert.Null(Blocklist.Initialise(_root, null));
        Assert.True(Blocklist.IsBlocked(RealId));
        var entries = Blocklist.List();
        Assert.Single(entries);
        Assert.Equal("Griefer", entries[0].Label);
        Assert.Equal(1700000000L, entries[0].AddedUnix);
    }

    [Fact]
    public void RemoveUnblocksAndPersistsThat()
    {
        Assert.Null(Blocklist.Initialise(_root, null));
        Assert.Null(Blocklist.Add(RealId, "x", 1L));
        Assert.Null(Blocklist.Remove(RealId, out bool found));
        Assert.True(found);

        Assert.Null(Blocklist.Initialise(_root, null));
        Assert.False(Blocklist.IsBlocked(RealId));

        Blocklist.Remove(RealId, out found);
        Assert.False(found);
    }

    [Fact]
    public void GroupIdsAndTheHostsReservedIdAreRefused()
    {
        Assert.Null(Blocklist.Initialise(_root, null));
        // Below the first individual account id: groups, lobbies, typos - and the host's own
        // reserved loopback identity, which sits deliberately below that floor so the server
        // can never block itself.
        Assert.NotNull(Blocklist.Add(103582791429521408UL % Blocklist.FirstIndividualSteamId, "group", 1L));
        Assert.NotNull(Blocklist.Add(76561190000000001UL, "the host itself", 1L));
        Assert.NotNull(Blocklist.Add(0UL, "zero", 1L));
        Assert.Equal(0, Blocklist.Count);
    }

    [Fact]
    public void AHostileLabelCannotForgeARowOrAColumn()
    {
        Assert.Null(Blocklist.Initialise(_root, null));
        Assert.Null(Blocklist.Add(RealId, "evil\t9\tinjected\n76561197960287931\t1\tforged", 1L));

        Assert.Null(Blocklist.Initialise(_root, null));
        var entries = Blocklist.List();
        Assert.Single(entries);
        Assert.Equal(RealId, entries[0].SteamId);
        Assert.False(Blocklist.IsBlocked(76561197960287931UL));
        Assert.DoesNotContain('\t', entries[0].Label);
        Assert.DoesNotContain('\n', entries[0].Label);
    }

    [Fact]
    public void TheCapRefusesLoudlyInsteadOfGrowingForever()
    {
        Assert.Null(Blocklist.Initialise(_root, null));
        for (int i = 0; i < Blocklist.MaxEntries; i++)
        {
            Assert.Null(Blocklist.Add(RealId + (ulong)i, "p" + i, 1L));
        }
        string refusal = Blocklist.Add(RealId + (ulong)Blocklist.MaxEntries, "one too many", 1L);
        Assert.NotNull(refusal);
        Assert.Contains("full", refusal);
        Assert.Equal(Blocklist.MaxEntries, Blocklist.Count);
    }

    [Fact]
    public void AddingTwiceIsANoOpNotAnError()
    {
        Assert.Null(Blocklist.Initialise(_root, null));
        Assert.Null(Blocklist.Add(RealId, "first", 1L));
        Assert.Null(Blocklist.Add(RealId, "second", 2L));
        var entries = Blocklist.List();
        Assert.Single(entries);
        // The original entry stands; a re-block does not rewrite history.
        Assert.Equal("first", entries[0].Label);
    }

    [Fact]
    public void NowhereToLiveIsReportedNotThrown()
    {
        string problem = Blocklist.Initialise(null, null);
        Assert.NotNull(problem);
        Assert.Contains("nowhere", problem);
        // The in-memory list still works for this run, and says the block will not survive.
        string persistProblem = Blocklist.Add(RealId, "x", 1L);
        Assert.NotNull(persistProblem);
        Assert.Contains("will not survive a restart", persistProblem);
        Assert.True(Blocklist.IsBlocked(RealId));
    }

    [Fact]
    public void ACorruptLineIsSkippedTheRestSurvive()
    {
        Assert.Null(Blocklist.Initialise(_root, null));
        Assert.Null(Blocklist.Add(RealId, "kept", 1L));
        string path = Path.Combine(_root, "Driftwood", "blocklist.txt");
        File.AppendAllText(path, "not-a-number\t1\tjunk\n1234\t1\ttoo-low\n");

        Assert.Null(Blocklist.Initialise(_root, null));
        Assert.Single(Blocklist.List());
        Assert.True(Blocklist.IsBlocked(RealId));
    }
}
