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

    private static ClaimDecision Decide(bool ticket, TicketCheck check, ulong checkedId, ulong claimed, bool known, bool sameAddress,
        out string why, out ClaimNotice notice) =>
        SteamTicketRules.Decide(ticket, check, checkedId, claimed, known, sameAddress, out why, out notice);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Decide_VerifiedMatchIsAccepted(bool known, bool sameAddress)
    {
        Assert.Equal(ClaimDecision.AcceptVerified, Decide(true, TicketCheck.Verified, Alice, Alice, known, sameAddress, out _, out ClaimNotice notice));
        Assert.Equal(ClaimNotice.None, notice);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]    // being on the victim's address does not rescue a wrong ticket
    public void Decide_SomeoneElsesTicketIsRefusedAndTold(bool known, bool sameAddress)
    {
        Assert.Equal(ClaimDecision.Refuse, Decide(true, TicketCheck.Verified, Bob, Alice, known, sameAddress, out string why, out ClaimNotice notice));
        Assert.Contains("different account", why);
        Assert.Equal(ClaimNotice.NotConfirmed, notice);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]    // a real answer from Steam always wins over the outage rule
    public void Decide_ARejectedTicketIsRefusedAndTold(bool known, bool sameAddress)
    {
        Assert.Equal(ClaimDecision.Refuse, Decide(true, TicketCheck.Rejected, 0, Alice, known, sameAddress, out _, out ClaimNotice notice));
        Assert.Equal(ClaimNotice.NotConfirmed, notice);
    }

    [Theory]
    [InlineData(false, (int)TicketCheck.NotChecked)]   // older app, no ticket
    [InlineData(true, (int)TicketCheck.NoKey)]         // ticket but no key (self-host)
    [InlineData(false, (int)TicketCheck.NoKey)]
    [InlineData(true, (int)TicketCheck.Unavailable)]   // Steam unreachable
    [InlineData(true, (int)TicketCheck.OverBudget)]    // allowance spent
    public void Decide_UncheckableClaimForANewIdIsAcceptedUnverifiedAndSilent(bool ticket, int check)
    {
        Assert.Equal(ClaimDecision.AcceptUnverified, Decide(ticket, (TicketCheck)check, 0, Alice, false, false, out string why, out ClaimNotice notice));
        Assert.StartsWith("unverified", why);
        Assert.Equal(ClaimNotice.None, notice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decide_NoTicketForAKnownIdWhileSteamAnswersIsTheSpoofEvenFromItsAddress(bool sameAddress)
    {
        Assert.Equal(ClaimDecision.Refuse, Decide(false, TicketCheck.NotChecked, 0, Alice, true, sameAddress, out string why, out ClaimNotice notice));
        Assert.Contains("verified on this server before", why);
        Assert.Equal(ClaimNotice.NotConfirmed, notice);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]    // a Steam client with no connection cannot make a ticket either
    public void Decide_SteamDownLetsARegularInFromTheirLastVerifiedAddressOnly(bool ticket)
    {
        Assert.Equal(ClaimDecision.AcceptUnchecked, Decide(ticket, TicketCheck.Unavailable, 0, Alice, true, true, out string why, out ClaimNotice told));
        Assert.Contains("last verified join", why);
        Assert.Equal(ClaimNotice.None, told);

        Assert.Equal(ClaimDecision.Refuse, Decide(ticket, TicketCheck.Unavailable, 0, Alice, true, false, out _, out ClaimNotice refused));
        Assert.Equal(ClaimNotice.CannotReachSteam, refused);
    }

    [Fact]
    public void Decide_NoKeyLetsARegularInFromTheirLastVerifiedAddressOnly()
    {
        Assert.Equal(ClaimDecision.AcceptUnchecked, Decide(true, TicketCheck.NoKey, 0, Alice, true, true, out _, out _));
        Assert.Equal(ClaimDecision.Refuse, Decide(true, TicketCheck.NoKey, 0, Alice, true, false, out _, out ClaimNotice notice));
        Assert.Equal(ClaimNotice.NotConfirmed, notice);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decide_OverBudgetIsNeverAnOutage(bool sameAddress)
    {
        Assert.Equal(ClaimDecision.Refuse, Decide(true, TicketCheck.OverBudget, 0, Alice, true, sameAddress, out _, out ClaimNotice notice));
        Assert.Equal(ClaimNotice.NotConfirmed, notice);
    }

    [Fact]
    public void NoticeText_IsTheExactWordingAndFitsChat()
    {
        string confirm = SteamTicketRules.NoticeText(ClaimNotice.NotConfirmed, "Player-0002");
        string steam = SteamTicketRules.NoticeText(ClaimNotice.CannotReachSteam, "Player-0002");
        Assert.Equal("Player-0002: Steam could not confirm this account, so you are on a temporary character. Your saved character is safe. Restart the game to try again.", confirm);
        Assert.Equal("Player-0002: Steam cannot be reached right now, so you are on a temporary character. Your saved character is safe. Try again once Steam is back.", steam);
        Assert.Null(SteamTicketRules.NoticeText(ClaimNotice.None, "Player-0002"));
        Assert.True(confirm.Length <= 300 && steam.Length <= 300);    // OwnerActions.Broadcast caps a line at 300
    }

    [Theory]
    [InlineData("{\"response\":{\"error\":{\"errorcode\":3,\"errordesc\":\"Invalid parameter\"}}}", "Invalid parameter")]   // junk, measured 10/30
    [InlineData("{\"response\":{\"error\":{\"errorcode\":101,\"errordesc\":\"Invalid ticket\"}}}", "Invalid ticket")]       // a cancelled real ticket, measured 10/30
    public void ParseAuthenticateResponse_SteamsRealRefusalsAreRejected(string body, string detail)
    {
        Assert.Equal(TicketCheck.Rejected, SteamTicketRules.ParseAuthenticateResponse(body, out ulong id, out string got));
        Assert.Equal(0UL, id);
        Assert.Equal(detail, got);
    }

    private static string TempList(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "dw-verified-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "verified-steam-ids.txt");
    }

    [Fact]
    public void VerifiedIds_SurviveARestartAndIgnoreJunk()
    {
        string path = TempList(out string dir);
        try
        {
            var ids = new VerifiedIds(path);
            Assert.False(ids.Contains(Alice));
            Assert.True(ids.Remember(Alice, "203.0.113.7"));
            Assert.False(ids.Remember(Alice, "203.0.113.7"));    // already there
            Assert.False(ids.Remember(12345UL, "203.0.113.7"));  // not an individual account id
            File.AppendAllText(path, "not-a-number\n76561190000100002\n");   // junk + a synthetic slot id
            var again = new VerifiedIds(path);
            Assert.True(again.Contains(Alice));
            Assert.Equal(1, again.Count);
            Assert.True(again.IsLastVerifiedAddress(Alice, "203.0.113.7"));   // salt and hash survive too
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void VerifiedIds_MatchOnlyTheLastVerifiedAddressAndNeverStoreItPlain()
    {
        string path = TempList(out string dir);
        try
        {
            var ids = new VerifiedIds(path);
            ids.Remember(Alice, "203.0.113.7");
            Assert.True(ids.IsLastVerifiedAddress(Alice, "203.0.113.7:51000"));
            Assert.True(ids.IsLastVerifiedAddress(Alice, "::ffff:203.0.113.7"));
            Assert.False(ids.IsLastVerifiedAddress(Alice, "203.0.113.8"));
            Assert.False(ids.IsLastVerifiedAddress(Bob, "203.0.113.7"));
            Assert.False(ids.IsLastVerifiedAddress(Alice, "not an address"));
            Assert.DoesNotContain("203.0.113.7", File.ReadAllText(path));

            ids.Remember(Alice, "198.51.100.9");    // moved house: the newest verified join is what counts
            Assert.False(ids.IsLastVerifiedAddress(Alice, "203.0.113.7"));
            Assert.True(ids.IsLastVerifiedAddress(Alice, "198.51.100.9"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void VerifiedIds_AnIdWithNoAddressOnRecordMatchesNothing()
    {
        string path = TempList(out string dir);
        try
        {
            File.WriteAllText(path, Alice + "\n");
            var ids = new VerifiedIds(path);
            Assert.True(ids.Contains(Alice));
            Assert.False(ids.IsLastVerifiedAddress(Alice, "203.0.113.7"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void VerifiedIds_TwoServersHashTheSameAddressDifferently()
    {
        string pathA = TempList(out string dirA);
        string pathB = TempList(out string dirB);
        try
        {
            new VerifiedIds(pathA).Remember(Alice, "203.0.113.7");
            new VerifiedIds(pathB).Remember(Alice, "203.0.113.7");
            Assert.NotEqual(File.ReadAllLines(pathA)[1], File.ReadAllLines(pathB)[1]);
        }
        finally
        {
            Directory.Delete(dirA, true);
            Directory.Delete(dirB, true);
        }
    }

    [Fact]
    public void VerifiedIds_WithNoPathStillWorksInMemory()
    {
        var ids = new VerifiedIds(null);
        Assert.True(ids.Remember(Bob, "203.0.113.7"));
        Assert.True(ids.Contains(Bob));
        Assert.True(ids.IsLastVerifiedAddress(Bob, "203.0.113.7"));
    }

    private static readonly DateTime Noon = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TicketBudget_ARetryIsFreeAndAThirdTicketOnAConnectionIsNot()
    {
        var budget = new TicketBudget();
        Assert.True(budget.TryTake(1, "one", Noon));
        Assert.True(budget.TryTake(1, "one", Noon));
        Assert.True(budget.TryTake(1, "two", Noon));
        Assert.False(budget.TryTake(1, "three", Noon));
        Assert.True(budget.TryTake(2, "three", Noon));    // another connection is unaffected
    }

    [Fact]
    public void TicketBudget_CapsTheServerAndRefillsAfterTheWindow()
    {
        var budget = new TicketBudget();
        for (int i = 0; i < TicketBudget.PerWindow; i++) Assert.True(budget.TryTake(i, "ticket", Noon));
        Assert.False(budget.TryTake(999, "ticket", Noon));
        Assert.True(budget.TryTake(999, "ticket", Noon + TicketBudget.Window));
    }

    [Fact]
    public void TicketBudget_AFloodWithFreshConnectionsCannotPassTheServerCap()
    {
        var budget = new TicketBudget();
        int allowed = 0;
        for (int i = 0; i < 5000; i++) if (budget.TryTake(i, "ticket-" + i, Noon.AddSeconds(i % 500))) allowed++;
        Assert.Equal(TicketBudget.PerWindow, allowed);
    }
}
