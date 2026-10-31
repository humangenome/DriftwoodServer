using System;
using System.IO;
using System.Threading;
using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The 2026-10-30 audit fixes that can be proven without a running game: state files are replaced
// in one step, a restore swap leaves either the old world or the new one, and a main-thread request
// that timed out never runs late.
public class FileSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dw-filesafety-" + Guid.NewGuid().ToString("N"));

    public FileSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Folder(string name, params (string File, string Text)[] files)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        foreach (var (file, text) in files) File.WriteAllText(Path.Combine(path, file), text);
        return path;
    }

    [Fact]
    public void ReplaceWith_SwapsAnExistingFileAndMovesWhenThereIsNone()
    {
        string path = Path.Combine(_root, "state.json");
        string first = path + ".a.tmp";
        File.WriteAllText(first, "one");
        HostFiles.ReplaceWith(first, path);
        Assert.Equal("one", File.ReadAllText(path));
        Assert.False(File.Exists(first));

        string second = path + ".b.tmp";
        File.WriteAllText(second, "two");
        HostFiles.ReplaceWith(second, path);
        Assert.Equal("two", File.ReadAllText(path));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void WorldSwap_AppliesTheStagedWorldAndLeavesNoSideFolders()
    {
        string saves = Folder("Saves", ("Driftwood.txt", "old"), ("Driftwood.txt.backup", "older"));
        string staging = Folder("pending", ("Driftwood.txt", "restored"));

        Assert.Equal(WorldSwapResult.Applied, WorldSwap.Apply(staging, saves, out string failure));
        Assert.Null(failure);
        Assert.Equal("restored", File.ReadAllText(Path.Combine(saves, "Driftwood.txt")));
        Assert.False(File.Exists(Path.Combine(saves, "Driftwood.txt.backup")));    // the old world's files go, as before
        Assert.False(Directory.Exists(saves + ".restore-incoming"));
        Assert.False(Directory.Exists(saves + ".restore-replaced"));
    }

    [Fact]
    public void WorldSwap_AFailureMovingTheNewWorldInPutsTheOldWorldBack()
    {
        string saves = Folder("Saves", ("Driftwood.txt", "old"), ("Players.txt", "old players"));
        // A directory named like a staged file makes the last move fail, after the old files moved aside.
        Directory.CreateDirectory(Path.Combine(saves, "Zeta.txt"));
        string staging = Folder("pending", ("Driftwood.txt", "restored"), ("Zeta.txt", "z"));

        Assert.Equal(WorldSwapResult.Untouched, WorldSwap.Apply(staging, saves, out string failure));
        Assert.Contains("could not be moved into the save folder", failure);
        Assert.Equal("old", File.ReadAllText(Path.Combine(saves, "Driftwood.txt")));
        Assert.Equal("old players", File.ReadAllText(Path.Combine(saves, "Players.txt")));
        Assert.False(Directory.Exists(saves + ".restore-replaced"));
        Assert.True(File.Exists(Path.Combine(staging, "Driftwood.txt")));    // staged files kept for the next try
    }

    [Fact]
    public void WorldSwap_NeverClearsAFolderAPreviousSwapLeftTheOldWorldIn()
    {
        string saves = Folder("Saves");
        string replaced = Folder("Saves.restore-replaced", ("Driftwood.txt", "the only copy"));
        string staging = Folder("pending", ("Driftwood.txt", "restored"));

        Assert.Equal(WorldSwapResult.Broken, WorldSwap.Apply(staging, saves, out string failure));
        Assert.Contains("previous restore left", failure);
        Assert.Equal("the only copy", File.ReadAllText(Path.Combine(replaced, "Driftwood.txt")));
        Assert.Empty(Directory.GetFiles(saves));
    }

    [Fact]
    public void MainThread_ARequestThatTimedOutBeforeItStartedNeverRuns()
    {
        MainThread.Pump();    // the frame loop has started at least once
        bool ran = false;
        Assert.False(MainThread.Run(() => { ran = true; }, 100, out string failure));
        Assert.Contains("nothing was changed", failure);
        MainThread.Pump();    // the main thread recovers
        Assert.False(ran);
    }

    [Fact]
    public void MainThread_ARequestThatStartedBeforeTheTimeoutReportsItsRealResult()
    {
        MainThread.Pump();
        bool ran = false;
        Thread pump = new Thread(() => { Thread.Sleep(50); MainThread.Pump(); });
        pump.Start();
        Assert.True(MainThread.Run(() => { Thread.Sleep(400); ran = true; }, 150, out string failure));
        pump.Join();
        Assert.Null(failure);
        Assert.True(ran);
    }
}
