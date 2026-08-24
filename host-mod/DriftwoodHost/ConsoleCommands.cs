using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DriftwoodHost
{
	// The owner's console - the launcher's Console tab and the panel both land here, through
	// POST /api/v1/console (signed, or loopback via the panel's own path).
	//
	// THIS GAME SHIPS NO ADMIN SURFACE. The retail How to Fish build has no admin concept, no
	// kick, no ban and no server console - its cheat commands are local and client-side. So
	// everything here is built by the host from the two primitives the game genuinely has
	// (FishNet's connection kick, and the game's own chat RPC driven from the server side) -
	// see OwnerActions for the mechanics. The identity every action keys on is the SteamID64
	// the game itself replicates to this server; a display name is never the key, only a
	// convenience for finding the id.
	//
	// EVERY action that touches a person is audited: who asked (transport-derived, never
	// claimed), what it hit, and whether it worked. The support ticket arrives days later as
	// "the host kicked me for no reason", and the audit line is the difference between an
	// answer and a shrug.
	//
	// Lifecycle (stop / restart) is deliberately NOT here. The panel's stop and restart flush
	// the world and take a backup first; a console shortcut past that ordering would be a
	// data-safety regression dressed as a convenience.
	internal static class ConsoleCommands
	{
		// Kept in sync with the launcher's own suggestion list by the cross-repo contract
		// test - a suggestion for a command the server does not implement is a promise the
		// UI makes on the server's behalf. (The test asserts every suggestion exists here;
		// the server may implement more than the UI suggests.)
		internal static readonly string[] Names =
		{
			"help", "status", "players", "version", "world", "save", "snapshot", "snapshots",
			"kick", "block", "unblock", "blocked", "say", "audit"
		};

		// How long a command may wait for the main thread. Generous against a busy frame, small
		// against the HTTP client's own timeout, so the caller always gets a sentence back.
		private const int ActionTimeoutMs = 10000;

		internal static bool Execute(string command, string actor, Readiness readiness, HostConfig config, out string output)
		{
			string trimmed = (command ?? string.Empty).Trim();
			if (trimmed.Length == 0)
			{
				output = "type a command, or 'help' for the list";
				return false;
			}

			string verb = trimmed;
			string args = string.Empty;
			int space = trimmed.IndexOf(' ');
			if (space > 0)
			{
				verb = trimmed.Substring(0, space);
				args = trimmed.Substring(space + 1).Trim();
			}
			verb = verb.ToLowerInvariant();

			switch (verb)
			{
				case "help":
					output = Help();
					return true;

				case "status":
					output = Status(readiness, config);
					return true;

				case "players":
					output = Players(readiness);
					return true;

				case "version":
					output = "Driftwood host " + readiness.PluginVersion +
						" on How to Fish " + (readiness.GameVersion.Length == 0 ? "(unknown build)" : readiness.GameVersion);
					return true;

				case "world":
					output = "World \"" + readiness.WorldName + "\", auto-saving every " +
						config.AutoSaveMinutes.ToString("0.#") + " minutes.";
					return true;

				case "save":
				{
					string failure;
					if (!MainThread.Run(WorldLifecycle.SaveNow, 15000, out failure))
					{
						output = "the world was NOT saved: " + failure;
						return false;
					}
					output = "world saved";
					return true;
				}

				case "snapshot":
				{
					string id, failure;
					if (!SnapshotStore.Create("console", out id, out failure))
					{
						output = "snapshot failed: " + failure;
						return false;
					}
					output = "snapshot taken: " + id;
					return true;
				}

				case "snapshots":
				{
					List<SnapshotStore.Summary> list = SnapshotStore.List();
					if (list.Count == 0)
					{
						output = "no snapshots yet - run 'snapshot' to take one";
						return true;
					}
					StringBuilder builder = new StringBuilder();
					for (int i = 0; i < list.Count; i++)
					{
						if (i > 0) builder.Append('\n');
						builder.Append(list[i].Id).Append("  ")
							.Append((list[i].SizeBytes / 1024).ToString()).Append(" KB");
					}
					output = builder.ToString();
					return true;
				}

				case "kick":
					return Kick(actor, args, out output);

				case "block":
				case "ban":
					return Block(actor, args, out output);

				case "unblock":
				case "unban":
					return Unblock(actor, args, out output);

				case "blocked":
				case "banlist":
					output = BlockedList();
					return true;

				case "say":
				case "broadcast":
					return Say(actor, args, out output);

				case "audit":
					output = AuditTail(args);
					return true;

				// Named refusals. Each is a thing a player reasonably expects a server console
				// to do, and each answers with where that thing actually is rather than with
				// "unknown command", which would read as a typo.
				case "stop":
				case "shutdown":
				case "restart":
					output = "lifecycle is not on this console. Stop and restart from your hosting panel or supervisor, " +
						"which flushes the world and takes a backup first - this console would skip both.";
					return false;

				case "op":
				case "admin":
					output = "there are no in-game admin levels: How to Fish ships no admin concept, so there is nothing " +
						"to grant a player. Everyone who can open this console has every command it offers.";
					return false;

				default:
					output = "unknown command \"" + verb + "\". Try: " + string.Join(", ", Names);
					return false;
			}
		}

		// ------------------------------------------------------------------
		// Owner actions. Each resolves its target AND acts in a single main-thread hop, so a
		// player leaving between "find" and "act" cannot make the action land on the wrong
		// object - the world simply does not advance between the two.
		// ------------------------------------------------------------------

		private static bool Kick(string actor, string args, out string output)
		{
			string failure = null;
			string target = args;
			string kickedName = null;
			string runFailure;
			bool ran = MainThread.Run(() =>
			{
				OwnerActions.Found found;
				failure = OwnerActions.FindConnected(args, out found);
				if (failure != null) return;
				target = Describe(found.SteamId, found.Name);
				failure = OwnerActions.Kick(found);
				if (failure == null) kickedName = found.Name;
			}, ActionTimeoutMs, out runFailure);

			if (!ran) failure = failure ?? runFailure;
			string auditProblem = OwnerAudit.Record(actor, "kick", target, failure == null,
				failure ?? "removed from the server");
			output = failure != null
				? "nobody was kicked: " + failure
				: kickedName + " was removed from the server. They can reconnect - use 'block' to keep them out.";
			return Finish(failure == null, auditProblem, ref output);
		}

		private static bool Block(string actor, string args, out string output)
		{
			if (args.Length == 0)
			{
				output = "say who: block <SteamID64 or connected player's name>. 'players' shows both.";
				return false;
			}

			string failure = null;
			string target = args;
			string doneSentence = null;
			string runFailure;
			bool ran = MainThread.Run(() =>
			{
				ulong steamId;
				string label;
				bool connected = false;
				OwnerActions.Found found = null;

				if (ulong.TryParse(args, NumberStyles.None, CultureInfo.InvariantCulture, out steamId))
				{
					// A raw id can be blocked whether or not its player is here - that is the
					// point of a block. If they ARE here, they also get removed, below.
					foreach (OwnerActions.Found candidate in OwnerActions.ConnectedPlayers())
					{
						if (candidate.SteamId != steamId) continue;
						found = candidate;
						connected = true;
						break;
					}
					label = found != null ? found.Name : (DriftwoodIdentity.KnownNameOrNull(steamId) ?? string.Empty);
				}
				else
				{
					// A name only means anything among CONNECTED players - it is display text,
					// not identity - so an offline block must use the id.
					failure = OwnerActions.FindConnected(args, out found);
					if (failure != null)
					{
						failure += " To block somebody who already left, use their SteamID64 (the 'audit' history may have it).";
						return;
					}
					steamId = found.SteamId;
					label = found.Name;
					connected = true;
				}

				target = Describe(steamId, label);
				failure = Blocklist.Add(steamId, label, PlayerDirectory.NowUnix());
				if (failure != null) return;

				if (connected)
				{
					string kickFailure = OwnerActions.Kick(found);
					doneSentence = kickFailure == null
						? target + " is now blocked and has been removed from the server."
						: target + " is now blocked, but removing them just now failed (" + kickFailure +
							") - the block sweep will remove them within seconds.";
				}
				else
				{
					doneSentence = target + " is now blocked. If they connect they will be removed within seconds.";
				}
			}, ActionTimeoutMs, out runFailure);

			if (!ran) failure = failure ?? runFailure;
			string auditProblem = OwnerAudit.Record(actor, "block", target, failure == null,
				failure ?? "added to the block list");
			output = failure != null ? "nobody was blocked: " + failure : doneSentence;
			return Finish(failure == null, auditProblem, ref output);
		}

		private static bool Unblock(string actor, string args, out string output)
		{
			if (args.Length == 0)
			{
				output = "say who: unblock <SteamID64>. 'blocked' shows the list with ids.";
				return false;
			}

			ulong steamId;
			if (!ulong.TryParse(args, NumberStyles.None, CultureInfo.InvariantCulture, out steamId))
			{
				// A label is display text and can repeat; the list is small, so an unambiguous
				// label is accepted as a convenience and anything else points at the id.
				List<Blocklist.Entry> entries = Blocklist.List();
				Blocklist.Entry match = null;
				bool ambiguous = false;
				foreach (Blocklist.Entry entry in entries)
				{
					if (!string.Equals(entry.Label, args, StringComparison.OrdinalIgnoreCase)) continue;
					if (match != null) { ambiguous = true; break; }
					match = entry;
				}
				if (ambiguous)
				{
					output = "more than one blocked entry is labelled \"" + args + "\" - use the SteamID64 from 'blocked'.";
					return false;
				}
				if (match == null)
				{
					output = "nothing on the block list matches \"" + args + "\". 'blocked' shows the list.";
					return false;
				}
				steamId = match.SteamId;
			}

			bool found;
			string failure = Blocklist.Remove(steamId, out found);
			string target = steamId.ToString(CultureInfo.InvariantCulture);
			if (!found)
			{
				output = target + " was not on the block list.";
				return false;
			}
			string auditProblem = OwnerAudit.Record(actor, "unblock", target, failure == null,
				failure ?? "removed from the block list");
			output = failure != null
				? target + " is unblocked for now, but: " + failure
				: target + " is unblocked and can join again.";
			return Finish(failure == null, auditProblem, ref output);
		}

		private static string BlockedList()
		{
			List<Blocklist.Entry> entries = Blocklist.List();
			if (entries.Count == 0) return "the block list is empty";
			StringBuilder builder = new StringBuilder();
			for (int i = 0; i < entries.Count; i++)
			{
				if (i > 0) builder.Append('\n');
				builder.Append(entries[i].SteamId.ToString(CultureInfo.InvariantCulture));
				if (entries[i].Label.Length > 0) builder.Append("  ").Append(entries[i].Label);
				builder.Append("  (blocked ").Append(AgeSentence(entries[i].AddedUnix)).Append(')');
			}
			return builder.ToString();
		}

		private static bool Say(string actor, string args, out string output)
		{
			string failure = null;
			string runFailure;
			bool ran = MainThread.Run(() => { failure = OwnerActions.Broadcast(args); },
				ActionTimeoutMs, out runFailure);
			if (!ran) failure = failure ?? runFailure;

			// The message itself is the detail, so the audit history doubles as a record of
			// what the server told its players.
			string auditProblem = OwnerAudit.Record(actor, "say",
				SteamProfileParser.Sanitize(args, 300), failure == null, failure ?? "broadcast to all players");
			output = failure != null
				? "nothing was broadcast: " + failure
				: "broadcast as [Server] to everyone connected.";
			return Finish(failure == null, auditProblem, ref output);
		}

		private static string AuditTail(string args)
		{
			int count = 20;
			if (args.Length > 0 && !int.TryParse(args, NumberStyles.None, CultureInfo.InvariantCulture, out count))
			{
				return "usage: audit [how many lines, newest last]";
			}
			List<string> lines = OwnerAudit.Tail(count);
			if (lines.Count == 0) return "no owner actions have been recorded on this server yet";
			return string.Join("\n", lines.ToArray());
		}

		// A failed audit write must not fail the action - the kick already happened - but the
		// owner deserves to know their history is not keeping up.
		private static bool Finish(bool ok, string auditProblem, ref string output)
		{
			if (auditProblem != null)
			{
				output += "\n(warning: " + auditProblem + ", so this action is missing from 'audit')";
			}
			return ok;
		}

		private static string Describe(ulong steamId, string name)
		{
			string id = steamId.ToString(CultureInfo.InvariantCulture);
			return string.IsNullOrEmpty(name) ? id : id + " (" + name + ")";
		}

		private static string AgeSentence(long addedUnix)
		{
			long seconds = Math.Max(0, PlayerDirectory.NowUnix() - addedUnix);
			if (seconds < 3600) return (seconds / 60) + "m ago";
			if (seconds < 86400) return (seconds / 3600) + "h ago";
			return (seconds / 86400) + "d ago";
		}

		private static string Help()
		{
			return string.Join("\n", new[]
			{
				"help        this list",
				"status      phase, world, players, uptime",
				"players     who is connected: name, SteamID64, time connected",
				"version     Driftwood host and How to Fish build",
				"world       world name and auto-save interval",
				"save        flush the world to disk now",
				"snapshot    save, then archive the world",
				"snapshots   list the archives on this server",
				"kick <who>  remove a player (SteamID64 or name); they can reconnect",
				"block <who> keep a player out until unblocked (kicks them too)",
				"unblock <id> let a blocked player back in",
				"blocked     the block list, with ids",
				"say <text>  send a [Server] line to every player's chat",
				"audit [n]   the last n owner actions on this server (default 20)",
				"",
				"Blocks key on the SteamID64, never the name - names are display text anybody can change.",
				"Stop and restart live in your hosting panel or supervisor: they flush and back up first."
			});
		}

		private static string Status(Readiness readiness, HostConfig config)
		{
			StringBuilder builder = new StringBuilder();
			builder.Append(readiness.Phase.ToString()).Append(" - ").Append(readiness.Reason).Append('\n');
			builder.Append("world     ").Append(readiness.WorldName).Append('\n');
			builder.Append("players   ");
			if (readiness.WorldRunning) builder.Append(readiness.Players).Append(" of ").Append(readiness.Slots);
			else builder.Append("unknown (the world is not running)");
			builder.Append('\n');
			builder.Append("port      ").Append(readiness.Port).Append(" UDP\n");
			builder.Append("driftwood ").Append(readiness.PluginVersion).Append('\n');
			builder.Append("game      ").Append(readiness.GameVersion.Length == 0 ? "unknown" : readiness.GameVersion).Append('\n');
			builder.Append("blocked   ").Append(Blocklist.Count).Append(" player(s)\n");
			builder.Append("names     ").Append(readiness.SteamNameResolution.Length == 0 ? "(not sampled yet)" : readiness.SteamNameResolution).Append('\n');
			builder.Append("fps       ").Append(readiness.ActualFrameRate.ToString("0.#"))
				.Append(readiness.FrameLimiterActive ? " (capped at " + readiness.EffectiveTargetFrameRate + ")" : " (uncapped)");
			if (config != null && config.PauseWorldWhenEmpty && readiness.WorldPaused) builder.Append("\nworld clock is PAUSED - nobody is connected");
			return builder.ToString();
		}

		// The console is an AUTHENTICATED surface - the caller signed with the server's own
		// secret, or is the panel on loopback - so unlike the public /players route this list
		// carries the SteamID64. It has to: the id is the only honest key for kick and block,
		// and hiding it here would push owners to act on names, which is the mistake the whole
		// identity design exists to prevent.
		private static string Players(Readiness readiness)
		{
			if (!readiness.WorldRunning) return "unknown - the world is not running";
			List<PlayerDirectory.Row> rows = PlayerDirectory.Snapshot();
			if (rows.Count == 0) return "nobody is connected";
			StringBuilder builder = new StringBuilder();
			for (int i = 0; i < rows.Count; i++)
			{
				if (i > 0) builder.Append('\n');
				builder.Append(rows[i].Name)
					.Append("  ").Append(rows[i].SteamId.ToString(CultureInfo.InvariantCulture))
					.Append("  ").Append(Duration(rows[i].ConnectedSeconds));
				if (rows[i].PingMs.HasValue) builder.Append("  ").Append(rows[i].PingMs.Value).Append(" ms");
			}
			return builder.ToString();
		}

		private static string Duration(long seconds)
		{
			if (seconds < 60) return seconds + "s";
			if (seconds < 3600) return (seconds / 60) + "m";
			return (seconds / 3600) + "h" + ((seconds % 3600) / 60) + "m";
		}
	}
}
