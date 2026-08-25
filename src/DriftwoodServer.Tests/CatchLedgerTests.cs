using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The catch leaderboard is a file a customer will open over FTP and a card the panel will
// render, so its arithmetic, its ordering and its round trip through disk are pinned here.
// CatchLedger is static state; every test re-Initialises against its own directory.
public class CatchLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "driftwood-ledger-" + Guid.NewGuid().ToString("N"));
    private const ulong Ryan = 76561197960287930UL;
    private const ulong Bob = 76561198000000001UL;

    public CatchLedgerTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        CatchLedger.Initialise(null, null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void AMissingFileIsTheNormalFirstBoot()
    {
        Assert.Null(CatchLedger.Initialise(_root, "Driftwood"));
        Assert.True(CatchLedger.Enabled);
        Assert.Equal(0, CatchLedger.Count);
        Assert.Equal(Path.Combine(_root, "Driftwood.leaderboard.tsv"), CatchLedger.Path_);
        Assert.Equal("[]", CatchLedger.TopJson(10));
    }

    [Fact]
    public void NowhereToLiveMeansOffNotThrown()
    {
        Assert.NotNull(CatchLedger.Initialise("", "Driftwood"));
        Assert.False(CatchLedger.Enabled);
        CatchLedger.RecordCatch(Ryan, "Ryan", "Tuna", 100, 1000);
        Assert.Equal(0, CatchLedger.Count);
    }

    [Fact]
    public void CountsCatchesSalesAndBossesPerPlayer()
    {
        CatchLedger.Initialise(_root, "Driftwood");
        CatchLedger.RecordCatch(Ryan, "Ryan", "Tuna", 120, 1000);
        CatchLedger.RecordCatch(Ryan, "Ryan", "Cod", 40, 1001);
        CatchLedger.RecordSale(Ryan, "Ryan", 120, 1002);
        CatchLedger.RecordSale(Ryan, "Ryan", 40, 1003);
        CatchLedger.RecordBoss(Ryan, "Ryan", 1004);
        CatchLedger.RecordCatch(Bob, "Bob", "Shark", 900, 1005);

        CatchLedger.Entry? ryan = CatchLedger.Get(Ryan);
        Assert.NotNull(ryan);
        Assert.Equal(2, ryan!.Catches);
        Assert.Equal(160, ryan.Earnings);
        Assert.Equal(1, ryan.Bosses);
        Assert.Equal("Tuna", ryan.BestCatchName);
        Assert.Equal(120, ryan.BestCatchWorth);
        Assert.Equal(1000, ryan.FirstSeenUnix);
        Assert.Equal(1004, ryan.LastSeenUnix);

        CatchLedger.Entry? bob = CatchLedger.Get(Bob);
        Assert.NotNull(bob);
        Assert.Equal(1, bob!.Catches);
        Assert.Equal(0, bob.Earnings);
        Assert.Equal("Shark", bob.BestCatchName);
    }

    [Fact]
    public void RanksByEarningsThenCatches()
    {
        CatchLedger.Initialise(_root, "Driftwood");
        CatchLedger.RecordSale(Bob, "Bob", 500, 1);
        CatchLedger.RecordCatch(Ryan, "Ryan", "Tuna", 10, 1);
        CatchLedger.RecordCatch(Ryan, "Ryan", "Tuna", 10, 2);
        CatchLedger.RecordSale(Ryan, "Ryan", 20, 3);
        CatchLedger.RecordCatch(3UL, "Cat", "Crab", 1, 4);

        var top = CatchLedger.Top(10);
        Assert.Equal(3, top.Count);
        Assert.Equal(Bob, top[0].SteamId);
        Assert.Equal(Ryan, top[1].SteamId);
        Assert.Equal(3UL, top[2].SteamId);
        Assert.Equal(1, CatchLedger.RankOf(Bob));
        Assert.Equal(2, CatchLedger.RankOf(Ryan));
        Assert.Equal(0, CatchLedger.RankOf(99UL));
        Assert.Single(CatchLedger.Top(1));
    }

    [Fact]
    public void PlaytimeIsCreditedFromThisServersClockNeverInOneJump()
    {
        CatchLedger.Initialise(_root, "Driftwood");
        var ids = new List<ulong> { Ryan };
        var names = new List<string> { "Ryan" };
        CatchLedger.ObservePlaytime(ids, names, 1000);   // baseline, no credit
        CatchLedger.ObservePlaytime(ids, names, 1002);   // +2
        CatchLedger.ObservePlaytime(ids, names, 1004);   // +2
        CatchLedger.ObservePlaytime(ids, names, 1500);   // a stalled sampler: gap, no credit
        CatchLedger.ObservePlaytime(ids, names, 1502);   // +2
        Assert.Equal(6, CatchLedger.Get(Ryan)!.PlaytimeSeconds);

        // Leaving resets the baseline; the next sighting credits nothing.
        CatchLedger.ObservePlaytime(new List<ulong>(), new List<string>(), 1504);
        CatchLedger.ObservePlaytime(ids, names, 1600);
        Assert.Equal(6, CatchLedger.Get(Ryan)!.PlaytimeSeconds);
        CatchLedger.ObservePlaytime(ids, names, 1602);
        Assert.Equal(8, CatchLedger.Get(Ryan)!.PlaytimeSeconds);
    }

    [Fact]
    public void ARealNameReplacesAPlaceholderAndAPlaceholderNeverReplacesARealOne()
    {
        CatchLedger.Initialise(_root, "Driftwood");
        CatchLedger.RecordCatch(Ryan, "Player-7930", "Tuna", 1, 1);
        Assert.Equal("Player-7930", CatchLedger.Get(Ryan)!.Name);
        CatchLedger.RecordCatch(Ryan, "Ryan", "Tuna", 1, 2);
        Assert.Equal("Ryan", CatchLedger.Get(Ryan)!.Name);
        CatchLedger.RecordCatch(Ryan, "Player-7930", "Tuna", 1, 3);
        Assert.Equal("Ryan", CatchLedger.Get(Ryan)!.Name);
        CatchLedger.RecordCatch(Ryan, "Ryan Renamed", "Tuna", 1, 4);
        Assert.Equal("Ryan Renamed", CatchLedger.Get(Ryan)!.Name);
    }

    [Fact]
    public void TheHostAndIdZeroNeverBecomeRows()
    {
        CatchLedger.Initialise(_root, "Driftwood");
        CatchLedger.RecordSale(0UL, "nobody", 100, 1);
        CatchLedger.RecordSale(CatchLedger.HostSteamId, "Server", 100, 1);
        Assert.Equal(0, CatchLedger.Count);
    }

    [Fact]
    public void RoundTripsThroughDiskAndSurvivesHostileNames()
    {
        CatchLedger.Initialise(_root, "Driftwood");
        CatchLedger.RecordCatch(Ryan, "Ry\tan\nline", "Tuna\t(Clone)", 120, 1000);
        CatchLedger.RecordSale(Ryan, "Ry\tan\nline", 120, 1001);
        CatchLedger.RecordBoss(Bob, "Bob", 1002);
        Assert.True(CatchLedger.Dirty);
        Assert.Null(CatchLedger.FlushIfDirty());
        Assert.False(CatchLedger.Dirty);
        Assert.True(File.Exists(CatchLedger.Path_));

        string text = File.ReadAllText(CatchLedger.Path_);
        // Two data rows, each with exactly nine structural tabs, after the two # header lines.
        var rows = text.Split('\n').Where(l => l.Length > 0 && l[0] != '#').ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(9, r.Count(c => c == '\t')));

        // A fresh boot reads it back.
        Assert.Null(CatchLedger.Initialise(_root, "Driftwood"));
        Assert.Equal(2, CatchLedger.Count);
        CatchLedger.Entry? ryan = CatchLedger.Get(Ryan);
        Assert.NotNull(ryan);
        Assert.Equal(1, ryan!.Catches);
        Assert.Equal(120, ryan.Earnings);
        Assert.Equal("Tuna", ryan.BestCatchName);
        Assert.DoesNotContain('\t', ryan.Name);
        Assert.DoesNotContain('\n', ryan.Name);
        Assert.Equal(1, CatchLedger.Get(Bob)!.Bosses);
    }

    [Fact]
    public void ADamagedLineIsSkippedNotFatal()
    {
        string path = CatchLedger.PathFor(_root, "Driftwood");
        File.WriteAllText(path,
            "# header\n" +
            Ryan + "\tRyan\t3\t400\t0\t60\tTuna\t120\t1\t2\n" +
            "garbage line\n" +
            "0\tzero\t1\t1\t1\t1\n" +
            Bob + "\tBob\tx\t1\t1\t1\n" +
            "76561198000000002\tOld\t1\t50\t0\t10\n");
        string? problem = CatchLedger.Initialise(_root, "Driftwood");
        Assert.NotNull(problem);
        Assert.Contains("3 line(s)", problem);
        Assert.Equal(2, CatchLedger.Count);
        Assert.Equal(3, CatchLedger.Get(Ryan)!.Catches);
        // An older, shorter row (no best-catch columns) still loads.
        Assert.Equal(50, CatchLedger.Get(76561198000000002UL)!.Earnings);
    }

    [Fact]
    public void TopJsonCarriesTheFieldsThePanelReads()
    {
        CatchLedger.Initialise(_root, "Driftwood");
        CatchLedger.RecordCatch(Ryan, "Ryan \"quoted\"", "Tuna", 120, 1000);
        CatchLedger.RecordSale(Ryan, "Ryan \"quoted\"", 120, 1001);
        string json = CatchLedger.TopJson(10);
        Assert.StartsWith("[{", json);
        Assert.Contains("\"rank\":1", json);
        Assert.Contains("\"steamId\":\"" + Ryan + "\"", json);
        Assert.Contains("\"name\":\"Ryan \\\"quoted\\\"\"", json);
        Assert.Contains("\"catches\":1", json);
        Assert.Contains("\"earnings\":120", json);
        Assert.Contains("\"bosses\":0", json);
        Assert.Contains("\"playtimeSeconds\":0", json);
        Assert.Contains("\"bestCatch\":\"Tuna\"", json);
        Assert.Contains("\"bestCatchWorth\":120", json);
    }

    [Fact]
    public void TheWorldNameIsMadeSafeForAFileName()
    {
        Assert.EndsWith("My_World_.leaderboard.tsv", CatchLedger.PathFor(_root, "My/World?"));
        Assert.EndsWith("world.leaderboard.tsv", CatchLedger.PathFor(_root, "  "));
    }
}
