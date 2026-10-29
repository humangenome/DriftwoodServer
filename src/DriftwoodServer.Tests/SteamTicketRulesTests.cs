using System;
using System.IO;
using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The Steam ticket check on identity claims: who gets their saved character, and when a claim
// that cannot prove itself is still let in.
public class SteamTicketRulesTests
{
    private const ulong Alice = 76561197966093888UL;
    private const ulong Bob = 76561198000000001UL;
    private static readonly string GoodTicket = new string('a', 64) + "0123456789ABCDEF";

    [Fact]
    public void TryParseTicket_AcceptsEvenLengthHexAndLowercasesIt()
    {
        Assert.True(SteamTicketRules.TryParseTicket(GoodTicket, out string hex));
        Assert.Equal(GoodTicket.ToLowerInvariant(), hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]                                   // too short
    [InlineData("0123456789abcdef0123456789abcdef0")]     // odd length
    [InlineData("0123456789abcdef0123456789abcdeg")]      // not hex
    public void TryParseTicket_RefusesWhatIsNotATicket(string? text)
    {
        Assert.False(SteamTicketRules.TryParseTicket(text, out string hex));
        Assert.Null(hex);
    }

    [Fact]
    public void TryParseTicket_RefusesOneTooLongForTheBodyLimit()
    {
        Assert.False(SteamTicketRules.TryParseTicket(new string('b', SteamTicketRules.MaxTicketHexLength + 2), out _));
        Assert.True(SteamTicketRules.TryParseTicket(new string('b', SteamTicketRules.MaxTicketHexLength), out _));
    }

    [Fact]
    public void ParseResponse_OkIsVerifiedWithTheTicketOwnersId()
    {
        string json = "{\"response\":{\"params\":{\"result\":\"OK\",\"steamid\":\"76561197966093888\",\"ownersteamid\":\"76561197966093888\",\"vacbanned\":false,\"publisherbanned\":false}}}";
        Assert.Equal(TicketCheck.Verified, SteamTicketRules.ParseAuthenticateResponse(json, out ulong id, out _));
        Assert.Equal(Alice, id);
    }

    [Fact]
    public void ParseResponse_SteamErrorIsRejected()
    {
        string json = "{\"response\":{\"error\":{\"errorcode\":101,\"errordesc\":\"Invalid ticket\"}}}";
        Assert.Equal(TicketCheck.Rejected, SteamTicketRules.ParseAuthenticateResponse(json, out ulong id, out string detail));
        Assert.Equal(0UL, id);
        Assert.Equal("Invalid ticket", detail);
    }

    [Fact]
    public void ParseResponse_ParamsWithoutOkOrWithABadIdIsRejected()
    {
        Assert.Equal(TicketCheck.Rejected, SteamTicketRules.ParseAuthenticateResponse(
            "{\"response\":{\"params\":{\"result\":\"Invalid\",\"steamid\":\"76561197966093888\"}}}", out _, out _));
        Assert.Equal(TicketCheck.Rejected, SteamTicketRules.ParseAuthenticateResponse(
            "{\"response\":{\"params\":{\"result\":\"OK\",\"steamid\":\"12\"}}}", out ulong id, out _));
        Assert.Equal(0UL, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Bad Gateway</body></html>")]
    [InlineData("{\"response\":{}}")]
    public void ParseResponse_NoUsableAnswerIsUnavailableNeverVerified(string body)
    {
        Assert.Equal(TicketCheck.Unavailable, SteamTicketRules.ParseAuthenticateResponse(body, out ulong id, out _));
        Assert.Equal(0UL, id);
    }

    [Fact]
    public void Decide_VerifiedMatchIsAccepted()
    {
        Assert.Equal(ClaimDecision.AcceptVerified, SteamTicketRules.Decide(true, TicketCheck.Verified, Alice, Alice, false, out _));
        Assert.Equal(ClaimDecision.AcceptVerified, SteamTicketRules.Decide(true, TicketCheck.Verified, Alice, Alice, true, out _));
    }

    [Fact]
    public void Decide_SomeoneElsesTicketIsRefused()
    {
        Assert.Equal(ClaimDecision.Refuse, SteamTicketRules.Decide(true, TicketCheck.Verified, Bob, Alice, false, out string why));
        Assert.Contains("different account", why);
    }

    [Fact]
    public void Decide_ARejectedTicketIsRefused()
    {
        Assert.Equal(ClaimDecision.Refuse, SteamTicketRules.Decide(true, TicketCheck.Rejected, 0, Alice, false, out _));
    }

    [Theory]
    [InlineData(false, (int)TicketCheck.NotChecked)]   // older app, no ticket
    [InlineData(true, (int)TicketCheck.NotChecked)]    // ticket but no key (self-host)
    [InlineData(true, (int)TicketCheck.Unavailable)]   // Steam unreachable
    public void Decide_UncheckableClaimForANewIdIsAcceptedUnverified(bool ticket, int check)
    {
        Assert.Equal(ClaimDecision.AcceptUnverified, SteamTicketRules.Decide(ticket, (TicketCheck)check, 0, Alice, false, out string why));
        Assert.StartsWith("unverified", why);
    }

    [Theory]
    [InlineData(false, (int)TicketCheck.NotChecked)]
    [InlineData(true, (int)TicketCheck.NotChecked)]
    [InlineData(true, (int)TicketCheck.Unavailable)]
    public void Decide_UncheckableClaimForAnIdVerifiedBeforeIsRefused(bool ticket, int check)
    {
        Assert.Equal(ClaimDecision.Refuse, SteamTicketRules.Decide(ticket, (TicketCheck)check, 0, Alice, true, out string why));
        Assert.Contains("verified on this server before", why);
    }

    [Fact]
    public void VerifiedIds_SurviveARestartAndIgnoreJunk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dw-verified-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "verified-steam-ids.txt");
        try
        {
            var ids = new VerifiedIds(path);
            Assert.False(ids.Contains(Alice));
            Assert.True(ids.Add(Alice));
            Assert.False(ids.Add(Alice));            // already there
            Assert.False(ids.Add(12345UL));          // not an individual account id
            File.AppendAllText(path, "not-a-number\n76561190000100002\n");   // junk + a synthetic slot id
            var again = new VerifiedIds(path);
            Assert.True(again.Contains(Alice));
            Assert.Equal(1, again.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void VerifiedIds_WithNoPathStillWorksInMemory()
    {
        var ids = new VerifiedIds(null);
        Assert.True(ids.Add(Bob));
        Assert.True(ids.Contains(Bob));
    }
}
