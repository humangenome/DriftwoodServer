using System;
using System.Linq;
using System.Reflection;
using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The API port's wrong-signature limit. The signed routes download and replace the world, and the
// key is derived from the owner's password, so without a limit anybody can guess that password at
// line speed. The rules (the same as the family's other hosts):
//
//   - only a well-formed signature with a FRESH timestamp that does not match counts as a guess
//     (missing or malformed headers, a stale timestamp - a clock problem - and a server with no
//     key are not counted);
//   - five guesses in ten minutes from one address turn that address away for a minute, then
//     two, four, up to fifteen;
//   - a good signature clears the count;
//   - a turned-away address is refused on every signed route BEFORE any body byte is read, while
//     the public routes (health, players, manifest, identity) stay open to it;
//   - loopback (the box's own tools) is never limited;
//   - each block is logged once, with the address, the door and the length, never a secret.
//
// The host mod compiles against the game, so these run on its socket-free rules, linked from
// ApiGate.cs like the rest of the gate. The members are reached through DoorLimit below rather
// than named, so this file also builds against a gate that has no limit at all (that is how it
// was shown to fail before the limit existed).
public class AuthLimitTests
{
    private const string Ip = "203.0.113.77";
    private const long T = 1_000_000;
    private const long Upload = 64L * 1024L * 1024L;
    private const int Window = ApiSignature.ReplayWindowSeconds;
    private const long Now = 1_800_000_000;
    private static readonly string GoodSha = new string('a', 64);

    // ------------------------------------------------------------------
    // Counting and blocking
    // ------------------------------------------------------------------

    [Fact]
    public void FiveWrongSignaturesTurnAnAddressAwayForAMinute()
    {
        object guard = DoorLimit.NewGuard();
        for (int i = 0; i < 4; i++)
            Assert.False(DoorLimit.Failure(guard, Ip, T), "four wrong signatures are not a block");
        Assert.Equal(0, DoorLimit.Blocked(guard, Ip, T));

        Assert.True(DoorLimit.Failure(guard, Ip, T), "the fifth wrong signature starts a block");
        Assert.Equal(60, DoorLimit.Blocked(guard, Ip, T));
        Assert.Equal(0, DoorLimit.Blocked(guard, "203.0.113.78", T));
        Assert.Equal(0, DoorLimit.Blocked(guard, Ip, T + 60_000));
    }

    [Fact]
    public void BlocksDoubleUpToFifteenMinutes()
    {
        object guard = DoorLimit.NewGuard();
        long t = T;
        long[] seen = new long[8];
        for (int round = 0; round < seen.Length; round++)
        {
            for (int i = 0; i < 5; i++) DoorLimit.Failure(guard, Ip, t);
            seen[round] = DoorLimit.Blocked(guard, Ip, t);
            t += seen[round] * 1000 + 1;
        }
        Assert.Equal(new long[] { 60, 120, 240, 480, 900, 900, 900, 900 }, seen);
    }

    [Fact]
    public void AGoodSignatureClearsTheCountAndAQuietWindowRestartsIt()
    {
        object guard = DoorLimit.NewGuard();
        for (int i = 0; i < 4; i++) DoorLimit.Failure(guard, Ip, T);
        DoorLimit.Success(guard, Ip);
        for (int i = 0; i < 4; i++)
            Assert.False(DoorLimit.Failure(guard, Ip, T), "four wrong, one right, four wrong is never five in a row");

        // Ten quiet minutes later the count starts again from one.
        Assert.False(DoorLimit.Failure(guard, Ip, T + 10 * 60_000 + 1));
        Assert.Equal(0, DoorLimit.Blocked(guard, Ip, T + 10 * 60_000 + 1));
    }

    [Fact]
    public void LoopbackIsNeverLimited()
    {
        object guard = DoorLimit.NewGuard();
        foreach (string local in new[] { "127.0.0.1", "::1" })
        {
            for (int i = 0; i < 50; i++) Assert.False(DoorLimit.Failure(guard, local, T));
            Assert.Equal(0, DoorLimit.Blocked(guard, local, T));
        }
    }

    [Fact]
    public void TheBlockLineNamesTheAddressTheDoorAndTheLengthOnly()
    {
        object guard = DoorLimit.NewGuard();
        for (int i = 0; i < 5; i++) DoorLimit.Failure(guard, Ip, T);
        Assert.Equal("auth block: door=http address=203.0.113.77 blocked=60s after 5 wrong tries in 10 min",
            DoorLimit.BlockLine(guard, Ip, T));
    }

    // ------------------------------------------------------------------
    // What counts as a guess
    // ------------------------------------------------------------------

