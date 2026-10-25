using System;
using System.Linq;
using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The log guard: one game error repeating every frame must not fill the disk, and nothing it
// holds back may vanish without a count.
public class LogRepeatLimiterTests
{
    private sealed class Clock
    {
        public DateTime Now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private const string Flood =
        "System.NullReferenceException: Object reference not set to an instance of an object @ at ProjectileManager.UpdateProjectileScan (Projectile p) [0x00012] in <abc>:0";

    [Fact]
    public void TheLabFlood_IsWrittenFiveTimesAMinuteWithOneSummary()
    {
        var clock = new Clock();
        var limiter = new LogRepeatLimiter(() => clock.Now);
        int written = 0;
        // The 10/23 lab rate: 55 a second for five minutes.
        for (int second = 0; second < 300; second++)
        {
            for (int i = 0; i < 55; i++) if (limiter.Admit(Flood)) written++;
            clock.Advance(1);
        }
        var summaries = limiter.TakeSummaries();
        Assert.Equal(25, written);                       // 5 a minute x 5 minutes, not 16,500
        Assert.Equal(16500 - 25, limiter.TotalHeldBack);
        Assert.Equal(5, summaries.Count);                // one per minute
        Assert.All(summaries, s => Assert.Equal(55 * 60 - 5, s.HeldBack));
        Assert.All(summaries, s => Assert.False(s.OverTotal));
        Assert.Equal(limiter.TotalHeldBack, summaries.Sum(s => s.HeldBack));
    }

    [Fact]
    public void LinesThatDifferOnlyByNumbers_CountAsOneLine()
    {
        var clock = new Clock();
        var limiter = new LogRepeatLimiter(() => clock.Now);
        int written = Enumerable.Range(0, 50).Count(i => limiter.Admit("Error: packet " + i + " from connection " + (i * 7) + " was refused"));
        Assert.Equal(5, written);
    }

    [Fact]
    public void DifferentErrors_AreEachWritten()
    {
        var clock = new Clock();
        var limiter = new LogRepeatLimiter(() => clock.Now);
        string a = LogRepeatLimiter.ExceptionLine("System.NullReferenceException", "Object reference not set", "  at ProjectileManager.UpdateProjectileScan ()\n  at ProjectileManager.FixedUpdate ()");
        string b = LogRepeatLimiter.ExceptionLine("System.NullReferenceException", "Object reference not set", "  at NPC.Update ()\n  at x");
        Assert.NotEqual(a, b);
        for (int i = 0; i < 5; i++) { Assert.True(limiter.Admit(a)); Assert.True(limiter.Admit(b)); }
        Assert.False(limiter.Admit(a));
        Assert.False(limiter.Admit(b));
    }

    [Fact]
    public void ANewWindow_WritesTheLineAgain()
    {
        var clock = new Clock();
        var limiter = new LogRepeatLimiter(() => clock.Now);
        for (int i = 0; i < 20; i++) limiter.Admit(Flood);
        Assert.False(limiter.Admit(Flood));
        clock.Advance(61);
        Assert.True(limiter.Admit(Flood));
    }

    [Fact]
    public void AQuietLog_StillGetsItsSummary()
    {
        var clock = new Clock();
        var limiter = new LogRepeatLimiter(() => clock.Now);
        for (int i = 0; i < 12; i++) limiter.Admit(Flood);
        Assert.Empty(limiter.TakeSummaries());          // window still open
        clock.Advance(60);
        var summary = Assert.Single(limiter.TakeSummaries());
        Assert.Equal(7, summary.HeldBack);
        Assert.Empty(limiter.TakeSummaries());          // handed out once
    }

    [Fact]
    public void ManyDifferentLines_StopAtTheTotalCapAndAreCounted()
    {
        var clock = new Clock();
        var limiter = new LogRepeatLimiter(() => clock.Now, perKey: 5, totalPerWindow: 300);
        int written = 0;
        // Letters, not digits, so each line is distinct after normalising.
        for (int i = 0; i < 1000; i++) if (limiter.Admit("Warning: asset " + Name(i) + " is missing")) written++;
        Assert.Equal(300, written);
        clock.Advance(60);
        var summaries = limiter.TakeSummaries();
        Assert.Equal(700, limiter.TotalHeldBack);
        Assert.Equal(700, summaries.Sum(s => s.HeldBack));
        Assert.True(summaries.Count <= LogRepeatLimiter.MaxSummaries + 1);
        Assert.Contains(summaries, s => s.OverTotal);
    }

    [Fact]
    public void SummaryLine_SaysHowManyAndQuotesTheLine()
    {
        var clock = new Clock();
        var limiter = new LogRepeatLimiter(() => clock.Now);
        for (int i = 0; i < 9; i++) limiter.Admit(Flood);
        clock.Advance(60);
        string line = limiter.SummaryLine(limiter.TakeSummaries().Single());
        Assert.StartsWith("Driftwood log guard: the line below came 4 more times in the last 60 s", line);
        Assert.EndsWith(Flood, line);
    }

    [Theory]
    [InlineData("slot 3 of 12", "slot # of #")]
    [InlineData("  two   spaces\tand tab ", "two spaces and tab")]
    [InlineData("", "")]
    public void Normalise_BlanksDigitsAndCollapsesSpace(string input, string expected) =>
        Assert.Equal(expected, LogRepeatLimiter.Normalise(input));

    [Fact]
    public void ExceptionLine_UsesOnlyTheTopFrame()
    {
        string line = LogRepeatLimiter.ExceptionLine("System.InvalidOperationException", "bad", "  at A.B ()\n  at C.D ()");
        Assert.Equal("System.InvalidOperationException: bad @ at A.B ()", line);
        Assert.Equal("E: m", LogRepeatLimiter.ExceptionLine("E", "m", null));
    }

    private static string Name(int i)
    {
        var chars = new char[4];
        for (int k = 0; k < 4; k++) { chars[k] = (char)('a' + i % 26); i /= 26; }
        return new string(chars);
    }
}
