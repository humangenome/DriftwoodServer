using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
	// unverified. One exception closes the spoof without locking older apps out: once an id has
	// been verified on this server, an unverified claim for it is refused.
	internal enum TicketCheck
	{
		// No ticket, or no key to check it with.
		NotChecked,
		// Steam says the ticket is valid; CheckedSteamId says whose it is.
		Verified,
		// Steam answered and refused the ticket (forged, expired, wrong game or identity).
		Rejected,
		// No usable answer: timeout, network error, rate limit, a key Steam will not accept.
		Unavailable
	}

	internal enum ClaimDecision
	{
		AcceptVerified,
		AcceptUnverified,
		Refuse
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
			ulong claimedSteamId, bool previouslyVerified, out string reason)
		{
			if (ticketPresent && check == TicketCheck.Verified)
			{
				if (checkedSteamId == claimedSteamId)
				{
					reason = "verified by Steam";
					return ClaimDecision.AcceptVerified;
				}
				reason = "the Steam ticket belongs to a different account";
				return ClaimDecision.Refuse;
			}
			if (ticketPresent && check == TicketCheck.Rejected)
			{
				reason = "Steam rejected the ticket";
				return ClaimDecision.Refuse;
			}
			string why = !ticketPresent ? "no ticket (older app)"
				: check == TicketCheck.Unavailable ? "Steam could not be asked"
				: "no Steam Web API key to check it with";
			if (previouslyVerified)
			{
				// Fail closed for an id that has proved itself here before: an unverifiable claim
				// for it is exactly the spoof this check exists to stop. A Steam outage costs that
				// player their character for one session; it never hands it to someone else.
				reason = "this Steam ID was verified on this server before and this claim could not be (" + why + ")";
				return ClaimDecision.Refuse;
			}
			reason = "unverified: " + why;
			return ClaimDecision.AcceptUnverified;
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

	// The ids this server has seen verified by Steam. One SteamID64 per line in the host's state
	// directory, so it survives restarts and world changes. Bounded; written through a temp file.
	internal sealed class VerifiedIds
	{
		internal const int MaxIds = 20000;
		private readonly object _sync = new object();
		private readonly HashSet<ulong> _ids = new HashSet<ulong>();
		private readonly string _path;

		internal VerifiedIds(string path)
		{
			_path = path;
			try
			{
				if (!string.IsNullOrEmpty(path) && File.Exists(path))
				{
					foreach (string line in File.ReadAllLines(path))
					{
						ulong id;
						if (ulong.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
							IdentityClaimRules.IsClaimableSteamId(id) && _ids.Count < MaxIds)
						{
							_ids.Add(id);
						}
					}
				}
			}
			catch
			{
				// An unreadable list starts empty: every id is then treated as new (fail-soft),
				// which is today's behaviour, never a lock-out.
			}
		}

		internal int Count { get { lock (_sync) return _ids.Count; } }

		internal bool Contains(ulong steamId)
		{
			lock (_sync) return _ids.Contains(steamId);
		}

		// True when the id was new and is now on disk.
		internal bool Add(ulong steamId)
		{
			if (!IdentityClaimRules.IsClaimableSteamId(steamId)) return false;
			lock (_sync)
			{
				if (_ids.Contains(steamId) || _ids.Count >= MaxIds) return false;
				_ids.Add(steamId);
				if (string.IsNullOrEmpty(_path)) return true;
				try
				{
					StringBuilder text = new StringBuilder(_ids.Count * 18);
					foreach (ulong id in _ids) text.Append(id.ToString(CultureInfo.InvariantCulture)).Append('\n');
					string temp = _path + ".tmp";
					File.WriteAllText(temp, text.ToString());
					if (File.Exists(_path)) File.Replace(temp, _path, null);
					else File.Move(temp, _path);
				}
				catch
				{
					// Kept in memory for this session even if the disk write failed.
				}
				return true;
			}
		}
	}
}
