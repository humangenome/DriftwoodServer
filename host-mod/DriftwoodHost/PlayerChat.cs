using System;
using System.Collections.Generic;
using System.Globalization;
using FishNet.Connection;
using HarmonyLib;
using UnityEngine;

namespace DriftwoodHost
{
	// The chat hook: where a player's "!command" leaves the game's chat pipe and becomes an
	// answer from this server.
	//
	// THE INBOUND PATH, from the decompiled game. A player presses Enter, ChatManager
	// .SendTypedMessage runs on their client and calls Server.SendChatMessage(steamId, text),
	// a ServerRpc. On this server FishNet delivers it on the main thread as
	// Server.RpcReader___SendChatMessage___3264264606(reader, channel, conn) - which reads
	// the two fields and calls Server.RpcLogic___SendChatMessage___3264264606(from, message)
	// - which does exactly one thing: OnlineChatManager.Instance.SendChatMessage(from, message),
	// the observers broadcast every client renders. There is no server-side handling of chat
	// at all; the server is a relay.
	//
	// Two patches, one feature:
	//   1. a prefix on the READER captures the NetworkConnection the line arrived on - the
	//      transport-derived identity, the same kind the audit log keys on;
	//   2. a prefix on the LOGIC looks at the text. Ordinary chat returns true and the relay
	//      runs untouched. A "!command" is handled here and returns false, so the command line
	//      itself is never broadcast - the crew sees the server's answer, not the request.
	// The sender is the Player whose Owner is that connection. The `from` id the client sent
	// is only a fallback (when the reader patch is not in force), and it is the same
	// client-claimed SteamID64 the game itself keys every player on.
	//
	// REPLIES ARE PUBLIC. The game has no private server-to-player chat - its one server chat
	// pipe is an observers RPC - so every answer is a "[Server]" line the whole crew sees,
	// addressed by name. That is why ChatCooldowns exists: the server must never amplify one
	// player's keyboard into everybody's chat.
	//
	// Runs entirely on the main thread (FishNet delivers RPCs there), so handlers touch game
	// objects directly, and nothing here may throw past the prefix: an exception escaping a
	// Harmony prefix aborts the RPC dispatch it sits in, which would mean a player's ordinary
	// chat line vanishing because of us. Every entry point is wrapped.
	internal static class PlayerChat
	{
		internal const string GroupName = "PlayerChat";
		// Between two replies to the same player, and the crew-wide cap. Small enough that a
		// deliberate double-tap still answers; large enough that a held key does not.
		private const double ReplyGapSeconds = 3;
		private const int GlobalRepliesPerWindow = 12;
		private const double GlobalWindowSeconds = 10;

		internal static bool Enabled;
		private static ChatCooldowns _cooldowns = new ChatCooldowns(ReplyGapSeconds, 60, GlobalRepliesPerWindow, GlobalWindowSeconds);
		private static volatile string _state = "off (not started)";
		private static NetworkConnection _currentSender;
		private static bool _warnedOnce;

		// One plain sentence for the readiness document and the console's `status`.
		internal static string State => _state;

		internal static void Configure(bool enabled, double stuckCooldownSeconds)
		{
			Enabled = enabled;
			_cooldowns = new ChatCooldowns(ReplyGapSeconds, stuckCooldownSeconds, GlobalRepliesPerWindow, GlobalWindowSeconds);
			_state = enabled ? "off (the chat hook is not in force)" : "off (disabled in this server's configuration)";
		}

		// Called by Plugin after the patch plan ran, with whether this feature's group applied.
		internal static void OnPatched(bool applied)
		{
			if (!Enabled) return;
			_state = applied
				? "on (" + string.Join(", ", Array.ConvertAll(PlayerCommands.Names, n => PlayerCommands.Prefix + n)) + ")"
				: "off (the game build no longer has the chat RPC this hooks; ordinary chat is unaffected)";
		}