    [Fact]
    public void OnlyAFreshWellFormedWrongSignatureCountsAsAGuess()
    {
        byte[] key = ApiSignature.KeyFromToken("the-owners-password");
        byte[] guess = ApiSignature.KeyFromToken("a-guess");
        string empty = ApiSignature.Sha256Hex(Array.Empty<byte>());
        string ts = Now.ToString();
        string right = Sign(key, "POST", "/api/v1/console", Now, empty);
        string wrong = Sign(guess, "POST", "/api/v1/console", Now, empty);

        Assert.Equal("Valid", DoorLimit.Verdict(key, true, "POST", "/api/v1/console", ts, right, empty, Now, Window));
        Assert.Equal("Valid", DoorLimit.Verdict(key, true, "POST", "/api/v1/console", ts, right.ToUpperInvariant(), empty, Now, Window));
        Assert.Equal("Wrong", DoorLimit.Verdict(key, true, "POST", "/api/v1/console", ts, wrong, empty, Now, Window));

        // Signed over a different body than the one read: still a signature that does not match.
        string other = ApiSignature.Sha256Hex(new byte[] { 1, 2, 3 });
        Assert.Equal("Wrong", DoorLimit.Verdict(key, true, "POST", "/api/v1/console", ts, right, other, Now, Window));

        // Not guesses: a stale timestamp, a malformed or missing header, a server with no key.
        long stale = Now - 3600;
        Assert.Equal("NotCounted", DoorLimit.Verdict(key, true, "POST", "/api/v1/console", stale.ToString(),
            Sign(guess, "POST", "/api/v1/console", stale, empty), empty, Now, Window));
        Assert.Equal("NotCounted", DoorLimit.Verdict(key, true, "POST", "/api/v1/console", ts, "zz" + wrong.Substring(2), empty, Now, Window));
        Assert.Equal("NotCounted", DoorLimit.Verdict(key, true, "POST", "/api/v1/console", ts, wrong.Substring(4), empty, Now, Window));
        Assert.Equal("NotCounted", DoorLimit.Verdict(key, true, "POST", "/api/v1/console", null, wrong, empty, Now, Window));
        Assert.Equal("NotCounted", DoorLimit.Verdict(key, true, "POST", "/api/v1/console", ts, null, empty, Now, Window));
        Assert.Equal("NotCounted", DoorLimit.Verdict(key, true, "POST", "/api/v1/console", "soon", wrong, empty, Now, Window));
        Assert.Equal("NotCounted", DoorLimit.Verdict(ApiSignature.KeyFromToken(""), false, "POST", "/api/v1/console", ts, wrong, empty, Now, Window));
    }

    // ------------------------------------------------------------------
    // Refused before the body
    // ------------------------------------------------------------------

    [Fact]
    public void ATurnedAwayAddressIsRefusedOnEverySignedRouteBeforeAnyBody()
    {
        Assert.Equal("RefuseBlocked", DoorLimit.Plan(GateTier.Signed, false, false, 0, null, 60));
        Assert.Equal("RefuseBlocked", DoorLimit.Plan(GateTier.Signed, false, false, 200, null, 60));
        Assert.Equal("RefuseBlocked", DoorLimit.Plan(GateTier.Signed, false, true, 10_000_000, GoodSha, 60));
        Assert.Equal("RefuseBlocked", DoorLimit.Plan(GateTier.Signed, false, true, Upload + 1, GoodSha, 60));
        Assert.Equal("RefuseBlocked", DoorLimit.Plan(GateTier.LoopbackOrSigned, false, false, 0, null, 60));
    }

    [Fact]
    public void ATurnedAwayAddressStillReachesThePublicRoutes()
    {
        Assert.Equal("NoBody", DoorLimit.Plan(GateTier.Public, false, false, 0, null, 900));
        Assert.Equal("Read", DoorLimit.Plan(GateTier.Public, false, false, 300, null, 900));
    }

    [Fact]
    public void WithNoBlockThePlanIsUnchanged()
    {
        Assert.Equal("NoBody", DoorLimit.Plan(GateTier.Signed, false, false, 0, null, 0));
        Assert.Equal("ReadThenVerify", DoorLimit.Plan(GateTier.Signed, false, false, 200, null, 0));
        Assert.Equal("VerifyThenRead", DoorLimit.Plan(GateTier.Signed, false, true, 10_000_000, GoodSha, 0));
        Assert.Equal("NoBody", DoorLimit.Plan(GateTier.LoopbackOrSigned, true, false, 0, null, 0));
    }

    private static string Sign(byte[] key, string method, string path, long ts, string bodySha) =>
        ApiSignature.Compute(key, ApiSignature.Canonical(method, path, ts, bodySha));

    // The limit's members, found by name. Each says plainly when the gate has no such member.
    private static class DoorLimit
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static Type GuardType()
        {
            Type t = typeof(ApiGate).Assembly.GetType("DriftwoodHost.AuthFailureGuard");
            Assert.True(t != null, "the API door has no wrong-signature limit (no AuthFailureGuard)");
            return t;
        }

        private static MethodInfo Find(Type type, string name, int parameters)
        {
            MethodInfo m = type.GetMethods(Any).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == parameters);
            Assert.True(m != null, type.Name + "." + name + " with " + parameters + " parameters does not exist");
            return m;
        }

        internal static object NewGuard() => Activator.CreateInstance(GuardType(), nonPublic: true);

        internal static bool Failure(object guard, string address, long nowMs) =>
            (bool)Find(GuardType(), "Failure", 2).Invoke(guard, new object[] { address, nowMs })!;

        internal static long Blocked(object guard, string address, long nowMs) =>
            (long)Find(GuardType(), "BlockedSeconds", 2).Invoke(guard, new object[] { address, nowMs })!;

        internal static void Success(object guard, string address) =>
            Find(GuardType(), "Success", 1).Invoke(guard, new object[] { address });

        internal static string BlockLine(object guard, string address, long nowMs) =>
            (string)Find(GuardType(), "BlockLine", 2).Invoke(guard, new object[] { address, nowMs });

        internal static string Verdict(byte[] key, bool authUsable, string method, string path,
            string timestampHeader, string signatureHeader, string bodySha, long nowUnix, int windowSeconds) =>
            Find(typeof(ApiGate), "CheckSignature", 9).Invoke(null, new object[]
                { key, authUsable, method, path, timestampHeader, signatureHeader, bodySha, nowUnix, windowSeconds }).ToString();

        internal static string Plan(GateTier tier, bool loopback, bool upload, long length, string declared, long blockedSeconds) =>
            Find(typeof(ApiGate), "Plan", 9).Invoke(null, new object[]
                { tier, loopback, upload, length, declared, true, true, Upload, blockedSeconds }).ToString();
    }
}
