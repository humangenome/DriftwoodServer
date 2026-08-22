using DriftwoodServer;
using Xunit;

namespace DriftwoodServer.Tests;

public class ReadinessDocumentTests
{
    // Verbatim from the first real run on the HTPC, 2026-08-22.
    private const string Real = """
{"schema":1,"product":"Driftwood","pluginVersion":"0.1.0","gameVersion":"1.0.4","timestampUtc":"2026-08-22T12:23:55.3545421Z","phase":"Hosting","reason":"Hosting \"Driftwood\" on port 7801","worldRunning":true,"serverStarted":true,"localClientStarted":true,"worldObjectPresent":true,"islandLoaded":true,"islandLoading":false,"port":7801,"slots":8,"transportMaxClients":9,"connectedTransportClients":1,"players":0,"worldName":"Driftwood","saveDirectory":"C:/driftbench/i1/driftwood-saves/","ghostHostSuppressed":true,"displayNamesResolved":true,"effectiveBindAddress":"0.0.0.0","effectiveTargetFrameRate":30,"swallowedTotal":0,"swallowed":[],"patchesApplied":["InstanceManager.RenderBatches"],"patchesMissing":[],"patchesFailed":[],"featuresStoodDown":[]}
""";

    [Fact]
    public void ParsesTheDocumentTheHostModActuallyWrites()
    {
        string path = Path.Combine(Path.GetTempPath(), "driftwood-readiness-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, Real);
        try
        {
            ReadinessDocument? document = ReadinessDocument.TryRead(path);
            Assert.NotNull(document);
            Assert.True(document!.WorldRunning);
            Assert.Equal(7801, document.Port);
            Assert.Equal(8, document.Slots);
            // The host's own loopback connection occupies a transport slot and is never sold.
            Assert.Equal(9, document.TransportMaxClients);
            Assert.Equal(1, document.ConnectedTransportClients);
            Assert.Equal(0, document.Players);
            Assert.True(document.GhostHostSuppressed);
            Assert.Equal(0, document.SwallowedTotal);
            Assert.Empty(document.PatchesFailed);
            Assert.Equal("1.0.4", document.GameVersion);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void AStaleDocumentIsNotFresh()
    {
        ReadinessDocument document = new() { TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
        Assert.False(document.IsFresh(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void AMissingFileReadsAsNullRatherThanThrowing()
    {
        Assert.Null(ReadinessDocument.TryRead(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"))));
    }
}