		// THE RPC CHANGED SHAPE IN GAME 1.0.10, and matching it by its full generated name is
		// what silently switched this feature off on every server since (the name carries a
		// hash of the signature, ___3264264606 up to 1.0.9, ___2943234052 from 1.0.10):
		//
		//   up to 1.0.9   RpcLogic(ulong from, string message)             the client sends its id
		//   1.0.10+       RpcLogic(string message, NetworkConnection conn) the server works it out
		//
		// and in the 1.0.10+ body the server works it out as SteamUser.GetSteamID() whenever the
		// session is not using Steam - which on a dedicated server is always, and which our
		// GetSteamID guard answers with the server's own identity. So every player's chat line
		// has been relayed under the SERVER's id: the crew saw each other's messages under the
		// server's name. The 1.0.10+ prefix fixes that too - ordinary chat is relayed under the
		// sender's own id, resolved from the connection the line arrived on.
		//
		// Both methods are found by name PREFIX and the handler is chosen by the signature the
		// game actually ships, so the next hash move changes nothing here.
		internal const string ReaderPrefixName = "RpcReader___SendChatMessage___";
		internal const string LogicPrefixName = "RpcLogic___SendChatMessage___";

		internal static IEnumerable<PatchTarget> Targets()
		{
			bool connectionShape = LogicTakesConnection();
			// Both members stand or fall together: without the reader the sender would have to
			// be taken from the client's own claim, and a chat command that acts on a player is
			// worth having only with the transport-derived identity everything else keys on.
			yield return new PatchTarget
			{
				TypeName = "Server",
				MethodName = ReaderPrefixName + "*",
				MethodNamePrefix = ReaderPrefixName,
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = GroupName,
				Prefix = AccessTools.Method(typeof(PlayerChat), nameof(ReaderPrefix)),
				Finalizer = AccessTools.Method(typeof(PlayerChat), nameof(ReaderFinalizer)),
				Why = "Captures the connection a chat line arrived on, so a player command is attributed by transport, not by the client's claim."
			};
			yield return new PatchTarget
			{
				TypeName = "Server",
				MethodName = LogicPrefixName + "*",
				MethodNamePrefix = LogicPrefixName,
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = GroupName,
				Prefix = connectionShape
					? AccessTools.Method(typeof(PlayerChat), nameof(LogicPrefixByConnection))
					: AccessTools.Method(typeof(PlayerChat), nameof(LogicPrefix)),
				Why = connectionShape
					? "Player chat commands, and ordinary chat relayed under the sender's own id instead of the server's (game 1.0.10+)."
					: "Player chat commands (!stuck, !playtime, !top, !help). Ordinary chat passes through untouched."
			};
		}

