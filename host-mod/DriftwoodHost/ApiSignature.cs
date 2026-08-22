using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DriftwoodHost
{
	// The signature, in one place, with no Unity or BepInEx in it.
	//
	// Split out of HostHttpApi for one reason: it is the piece of this product that three
	// separate codebases have to compute IDENTICALLY - the panel (PHP), the launcher (.NET),
	// and this server - and a mismatch has no symptom. Every signature is well-formed; they
	// simply never verify, and the customer sees "the console cannot connect" with nothing in
	// any log on either side.
	//
	// Being dependency-free means the xunit suite can link this file directly (the same trick
	// HostConfig uses) and pin it to a GOLDEN VECTOR that the endpoint's own test carries the
	// other half of. Two repositories asserting the same constant is the only kind of proof
	// this seam can have without a running game.
	internal static class ApiSignature
	{
		internal const string TimestampHeader = "X-Driftwood-Timestamp";
		internal const string SignatureHeader = "X-Driftwood-Signature";

		// Signatures more than this far from now are refused. Wide enough for a launcher on a
		// clock that has drifted a couple of minutes, narrow enough that a captured request is
		// not a standing key - and the replay cache in HostHttpApi closes the window inside it.
		internal const int ReplayWindowSeconds = 300;

		// METHOD\npath\nunix-seconds\nsha256hex(body). Path only - no host, no query string.
		internal static string Canonical(string method, string path, long timestamp, string bodySha256Hex)
		{
			return method + "\n" + path + "\n" + timestamp.ToString(CultureInfo.InvariantCulture) + "\n" + bodySha256Hex;
		}

		// The key is the RAW 32-byte sha256 digest of the token, never its hex text. The
		// launcher does SHA256.HashData(Encoding.UTF8.GetBytes(password)) and hands those bytes
		// straight to HMACSHA256; PHP has to pass true to hash() for the same reason.
		internal static byte[] KeyFromToken(string token)
		{
			using (SHA256 sha = SHA256.Create())
			{
				return sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? string.Empty));
			}
		}

		internal static string Compute(byte[] key, string canonical)
		{
			using (HMACSHA256 hmac = new HMACSHA256(key))
			{
				return HexEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
			}
		}

		internal static string Sha256Hex(byte[] bytes)
		{
			using (SHA256 sha = SHA256.Create()) return HexEncode(sha.ComputeHash(bytes ?? new byte[0]));
		}

		// Convert.ToHexString is .NET 5+. This mod targets netstandard2.1 and runs on the
		// game's Mono, so it does not exist here - and a signature that is right but hex-encoded
		// differently is a signature that never verifies.
		internal static string HexEncode(byte[] bytes)
		{
			StringBuilder builder = new StringBuilder(bytes.Length * 2);
			for (int i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
			return builder.ToString();
		}

		// Length is compared without an early return, because
		// CryptographicOperations.FixedTimeEquals is not available on the game's Mono.
		internal static bool ConstantTimeEquals(string a, string b)
		{
			a = a ?? string.Empty;
			b = b ?? string.Empty;
			int difference = a.Length ^ b.Length;
			int shared = Math.Min(a.Length, b.Length);
			for (int i = 0; i < shared; i++) difference |= a[i] ^ b[i];
			return difference == 0;
		}
	}
}
