using System.IO.Compression;
using DriftwoodServer;
using Xunit;

namespace DriftwoodServer.Tests;

public class SaveSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "driftwood-snap-" + Guid.NewGuid().ToString("N"));
    private readonly HostOptions _options;

    public SaveSnapshotTests()
    {
        _options = new HostOptions
        {
            InstanceId = "gs1",
            GameRoot = Path.Combine(_root, "game"),
            StateRoot = Path.Combine(_root, "state"),
            SaveRoot = Path.Combine(_root, "saves"),
            BackupRoot = Path.Combine(_root, "backups"),
            WorldName = "Driftwood",
            // No server is running in a unit test, so the save request fails and the snapshot is
            // taken anyway with that fact reported - which is the documented behaviour.
            GamePort = 22003,
            HttpPort = 22004
        };
        Directory.CreateDirectory(_options.SaveRoot);
        Directory.CreateDirectory(_options.BackupRoot);
    }

    [Fact]
    public async Task CapturesTheWorldAndSaysTheFlushWasNotConfirmed()
    {
        File.WriteAllText(Path.Combine(_options.SaveRoot, "Driftwood.txt"), "{\"Name\":\"Driftwood\",\"Money\":42}");
        File.WriteAllText(Path.Combine(_options.SaveRoot, "local.txt"), "{}");

        SaveSnapshot.Result result = await new SaveSnapshot(_options).CaptureAsync("token", CancellationToken.None);

        Assert.True(result.Ok, result.Reason);
        Assert.True(File.Exists(result.Path));
        // The honesty requirement: an unconfirmed flush is stated, not swallowed.
        Assert.Contains("did not confirm a save", result.Reason, StringComparison.OrdinalIgnoreCase);
        using ZipArchive archive = ZipFile.OpenRead(result.Path);
        Assert.Contains(archive.Entries, entry => entry.Name == "Driftwood.txt");
    }

    [Fact]
    public async Task RefusesWhenTheArchiveWouldNotContainTheWorld()
    {
        // Everything succeeds mechanically and the archive is valid - it just would not restore
        // the customer's world. Verified by reading the archive back, never by a successful write.
        File.WriteAllText(Path.Combine(_options.SaveRoot, "local.txt"), "{}");

        SaveSnapshot.Result result = await new SaveSnapshot(_options).CaptureAsync("token", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Driftwood.txt", result.Reason);
    }

    [Fact]
    public async Task RefusesWhenNoBackupFolderIsConfigured()
    {
        _options.BackupRoot = string.Empty;
        SaveSnapshot.Result result = await new SaveSnapshot(_options).CaptureAsync("token", CancellationToken.None);
        Assert.False(result.Ok);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }
}
