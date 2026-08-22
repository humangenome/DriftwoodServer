using System.Text;
using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// THE GOLDEN VECTOR.
//
// Three codebases compute this signature and all three have to agree byte for byte: this
// server (DriftwoodHost.HostHttpApi), the Driftwood launcher
// (DriftwoodHttpClient.BuildSignedRequest), and the hosting endpoint
// (its signed-request helper). Nothing at runtime tells you when they
// stop agreeing - every signature is still perfectly well-formed, they simply never verify,
// and the customer sees "the console cannot connect" with nothing in any log on either side.
//
// So the constant below is pinned here AND in the hosting endpoint's behavioural test. Two
// repositories asserting the same hex string is the only proof this seam can have without a
// running game, and if either side's construction moves, exactly one of them goes red.
//
// If you change this vector you are changing the wire protocol. Update protocol/http-api.md,
// the endpoint's test, and the launcher, in the same change.
public class ApiSignatureTests
{
    private const string Token = "driftwoodgolden01";
    private const string Method = "POST";
    private const string Path = "/api/v1/save";
    private const long Timestamp = 1700000000;
    private const string ExpectedSignature = "71de34180e92f178131f264f640c09c8cd1f6df83645eba6abf364182c403be0";

    [Fact]
    public void GoldenVectorMatchesTheEndpointAndTheLauncher()
    {
        string bodySha = ApiSignature.Sha256Hex(System.Array.Empty<byte>());
        string canonical = ApiSignature.Canonical(Method, Path, Timestamp, bodySha);
        Assert.Equal(
            "POST\n/api/v1/save\n1700000000\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            canonical);
        Assert.Equal(ExpectedSignature, ApiSignature.Compute(ApiSignature.KeyFromToken(Token), canonical));
    }

    // The key is the RAW 32-byte digest, never its hex text. This is the single most likely
    // way for a re-implementation to be wrong while looking right, and it is why the endpoint
    // passes true to hash().
    [Fact]
    public void KeyIsTheRawDigestNotItsHexText()
    {
        byte[] key = ApiSignature.KeyFromToken(Token);
        Assert.Equal(32, key.Length);
        string hexKeyed = ApiSignature.Compute(
            Encoding.UTF8.GetBytes(ApiSignature.HexEncode(key)),
            ApiSignature.Canonical(Method, Path, Timestamp, ApiSignature.Sha256Hex(System.Array.Empty<byte>())));
        Assert.NotEqual(ExpectedSignature, hexKeyed);
    }

    // Body bytes are part of the signature, so a tampered body cannot ride a captured header
    // pair. The console route is the one that matters: its body IS the command.
    [Fact]
    public void BodyIsCoveredBySignature()
    {
        byte[] key = ApiSignature.KeyFromToken(Token);
        string one = ApiSignature.Compute(key, ApiSignature.Canonical(
            "POST", "/api/v1/console", Timestamp, ApiSignature.Sha256Hex(Encoding.UTF8.GetBytes("{\"command\":\"status\"}"))));
        string two = ApiSignature.Compute(key, ApiSignature.Canonical(
            "POST", "/api/v1/console", Timestamp, ApiSignature.Sha256Hex(Encoding.UTF8.GetBytes("{\"command\":\"snapshot\"}"))));
        Assert.NotEqual(one, two);
    }

    // Method and path are covered too, so a signature for a harmless GET cannot be replayed
    // onto a POST that restores a world.
    [Fact]
    public void MethodAndPathAreCoveredBySignature()
    {
        byte[] key = ApiSignature.KeyFromToken(Token);
        string bodySha = ApiSignature.Sha256Hex(System.Array.Empty<byte>());
        string get = ApiSignature.Compute(key, ApiSignature.Canonical("GET", "/api/v1/snapshots", Timestamp, bodySha));
        string post = ApiSignature.Compute(key, ApiSignature.Canonical("POST", "/api/v1/snapshots", Timestamp, bodySha));
        string other = ApiSignature.Compute(key, ApiSignature.Canonical("GET", "/api/v1/status", Timestamp, bodySha));
        Assert.NotEqual(get, post);
        Assert.NotEqual(get, other);
    }

    [Fact]
    public void ConstantTimeCompareStillCompares()
    {
        Assert.True(ApiSignature.ConstantTimeEquals(ExpectedSignature, ExpectedSignature));
        Assert.False(ApiSignature.ConstantTimeEquals(ExpectedSignature, ExpectedSignature.Substring(1)));
        Assert.False(ApiSignature.ConstantTimeEquals(ExpectedSignature, new string('0', ExpectedSignature.Length)));
        Assert.False(ApiSignature.ConstantTimeEquals(ExpectedSignature, null));
    }
}
