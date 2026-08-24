using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DriftwoodHost
{
	// Extracts (steamid, personaname) pairs out of an ISteamUser/GetPlayerSummaries response.
	//
	// Deliberately NOT a JSON library, for the same reason JsonRead in HostHttpApi is not: the
	// mod refuses to bind the game's Newtonsoft (build-churn liability) and the game's Mono has
	// no System.Text.Json. This is the whole parser the one response shape needs.
	//
	// SAFETY OF THE NEEDLE SCAN, stated because a persona name is attacker-controlled text:
	// inside valid JSON every quote in a string VALUE arrives escaped as \", so the needle
	// "steamid" - which includes both raw quotes - can never match inside a value. A persona
	// name that literally contains "steamid":"..." reaches us as \"steamid\":\"...\" and the
	// scan walks straight past it. The value itself is then read with full escape handling.
	//
	// Dependency-free on purpose so the xunit suite can link this file directly (the HostConfig
	// trick) and feed it hostile inputs without a running game.
	internal static class SteamProfileParser
	{
		internal struct Profile
		{
			internal ulong SteamId;
			internal string PersonaName;
		}

		// Returns every well-formed (steamid, personaname) pair found. A malformed record is
		// skipped, never thrown on - the caller treats an absent id as unresolved and keeps the
		// placeholder, which is the fail-soft this feature promises.
		internal static List<Profile> Parse(string body)
		{
			List<Profile> profiles = new List<Profile>();
			if (string.IsNullOrEmpty(body)) return profiles;

			const string idNeedle = "\"steamid\"";
			const string nameNeedle = "\"personaname\"";

			int at = 0;
			while (true)
			{
				int idKey = body.IndexOf(idNeedle, at, StringComparison.Ordinal);
				if (idKey < 0) break;

				// The window for this player's fields runs to the NEXT steamid key (or the end),
				// so one player's missing personaname can never steal the next player's.
				int nextIdKey = body.IndexOf(idNeedle, idKey + idNeedle.Length, StringComparison.Ordinal);
				int windowEnd = nextIdKey < 0 ? body.Length : nextIdKey;

				string idText = ReadStringValue(body, idKey + idNeedle.Length, windowEnd);
				at = windowEnd;

				if (idText == null) continue;
				ulong steamId;
				if (!ulong.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out steamId)) continue;
				if (steamId == 0UL) continue;

				int nameKey = body.IndexOf(nameNeedle, idKey, StringComparison.Ordinal);
				if (nameKey < 0 || nameKey >= windowEnd) continue;
				string name = ReadStringValue(body, nameKey + nameNeedle.Length, windowEnd);
				if (name == null) continue;

				name = Sanitize(name);
				if (name.Length == 0) continue;

				profiles.Add(new Profile { SteamId = steamId, PersonaName = name });
			}
			return profiles;
		}

		// A display name goes into a roster, a console line, and a log file. Control characters
		// (including tab and newline, which the blocklist file and the audit log use as
		// structure) are stripped rather than trusted. Steam caps persona names at 32
		// characters; anything longer than 48 here is a parser bug or an attack, and is cut.
		internal static string Sanitize(string name)
		{
			return Sanitize(name, 48);
		}

		// The same cleaning with a caller-chosen cap - a broadcast body gets more room than a
		// display name but obeys the same no-control-characters rule, because both end up in
		// tab-structured files and single-line console output.
		internal static string Sanitize(string name, int max)
		{
			if (string.IsNullOrEmpty(name)) return string.Empty;
			StringBuilder builder = new StringBuilder(name.Length);
			foreach (char c in name)
			{
				if (c < ' ' || c == (char)0x7f) continue;
				builder.Append(c);
				if (builder.Length >= max) break;
			}
			return builder.ToString().Trim();
		}

		// Reads the JSON string value that follows a key, honouring escapes. Returns null when
		// the shape is not key-colon-string inside the window.
		private static string ReadStringValue(string text, int afterKey, int windowEnd)
		{
			int i = afterKey;
			while (i < windowEnd && char.IsWhiteSpace(text[i])) i++;
			if (i >= windowEnd || text[i] != ':') return null;
			i++;
			while (i < windowEnd && char.IsWhiteSpace(text[i])) i++;
			if (i >= windowEnd || text[i] != '"') return null;
			i++;

			StringBuilder value = new StringBuilder();
			while (i < windowEnd)
			{
				char c = text[i];
				if (c == '\\')
				{
					i++;
					if (i >= windowEnd) return null;
					char escaped = text[i];
					switch (escaped)
					{
						case 'n': value.Append('\n'); break;
						case 'r': value.Append('\r'); break;
						case 't': value.Append('\t'); break;
						case 'b': value.Append('\b'); break;
						case 'f': value.Append('\f'); break;
						case '/': value.Append('/'); break;
						case '\\': value.Append('\\'); break;
						case '"': value.Append('"'); break;
						case 'u':
							if (i + 4 < windowEnd &&
								int.TryParse(text.Substring(i + 1, 4), NumberStyles.HexNumber,
									CultureInfo.InvariantCulture, out int code))
							{
								value.Append((char)code);
								i += 4;
							}
							else
							{
								return null;
							}
							break;
						default: return null;
					}
					i++;
					continue;
				}
				if (c == '"') return value.ToString();
				value.Append(c);
				i++;
			}
			return null;
		}
	}
}
