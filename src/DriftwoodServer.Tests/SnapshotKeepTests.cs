using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The store and the main-thread queue are static, so these run by themselves.
[CollectionDefinition("Snapshot store", DisableParallelization = true)]
public class SnapshotStoreCollection
{
}

// Nothing removes a stored world backup: not its age, not how many there are, not a newer one.
[Collection("Snapshot store")]
public class SnapshotKeepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dw-keep-" + Guid.NewGuid().ToString("N"));
    private readonly string _saves;
    private readonly string _snapshots;

    public SnapshotKeepTests()
    {
        _saves = Path.Combine(_root, "Saves");
        _snapshots = Path.Combine(_root, "Snapshots");
        Directory.CreateDirectory(_saves);
        File.WriteAllText(Path.Combine(_saves, "Island.txt"), "live world");
        SnapshotStore.Initialise(_saves, _root, "Island");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Zip(string path)
    {
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (StreamWriter writer = new StreamWriter(archive.CreateEntry("Island.txt").Open()))
        {
            writer.Write("stored world");
        }
        return path;
    }

    // Every kind of id the store has ever made, and names it never made, up to 400 days old.
    private string[] Stored(int count)
    {
        string one = Zip(Path.Combine(_root, "one.zip"));
        string[] kinds = { "manual", "console", "imported", "pre-restore" };
        string[] files = new string[count];
        for (int i = 0; i < count; i++)
        {
            string id = i % 5 == 4
                ? "my backup " + i
                : "driftwood-20250101-000000-" + kinds[i % 5] + "-" + i.ToString("x6");
            files[i] = Path.Combine(_snapshots, id + ".zip");
            File.Copy(one, files[i]);
            File.SetLastWriteTimeUtc(files[i], DateTime.UtcNow.AddMinutes(-1 - i * 800));
        }
        return files;
    }

    private static T WithMainThread<T>(Func<T> call)
    {
        MainThread.Pump(); // the frame loop has started
        Task<T> running = Task.Run(call);
        DateTime until = DateTime.UtcNow.AddSeconds(60);
        while (!running.IsCompleted && DateTime.UtcNow < until)
        {
            MainThread.Pump();
            Thread.Sleep(5);
        }
        Assert.True(running.IsCompleted, "the store did not answer");
        return running.Result;
    }

    private int Count() => Directory.GetFiles(_snapshots, "*.zip").Length;

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(720)]
    public void An_upload_removes_no_stored_backup(int stored)
    {
        string[] before = Stored(stored);

        Assert.True(SnapshotStore.Adopt(Zip(Path.Combine(_root, "upload.zip")), out _, out string failure), failure);

        Assert.All(before, file => Assert.True(File.Exists(file), file));
        Assert.Equal(stored + 1, Count());
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("console")]
    public void A_backup_by_hand_removes_no_stored_backup(string reason)
    {
        string[] before = Stored(720);

        (bool ok, string failure) = WithMainThread(() => (SnapshotStore.Create(reason, out _, out string why), why));

        Assert.True(ok, failure);
        Assert.All(before, file => Assert.True(File.Exists(file), file));
        Assert.Equal(721, Count());
    }

    [Fact]
    public void Restoring_the_oldest_removes_no_stored_backup()
    {
        string[] before = Stored(720);
        // the oldest with an id the restore route accepts (the last one is a name the store never made)
        string oldest = Path.GetFileNameWithoutExtension(before[^2]);

        (bool ok, string failure) = WithMainThread(() => (SnapshotStore.Restore(oldest, out string why), why));

        Assert.True(ok, failure);
        Assert.All(before, file => Assert.True(File.Exists(file), file));
        Assert.Equal(721, Count());
    }
}
