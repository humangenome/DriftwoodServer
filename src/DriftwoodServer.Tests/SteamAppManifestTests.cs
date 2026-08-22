using DriftwoodServer;
using Xunit;

namespace DriftwoodServer.Tests;

public class SteamAppManifestTests
{
    private const string Sample = """
"AppState"
{
	"appid"		"4001890"
	"Universe"		"1"
	"name"		"How to Fish"
	"StateFlags"		"4"
	"installdir"		"How to Fish"
	"LastUpdated"		"1755800000"
	"UpdateResult"		"0"
	"buildid"		"24866339"
	"TargetBuildID"		"0"
	"InstalledDepots"
	{
		"4001891"
		{
			"manifest"		"1234567890123456789"
			"size"		"642000000"
		}
	}
}
""";

    [Fact]
    public void ParsesTopLevelScalars()
    {
        SteamAppManifest manifest = SteamAppManifest.Parse(Sample);
        Assert.Equal(4001890, manifest.AppId);
        Assert.Equal("24866339", manifest.BuildId);
        Assert.Equal("0", manifest.TargetBuildId);
        Assert.Equal("4", manifest.StateFlags);
        Assert.Equal("0", manifest.UpdateResult);
        Assert.True(manifest.TryGetStateFlags(out int flags));
        Assert.Equal(SteamAppManifest.StateFullyInstalled, flags & SteamAppManifest.StateFullyInstalled);
    }

    [Fact]
    public void NestedBlocksDoNotOverwriteTopLevelKeys()
    {
        // "InstalledDepots" reuses names like "manifest" and "size". First occurrence must win,
        // or a nested value silently becomes the answer to a top-level question.
        SteamAppManifest manifest = SteamAppManifest.Parse(Sample);
        Assert.Equal("24866339", manifest.BuildId);
    }

    [Fact]
    public void MissingFieldsBecomeEmptyRatherThanThrowing()
    {
        SteamAppManifest manifest = SteamAppManifest.Parse("\"AppState\"\n{\n}\n");
        Assert.Equal(string.Empty, manifest.BuildId);
        Assert.False(manifest.TryGetStateFlags(out _));
    }
}
