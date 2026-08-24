using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The audit log is the record support answers "your host kicked me for no reason" from, days
// later. OwnerAudit is static state, so every test re-Initialises against its own directory
// and all of them stay in this one class.
public class OwnerAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "driftwood-audit-" + Guid.NewGuid().ToString("N"));

    public OwnerAuditTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void RecordsAndTailsInOrderNewestLast()
    {
        OwnerAudit.Initialise(_root);
        Assert.Null(OwnerAudit.Record("console", "kick", "76561197960287930 (Griefer)", ok: true, "removed"));
        Assert.Null(OwnerAudit.Record("panel", "block", "76561197960287930", ok: true, "added"));
        Assert.Null(OwnerAudit.Record("console", "say", "hello", ok: false, "the world is not running"));

        var tail = OwnerAudit.Tail(2);
        Assert.Equal(2, tail.Count);
        Assert.Contains("block", tail[0]);
        Assert.Contains("say", tail[1]);
        Assert.Contains("refused", tail[1]);

        var all = OwnerAudit.Tail(50);
        Assert.Equal(3, all.Count);
        Assert.Contains("kick", all[0]);
        Assert.Contains("\tok\t", all[0]);
    }

    [Fact]
    public void HostileTextCannotForgeAColumnOrARow()
    {
        OwnerAudit.Initialise(_root);
        Assert.Null(OwnerAudit.Record("console", "say", "line\nbreak\tand\ttabs", ok: true, "detail\nwith\nnewlines"));

        var tail = OwnerAudit.Tail(10);
        Assert.Single(tail);
        // Exactly the five structural tabs of one record: time, actor, verb, target, ok, detail.
        Assert.Equal(5, tail[0].Count(c => c == '\t'));
    }

    [Fact]
    public void NowhereToLiveIsReportedNotThrown()
    {
        OwnerAudit.Initialise(null);
        string problem = OwnerAudit.Record("console", "kick", "x", ok: true, "y");
        Assert.NotNull(problem);
        Assert.Contains("nowhere", problem);
        Assert.Empty(OwnerAudit.Tail(10));
    }

    [Fact]
    public void RotatesInsteadOfGrowingForever()
    {
        OwnerAudit.Initialise(_root);
        string path = Path.Combine(_root, "owner-actions.log");
        // Pre-grow the file past the rotation size, then record once more.
        File.WriteAllText(path, new string('x', 1024 * 1024 + 10) + "\n");
        Assert.Null(OwnerAudit.Record("console", "kick", "target", ok: true, "after rotation"));

        Assert.True(File.Exists(path + ".1"));
        var tail = OwnerAudit.Tail(10);
        Assert.Single(tail);
        Assert.Contains("after rotation", tail[0]);
    }

    [Fact]
    public void EmptyFieldsBecomeDashesSoTheColumnsStayCountable()
    {
        OwnerAudit.Initialise(_root);
        Assert.Null(OwnerAudit.Record(null, "kick", "", ok: false, null));
        var tail = OwnerAudit.Tail(1);
        string[] columns = tail[0].Split('\t');
        Assert.Equal(6, columns.Length);
        Assert.Equal("-", columns[1]);
        Assert.Equal("-", columns[3]);
        Assert.Equal("-", columns[5]);
    }
}
