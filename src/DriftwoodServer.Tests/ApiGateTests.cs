using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// The API port faces the internet. These pin the two things that decide what a caller who has
// proved nothing can cost the server: whether it gets a connection at all, and how much of a
// request body is read before its signature is checked.
public class ApiGateTests
{
    private const long Upload = 64L * 1024L * 1024L;
    private static readonly string GoodSha = new string('a', 64);

    private static BodyPlan Plan(GateTier tier, bool loopback, bool upload, long length,
        string declared = null, bool authUsable = true, bool shapeOk = true) =>
        ApiGate.Plan(tier, loopback, upload, length, declared, authUsable, shapeOk, Upload);

    // ------------------------------------------------------------------
    // Body plan
    // ------------------------------------------------------------------

    [Fact]
    public void APublicClaimIsReadWithoutASignature()
    {
        Assert.Equal(BodyPlan.Read, Plan(GateTier.Public, false, false, 300));
        Assert.Equal(BodyPlan.NoBody, Plan(GateTier.Public, false, false, 0));
    }

    [Fact]
    public void APublicBodyOverFourKilobytesIsRefusedUnread()
    {
        Assert.Equal(BodyPlan.RefuseTooLarge, Plan(GateTier.Public, false, false, ApiGate.PublicBodyBytes + 1));
    }

    [Fact]
    public void TheLocalPanelNeedsNoSignatureOnLoopbackRoutes()
    {
        Assert.Equal(BodyPlan.NoBody, Plan(GateTier.LoopbackOrSigned, true, false, 0, shapeOk: false));
        Assert.Equal(BodyPlan.Read, Plan(GateTier.LoopbackOrSigned, true, false, 2048, shapeOk: false));
    }

    [Fact]
    public void ARemoteCallerWithoutSignatureHeadersIsRefusedBeforeAnyBody()
    {
        Assert.Equal(BodyPlan.RefuseUnauthorised, Plan(GateTier.LoopbackOrSigned, false, false, 0, shapeOk: false));
        Assert.Equal(BodyPlan.RefuseUnauthorised, Plan(GateTier.Signed, false, false, 1000, shapeOk: false));
        Assert.Equal(BodyPlan.RefuseUnauthorised, Plan(GateTier.Signed, false, true, 10_000_000, shapeOk: false));
    }

    [Fact]
    public void NoTokenMeansEverySignedRouteRefusesUnread()
    {
        Assert.Equal(BodyPlan.RefuseUnauthorised, Plan(GateTier.Signed, false, false, 100, authUsable: false));
        Assert.Equal(BodyPlan.RefuseUnauthorised, Plan(GateTier.Signed, true, false, 100, authUsable: false));
    }

    [Fact]
    public void ASmallSignedBodyIsReadThenVerified()
    {
        // The panel's console call, the web tools and the app's console all send small JSON
        // and no declared hash; they keep working exactly as before.
        Assert.Equal(BodyPlan.ReadThenVerify, Plan(GateTier.Signed, false, false, 200));
        Assert.Equal(BodyPlan.ReadThenVerify, Plan(GateTier.Signed, false, false, ApiGate.UnhashedBodyBytes));
        Assert.Equal(BodyPlan.NoBody, Plan(GateTier.Signed, false, false, 0));
    }

    [Fact]
    public void ALargerRemoteBodyMustDeclareItsHash()
    {
        Assert.Equal(BodyPlan.RefuseNeedsHash, Plan(GateTier.Signed, false, false, ApiGate.UnhashedBodyBytes + 1));
        Assert.Equal(BodyPlan.VerifyThenRead, Plan(GateTier.Signed, false, false, ApiGate.UnhashedBodyBytes + 1, GoodSha));
    }

    [Fact]
    public void ARemoteWorldUploadIsVerifiedBeforeItsFirstByte()
    {
        Assert.Equal(BodyPlan.RefuseNeedsHash, Plan(GateTier.Signed, false, true, 1024));
        Assert.Equal(BodyPlan.VerifyThenRead, Plan(GateTier.Signed, false, true, 10_000_000, GoodSha));
        Assert.Equal(BodyPlan.VerifyThenRead, Plan(GateTier.Signed, false, true, 10_000_000, GoodSha.ToUpperInvariant()));
    }

    [Fact]
    public void AMalformedDeclaredHashIsRefused()
    {
        Assert.Equal(BodyPlan.RefuseUnauthorised, Plan(GateTier.Signed, false, true, 1000, "abc"));
        Assert.Equal(BodyPlan.RefuseUnauthorised, Plan(GateTier.Signed, false, true, 1000, new string('g', 64)));
    }

    [Fact]
    public void AnUploadOnTheBoxItselfKeepsTodaysPath()
    {
        Assert.Equal(BodyPlan.ReadThenVerify, Plan(GateTier.Signed, true, true, 10_000_000));
    }

    [Fact]
    public void SizeCapsHoldWhateverTheCallerDeclares()
    {
        Assert.Equal(BodyPlan.RefuseTooLarge, Plan(GateTier.Signed, false, true, Upload + 1, GoodSha));
        Assert.Equal(BodyPlan.RefuseTooLarge, Plan(GateTier.Signed, false, false, ApiGate.MaxJsonBodyBytes + 1, GoodSha));
        Assert.Equal(BodyPlan.RefuseTooLarge, Plan(GateTier.Signed, true, false, ApiGate.MaxJsonBodyBytes + 1));
    }

