using System;
using System.Globalization;
using System.Net;
using System.Text;

namespace DriftwoodHost
{
	// THE PURE HALF OF IDENTITY CLAIMS - every rule that can be decided without a live
	// connection, kept free of Unity, FishNet and BepInEx so the test suite can prove it
	// the way it proves the signature and the block list. IdentityClaims.cs holds the
	// half that needs the server's connection table.
	//
	// What a claim is: game 1.0.6 stopped sending a joining player's SteamID64, so the
	// server keys their save record on a synthetic per-connection id (SpawnIdentity.cs)
	// and their character belongs to a connection slot instead of to them. A client
	// running DriftwoodConnect posts its own real SteamID64 to this server's HTTP API
	// before it spawns, and the server - after every check here and in
	// IdentityClaims.Submit - keys the spawn on the real id instead. A client without
	// the mod sends nothing and keeps the synthetic fallback; nothing about joining or
	// playing depends on a claim existing.
	//
	// The threat model these rules answer: the claim route is PUBLIC (it must be - the
	// claimant by definition has no credential yet), so everything in a claim is
	// attacker-controlled. A claim may only ever attach a real, well-formed individual
	// SteamID to the claimant's OWN connection - never the host's identity, never a
	// synthetic id, never another player's.
	internal static class IdentityClaimRules
	{
		// The first SteamID64 Valve ever issued to an individual account. Everything
		// below it is reserved for this product's own synthetic identities (the host
		// placeholder and the per-connection spawn ids), so a claim below it is by
		// definition an attempt to be the server or to collide with a synthetic
		// player. DriftwoodIdentity aliases this constant rather than carrying its own.
		public const ulong FirstRealSteamId = 76561197960265729UL;

		// The top of the individual-account space: universe 1, type individual,
		// instance 1, 32-bit account id. Steam cannot issue past it, so anything above
		// is malformed by construction.
		public const ulong LastRealSteamId = 76561197960265728UL + uint.MaxValue;

		// Steam persona names cap at 32 characters; double that is generous for any
		// future change and still too short to be a payload.
		private const int MaxNameLength = 64;

		// A well-formed, issuable, individual-account SteamID64 - and nothing else.
		public static bool IsClaimableSteamId(ulong steamId)
		{
			return steamId >= FirstRealSteamId && steamId <= LastRealSteamId;
		}

		// Claims carry their numbers as JSON STRINGS, because a SteamID64 does not
		// survive every JSON number path (2^53) and one parser on each side is one
		// bug fewer. Strict: digits only, no sign, no whitespace, must round-trip.
		public static bool TryParseSteamId(string text, out ulong steamId)
		{
			steamId = 0UL;
			if (string.IsNullOrEmpty(text) || text.Length > 20) return false;
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] < '0' || text[i] > '9') return false;
			}
			return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out steamId);
		}

		public static bool TryParseClientId(string text, out int clientId)
		{
			clientId = -1;
			if (string.IsNullOrEmpty(text) || text.Length > 10) return false;
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] < '0' || text[i] > '9') return false;
			}
			return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out clientId) && clientId >= 0;
		}

		// Display-only, and sanitised as hostile input: control characters stripped
		// (a name must never carry a newline into a log line or an escape into a
		// terminal), length capped, whitespace collapsed at the ends. Null when
		// nothing usable remains - an absent name is honest, a placeholder here would
		// masquerade as something a person chose.
		public static string SanitizeName(string name)
		{
			if (string.IsNullOrEmpty(name)) return null;
			StringBuilder kept = new StringBuilder(Math.Min(name.Length, MaxNameLength));
			foreach (char c in name)
			{
				if (char.IsControl(c)) continue;
				kept.Append(c);
				if (kept.Length >= MaxNameLength) break;
			}
			string trimmed = kept.ToString().Trim();
			return trimmed.Length == 0 ? null : trimmed;
		}

		// Does the transport's address for the claimed connection match the address
		// the HTTP claim arrived from? This is the binding that stops a claim landing
		// on somebody ELSE's connection: to pass it an impostor has to share the
		// victim's IP (the same household), at which point they could share the
		// victim's keyboard. Ports are ignored - the game socket and the HTTP socket
		// are different conversations and NAT rewrites both independently.
		//
		// Fails CLOSED: an address that cannot be parsed matches nothing. A transport
		// that hides addresses degrades to the synthetic fallback, never to an
		// unverified claim.
		public static bool AddressesMatch(string transportAddress, string httpAddress)
		{
			IPAddress transport = ParseHost(transportAddress);
			IPAddress http = ParseHost(httpAddress);
			if (transport == null || http == null) return false;
			return Canonical(transport).Equals(Canonical(http));
		}

		private static IPAddress Canonical(IPAddress address)
		{
			// The same machine can present as 1.2.3.4 on UDP and ::ffff:1.2.3.4 on
			// TCP depending on which stack accepted the socket.
			return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
		}

		// Accepts "ip", "ip:port", "[v6]:port" and bare "v6". Returns null rather
		// than guessing.
		private static IPAddress ParseHost(string value)
		{
			if (string.IsNullOrEmpty(value)) return null;
			string text = value.Trim();
			if (text.Length == 0 || text.Length > 64) return null;

			// [v6]:port / [v6]
			if (text[0] == '[')
			{
				int close = text.IndexOf(']');
				if (close <= 1) return null;
				text = text.Substring(1, close - 1);
			}
			else
			{
				int firstColon = text.IndexOf(':');
				int lastColon = text.LastIndexOf(':');
				if (firstColon >= 0 && firstColon == lastColon)
				{
					// Exactly one colon: v4-with-port (or a malformed v6, which the
					// parse below refuses either way).
					text = text.Substring(0, firstColon);
				}
				else if (firstColon >= 0)
				{
					// Multiple colons, unbracketed: bare IPv6, parsed as-is. An
					// unbracketed mapped-v4 WITH a port refuses below (the port makes
					// it unparseable); the bracketed form above is the supported one.
				}
			}

			IPAddress parsed;
			if (!IPAddress.TryParse(text, out parsed)) return null;
			return parsed;
		}
	}
}
