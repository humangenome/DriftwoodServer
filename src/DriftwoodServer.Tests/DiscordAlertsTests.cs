using System.Collections.Generic;
using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The Discord alert pipe's pure parts. The HTTP worker is never started here - these tests
// prove the three decisions that matter without a network:
//
//   1. The URL gate. The webhook can come from a CUSTOMER-EDITABLE file inside the FTP jail,
//      so anything that is not a genuine Discord webhook URL must be refused - otherwise the
//      field is a server-side request-forgery primitive aimed wherever an FTP user types.
//   2. The payload shape. allowed_mentions is pinned empty because half of every line is a
//      player-chosen persona name, and "@everyone" is a perfectly legal Steam persona.
//   3. The join/leave diff, because a wrong diff spams a channel or goes silent.
public class DiscordAlertsTests
{
    // ------------------------------------------------------------------
    // The URL gate.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456/abcDEF-ghi")]
    [InlineData("https://discordapp.com/api/webhooks/123456/token")]
    [InlineData("https://ptb.discord.com/api/webhooks/1/t")]
    [InlineData("https://canary.discord.com/api/webhooks/1/t")]
    [InlineData("  https://discord.com/api/webhooks/1/t  ")]
    public void RealWebhookUrlsPass(string url)
    {
        Assert.True(DiscordAlerts.LooksLikeWebhookUrl(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    // Plain HTTP would put the webhook token on the wire in clear.
    [InlineData("http://discord.com/api/webhooks/1/t")]
    // The SSRF shapes: an arbitrary host, a loopback service, a host that merely CONTAINS
    // the word discord, and a userinfo trick.
    [InlineData("https://example.com/api/webhooks/1/t")]
    [InlineData("https://127.0.0.1/api/webhooks/1/t")]
    [InlineData("https://localhost:8443/api/webhooks/1/t")]
    [InlineData("https://evildiscord.com/api/webhooks/1/t")]
    [InlineData("https://discord.com.evil.example/api/webhooks/1/t")]
    [InlineData("https://discord.com@evil.example/api/webhooks/1/t")]
    // The right host but not the webhook surface.
    [InlineData("https://discord.com/channels/1/2")]
    [InlineData("https://discord.com/")]
    public void EverythingElseIsRefused(string url)
    {
        Assert.False(DiscordAlerts.LooksLikeWebhookUrl(url));
    }

    // ------------------------------------------------------------------
    // The payload shape.
    // ------------------------------------------------------------------

    [Fact]
    public void PayloadPinsMentionsOffAndCarriesTheLines()
    {
        List<string> payloads = DiscordAlerts.BuildPayloads("My Server",
            new List<string> { "Ryan joined the server (1 of 8 aboard).", "The crew moved to island 2 of 7." });
        string payload = Assert.Single(payloads);
        Assert.Contains("\"allowed_mentions\":{\"parse\":[]}", payload);
        Assert.Contains("\"username\":\"My Server\"", payload);
        Assert.Contains("Ryan joined the server", payload);
        Assert.Contains("\\nThe crew moved to island 2 of 7.", payload);
    }

    [Fact]
    public void HostileNamesCannotForgeThePayload()
    {
        // A persona name is attacker-controlled text. Quotes must be escaped, control
        // characters stripped, and a mention attempt survives only as inert text.
        List<string> payloads = DiscordAlerts.BuildPayloads("s",
            new List<string> { "a\",\"content\":\"forged joined the server (1 of 8 aboard).", "@everyone left the server (0 of 8 aboard)." });
        string payload = Assert.Single(payloads);
        Assert.Contains("a\\\",\\\"content\\\":\\\"forged", payload);
        // Exactly one real content key.
        Assert.Equal(1, CountOf(payload, "\"content\":"));
    }

    [Fact]
    public void LongBatchesSplitInsteadOfBeingRejected()
    {
        List<string> lines = new List<string>();
        for (int i = 0; i < 40; i++) lines.Add(new string('x', 250) + " " + i);
        List<string> payloads = DiscordAlerts.BuildPayloads("s", lines);
        Assert.True(payloads.Count > 1);
        foreach (string payload in payloads)
        {
            // The CONTENT stays under Discord's 2000-character cap in every split.
            Assert.True(payload.Length < 2100);
        }
    }

    [Fact]
    public void EmptyLinesProduceNoPost()
    {
        Assert.Empty(DiscordAlerts.BuildPayloads("s", new List<string> { "", "   " }));
    }

    // ------------------------------------------------------------------
    // The join/leave diff.
    // ------------------------------------------------------------------

    [Fact]
    public void FirstSampleEmitsJoinsBecauseAFreshHostHasNoEarlierState()
    {
        List<string> lines = DiscordAlerts.RosterChanges(null,
            Roster(1, "Ryan"), slots: 8);
        string line = Assert.Single(lines);
        Assert.Equal("Ryan joined the server (1 of 8 aboard).", line);
    }

    [Fact]
    public void JoinAndLeaveAreBothReported()
    {
        List<string> lines = DiscordAlerts.RosterChanges(
            Roster(1, "Ryan"),
            Roster(2, "Peter"), slots: 8);
        Assert.Contains("Peter joined the server (1 of 8 aboard).", lines);
        Assert.Contains("Ryan left the server (1 of 8 aboard).", lines);
    }

    [Fact]
    public void AnUnchangedRosterIsSilent()
    {
        Assert.Empty(DiscordAlerts.RosterChanges(Roster(1, "Ryan"), Roster(1, "Ryan"), 8));
    }

    private static Dictionary<ulong, string> Roster(ulong id, string name) =>
        new() { [76561197960265728UL + id] = name };

    private static int CountOf(string haystack, string needle)
    {
        int count = 0;
        for (int at = haystack.IndexOf(needle, System.StringComparison.Ordinal); at >= 0;
            at = haystack.IndexOf(needle, at + needle.Length, System.StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
