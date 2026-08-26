using System;
using System.Collections.Generic;
using System.Globalization;

namespace DriftwoodHost
{
	// Player chat commands: what a PLAYER types into the game's ordinary chat box, starting
	// with "!". Vanilla clients, nothing to install - the game already ships the text to this
	// server (Server.SendChatMessage, a ServerRpc every client calls for every line), and the
	// host answers on the same pipe.
	//
	// This file is the PURE half: what counts as a command, the per-player cooldown ledger,
	// the help text and the duration formatting. No Unity, no BepInEx, no game type, so the
	// xunit suite links it and proves the parsing and the throttle without a running game. The
	// Harmony hook on the chat RPC and the handlers that touch the world live in PlayerChat.cs;
	// the teleport itself lives in PlayerRescue.cs.
	internal static class PlayerCommands
	{
		internal const char Prefix = '!';

		// Every command a player can type. The help line is built from this so the two cannot
		// drift apart.
		internal static readonly string[] Names = { "help", "stuck", "playtime", "top" };

		internal const int MaxArgsLength = 64;

		// No real command is anywhere near this long. A longer "verb" is somebody leaning on
		// the keyboard (or a client probing), and it stays ordinary chat rather than earning a
		// broadcast reply that echoes it back at the crew.
		internal const int MaxVerbLength = 24;

		// A command is "!" followed immediately by a letter. "!!!", "! hi" and "!1" are ordinary
		// chat and pass through untouched - players use "!" for emphasis, and swallowing those
		// lines would look like the server eating their messages.
		internal static bool TryParse(string text, out string verb, out string args)
		{
			verb = string.Empty;
			args = string.Empty;
			if (string.IsNullOrEmpty(text)) return false;
			string trimmed = text.TrimStart();
			if (trimmed.Length < 2 || trimmed[0] != Prefix) return false;
			if (!char.IsLetter(trimmed[1])) return false;

			int end = 1;
			while (end < trimmed.Length && char.IsLetterOrDigit(trimmed[end])) end++;
			if (end - 1 > MaxVerbLength) return false;
			verb = trimmed.Substring(1, end - 1).ToLowerInvariant();
			string rest = trimmed.Substring(end).Trim();
			if (rest.Length > MaxArgsLength) rest = rest.Substring(0, MaxArgsLength);
			args = rest;
			return true;
		}

		internal static bool IsKnown(string verb)
		{
			if (string.IsNullOrEmpty(verb)) return false;
			foreach (string name in Names)
			{
				if (name == verb) return true;
			}
			return false;
		}

		// One line, because the reply is a chat message every player sees.
		internal static string HelpLine(bool leaderboardOn)
		{
			string line = "!stuck (back to the island spawn)  !playtime (your time on this server)";
			if (leaderboardOn) line += "  !top (the catch leaderboard)";
			return line + "  !help";
		}

		internal static string Duration(long seconds)
		{
			if (seconds < 0) seconds = 0;
			if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + "s";
			long minutes = seconds / 60;
			if (minutes < 60) return minutes.ToString(CultureInfo.InvariantCulture) + "m";
			long hours = minutes / 60;
			if (hours < 48)
			{
				return hours.ToString(CultureInfo.InvariantCulture) + "h " +
					(minutes % 60).ToString(CultureInfo.InvariantCulture) + "m";
			}
			long days = hours / 24;
			return days.ToString(CultureInfo.InvariantCulture) + "d " +
				(hours % 24).ToString(CultureInfo.InvariantCulture) + "h";
		}

		internal static string Money(long amount)
		{
			return "$" + amount.ToString("N0", CultureInfo.InvariantCulture);
		}

