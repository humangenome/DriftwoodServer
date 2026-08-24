using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The parser eats the one payload in the product that is built from attacker-controlled text:
// a Steam persona name can be anything 32 characters long, including things that look like
// JSON, tabs that would forge a column in the cache file, and newlines that would forge a row.
public class SteamProfileParserTests
{
    private const string RealShape =
        "{\"response\":{\"players\":[" +
        "{\"steamid\":\"76561197960287930\",\"communityvisibilitystate\":3,\"personaname\":\"Rabscuttle\",\"avatar\":\"x\"}," +
        "{\"steamid\":\"76561198000000001\",\"personaname\":\"Second Player\"}" +
        "]}}";

    [Fact]
    public void ParsesTheRealResponseShape()
    {
        var profiles = SteamProfileParser.Parse(RealShape);

        Assert.Equal(2, profiles.Count);
        Assert.Equal(76561197960287930UL, profiles[0].SteamId);
        Assert.Equal("Rabscuttle", profiles[0].PersonaName);
        Assert.Equal("Second Player", profiles[1].PersonaName);
    }

    [Fact]
    public void APersonaNameThatContainsAFakeRecordCannotInjectOne()
    {
        // Inside valid JSON every quote in a string value arrives escaped, so the needle
        // "steamid" (raw quotes included) can never match inside a name. This is the exact
        // hostile input the parser's header comment stakes its safety on.
        string body =
            "{\"response\":{\"players\":[" +
            "{\"steamid\":\"76561197960287930\",\"personaname\":\"evil \\\"steamid\\\":\\\"76561190000000009\\\" name\"}" +
            "]}}";

        var profiles = SteamProfileParser.Parse(body);

        Assert.Single(profiles);
        Assert.Equal(76561197960287930UL, profiles[0].SteamId);
        Assert.Contains("steamid", profiles[0].PersonaName);
    }

    [Fact]
    public void OnePlayersMissingNameCannotStealTheNextPlayers()
    {
        string body =
            "{\"response\":{\"players\":[" +
            "{\"steamid\":\"76561197960287930\",\"avatar\":\"x\"}," +
            "{\"steamid\":\"76561198000000001\",\"personaname\":\"Owner\"}" +
            "]}}";

        var profiles = SteamProfileParser.Parse(body);

        // The first record has no name inside its own window and is dropped; the second keeps
        // its name. A window-less scan would have paired the first id with the second name.
        Assert.Single(profiles);
        Assert.Equal(76561198000000001UL, profiles[0].SteamId);
        Assert.Equal("Owner", profiles[0].PersonaName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"response\":{\"players\":[]}}")]
    [InlineData("{\"response\":{\"players\":[{\"steamid\":\"zero\",\"personaname\":\"x\"}]}}")]
    [InlineData("{\"response\":{\"players\":[{\"steamid\":\"0\",\"personaname\":\"x\"}]}}")]
    public void MalformedOrEmptyBodiesYieldNothingAndNeverThrow(string body)
    {
        Assert.Empty(SteamProfileParser.Parse(body));
    }

    [Fact]
    public void SanitizeStripsTheCharactersTheStoresUseAsStructure()
    {
        // Tab and newline are column and row separators in the cache, the block list and the
        // audit log; a name keeping either could forge an entry in all three.
        Assert.Equal("ab", SteamProfileParser.Sanitize("a\tb\r\n"));
        Assert.Equal("spaced", SteamProfileParser.Sanitize("  spaced  "));
        Assert.Equal(string.Empty, SteamProfileParser.Sanitize("\t\r\n"));
    }

    [Fact]
    public void SanitizeCapsRunawayLength()
    {
        string absurd = new string('x', 500);
        Assert.Equal(48, SteamProfileParser.Sanitize(absurd).Length);
        Assert.Equal(300, SteamProfileParser.Sanitize(absurd, 300).Length);
    }

    [Fact]
    public void UnicodeEscapesAreDecoded()
    {
        string body = "{\"response\":{\"players\":[{\"steamid\":\"76561197960287930\",\"personaname\":\"\\u00c5ke\"}]}}";
        var profiles = SteamProfileParser.Parse(body);
        Assert.Single(profiles);
        Assert.Equal("\u00c5ke", profiles[0].PersonaName);
    }
}
