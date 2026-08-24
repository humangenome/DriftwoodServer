using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DriftwoodHost
{
	// The owner's block list, keyed on SteamID64 AND NOTHING ELSE.
	//
	// How to Fish ships no admin system - no kick, no ban, nothing - so this store is not
	// exposing a hidden game feature, it IS the feature. The identity rule is absolute: a
	// persona name is user-controlled display text that can change hourly and can imitate
	// anybody, so the name stored beside an entry is a label for the owner's benefit and is
	// never consulted by enforcement. Enforcement compares the replicated SteamID64, which the
	// game itself syncs to this server and which SaveManager already trusts to key saves.
	//
	// STORAGE: "<steamid64>\t<addedUnix>\t<label>" per line under the INSTANCE root - beside
	// Snapshots\ and Logs\, outside the game tree SteamCMD owns (a validate must not clear a
	// ban), and deliberately NOT inside Saves\ (a world restore must not roll identity state
	// back with the terrain).
	//
	// Dependency-free (no Unity, no BepInEx) so the xunit suite links this file directly and
	// proves the round-trip without a running game.
	internal static class Blocklist
	{
		// The lowest SteamID64 an individual account can have. Anything below it is a group,
		// a lobby, or a typo, and blocking it would be a no-op wearing a confirmation message.
		internal const ulong FirstIndividualSteamId = 76561197960265728UL;
		internal const int MaxEntries = 500;

		internal sealed class Entry
		{
			internal ulong SteamId;
			internal long AddedUnix;
			internal string Label = string.Empty;
		}

		private static readonly object Sync = new object();
		private static readonly Dictionary<ulong, Entry> Entries = new Dictionary<ulong, Entry>();
		private static string _path = string.Empty;

		// Never throws: a blocklist that cannot load starts empty and says so via the return
		// value, and the caller decides how loudly to report it. It must not stop a boot.
		internal static string Initialise(string instanceRoot, string fallbackDirectory)
		{
			string directory = !string.IsNullOrWhiteSpace(instanceRoot)
				? Path.Combine(instanceRoot.Trim(), "Driftwood")
				: (fallbackDirectory ?? string.Empty);
			lock (Sync)
			{
				Entries.Clear();
				_path = string.IsNullOrEmpty(directory) ? string.Empty : Path.Combine(directory, "blocklist.txt");
			}
			if (string.IsNullOrEmpty(_path)) return "the blocklist has nowhere to live (no instance root and no state directory)";
			try
			{
				if (!File.Exists(_path)) return null;
				foreach (string line in File.ReadAllLines(_path))
				{
					string[] parts = line.Split('\t');
					if (parts.Length < 2) continue;
					if (!ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) continue;
					if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long added)) continue;
					if (id < FirstIndividualSteamId) continue;
					lock (Sync)
					{
						Entries[id] = new Entry
						{
							SteamId = id,
							AddedUnix = added,
							Label = parts.Length > 2 ? SteamProfileParser.Sanitize(parts[2]) : string.Empty
						};
					}
				}
				return null;
			}
			catch (Exception exception)
			{
				return "the blocklist could not be read (" + exception.GetType().Name + "); starting with an empty one";
			}
		}

		internal static bool IsBlocked(ulong steamId)
		{
			lock (Sync) return Entries.ContainsKey(steamId);
		}

		internal static int Count
		{
			get { lock (Sync) return Entries.Count; }
		}

		// Returns null on success, or one plain sentence. The label is display-only.
		internal static string Add(ulong steamId, string label, long nowUnix)
		{
			// This floor also rejects the host's own reserved id (76561190000000001), which sits
			// deliberately below the individual-account space - so the server's internal
			// connection can never be blocked, without this file having to know about it.
			if (steamId < FirstIndividualSteamId)
				return steamId.ToString(CultureInfo.InvariantCulture) + " is not an individual account's SteamID64, so blocking it would do nothing.";
			lock (Sync)
			{
				if (Entries.ContainsKey(steamId)) return null;
				if (Entries.Count >= MaxEntries)
					return "the blocklist is full (" + MaxEntries + " entries). Remove one before adding another.";
				Entries[steamId] = new Entry
				{
					SteamId = steamId,
					AddedUnix = nowUnix,
					Label = SteamProfileParser.Sanitize(label ?? string.Empty)
				};
			}
			return Persist();
		}

		// Returns null on success (including "was not blocked", reported via found).
		internal static string Remove(ulong steamId, out bool found)
		{
			lock (Sync) found = Entries.Remove(steamId);
			return found ? Persist() : null;
		}

		internal static List<Entry> List()
		{
			lock (Sync)
			{
				List<Entry> list = new List<Entry>(Entries.Values);
				list.Sort((a, b) => b.AddedUnix.CompareTo(a.AddedUnix));
				return list;
			}
		}

		// Atomic write, because a block that silently fails to persist un-bans somebody on the
		// next restart. A persist failure is REPORTED to the caller - the in-memory block still
		// holds for this run, and the owner is told it will not survive a restart.
		private static string Persist()
		{
			string path;
			StringBuilder builder = new StringBuilder();
			lock (Sync)
			{
				path = _path;
				foreach (Entry entry in Entries.Values)
				{
					builder.Append(entry.SteamId.ToString(CultureInfo.InvariantCulture)).Append('\t')
						.Append(entry.AddedUnix.ToString(CultureInfo.InvariantCulture)).Append('\t')
						.Append(entry.Label).Append('\n');
				}
			}
			if (string.IsNullOrEmpty(path)) return "the change is in force now but has nowhere to be saved, so it will not survive a restart.";
			try
			{
				string directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
				string temporary = path + ".tmp";
				File.WriteAllText(temporary, builder.ToString(), new UTF8Encoding(false));
				if (File.Exists(path)) File.Delete(path);
				File.Move(temporary, path);
				return null;
			}
			catch (Exception exception)
			{
				return "the change is in force now but could not be saved (" + exception.GetType().Name +
					"), so it will not survive a restart.";
			}
		}
	}
}
