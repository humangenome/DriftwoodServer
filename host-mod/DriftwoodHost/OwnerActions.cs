using System;
using System.Collections.Generic;
using System.Globalization;
using FishNet.Connection;
using FishNet.Managing.Logging;
using FishNet.Managing.Server;

namespace DriftwoodHost
{
	// The owner's hands on the live world: remove a player, keep a blocked one out, put a
	// sentence in every player's chat. This game ships NONE of this - no admin concept, no
	// kick, no ban, no server-to-player chat - so each action here is built from the two
	// primitives the game genuinely has:
	//
	//   REMOVE = FishNet's own connection kick. Player is a NetworkBehaviour, its Owner is the
	//   transport connection, and NetworkConnection.Kick tears it down server-side. The client
	//   drops to the menu exactly as it does on a connection loss. No client mod involved.
	//
	//   BROADCAST = the game's own chat pipe, driven from our side. OnlineChatManager carries
	//   an ObserversRpc the server may invoke directly, and the shipped client renders a
	//   message whose from-id equals its current lobby id as "[Server]" in the highlight
	//   colour. A launcher-joined client has no lobby, so its lobby id is Nil - which is why
	//   broadcasting from id 0 lands as a proper server line on every vanilla client.
	//
	// EVERY method in this class touches Unity objects and therefore RUNS ON THE MAIN THREAD
	// ONLY: command handlers reach it through MainThread.Run, and the blocklist sweep is called
	// from the readiness sampler, which already lives on the main thread. Server-authoritative
	// by construction - decisions are made here on server state; nothing a client asserts is
	// consulted for anything.
	internal static class OwnerActions
	{
		internal sealed class Found
		{
			internal Player Player;
			internal ulong SteamId;
			internal string Name = string.Empty;
		}

		// Resolves "who" from a 17-digit SteamID64 or a display name. Names are matched
		// case-insensitively and must be UNIQUE among connected players - two players can
		// wear the same name (names are user-controlled), and guessing between them would
		// kick somebody on a coin flip. The id path has no such ambiguity, which is the
		// polite nudge towards using it.
		internal static string FindConnected(string needle, out Found found)
		{
			found = null;
			needle = (needle ?? string.Empty).Trim();
			if (needle.Length == 0) return "say who: a SteamID64 or a connected player's name.";

			List<Found> connected = ConnectedPlayers();

			if (ulong.TryParse(needle, NumberStyles.None, CultureInfo.InvariantCulture, out ulong wanted))
			{
				foreach (Found candidate in connected)
				{
					if (candidate.SteamId == wanted) { found = candidate; return null; }
				}
				return "nobody connected has SteamID " + wanted.ToString(CultureInfo.InvariantCulture) + ".";
			}

			List<Found> matches = new List<Found>();
			foreach (Found candidate in connected)
			{
				if (string.Equals(candidate.Name, needle, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);
			}
			if (matches.Count == 1) { found = matches[0]; return null; }
			if (matches.Count == 0) return "nobody connected is named \"" + needle + "\". Try `players` for the list, or use the SteamID64.";
			return "more than one connected player is named \"" + needle + "\" - use the SteamID64 from `players` instead.";
		}

		// MAIN THREAD. The one walk over the game's roster, shared by find, kick and the
		// blocklist sweep, with every deref guarded: a null list, a null player and a null
		// connection are all normal states mid-join and mid-leave.
		internal static List<Found> ConnectedPlayers()
		{
			List<Found> list = new List<Found>();
			try
			{
				// ServerRoster, not PlayerManager - the game's list reads empty on a
				// headless host with players aboard, which left kick, block and the
				// sweep blind to every remote player. See ServerRoster.cs.
				foreach (Player player in ServerRoster.Connected())
				{
					if (player == null) continue;
					ulong steamId;
					try { steamId = player.SteamID; }
					catch { continue; }
					if (steamId == 0UL || steamId == DriftwoodIdentity.HostSteamId) continue;
					list.Add(new Found
					{
						Player = player,
						SteamId = steamId,
						Name = DriftwoodIdentity.ResolveName(new Steamworks.CSteamID(steamId))
					});
				}
			}
			catch (Exception exception)
			{
				Plugin.Log?.LogWarning("Reading the player roster failed: " + exception.GetType().Name + " " + exception.Message);
			}
			return list;
		}

		// MAIN THREAD. Returns null on success.
		internal static string Kick(Found target)
		{
			if (target?.Player == null) return "that player is no longer here.";
			try
			{
				NetworkConnection connection = target.Player.Owner;
				if (connection == null || !connection.IsValid) return "that player's connection is already gone.";
				connection.Kick(KickReason.Unset, LoggingType.Common,
					"Removed by the server owner (Driftwood).");
				return null;
			}
			catch (Exception exception)
			{
				return "the kick failed (" + exception.GetType().Name + ": " + exception.Message + ").";
			}
		}

		// MAIN THREAD. Returns null on success. From-id 0 renders as "[Server]" on every
		// launcher-joined client - see the class comment.
		internal static string Broadcast(string message)
		{
			message = SteamProfileParser.Sanitize(message, 300);
			if (message.Length == 0) return "say what? Usage: say <message>.";
			try
			{
				OnlineChatManager manager = OnlineChatManager.Instance;
				if (manager == null || !manager.IsServerInitialized)
					return "the world is not running, so there is nobody to talk to.";
				manager.SendChatMessage(0UL, message);
				return null;
			}
			catch (Exception exception)
			{
				return "the broadcast failed (" + exception.GetType().Name + ": " + exception.Message + ").";
			}
		}

		// MAIN THREAD - called from the readiness sampler every ~2 seconds, which makes two
		// seconds the longest a blocked player can stand in the world after connecting. The
		// game gives us no earlier hook that already knows the SteamID: the id itself only
		// arrives on the client's own schedule after spawn. Targets are collected first and
		// kicked after the walk, so the kick's side effects never mutate the list mid-iteration.
		internal static void EnforceBlocklist()
		{
			if (Blocklist.Count == 0) return;
			List<Found> targets = null;
			foreach (Found candidate in ConnectedPlayers())
			{
				if (!Blocklist.IsBlocked(candidate.SteamId)) continue;
				if (targets == null) targets = new List<Found>();
				targets.Add(candidate);
			}
			if (targets == null) return;
			foreach (Found target in targets)
			{
				string failure = Kick(target);
				OwnerAudit.Record("server", "enforce-block",
					target.SteamId.ToString(CultureInfo.InvariantCulture) + " (" + target.Name + ")",
					failure == null, failure ?? "blocked player removed on connect");
				if (failure == null)
				{
					Plugin.Log?.LogInfo("Blocked player " + target.SteamId + " connected and was removed.");
					DiscordAlerts.BlockedPlayerRejected(target.SteamId, target.Name);
				}
			}
		}
	}
}