		// A name on its way into a BROADCAST chat line. The game's chat renders TextMeshPro
		// rich text (its own save notice is sent wrapped in <i>), so a persona name carrying
		// "<size=..." would style-bomb every client's chat through the server's mouth. The
		// roster, the console and the files keep the name as chosen; only the chat pipe
		// strips the one character that opens a tag.
		internal static string ChatSafe(string name)
		{
			if (string.IsNullOrEmpty(name)) return string.Empty;
			return name.IndexOf('<') < 0 ? name : name.Replace("<", string.Empty);
		}
	}

	// The throttle. Every reply is a chat line EVERY connected player sees (the game has no
	// private server-to-player message - its one server chat pipe is an observers broadcast),
	// so a player who spams "!help" is spamming the whole crew through the server's mouth. Two
	// limits, both cheap: a per-player gap between replies (excess lines are dropped silently,
	// which is what a spammer deserves and what a fast double-tap barely notices), and a global
	// cap per window so eight players cannot turn the server into a chat cannon together. The
	// teleport has its own, longer, per-player cooldown, and THAT refusal is answered with the
	// seconds remaining, because it is a real question with a real answer.
	//
	// Clock is injected (seconds, any monotonic origin) so the tests can drive it.
	internal sealed class ChatCooldowns
	{
		private readonly double _replyGapSeconds;
		private readonly double _stuckSeconds;
		private readonly int _globalCap;
		private readonly double _globalWindowSeconds;

		private readonly Dictionary<ulong, double> _lastReply = new Dictionary<ulong, double>();
		private readonly Dictionary<ulong, double> _lastStuck = new Dictionary<ulong, double>();
		private readonly Queue<double> _globalReplies = new Queue<double>();

		// Bounded: a player id that has not spoken in this long is forgotten, so a long-running
		// server with thousands of visitors never grows these tables without limit.
		private const double ForgetAfterSeconds = 6 * 3600;
		private const int PruneEvery = 256;
		private int _sincePrune;

		internal ChatCooldowns(double replyGapSeconds, double stuckSeconds, int globalCap, double globalWindowSeconds)
		{
			_replyGapSeconds = Math.Max(0, replyGapSeconds);
			_stuckSeconds = Math.Max(0, stuckSeconds);
			_globalCap = Math.Max(1, globalCap);
			_globalWindowSeconds = Math.Max(1, globalWindowSeconds);
		}

		internal double StuckCooldownSeconds => _stuckSeconds;

		// True when a reply may be sent now; records it. False means DROP - say nothing.
		internal bool TryReply(ulong steamId, double now)
		{
			Prune(now);
			double last;
			if (_lastReply.TryGetValue(steamId, out last) && now - last < _replyGapSeconds) return false;
			while (_globalReplies.Count > 0 && now - _globalReplies.Peek() > _globalWindowSeconds) _globalReplies.Dequeue();
			if (_globalReplies.Count >= _globalCap) return false;
			_lastReply[steamId] = now;
			_globalReplies.Enqueue(now);
			return true;
		}

		// Seconds until this player may use the teleport again; 0 = now.
		internal double StuckRemaining(ulong steamId, double now)
		{
			double last;
			if (!_lastStuck.TryGetValue(steamId, out last)) return 0;
			double remaining = _stuckSeconds - (now - last);
			return remaining > 0 ? remaining : 0;
		}

		internal void MarkStuck(ulong steamId, double now)
		{
			_lastStuck[steamId] = now;
		}

		private void Prune(double now)
		{
			if (++_sincePrune < PruneEvery) return;
			_sincePrune = 0;
			List<ulong> stale = new List<ulong>();
			foreach (KeyValuePair<ulong, double> pair in _lastReply)
			{
				if (now - pair.Value > ForgetAfterSeconds) stale.Add(pair.Key);
			}
			foreach (ulong id in stale) _lastReply.Remove(id);
			stale.Clear();
			foreach (KeyValuePair<ulong, double> pair in _lastStuck)
			{
				if (now - pair.Value > ForgetAfterSeconds) stale.Add(pair.Key);
			}
			foreach (ulong id in stale) _lastStuck.Remove(id);
		}
	}
}
