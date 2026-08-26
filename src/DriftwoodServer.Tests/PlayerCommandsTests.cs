using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// Player chat commands are text a player types into the game's ordinary chat, so the parser
// decides which lines the server eats and which it relays. Eating a line that was not a
// command looks like the server censoring chat; relaying one that was leaks "!stuck" to the
// crew and does nothing. Both edges are pinned here, with the throttle beside them.
public class PlayerCommandsTests
{
    [Theory]
    [InlineData("!stuck", "stuck", "")]
    [InlineData("!STUCK", "stuck", "")]
    [InlineData("  !help  ", "help", "")]
    [InlineData("!playtime please", "playtime", "please")]
    [InlineData("!top10", "top10", "")]
    public void ParsesACommandAndItsArguments(string text, string verb, string args)
    {
        Assert.True(PlayerCommands.TryParse(text, out string parsedVerb, out string parsedArgs));
        Assert.Equal(verb, parsedVerb);
        Assert.Equal(args, parsedArgs);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("!")]
    [InlineData("!!!")]
    [InlineData("! hi")]
    [InlineData("!1")]
    [InlineData("hello!")]
    [InlineData("nice catch !stuck")]
    [InlineData("/spawn tuna")]
    public void OrdinaryChatIsNotACommand(string? text)
    {
        Assert.False(PlayerCommands.TryParse(text!, out _, out _));
    }

    [Fact]
    public void ArgumentsAreCapped()
    {
        string longArgs = new string('x', 500);
        Assert.True(PlayerCommands.TryParse("!top " + longArgs, out _, out string args));
        Assert.Equal(PlayerCommands.MaxArgsLength, args.Length);
    }

    [Fact]
    public void AKeyboardLeanIsOrdinaryChatNotACommand()
    {
        // The unknown-verb reply echoes the verb back at the whole crew, so a "verb" longer
        // than any real command must stay ordinary chat rather than be amplified.
        Assert.False(PlayerCommands.TryParse("!" + new string('a', PlayerCommands.MaxVerbLength + 1), out _, out _));
        Assert.True(PlayerCommands.TryParse("!" + new string('a', PlayerCommands.MaxVerbLength), out _, out _));
    }

    [Theory]
    [InlineData("Steve", "Steve")]
    [InlineData("<size=200>Steve", "size=200>Steve")]
    [InlineData("<<i>>", "i>>")]
    [InlineData("", "")]
    public void ChatBoundNamesCannotOpenARichTextTag(string name, string expected)
    {
        // The game's chat renders TextMeshPro markup (its own save notice ships in <i>), so
        // a name that reaches a broadcast line loses the character that opens a tag.
        Assert.Equal(expected, PlayerCommands.ChatSafe(name));
    }

    [Fact]
    public void EveryNamedCommandIsKnownAndInTheHelpLine()
    {
        string help = PlayerCommands.HelpLine(leaderboardOn: true);
        foreach (string name in PlayerCommands.Names)
        {
            Assert.True(PlayerCommands.IsKnown(name));
            Assert.Contains("!" + name, help);
        }
        Assert.False(PlayerCommands.IsKnown("kick"));
        Assert.DoesNotContain("!top", PlayerCommands.HelpLine(leaderboardOn: false));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m")]
    [InlineData(3599, "59m")]
    [InlineData(3600, "1h 0m")]
    [InlineData(4380, "1h 13m")]
    [InlineData(48 * 3600, "2d 0h")]
    [InlineData(-5, "0s")]
    public void FormatsDurationsTheWayPlayersReadThem(long seconds, string expected)
    {
        Assert.Equal(expected, PlayerCommands.Duration(seconds));
    }

    [Fact]
    public void MoneyIsGroupedAndInvariant()
    {
        Assert.Equal("$1,234,567", PlayerCommands.Money(1234567));
        Assert.Equal("$0", PlayerCommands.Money(0));
    }

    [Fact]
    public void RepliesToTheSamePlayerAreSpaced()
    {
        var cooldowns = new ChatCooldowns(replyGapSeconds: 3, stuckSeconds: 60, globalCap: 100, globalWindowSeconds: 10);
        Assert.True(cooldowns.TryReply(1, 100.0));
        Assert.False(cooldowns.TryReply(1, 101.0));
        Assert.False(cooldowns.TryReply(1, 102.9));
        Assert.True(cooldowns.TryReply(1, 103.0));
        // A different player is not throttled by the first one's gap.
        Assert.True(cooldowns.TryReply(2, 103.1));
    }

    [Fact]
    public void TheCrewTogetherCannotExceedTheGlobalCap()
    {
        var cooldowns = new ChatCooldowns(replyGapSeconds: 0, stuckSeconds: 60, globalCap: 3, globalWindowSeconds: 10);
        Assert.True(cooldowns.TryReply(1, 0));
        Assert.True(cooldowns.TryReply(2, 1));
        Assert.True(cooldowns.TryReply(3, 2));
        Assert.False(cooldowns.TryReply(4, 3));
        // The window slides: once the first reply is older than the window, room opens.
        Assert.True(cooldowns.TryReply(4, 10.5));
    }

    [Fact]
    public void TheTeleportHasItsOwnLongerCooldownAndReportsTheRemainder()
    {
        var cooldowns = new ChatCooldowns(replyGapSeconds: 3, stuckSeconds: 60, globalCap: 100, globalWindowSeconds: 10);
        Assert.Equal(0, cooldowns.StuckRemaining(7, 50));
        cooldowns.MarkStuck(7, 50);
        Assert.Equal(60, cooldowns.StuckRemaining(7, 50), 3);
        Assert.Equal(20, cooldowns.StuckRemaining(7, 90), 3);
        Assert.Equal(0, cooldowns.StuckRemaining(7, 110));
        Assert.Equal(0, cooldowns.StuckRemaining(8, 51));
    }

    [Fact]
    public void ZeroCooldownMeansNoTeleportCooldown()
    {
        var cooldowns = new ChatCooldowns(replyGapSeconds: 3, stuckSeconds: 0, globalCap: 100, globalWindowSeconds: 10);
        cooldowns.MarkStuck(7, 50);
        Assert.Equal(0, cooldowns.StuckRemaining(7, 50));
    }
}
