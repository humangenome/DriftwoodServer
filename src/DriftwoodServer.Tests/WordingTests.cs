using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// What an owner reads in the console's status: real plurals, and the headline said once.
public class WordingTests
{
    [Theory]
    [InlineData(0, "0 players")]
    [InlineData(1, "1 player")]
    [InlineData(3, "3 players")]
    public void Count_UsesRealPlurals(int n, string expected) =>
        Assert.Equal(expected, Wording.Count(n, "player"));

    [Theory]
    [InlineData("Hosting", "Hosting \"Gull Rock\" on port 22043", "Hosting \"Gull Rock\" on port 22043")]
    [InlineData("Starting", "Starting", "Starting")]
    [InlineData("Loading", "Loading the world", "Loading the world")]
    [InlineData("WillNotHost", "the game build moved SaveSystem.Init", "Will not host: the game build moved SaveSystem.Init")]
    [InlineData("Stopped", "", "Stopped")]
    public void StatusHeadline_SaysThePhaseOnce(string phase, string reason, string expected) =>
        Assert.Equal(expected, Wording.StatusHeadline(phase, reason));
}
