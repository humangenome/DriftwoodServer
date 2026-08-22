using DriftwoodServer;
using Xunit;

namespace DriftwoodServer.Tests;

public class HostOptionsTests
{
    private static HostOptions Valid() => new()
    {
        InstanceId = "gs219092",
        GameRoot = "/srv/game",
        StateRoot = "/srv/state",
        SaveRoot = "/srv/saves",
        GamePort = 7777,
        HttpPort = 7781,
        Slots = 8,
        WorldName = "Driftwood",
        PinnedBuild = new PinnedBuildOptions { AssemblySha256 = new string('a', 64) }
    };

    [Fact]
    public void AcceptsAValidConfiguration() => Valid().Validate();

    [Fact]
    public void RejectsAnEmptyBuildPin()
    {
        // An empty pin is not "no pin" - it means no validated build has been published, and every
        // start must refuse until one has been.
        HostOptions options = Valid();
        options.PinnedBuild.AssemblySha256 = string.Empty;
        Assert.Throws<InvalidDataException>(options.Validate);
    }

    [Fact]
    public void RejectsTheReservedWorldName()
    {
        // The game reserves "local" for its per-machine settings file and would overwrite it.
        HostOptions options = Valid();
        options.WorldName = "local";
        Assert.Throws<InvalidDataException>(options.Validate);
    }

    [Fact]
    public void RejectsAWorldNameThatIsNotAFileName()
    {
        HostOptions options = Valid();
        options.WorldName = "bad/name";
        Assert.Throws<InvalidDataException>(options.Validate);
    }

    [Fact]
    public void RejectsAHttpPortThatCollidesWithTheGamePort()
    {
        HostOptions options = Valid();
        options.HttpPort = options.GamePort;
        Assert.Throws<InvalidDataException>(options.Validate);
    }

    [Fact]
    public void RejectsAStateRootInsideTheGameRoot()
    {
        HostOptions options = Valid();
        options.StateRoot = options.GameRoot;
        Assert.Throws<InvalidDataException>(options.Validate);
    }
}
