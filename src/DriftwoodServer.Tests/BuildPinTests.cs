using System.Security.Cryptography;
using System.Text;
using DriftwoodServer;
using Xunit;

namespace DriftwoodServer.Tests;

public class BuildPinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "driftwood-pin-" + Guid.NewGuid().ToString("N"));
    private readonly string _gameRoot;
    private readonly string _steamApps;
    private readonly string _assemblyRelative = Path.Combine("Data", "Managed", "Assembly-CSharp.dll");
    private readonly string _assemblyHash;

    public BuildPinTests()
    {
        _gameRoot = Path.Combine(_root, "game");
        _steamApps = Path.Combine(_root, "steamapps");
        Directory.CreateDirectory(Path.Combine(_gameRoot, "Data", "Managed"));
        Directory.CreateDirectory(_steamApps);
        byte[] bytes = Encoding.UTF8.GetBytes("pretend Assembly-CSharp");
        File.WriteAllBytes(Path.Combine(_gameRoot, _assemblyRelative), bytes);
        _assemblyHash = Convert.ToHexString(SHA256.HashData(bytes));
    }

    private void WriteManifest(string buildId, string targetBuildId, string stateFlags, string updateResult)
    {
        string manifest = string.Join("\n",
            "\"AppState\"",
            "{",
            "\t\"appid\"\t\t\"4001890\"",
            "\t\"StateFlags\"\t\t\"" + stateFlags + "\"",
            "\t\"UpdateResult\"\t\t\"" + updateResult + "\"",
            "\t\"buildid\"\t\t\"" + buildId + "\"",
            "\t\"TargetBuildID\"\t\t\"" + targetBuildId + "\"",
            "}");
        File.WriteAllText(Path.Combine(_steamApps, "appmanifest_4001890.acf"), manifest);
    }

    private BuildPinResult Verify(string expectedHash, string? expectedBuild) =>
        BuildPin.Verify(_gameRoot, _assemblyRelative, expectedHash, 4001890, expectedBuild, _steamApps);

    [Fact]
    public void PassesWhenHashAndManifestAgree()
    {
        WriteManifest("24866339", "0", "4", "0");
        Assert.True(Verify(_assemblyHash, "24866339").Ok);
    }

    [Fact]
    public void RefusesWhenTheAssemblyChanged()
    {
        WriteManifest("24866339", "0", "4", "0");
        BuildPinResult result = Verify(new string('a', 64), "24866339");
        Assert.False(result.Ok);
        Assert.Contains("the game's code has changed", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesWhenSteamHasQueuedAnUpdate()
    {
        // The hash is still correct here - the new files have not landed yet. This is the case a
        // hash check alone cannot see.
        WriteManifest("24866339", "24900000", "4", "0");
        BuildPinResult result = Verify(_assemblyHash, "24866339");
        Assert.False(result.Ok);
        Assert.Contains("queued an update", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesWhenNotFullyInstalled()
    {
        WriteManifest("24866339", "0", "1026", "0");
        Assert.False(Verify(_assemblyHash, "24866339").Ok);
    }

    [Fact]
    public void RefusesWhenTheLastUpdateFailed()
    {
        WriteManifest("24866339", "0", "4", "20");
        Assert.False(Verify(_assemblyHash, "24866339").Ok);
    }

    [Fact]
    public void RefusesWhenSteamMovedToANewBuild()
    {
        WriteManifest("24900000", "0", "4", "0");
        BuildPinResult result = Verify(_assemblyHash, "24866339");
        Assert.False(result.Ok);
        Assert.Contains("has been updated", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesWhenTheManifestIsMissing()
    {
        // A check that cannot make its decision FAILS, it does not pass.
        BuildPinResult result = Verify(_assemblyHash, "24866339");
        Assert.False(result.Ok);
        Assert.Contains("install record", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesWhenTheAssemblyIsMissing()
    {
        File.Delete(Path.Combine(_gameRoot, _assemblyRelative));
        WriteManifest("24866339", "0", "4", "0");
        Assert.False(Verify(_assemblyHash, "24866339").Ok);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }
}