		// True when the shipped chat logic takes (string message, NetworkConnection sender).
		private static bool LogicTakesConnection()
		{
			try
			{
				Type server = AccessTools.TypeByName("Server");
				if (server == null) return false;
				foreach (System.Reflection.MethodInfo method in server.GetMethods(
					System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
					System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
				{
					if (!method.Name.StartsWith(LogicPrefixName, StringComparison.Ordinal)) continue;
					System.Reflection.ParameterInfo[] parameters = method.GetParameters();
					return parameters.Length == 2 && parameters[0].ParameterType == typeof(string) &&
						typeof(NetworkConnection).IsAssignableFrom(parameters[1].ParameterType);
				}
			}
			catch
			{
			}
			return false;
		}

		// ------------------------------------------------------------------
		// Harmony entry points
		// ------------------------------------------------------------------

		// The reader's third parameter is the FishNet connection the RPC came from.
		private static void ReaderPrefix(NetworkConnection __2)
		{
			_currentSender = __2;
		}

		// A FINALIZER, not a postfix, for the same reason SpawnIdentity uses one: a reader
		// that throws would leave the previous sender in the field, and the next server-side
		// call into the logic would be attributed to a stale connection. The game's own
		// exception passes through untouched.
		private static Exception ReaderFinalizer(Exception __exception)
		{
			_currentSender = null;
			return __exception;
		}

		// true  = not a command (or the feature is off): let the game relay the line.
		// false = handled here; the line is not broadcast.
		private static bool LogicPrefix(ulong __0, string __1)
		{
			try
			{
				if (!Enabled) return true;
				string verb, args;
				if (!PlayerCommands.TryParse(__1, out verb, out args)) return true;
				Handle(__0, verb, args);
				return false;
			}
			catch (Exception exception)
			{
				if (!_warnedOnce)
				{
					_warnedOnce = true;
					Plugin.Log?.LogWarning("A player chat command failed (" + exception.GetType().Name + ": " +
						exception.Message + "). The line was passed through as ordinary chat. Further failures are not logged.");
				}
				return true;
			}
		}

		// Game 1.0.10+: the logic receives the connection itself. A command is handled exactly as
		// before. Ordinary chat is relayed by us under the sender's own id and the game's body is
		// skipped, because that body would stamp the line with the server's id (see Targets). If
		// the sender cannot be resolved the game's own relay runs, unchanged.
		private static bool LogicPrefixByConnection(string __0, NetworkConnection __1)
		{
			try
			{
				if (!Enabled) return true;
				if (__1 != null) _currentSender = __1;
				string verb, args;
				if (PlayerCommands.TryParse(__0, out verb, out args))
				{
					Handle(0UL, verb, args);
					return false;
				}
				OwnerActions.Found sender = ResolveSender(0UL);
				if (sender == null) return true;
				object chat = AccessTools.Field(AccessTools.TypeByName("OnlineChatManager"), "Instance")?.GetValue(null);
				System.Reflection.MethodInfo relay = chat == null ? null :
					AccessTools.Method(chat.GetType(), "SendChatMessage", new[] { typeof(ulong), typeof(string) });
				if (relay == null) return true;
				relay.Invoke(chat, new object[] { sender.SteamId, __0 });
				return false;
			}
			catch (Exception exception)
			{
				if (!_warnedOnce)
				{
					_warnedOnce = true;
					Plugin.Log?.LogWarning("A player chat line could not be handled (" + exception.GetType().Name + ": " +
						exception.Message + "). It was passed to the game's own relay. Further failures are not logged.");
				}
				return true;
			}
		}

		// ------------------------------------------------------------------
		// Dispatch. MAIN THREAD.
		// ------------------------------------------------------------------

		private static void Handle(ulong claimedId, string verb, string args)
		{
			OwnerActions.Found sender = ResolveSender(claimedId);
			if (sender == null)
			{
				// A "!" line from a connection with no player behind it - the host's own
				// loopback client, or a client mid-spawn. Nothing to answer, nobody to answer.
				Plugin.Log?.LogDebug("Chat command '" + verb + "' from an unresolvable sender (claimed id " + claimedId + ") was ignored.");
				return;
			}

			double now = Time.realtimeSinceStartup;
			if (!_cooldowns.TryReply(sender.SteamId, now))
			{
				// Throttled. Silence is the whole point - see ChatCooldowns.
				return;
			}

			switch (verb)
			{
				case "help":
					Reply(PlayerCommands.ChatSafe(sender.Name) + ": " + PlayerCommands.HelpLine(CatchLedger.Enabled));
					return;

				case "stuck":
					Stuck(sender, now);
					return;

				case "playtime":
					Playtime(sender);
					return;

				case "top":
					Top(sender);
					return;

				default:
					Reply(PlayerCommands.ChatSafe(sender.Name) + ": there is no !" + verb + " - try !help");
					return;
			}
		}

		private static void Stuck(OwnerActions.Found sender, double now)
		{
			string target = sender.SteamId.ToString(CultureInfo.InvariantCulture) + " (" + sender.Name + ")";
			double remaining = _cooldowns.StuckRemaining(sender.SteamId, now);
			if (remaining > 0)
			{
				Reply(PlayerCommands.ChatSafe(sender.Name) + ": !stuck is on cooldown for another " + Math.Ceiling(remaining).ToString(CultureInfo.InvariantCulture) + "s.");
				return;
			}

			string where;
			string failure = PlayerRescue.ToShore(sender.Player, out where);
			// A refused rescue is recorded like a refused kick: "the server would not help me"
			// deserves the same answer as "the server kicked me".
			OwnerAudit.Record("chat", "stuck", target, failure == null, failure ?? ("teleported to the " + where));
			if (failure != null)
			{
				Reply(PlayerCommands.ChatSafe(sender.Name) + ": " + failure);
				return;
			}
			_cooldowns.MarkStuck(sender.SteamId, now);
			Reply(PlayerCommands.ChatSafe(sender.Name) + ": sending you back to the island spawn.");
		}

		private static void Playtime(OwnerActions.Found sender)
		{
			long session = -1;
			foreach (PlayerDirectory.Row row in PlayerDirectory.Snapshot())
			{
				if (row.SteamId != sender.SteamId) continue;
				session = row.ConnectedSeconds;
				break;
			}
			string line = PlayerCommands.ChatSafe(sender.Name) + ": ";
			line += session < 0
				? "you have just arrived"
				: "you have been on this server for " + PlayerCommands.Duration(session) + " this session";
			if (CatchLedger.Enabled)
			{
				CatchLedger.Entry entry = CatchLedger.Get(sender.SteamId);
				if (entry != null && entry.PlaytimeSeconds > Math.Max(0, session))
				{
					line += ", " + PlayerCommands.Duration(entry.PlaytimeSeconds) + " in total";
				}
			}
			Reply(line + ".");
		}

		private static void Top(OwnerActions.Found sender)
		{
			if (!CatchLedger.Enabled)
			{
				Reply(PlayerCommands.ChatSafe(sender.Name) + ": the catch leaderboard is off on this server.");
				return;
			}
			List<CatchLedger.Entry> top = CatchLedger.Top(3);
			if (top.Count == 0)
			{
				Reply(PlayerCommands.ChatSafe(sender.Name) + ": nobody is on the board yet - it counts identified players from their first landed or sold catch.");
				return;
			}
			System.Text.StringBuilder builder = new System.Text.StringBuilder("Top anglers: ");
			for (int i = 0; i < top.Count; i++)
			{
				if (i > 0) builder.Append("  ");
				builder.Append(i + 1).Append(". ").Append(top[i].Name.Length == 0 ? "?" : PlayerCommands.ChatSafe(top[i].Name))
					.Append(' ').Append(PlayerCommands.Money(top[i].Earnings))
					.Append(" (").Append(top[i].Catches).Append(top[i].Catches == 1 ? " catch)" : " catches)");
			}
			int rank = CatchLedger.RankOf(sender.SteamId);
			if (rank > 3) builder.Append("  - you are #").Append(rank);
			Reply(builder.ToString());
		}

		// The Player behind the line. The connection captured by the reader prefix wins; the
		// client's own id is the fallback, and only when it names exactly one connected player.
		private static OwnerActions.Found ResolveSender(ulong claimedId)
		{
			NetworkConnection connection = _currentSender;
			List<OwnerActions.Found> connected = OwnerActions.ConnectedPlayers();
			if (connection != null)
			{
				bool local = false;
				try { local = connection.IsLocalClient; } catch { }
				if (local) return null;
				foreach (OwnerActions.Found candidate in connected)
				{
					NetworkConnection owner = null;
					try { owner = candidate.Player != null ? candidate.Player.Owner : null; } catch { }
					if (owner != null && ReferenceEquals(owner, connection)) return candidate;
				}
				return null;
			}
			OwnerActions.Found match = null;
			foreach (OwnerActions.Found candidate in connected)
			{
				if (candidate.SteamId != claimedId) continue;
				if (match != null) return null;
				match = candidate;
			}
			return match;
		}

		private static void Reply(string text)
		{
			string failure = OwnerActions.Broadcast(text);
			if (failure != null) Plugin.Log?.LogDebug("Chat reply not sent: " + failure);
		}
	}
}