    [Fact]
    public void NegativeLengthIsNoBody()
    {
        Assert.Equal(BodyPlan.NoBody, Plan(GateTier.Signed, false, false, -5));
    }

    // ------------------------------------------------------------------
    // Header shape
    // ------------------------------------------------------------------

    [Fact]
    public void HeaderShapeNeedsACurrentTimestampAndAHexDigest()
    {
        const long now = 1_800_000_000;
        string sig = new string('0', 64);
        Assert.True(ApiGate.HeaderShapeOk("1800000000", sig, now, 300));
        Assert.True(ApiGate.HeaderShapeOk(" 1799999800 ", " " + sig + " ", now, 300));
        Assert.False(ApiGate.HeaderShapeOk(null, sig, now, 300));
        Assert.False(ApiGate.HeaderShapeOk("1800000000", null, now, 300));
        Assert.False(ApiGate.HeaderShapeOk("soon", sig, now, 300));
        Assert.False(ApiGate.HeaderShapeOk("1799999000", sig, now, 300));
        Assert.False(ApiGate.HeaderShapeOk("1800000000", "x", now, 300));
        Assert.False(ApiGate.HeaderShapeOk("1800000000", new string('z', 64), now, 300));
    }

    // ------------------------------------------------------------------
    // Connection pools
    // ------------------------------------------------------------------

    private static ConnectionGate Gate() =>
        new ConnectionGate(ApiGate.LoopbackCap, ApiGate.RemoteCap, ApiGate.PerAddressCap, ApiGate.LongCap);

    [Fact]
    public void OneAddressHoldsAtMostFourConnections()
    {
        var gate = Gate();
        for (int i = 0; i < ApiGate.PerAddressCap; i++) Assert.NotNull(gate.TryAdmit("203.0.113.9", false));
        Assert.Null(gate.TryAdmit("203.0.113.9", false));
        Assert.NotNull(gate.TryAdmit("203.0.113.10", false));
    }

    [Fact]
    public void RemoteCallersCanNeverFillThePanelsLoopbackPool()
    {
        var gate = Gate();
        int admitted = 0;
        for (int a = 0; a < 10; a++)
            for (int i = 0; i < ApiGate.PerAddressCap; i++)
                if (gate.TryAdmit("198.51.100." + a, false) != null) admitted++;
        Assert.Equal(ApiGate.RemoteCap, admitted);
        Assert.Null(gate.TryAdmit("198.51.100.200", false));
        // the save before a stop still gets in
        Assert.NotNull(gate.TryAdmit("127.0.0.1", true));
    }

    [Fact]
    public void TheLoopbackPoolHasItsOwnCap()
    {
        var gate = Gate();
        for (int i = 0; i < ApiGate.LoopbackCap; i++) Assert.NotNull(gate.TryAdmit("127.0.0.1", true));
        Assert.Null(gate.TryAdmit("127.0.0.1", true));
        Assert.NotNull(gate.TryAdmit("203.0.113.9", false));
    }

    [Fact]
    public void ReleaseFreesTheSlotAndIsIdempotent()
    {
        var gate = Gate();
        var t = gate.TryAdmit("203.0.113.9", false);
        Assert.Equal(1, gate.RemoteInUse);
        gate.Release(t);
        gate.Release(t);
        Assert.Equal(0, gate.RemoteInUse);
        Assert.Equal(0, gate.ForAddress("203.0.113.9"));
    }

    [Fact]
    public void AVerifiedTransferMovesToTheLongPool()
    {
        var gate = Gate();
        var t = gate.TryAdmit("203.0.113.9", false);
        Assert.True(gate.TryPromoteLong(t));
        Assert.Equal(0, gate.RemoteInUse);
        Assert.Equal(1, gate.LongInUse);
        Assert.Equal(1, gate.ForAddress("203.0.113.9"));
        Assert.True(gate.TryPromoteLong(t));
        Assert.Equal(1, gate.LongInUse);
        gate.Release(t);
        Assert.Equal(0, gate.LongInUse);
        Assert.Equal(0, gate.ForAddress("203.0.113.9"));
    }

    [Fact]
    public void TheLongPoolIsCapped()
    {
        var gate = Gate();
        for (int i = 0; i < ApiGate.LongCap; i++)
            Assert.True(gate.TryPromoteLong(gate.TryAdmit("198.51.100." + i, false)));
        var extra = gate.TryAdmit("198.51.100.99", false);
        Assert.False(gate.TryPromoteLong(extra));
        Assert.Equal(1, gate.RemoteInUse);
    }

    [Fact]
    public void LoopbackTransfersStayInTheLoopbackPool()
    {
        var gate = Gate();
        var t = gate.TryAdmit("127.0.0.1", true);
        Assert.True(gate.TryPromoteLong(t));
        Assert.Equal(0, gate.LongInUse);
        Assert.Equal(1, gate.LoopbackInUse);
        gate.Release(t);
        Assert.Equal(0, gate.LoopbackInUse);
    }
}
