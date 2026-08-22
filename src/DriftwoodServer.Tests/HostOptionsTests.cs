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
    public void RejectsAFrameCapFarBelowTheNetworkTickRate()
    {
        // The game's netcode ticks at 50 Hz. A cap under 20 runs the loop far below that and
        // batches every send - a smoothness cost paid for CPU, in a game whose whole feel is
        // objects moving. If it is genuinely wanted, the tick rate has to come down with it.
        HostOptions options = Valid();
        options.TargetFrameRate = 5;
        Assert.Throws<InvalidDataException>(options.Validate);
    }

    [Fact]
    public void AllowsAnIdleRateOfFiveBecauseNobodyIsWatching()
    {
        // The IDLE rate is a different thing: with nobody connected there is no smoothness to cost.
        HostOptions options = Valid();
        options.TargetFrameRate = 60;
        options.IdleFrameRate = 5;
        options.Validate();
    }

    [Fact]
    public void RejectsAStateRootInsideTheGameRoot()
    {
        HostOptions options = Valid();
        options.StateRoot = options.GameRoot;
        Assert.Throws<InvalidDataException>(options.Validate);
    }
}
