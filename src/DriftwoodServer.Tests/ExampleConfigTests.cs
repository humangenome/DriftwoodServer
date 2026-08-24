using DriftwoodServer;
using Xunit;

namespace DriftwoodServer.Tests;

public class ExampleConfigTests
{
    // The shipped example config must be a config the supervisor can ACTUALLY load.
    //
    // This exists because the example briefly carried "_comment_" keys to explain the tuning
    // verdicts - and HostOptions deserialises with UnmappedMemberHandling.Disallow, so it would
    // have thrown on the first line for anybody who copied it. A documentation improvement that
    // breaks the artefact it documents is exactly the shape this repo refuses to ship, and nothing
    // else in the build would have caught it.
    private static string ExamplePath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "appsettings.example.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("appsettings.example.json was not found above the test output directory.");
    }

    [Fact]
    public void TheShippedExampleLoads()
    {
        // The example's build pin is deliberately empty (see the second test), which Load rejects
        // by design - so fill it with a placeholder to exercise everything else.
        string text = File.ReadAllText(ExamplePath()).Replace("\"assemblySha256\": \"\"", "\"assemblySha256\": \"" + new string('a', 64) + "\"");
        string temporary = Path.Combine(Path.GetTempPath(), "driftwood-example-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(temporary, text);
        HostOptions options;
        try { options = HostOptions.Load(temporary); }
        finally { try { File.Delete(temporary); } catch { } }
        Assert.Equal(22003, options.GamePort);
        // Zero, deliberately: the host mod takes gamePort + 1 for its API on its own, and
        // a nonzero httpPort of 22004 makes the supervisor's health endpoint claim the
        // port first - the mod then refuses to host. The executed self-hosting walk hit
        // exactly that with the old example value.
        Assert.Equal(0, options.HttpPort);
        Assert.True(options.SuppressGhostHost);
        // Off by default: standing the world down is only safe once resumption is proven with a
        // retail client.
        Assert.False(options.PauseWorldWhenEmpty);
        Assert.Equal(0, options.IdleFrameRate);
    }

    [Fact]
    public void TheExamplePinIsDeliberatelyEmptyAndThereforeRefuses()
    {
        // An empty pin is not "no pin" - it means no validated build has been published, and every
        // start must refuse until one has been. The example ships that way on purpose.
        string text = File.ReadAllText(ExamplePath());
        Assert.Contains("\"assemblySha256\": \"\"", text);
        HostOptions options = new()
        {
            InstanceId = "gs1",
            GameRoot = "/srv/game",
            StateRoot = "/srv/state",
            SaveRoot = "/srv/saves",
            PinnedBuild = new PinnedBuildOptions { AssemblySha256 = string.Empty }
        };
        Assert.Throws<InvalidDataException>(options.Validate);
    }
}
