using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// World restore imports: an uploaded or stored archive is unpacked into the staged-restore folder
// and swapped into the save folder at the next start. These prove the caps on what an archive may
// unpack to, and that a link or junction at any folder the restore uses stops it with nothing
// changed. A folder link here is a real junction on Windows (mklink /J) and a directory symbolic
// link elsewhere.
public class RestoreImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dw-restore-" + Guid.NewGuid().ToString("N"));
    private readonly string _instance;   // the server's own folder
    private readonly string _saves;      // <instance>\Saves
    private readonly string _snapshots;  // <instance>\Snapshots
    private readonly string _elsewhere;  // where a link would send the restore

    public RestoreImportTests()
    {
        _instance = Path.Combine(_root, "instance");
        _saves = Path.Combine(_instance, "Saves");
        _snapshots = Path.Combine(_instance, "Snapshots");
        _elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(_instance);
        Directory.CreateDirectory(_elsewhere);
        File.WriteAllText(Path.Combine(_elsewhere, "Driftwood.txt"), "a world that is not this server's");
        File.WriteAllText(Path.Combine(_elsewhere, "keep.txt"), "outside");
    }

    public void Dispose()
    {
        WorldSwap.TestBeforeMove = null;
        // Links first, as links, never into what they point at.
        try { RemoveLinks(_root); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void RemoveLinks(string folder)
    {
        foreach (string entry in Directory.GetFileSystemEntries(folder))
        {
            if (FolderLinks.IsLink(entry))
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, false); else File.Delete(entry);
            }
            else if (Directory.Exists(entry)) RemoveLinks(entry);
        }
    }

    private static void FolderLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start)!) { process.WaitForExit(15000); }
            Assert.True(Directory.Exists(link), "the junction was not made");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
        // The check the host relies on: a reparse point, seen without following it.
        Assert.True(FolderLinks.IsLink(link));
    }

    private string Zip(string name, params (string Entry, string Text)[] entries)
    {
        string path = Path.Combine(_root, name);
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (entry, text) in entries)
            {
                using (var writer = new StreamWriter(archive.CreateEntry(entry).Open())) writer.Write(text);
            }
        }
        return path;
    }

    private void LiveWorld()
    {
        Directory.CreateDirectory(_saves);
        File.WriteAllText(Path.Combine(_saves, "Driftwood.txt"), "live world");
    }

    private void OneSnapshot()
    {
        Directory.CreateDirectory(_snapshots);
        File.Copy(Zip("seed.zip", ("Driftwood.txt", "an older world")), Path.Combine(_snapshots, "driftwood-older.zip"));
    }

    // Everything under the folder, links named and not followed, files with a hash of their bytes.
    private static string Tree(string folder)
    {
        var lines = new List<string>();
        Walk(folder, folder, lines);
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    private static void Walk(string top, string folder, List<string> lines)
    {
        foreach (string entry in Directory.GetFileSystemEntries(folder))
        {
            string relative = Path.GetRelativePath(top, entry);
            if (FolderLinks.IsLink(entry)) lines.Add("link " + relative);
            else if (Directory.Exists(entry)) { lines.Add("folder " + relative); Walk(top, entry, lines); }
            else lines.Add("file " + relative + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))));
        }
    }

    private string Pending => SnapshotFiles.PendingDirectory(_snapshots);

    // ---- the link check itself ------------------------------------------------------------------

    [Fact]
    public void LinkCheck_SeesAFolderLinkWithOrWithoutATrailingSeparator()
    {
        string link = Path.Combine(_root, "link");
        FolderLink(link, _elsewhere);
        Assert.True(FolderLinks.IsLink(link + Path.DirectorySeparatorChar));
        Assert.False(FolderLinks.IsLink(_elsewhere));
        Assert.False(FolderLinks.IsLink(Path.Combine(_root, "not-there")));
        Assert.Null(FolderLinks.Problem(_root, Path.Combine(_root, "not-there", "below")));
    }

    [Fact]
    public void LinkCheck_TheServerFolderItselfMayBeALink()
    {
        // A hosting panel may keep a whole server folder on a second drive behind one link. The
        // check starts below the server folder, so a restore through it works as before.
        string real = Path.Combine(_root, "second-drive", "instance");
        Directory.CreateDirectory(Path.Combine(real, "Saves"));
        Directory.CreateDirectory(Path.Combine(real, "Snapshots"));
        File.WriteAllText(Path.Combine(real, "Saves", "Driftwood.txt"), "live world");
        string server = Path.Combine(_root, "linked-instance");
        FolderLink(server, real);
        string saves = Path.Combine(server, "Saves");
        string snapshots = Path.Combine(server, "Snapshots");

        Assert.Null(SnapshotFiles.RestoreLinkProblem(server, snapshots, saves));
        Assert.True(SnapshotFiles.Stage(Zip("restore.zip", ("Driftwood.txt", "restored")), server, snapshots, saves, out string failure), failure);
        Assert.Equal(WorldSwapResult.Applied, WorldSwap.Apply(SnapshotFiles.PendingDirectory(snapshots), saves, out failure));
        Assert.Equal("restored", File.ReadAllText(Path.Combine(real, "Saves", "Driftwood.txt")));
    }

    // ---- staging a restore ----------------------------------------------------------------------

    [Fact]
    public void Restore_ANormalArchiveStagesAndApplies()
    {
        LiveWorld();
        OneSnapshot();
        string archive = Zip("restore.zip", ("Saves/Driftwood.txt", "restored"), ("Saves/Driftwood.txt.backup", "restored backup"));

        Assert.True(SnapshotFiles.Stage(archive, _instance, _snapshots, _saves, out string failure), failure);
        Assert.Equal("restored", File.ReadAllText(Path.Combine(Pending, "Driftwood.txt")));
        Assert.Equal(WorldSwapResult.Applied, WorldSwap.Apply(Pending, _saves, out failure));
        Assert.Null(failure);
        Assert.Equal("restored", File.ReadAllText(Path.Combine(_saves, "Driftwood.txt")));
        Assert.Equal("restored backup", File.ReadAllText(Path.Combine(_saves, "Driftwood.txt.backup")));
        Assert.True(File.Exists(Path.Combine(_snapshots, "driftwood-older.zip")));
    }

    [Fact]
    public void Restore_KeepsOnlyBareFileNamesInsideTheStagedFolder()
    {
        LiveWorld();
        string archive = Zip("restore.zip", ("Driftwood.txt", "restored"), ("../../escape.txt", "x"), ("nested/deeper/inner.txt", "y"));

        Assert.True(SnapshotFiles.Stage(archive, _instance, _snapshots, _saves, out string failure), failure);
        Assert.True(File.Exists(Path.Combine(Pending, "escape.txt")));
        Assert.True(File.Exists(Path.Combine(Pending, "inner.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_instance, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_snapshots, "escape.txt")));
    }

    public static TheoryData<string> RestorePlaces => new()
    {
        "Snapshots",
        "Snapshots/.pending-restore",
        "Saves",
        "Saves.restore-incoming",
        "Saves.restore-replaced",
    };

    [Theory]
    [MemberData(nameof(RestorePlaces))]
    public void Restore_ALinkAtAnyFolderItUsesRefusesAndChangesNothing(string place)
    {
        string link = Path.Combine(_instance, place.Replace('/', Path.DirectorySeparatorChar));
        if (place != "Saves") LiveWorld();
        if (place != "Snapshots") OneSnapshot();
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        FolderLink(link, _elsewhere);
        string archive = Zip("restore.zip", ("Driftwood.txt", "restored"));
        string before = Tree(_root);

        Assert.False(SnapshotFiles.Stage(archive, _instance, _snapshots, _saves, out string failure));
        Assert.Contains("is a link or junction, so nothing was changed", failure);
        Assert.Equal(before, Tree(_root));
        // The same check runs at the next start, before a staged world is swapped in.
        Assert.NotNull(SnapshotFiles.RestoreLinkProblem(_instance, _snapshots, _saves));
    }

    // ---- what an archive may unpack to --------------------------------------------------------

    [Fact]
    public void Restore_AnArchiveThatUnpacksPastTheCapStopsAndLeavesNoStagedFolder()
    {
        Assert.Equal(64L * 1024 * 1024, SnapshotFiles.MaxUnpackedBytes);
        LiveWorld();
        OneSnapshot();
        // Honest sizes, a few hundred KB of zip: a small world, then one file the size of the cap.
        string archive = Path.Combine(_root, "big.zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("Driftwood.txt").Open())) writer.Write("restored");
            using (Stream big = zip.CreateEntry("filler.txt").Open())
            {
                byte[] zeros = new byte[1024 * 1024];
                for (int i = 0; i < 64; i++) big.Write(zeros, 0, zeros.Length);
            }
        }
        Assert.True(new FileInfo(archive).Length < SnapshotFiles.MaxUnpackedBytes / 16);
        string saves = Tree(_saves);

        Assert.False(SnapshotFiles.Stage(archive, _instance, _snapshots, _saves, out string failure));
        Assert.Equal("that archive unpacks to more than 64 MB", failure);
        Assert.False(Directory.Exists(Pending));
        Assert.Equal(saves, Tree(_saves));
    }

    [Fact]
    public void Restore_AnEntryThatHoldsMoreThanItStatesIsRefused()
    {
        LiveWorld();
        // A stored (uncompressed) entry of 100,000 bytes whose headers say 1,000. .NET reads a
        // stored entry for as long as its data runs, so only the count stops it.
        string archive = Path.Combine(_root, "lying.zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (Stream entry = zip.CreateEntry("Driftwood.txt", CompressionLevel.NoCompression).Open())
        {
            entry.Write(Encoding.ASCII.GetBytes(new string('a', 100_000)));
        }
        byte[] bytes = File.ReadAllBytes(archive);
        int patched = 0;
        for (int i = 0; i + 4 <= bytes.Length; i++)
        {
            uint signature = BitConverter.ToUInt32(bytes, i);
            if (signature == 0x04034b50) { BitConverter.GetBytes(1000u).CopyTo(bytes, i + 22); patched++; }   // local header
            if (signature == 0x02014b50) { BitConverter.GetBytes(1000u).CopyTo(bytes, i + 24); patched++; }   // central directory
        }
        Assert.Equal(2, patched);
        File.WriteAllBytes(archive, bytes);

        Assert.False(SnapshotFiles.Stage(archive, _instance, _snapshots, _saves, out string failure));
        Assert.Equal("that archive is damaged: a file in it holds more than the size it states", failure);
        Assert.False(Directory.Exists(Pending));
        Assert.Equal("live world", File.ReadAllText(Path.Combine(_saves, "Driftwood.txt")));
    }

    [Fact]
    public void CopyCapped_StopsAStreamThatRunsPastItsStatedSizeBeforeWritingIt()
    {
        // What a runtime that does not stop a compressed entry at its stated size hands over.
        using var source = new MemoryStream(new byte[200_000]);
        using var destination = new MemoryStream();
        long left = SnapshotFiles.MaxUnpackedBytes;
        Assert.False(SnapshotFiles.CopyCapped(source, destination, 1000, ref left, SnapshotFiles.MaxUnpackedBytes, out string failure));
        Assert.Contains("more than the size it states", failure);
        Assert.True(destination.Length <= 1000);
    }

    [Fact]
    public void Restore_AnArchiveWithTooManyFilesIsRefusedBeforeAnythingIsWritten()
    {
        LiveWorld();
        var entries = new List<(string, string)> { ("Driftwood.txt", "restored") };
        for (int i = 0; i < SnapshotFiles.MaxEntries; i++) entries.Add(("empty" + i + ".txt", ""));
        string archive = Zip("many.zip", entries.ToArray());

        Assert.False(SnapshotFiles.Stage(archive, _instance, _snapshots, _saves, out string failure));
        Assert.Equal("that archive holds more than 1000 files and folders", failure);
        Assert.False(Directory.Exists(Pending));
    }

    // ---- the swap at the next start -----------------------------------------------------------

    [Theory]
    [InlineData("pending")]
    [InlineData("Saves")]
    [InlineData("Saves.restore-incoming")]
    [InlineData("Saves.restore-replaced")]
    public void WorldSwap_ALinkAtAnyFolderItUsesRefusesAndChangesNothing(string place)
    {
        string staging = Path.Combine(_root, "pending");
        if (place != "Saves") LiveWorld();
        if (place != "pending")
        {
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "Driftwood.txt"), "restored");
        }
        FolderLink(place == "pending" ? staging : Path.Combine(_instance, place), _elsewhere);
        string before = Tree(_root);

        Assert.Equal(WorldSwapResult.Untouched, WorldSwap.Apply(staging, _saves, out string failure));
        Assert.Contains("is a link or junction, so nothing was changed", failure);
        Assert.Equal(before, Tree(_root));
    }

    [Fact]
    public void WorldSwap_AStagedFileThatIsALinkIsRefused()
    {
        LiveWorld();
        string staging = Path.Combine(_root, "pending");
        Directory.CreateDirectory(staging);
        File.CreateSymbolicLink(Path.Combine(staging, "Driftwood.txt"), Path.Combine(_elsewhere, "keep.txt"));
        Assert.True(FolderLinks.IsLink(Path.Combine(staging, "Driftwood.txt")));

        Assert.Equal(WorldSwapResult.Untouched, WorldSwap.Apply(staging, _saves, out string failure));
        Assert.Contains("is a link", failure);
        Assert.Equal("live world", File.ReadAllText(Path.Combine(_saves, "Driftwood.txt")));
        Assert.False(Directory.Exists(WorldSwap.IncomingFolder(_saves)));
    }

    [Fact]
    public void WorldSwap_ALinkThatAppearsRightBeforeTheWorldIsMovedAsideStopsIt()
    {
        LiveWorld();
        File.WriteAllText(Path.Combine(_saves, "Players.txt"), "players");
        string staging = Path.Combine(_root, "pending");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "Driftwood.txt"), "restored");
        string replaced = WorldSwap.ReplacedFolder(_saves);
        string elsewhere = Tree(_elsewhere);
        bool swapped = false;
        WorldSwap.TestBeforeMove = destination =>
        {
            if (swapped || !destination.StartsWith(replaced + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
            swapped = true;
            Directory.Delete(replaced);    // empty: the swap has only just made it
            FolderLink(replaced, _elsewhere);
        };

        WorldSwapResult result = WorldSwap.Apply(staging, _saves, out string failure);
        WorldSwap.TestBeforeMove = null;

        Assert.True(swapped);
        Assert.Equal(WorldSwapResult.Untouched, result);
        Assert.Contains("is a link or junction", failure);
        Assert.Equal("live world", File.ReadAllText(Path.Combine(_saves, "Driftwood.txt")));
        Assert.Equal("players", File.ReadAllText(Path.Combine(_saves, "Players.txt")));
        Assert.Equal(elsewhere, Tree(_elsewhere));
    }

    [Fact]
    public void WorldSwap_ALinkThatAppearsRightBeforeTheNewWorldIsMovedInStopsItAndPutsTheOldWorldBack()
    {
        LiveWorld();
        string staging = Path.Combine(_root, "pending");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "Driftwood.txt"), "restored");
        string incoming = WorldSwap.IncomingFolder(_saves);
        string elsewhere = Tree(_elsewhere);
        bool swapped = false;
        WorldSwap.TestBeforeMove = destination =>
        {
            if (swapped || !destination.StartsWith(_saves + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
            swapped = true;
            Directory.Move(incoming, incoming + ".moved");
            FolderLink(incoming, _elsewhere);
        };

        WorldSwapResult result = WorldSwap.Apply(staging, _saves, out string failure);
        WorldSwap.TestBeforeMove = null;

        Assert.True(swapped);
        Assert.Equal(WorldSwapResult.Untouched, result);
        Assert.Contains("is a link or junction", failure);
        Assert.Equal("live world", File.ReadAllText(Path.Combine(_saves, "Driftwood.txt")));
        Assert.Equal(elsewhere, Tree(_elsewhere));
    }

    // ---- pruning the store --------------------------------------------------------------------

    private static void Snapshots(string folder, int count)
    {
        Directory.CreateDirectory(folder);
        DateTime oldest = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(folder, "s" + i.ToString("00") + ".zip");
            File.WriteAllText(path, "zip " + i);
            File.SetLastWriteTimeUtc(path, oldest.AddMinutes(i));    // s00 oldest, s24 newest
        }
    }

    private static List<string> Names(string folder)
    {
        var names = new List<string>();
        foreach (string file in Directory.GetFiles(folder, "*.zip")) names.Add(Path.GetFileNameWithoutExtension(file));
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    [Fact]
    public void Prune_NeverDeletesTheNewestOrTheArchiveBeingRestored()
    {
        Snapshots(_snapshots, 25);

        SnapshotFiles.Prune(_instance, _snapshots, 20, "s00");
        List<string> kept = Names(_snapshots);
        Assert.Equal(21, kept.Count);
        Assert.Contains("s24", kept);
        Assert.Contains("s00", kept);
        Assert.DoesNotContain("s01", kept);
        Assert.DoesNotContain("s04", kept);
        Assert.Contains("s05", kept);

        SnapshotFiles.Prune(_instance, _snapshots, 20, null);
        kept = Names(_snapshots);
        Assert.Equal(20, kept.Count);
        Assert.Contains("s24", kept);
        Assert.DoesNotContain("s00", kept);
    }

    [Fact]
    public void Prune_DeletesNothingWhenTheStoreIsALink()
    {
        string target = Path.Combine(_elsewhere, "zips");
        Snapshots(target, 25);
        FolderLink(_snapshots, target);

        SnapshotFiles.Prune(_instance, _snapshots, 20, null);
        Assert.Equal(25, Names(target).Count);
    }
}
