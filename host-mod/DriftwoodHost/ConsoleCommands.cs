using System;
using System.Collections.Generic;
using System.Text;

namespace DriftwoodHost
{
	// The launcher's Console tab.
	//
	// THIS GAME HAS NO ADMIN SURFACE. That is not an omission on our side: the shipped How to
	// Fish build has no admin concept, no ban list and no server console - its cheat commands
	// are local and client-side. The web panel says so in a comment where the admin-UID field
	// would be, and there is deliberately no such field.
	//
	// So the console here is a HOST console, not a game console. Everything it offers is
	// something the host process genuinely knows or can genuinely do, and everything it does
	// not offer answers with a sentence saying where that thing actually lives. A console
	// that silently accepts "ban Steve" and does nothing is worse than one that says it
	// cannot.
	//
	// Lifecycle (stop / restart) is deliberately NOT here. The panel's stop and restart flush
	// the world and take a backup first; a console shortcut past that ordering would be a
	// data-safety regression dressed as a convenience.
	internal static class ConsoleCommands
	{
		// Kept in sync with the launcher's own suggestion list by the cross-repo contract
		// test - a suggestion for a command the server does not implement is a promise the
		// UI makes on the server's behalf.
		internal static readonly string[] Names =
		{
			"help", "status", "players", "version", "world", "save", "snapshot", "snapshots"
		};

		internal static bool Execute(string command, Readiness readiness, HostConfig config, out string output)
		{
			string trimmed = (command ?? string.Empty).Trim();
			if (trimmed.Length == 0)
			{
				output = "type a command, or 'help' for the list";
				return false;
			}

			string verb = trimmed;
			int space = trimmed.IndexOf(' ');
			if (space > 0) verb = trimmed.Substring(0, space);
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

				// Named refusals. Each of these is a thing a player reasonably expects a server
				// console to do, and each answers with where it actually is rather than with
				// "unknown command", which would read as a typo.
				case "stop":
				case "shutdown":
				case "restart":
					output = "lifecycle is not on this console. Stop and restart from your SurvivalServers control panel, " +
						"which flushes the world and takes a backup first - this console would skip both.";
					return false;

				case "kick":
				case "ban":
				case "unban":
				case "op":
				case "admin":
					output = "How to Fish has no admin, kick or ban system in the shipped game, so this server cannot do that. " +
						"Nothing here is disabled - there is nothing to enable.";
					return false;

				case "say":
				case "broadcast":
					output = "this game has no server-to-player chat channel, so there is nothing to broadcast on.";
					return false;

				default:
					output = "unknown command \"" + verb + "\". Try: " + string.Join(", ", Names);
					return false;
			}
		}

		private static string Help()
		{
			return string.Join("\n", new[]
			{
				"help        this list",
				"status      phase, world, players, uptime",
				"players     who is connected and for how long",
				"version     Driftwood host and How to Fish build",
				"world       world name and auto-save interval",
				"save        flush the world to disk now",
				"snapshot    save, then archive the world",
				"snapshots   list the archives on this server",
				"",
				"Stop and restart live in your SurvivalServers control panel: they flush and back up first.",
				"How to Fish itself ships no admin, kick or ban system, so this console has none either."
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
			builder.Append("fps       ").Append(readiness.ActualFrameRate.ToString("0.#"))
				.Append(readiness.FrameLimiterActive ? " (capped at " + readiness.EffectiveTargetFrameRate + ")" : " (uncapped)");
			if (config != null && config.PauseWorldWhenEmpty && readiness.WorldPaused) builder.Append("\nworld clock is PAUSED - nobody is connected");
			return builder.ToString();
		}

		private static string Players(Readiness readiness)
		{
			if (!readiness.WorldRunning) return "unknown - the world is not running";
			List<PlayerDirectory.Row> rows = PlayerDirectory.Snapshot();
			if (rows.Count == 0) return "nobody is connected";
			StringBuilder builder = new StringBuilder();
			for (int i = 0; i < rows.Count; i++)
			{
				if (i > 0) builder.Append('\n');
				builder.Append(rows[i].Name).Append("  ").Append(Duration(rows[i].ConnectedSeconds));
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
