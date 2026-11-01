using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DriftwoodHost
{
	// The rules half of the Steam ticket check on identity claims. No Unity, BepInEx or game
	// dependency, so the xunit suite links this file; SteamTicketCheck.cs makes the Steam call.
	//
	// Why it exists: a claim used to prove only that it came from the same address as the game
	// connection, so a crewmate who knew another player's SteamID64 (public on their profile)
	// could claim it and load that player's saved character while they were away. DriftwoodConnect
	// (app 0.1.6+) now sends a ticket from the player's own Steam client
	// (SteamUser.GetAuthTicketForWebApi("driftwood")), and the host asks Steam whose ticket it is
	// (ISteamUserAuth/AuthenticateUserTicket on api.steampowered.com, which game servers may call
	// with an ordinary Web API key).
	//
	// Fail-soft where we cannot check (an older app sends no ticket, a self-hoster has no key,
	// Steam is unreachable): today's same-address rule applies and the claim is logged as
	// unverified. Once an id has been verified on this server the rule tightens:
	//   - a claim for it with no ticket while Steam is answering is refused (the spoof);
	//   - when the check cannot run at all (Steam down, no key) it gets its own character only
	//     from the address of its last verified join. A plain let-in would be a hole: Steam
	//     throttling the shared key reads the same as an outage, so an outage must never be
	//     enough on its own. Every regular on their usual connection plays on through it.
	// A refused player keeps playing on the join slot's character and is told why in chat.
	internal enum TicketCheck
	{
		// No ticket came with the claim.
		NotChecked,
		// Steam says the ticket is valid; CheckedSteamId says whose it is.
		Verified,
		// Steam answered and refused the ticket (forged, expired, wrong game or identity).
		Rejected,
		// No usable answer: timeout, network error, rate limit, a key Steam will not accept.
		Unavailable,
		// This server has no Web API key, so it cannot ask.
		NoKey,
		// This server or this connection has spent its allowance of Steam calls (TicketBudget).
		// Never treated as an outage: anyone can cause it.
		OverBudget
	}

	internal enum ClaimDecision
	{
		AcceptVerified,
		AcceptUnverified,
		// An id verified here before, on its usual address, while the check cannot run.
		AcceptUnchecked,
		Refuse
	}

	// What a refused player is told. The game has no private server chat, so the crew sees it too.
	internal enum ClaimNotice
	{
		None,
		NotConfirmed,
		CannotReachSteam
	}

	internal static class SteamTicketRules
	{
		// The identity string both sides pass; Steam only authenticates a ticket made for it.
		internal const string Identity = "driftwood";
		// How to Fish on Steam.
		internal const uint AppId = 4001890;
		// A web-API ticket is a few hundred bytes. 3,000 hex characters keeps a claim inside the
		// public route's 4 KB body limit (ApiGate.PublicBodyBytes) with room for the other fields;
		// a longer one is treated as no ticket (fail-soft), never as a verified one.
		internal const int MinTicketHexLength = 32;
		internal const int MaxTicketHexLength = 3000;

		// Absent or empty is not an error (older apps send no ticket): returns false with hex = null.
		// Present but malformed also returns false; the claim is then treated as having no ticket.
		internal static bool TryParseTicket(string text, out string hex)
		{
			hex = null;
			if (string.IsNullOrEmpty(text)) return false;
			if (text.Length < MinTicketHexLength || text.Length > MaxTicketHexLength || (text.Length % 2) != 0) return false;
			char[] lower = new char[text.Length];
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (c >= '0' && c <= '9') lower[i] = c;
				else if (c >= 'a' && c <= 'f') lower[i] = c;
				else if (c >= 'A' && c <= 'F') lower[i] = (char)(c + 32);
				else return false;
			}
			hex = new string(lower);
			return true;
		}

		// Steam's answer to AuthenticateUserTicket. A good ticket comes back as
		// {"response":{"params":{"result":"OK","steamid":"7656...","ownersteamid":"7656...",...}}};
		// a refused one as {"response":{"error":{"errorcode":101,"errordesc":"Invalid ticket"}}}.
		// Anything else (empty, not JSON, an HTML error page) is Unavailable, never Verified.
		internal static TicketCheck ParseAuthenticateResponse(string json, out ulong steamId, out string detail)
		{
			steamId = 0UL;
			detail = null;
			if (string.IsNullOrEmpty(json))
			{
				detail = "empty answer";
				return TicketCheck.Unavailable;
			}
			int paramsAt = json.IndexOf("\"params\"", StringComparison.Ordinal);
			if (paramsAt >= 0)
			{
				string result = StringAfter(json, "\"result\"", paramsAt);
				string id = StringAfter(json, "\"steamid\"", paramsAt);
				if (string.Equals(result, "OK", StringComparison.Ordinal) &&
					id != null && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out steamId) &&
					IdentityClaimRules.IsClaimableSteamId(steamId))
				{
					return TicketCheck.Verified;
				}
				steamId = 0UL;
				detail = "result " + Clip(result ?? "missing");
				return TicketCheck.Rejected;
			}
			int errorAt = json.IndexOf("\"error\"", StringComparison.Ordinal);
			if (errorAt >= 0)
			{
				string desc = StringAfter(json, "\"errordesc\"", errorAt);
				detail = Clip(desc ?? "error");
				return TicketCheck.Rejected;
			}
			detail = "unrecognised answer";
			return TicketCheck.Unavailable;
		}

		internal static ClaimDecision Decide(bool ticketPresent, TicketCheck check, ulong checkedSteamId,
			ulong claimedSteamId, bool previouslyVerified, bool sameAddressAsLastVerified,
			out string reason, out ClaimNotice notice)
		{
			notice = ClaimNotice.None;
			if (ticketPresent && check == TicketCheck.Verified)
			{
				if (checkedSteamId == claimedSteamId)
				{
					reason = "verified by Steam";
					return ClaimDecision.AcceptVerified;
				}
				reason = "the Steam ticket belongs to a different account";
				notice = ClaimNotice.NotConfirmed;
				return ClaimDecision.Refuse;
			}
			if (ticketPresent && check == TicketCheck.Rejected)
			{
				reason = "Steam rejected the ticket";
				notice = ClaimNotice.NotConfirmed;
				return ClaimDecision.Refuse;
			}
			string why = check == TicketCheck.Unavailable ? "Steam could not be asked"
				: check == TicketCheck.NoKey ? "no Steam Web API key to check it with"
				: check == TicketCheck.OverBudget ? "too many ticket checks just now"
				: "no ticket (older app)";
			if (!previouslyVerified)
			{
				reason = "unverified: " + why;
				return ClaimDecision.AcceptUnverified;
			}

			// An id that has proved itself here before.
			bool cannotCheck = check == TicketCheck.Unavailable || check == TicketCheck.NoKey;
			if (cannotCheck && sameAddressAsLastVerified)
			{
				reason = "without a Steam check (" + why + "), from the address of this player's last verified join";
				return ClaimDecision.AcceptUnchecked;
			}
			if (cannotCheck)
			{
				reason = "this Steam ID was verified on this server before, this claim could not be checked (" + why +
					") and it comes from a different address than its last verified join";
				notice = check == TicketCheck.Unavailable ? ClaimNotice.CannotReachSteam : ClaimNotice.NotConfirmed;
				return ClaimDecision.Refuse;
			}
			reason = "this Steam ID was verified on this server before and this claim could not be (" + why + ")";
			notice = ClaimNotice.NotConfirmed;
			return ClaimDecision.Refuse;
		}

		// The [Server] line, addressed by the name the player has in the world. The game adds "[Server] ".
		internal static string NoticeText(ClaimNotice notice, string playerName)
		{
			switch (notice)
			{
				case ClaimNotice.NotConfirmed:
					return playerName + ": Steam could not confirm this account, so you are on a temporary character. Your saved character is safe. Restart the game to try again.";
				case ClaimNotice.CannotReachSteam:
					return playerName + ": Steam cannot be reached right now, so you are on a temporary character. Your saved character is safe. Try again once Steam is back.";
				default:
					return null;
			}
		}

		// The value of the first "key": "value" string pair after a position. Steam's answers are
		// small and flat enough that this is all the parsing they need.
		private static string StringAfter(string json, string quotedKey, int from)
		{
			int k = json.IndexOf(quotedKey, from, StringComparison.Ordinal);
			if (k < 0) return null;
			int colon = json.IndexOf(':', k + quotedKey.Length);
			if (colon < 0) return null;
			int open = colon + 1;
			while (open < json.Length && char.IsWhiteSpace(json[open])) open++;
			if (open >= json.Length || json[open] != '"') return null;
			int close = json.IndexOf('"', open + 1);
			if (close < 0) return null;
			return json.Substring(open + 1, close - open - 1);
		}

		private static string Clip(string s) => s.Length <= 80 ? s : s.Substring(0, 80);
	}

	// The ids this server has seen verified by Steam, each with a salted hash of the address of its
	// last verified join. One per line in the host's state directory, so it survives restarts and
	// world changes; the server owner can read that folder, so no player's address is stored in
	// the clear. Bounded; written through a temp file.
	internal sealed class VerifiedIds
	{
		internal const int MaxIds = 20000;
		private const string SaltPrefix = "salt ";
		private readonly object _sync = new object();
		private readonly Dictionary<ulong, string> _ids = new Dictionary<ulong, string>();
		private readonly string _path;
		private byte[] _salt;

		internal VerifiedIds(string path)
		{
			_path = path;
			try
			{
				if (!string.IsNullOrEmpty(path) && File.Exists(path))
				{
					foreach (string raw in File.ReadAllLines(path))
					{
						string line = raw.Trim();
						if (line.StartsWith(SaltPrefix, StringComparison.Ordinal))
						{
							_salt = FromHex(line.Substring(SaltPrefix.Length).Trim());
							continue;
						}
						int space = line.IndexOf(' ');
						string idText = space < 0 ? line : line.Substring(0, space);
						string hash = space < 0 ? string.Empty : line.Substring(space + 1).Trim();
						ulong id;
						if (ulong.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
							IdentityClaimRules.IsClaimableSteamId(id) && _ids.Count < MaxIds)
						{
							_ids[id] = hash;
						}
					}
				}
			}
			catch
			{
				// An unreadable list starts empty: every id is then treated as new (fail-soft),
				// which is today's behaviour, never a lock-out.
			}
			if (_salt == null || _salt.Length < 16)
			{
				// A list with no usable salt keeps its ids; their old hashes simply never match.
				_salt = new byte[16];
				using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(_salt);
			}
		}

		internal int Count { get { lock (_sync) return _ids.Count; } }

		internal bool Contains(ulong steamId)
		{
			lock (_sync) return _ids.ContainsKey(steamId);
		}

		// False for an unknown id, an id with no address on record, or an unreadable address.
		internal bool IsLastVerifiedAddress(ulong steamId, string address)
		{
			string wanted = HashAddress(address);
			if (wanted == null) return false;
			lock (_sync)
			{
				string known;
				return _ids.TryGetValue(steamId, out known) && string.Equals(known, wanted, StringComparison.Ordinal);
			}
		}

		// Records a verified join. True when the id was new. Writes only when something changed.
		internal bool Remember(ulong steamId, string address)
		{
			if (!IdentityClaimRules.IsClaimableSteamId(steamId)) return false;
			string hash = HashAddress(address) ?? string.Empty;
			lock (_sync)
			{
				string known;
				bool isNew = !_ids.TryGetValue(steamId, out known);
				if (isNew && _ids.Count >= MaxIds) return false;
				if (!isNew && (hash.Length == 0 || string.Equals(known, hash, StringComparison.Ordinal))) return false;
				_ids[steamId] = hash;
				if (string.IsNullOrEmpty(_path)) return isNew;
				try
				{
					StringBuilder text = new StringBuilder(_ids.Count * 36);
					text.Append(SaltPrefix).Append(ToHex(_salt)).Append('\n');
					foreach (KeyValuePair<ulong, string> pair in _ids)
					{
						text.Append(pair.Key.ToString(CultureInfo.InvariantCulture));
						if (pair.Value.Length > 0) text.Append(' ').Append(pair.Value);
						text.Append('\n');
					}
					string temp = _path + ".tmp";
					File.WriteAllText(temp, text.ToString());
					if (File.Exists(_path)) File.Replace(temp, _path, null);
					else File.Move(temp, _path);
				}
				catch
				{
					// Kept in memory for this session even if the disk write failed.
				}
				return isNew;
			}
		}

		private string HashAddress(string address)
		{
			string canonical = IdentityClaimRules.CanonicalAddress(address);
			if (canonical == null) return null;
			using (HMACSHA256 hmac = new HMACSHA256(_salt))
			{
				byte[] full = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
				byte[] part = new byte[16];
				Array.Copy(full, part, part.Length);
				return ToHex(part);
			}
		}

		private static string ToHex(byte[] bytes)
		{
			StringBuilder text = new StringBuilder(bytes.Length * 2);
			foreach (byte b in bytes) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
			return text.ToString();
		}

		private static byte[] FromHex(string hex)
		{
			if (hex.Length == 0 || (hex.Length % 2) != 0) return null;
			byte[] bytes = new byte[hex.Length / 2];
			for (int i = 0; i < bytes.Length; i++)
			{
				if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return null;
			}
			return bytes;
		}
	}

	// How many Steam calls claims may spend. The key is usually shared, and Steam throttling it is
	// indistinguishable from an outage, so a player must not be able to spend it freely: at most two
	// tickets per connection and thirty calls per server in any ten-minute window. A ticket already
	// taken costs nothing again (a client retries its claim). Over budget is TicketCheck.OverBudget.
	internal sealed class TicketBudget
	{
		internal const int PerConnection = 2;
		internal const int PerWindow = 30;
		internal static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

		private readonly object _sync = new object();
		private readonly Dictionary<int, List<string>> _byConnection = new Dictionary<int, List<string>>();
		private DateTime _windowStart = DateTime.MinValue;
		private int _spent;

		internal bool TryTake(int connectionKey, string ticket, DateTime utcNow)
		{
			lock (_sync)
			{
				if (utcNow - _windowStart >= Window)
				{
					_windowStart = utcNow;
					_spent = 0;
					_byConnection.Clear();
				}
				List<string> taken;
				if (!_byConnection.TryGetValue(connectionKey, out taken))
				{
					taken = new List<string>(PerConnection);
					_byConnection[connectionKey] = taken;
				}
				if (taken.Contains(ticket)) return true;
				if (taken.Count >= PerConnection || _spent >= PerWindow) return false;
				taken.Add(ticket);
				_spent++;
				return true;
			}
		}
	}
}
