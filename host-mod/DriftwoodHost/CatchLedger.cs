using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DriftwoodHost
{
	// The catch leaderboard: per player, how many fish they landed, what they earned, the
	// bosses they finished, their best catch and their time on this server. Keyed on the
	// SteamID64 the game itself replicates to this server; the name beside it is display-only
	// and is refreshed from the roster every time the player is seen.
	//
	// WHY THIS CAN BE HONEST. The game attributes on the server, in three places this ledger
	// listens to (CatchHooks.cs): the bite is decided server-side (CreatureManager.HookItem
	// runs only on the server, and ties the new fish to the rod's holder), the landing is a
	// server-side holder write (Item.SetSyncedHolder), and the sale is a server-side trigger
	// (SellBox -> MoneyManager.SellItem). Nothing here is a client's claim about itself.
	//
	// WHERE IT LIVES. One tab-separated file per world in the SAVE DIRECTORY, beside the
	// world's own save: <SaveRoot>\<world>.leaderboard.tsv. Deliberately WITH the world, unlike
	// the block list (which sits outside Saves\ so a restore cannot roll a ban back): the
	// ledger is world history, and a world restored to last Tuesday should show last Tuesday's
	// leaderboard, the same way it shows last Tuesday's money. Snapshots and the hosting
	// panel's backups already archive that directory, so the ledger rides along for free. The
	// game's own save loader only reads *.txt from that folder, so a .tsv beside the saves is
	// invisible to it.
	//
	// This file has NO Unity, BepInEx or game dependency, so the xunit suite links it and
	// proves the arithmetic, the ordering and the round trip through disk. Logging goes through
	// the settable delegate below; Plugin wires it at boot.
	internal static class CatchLedger
	{
		internal sealed class Entry
		{
			internal ulong SteamId;
			internal string Name = string.Empty;
			internal int Catches;
			internal long Earnings;
			internal int Bosses;
			internal long PlaytimeSeconds;
			internal string BestCatchName = string.Empty;
			internal int BestCatchWorth;
			internal long FirstSeenUnix;
			internal long LastSeenUnix;

			internal Entry Copy()
			{
				return new Entry
				{
					SteamId = SteamId, Name = Name, Catches = Catches, Earnings = Earnings, Bosses = Bosses,
					PlaytimeSeconds = PlaytimeSeconds, BestCatchName = BestCatchName, BestCatchWorth = BestCatchWorth,
					FirstSeenUnix = FirstSeenUnix, LastSeenUnix = LastSeenUnix
				};
			}
		}

		internal const string FileSuffix = ".leaderboard.tsv";
		// Enough for any real server's lifetime of visitors; refuses (loudly, once) beyond it so
		// a hostile id spray cannot grow the file without bound.
		internal const int MaxEntries = 2000;
		private const int NameCap = 64;
		private const int CreatureNameCap = 48;

		// The host's own reserved loopback identity (DriftwoodIdentity.HostSteamId). Duplicated
		// here as a literal because that class binds Steamworks, and this file must stay
		// linkable into the dependency-free test build.
		internal const ulong HostSteamId = 76561190000000001UL;

		// The board refuses every id below the first SteamID64 Valve ever issued - the host
		// placeholder AND the per-connection synthetic range (SpawnIdentity.cs). A synthetic id
		// is a connection SLOT, and FishNet reuses slots: a row keyed on one would migrate to
		// whoever lands that slot next, and a leaderboard that credits the wrong player is
		// worse than none. So an unmodded player (no identity claim, no launcher-supplied id)
		// gets no row at all rather than a row that will one day belong to a stranger; the
		// moment their client claims a real id (IdentityClaims.cs), their rows are theirs for
		// good. The constant is IdentityClaimRules.FirstRealSteamId, a file as dependency-free
		// as this one.

		// Explicitly null-initialised: in the linked test build nothing assigns them.
		internal static Action<string> LogWarning = null;
		internal static Action<string> LogInfo = null;

		private static readonly object Sync = new object();
		private static readonly Dictionary<ulong, Entry> Entries = new Dictionary<ulong, Entry>();
		private static readonly Dictionary<ulong, long> LastObservedUnix = new Dictionary<ulong, long>();
		private static string _path = string.Empty;
		private static string _world = string.Empty;
		private static bool _dirty;
		private static bool _capWarned;
		private static bool _enabled;

		internal static bool Enabled => _enabled;
		internal static string Path_ => _path;

		internal static int Count
		{
			get { lock (Sync) return Entries.Count; }
		}

		// The characters Windows refuses in a file name, spelled out rather than asked of the
		// runtime: the product runs on Windows and the tests run wherever the developer is,
		// and a name that is safe on one and not the other is the kind of gap that ships.
		private const string InvalidFileNameChars = "\\/:*?\"<>|";

		internal static string PathFor(string saveDirectory, string worldName)
		{
			StringBuilder safe = new StringBuilder();
			foreach (char c in (worldName ?? string.Empty).Trim())
			{
				safe.Append(c < ' ' || c == (char)0x7f || InvalidFileNameChars.IndexOf(c) >= 0 ? '_' : c);
			}
			string safeWorld = safe.ToString();
			if (safeWorld.Length == 0) safeWorld = "world";
			return Path.Combine((saveDirectory ?? string.Empty).Trim(), safeWorld + FileSuffix);
		}

		// Loads whatever is on disk. A missing file is the normal first-boot state. A
		// damaged file is reported and skipped line by line, never treated as "no history":
		// the lines that parse are kept, and the next flush rewrites the file cleanly.
		internal static string Initialise(string saveDirectory, string worldName)
		{
			lock (Sync)
			{
				Entries.Clear();
				LastObservedUnix.Clear();
				_dirty = false;
				_capWarned = false;
				_world = (worldName ?? string.Empty).Trim();
				_path = string.IsNullOrWhiteSpace(saveDirectory) ? string.Empty : PathFor(saveDirectory, worldName);
				_enabled = _path.Length > 0;
				if (!_enabled) return "the leaderboard has nowhere to live (no save directory)";
				if (!File.Exists(_path)) return null;
				try
				{
					string text = File.ReadAllText(_path);
					int bad;
					List<Entry> loaded = Parse(text, out bad);
					foreach (Entry entry in loaded) Entries[entry.SteamId] = entry;
					if (bad > 0)
					{
						return bad + " line(s) of " + _path + " could not be read and were skipped; the rest loaded";
					}
					return null;
				}
				catch (Exception exception)
				{
					return "the leaderboard file could not be read (" + exception.GetType().Name + ": " + exception.Message + ")";
				}
			}
		}

		// ------------------------------------------------------------------
		// Recording. Every method is cheap, lock-guarded and safe from any thread, and an id
		// below the first real SteamID64 - zero, the host, a synthetic connection slot - is
		// ignored rather than becoming a row (see the note on HostSteamId above).
		// ------------------------------------------------------------------

		internal static void RecordCatch(ulong steamId, string name, string creatureName, int worth, long nowUnix)
		{
			if (!_enabled) return;
			lock (Sync)
			{
				Entry entry = Touch(steamId, name, nowUnix);
				if (entry == null) return;
				entry.Catches++;
				string creature = Clean(creatureName, CreatureNameCap);
				if (worth > entry.BestCatchWorth || (entry.BestCatchName.Length == 0 && creature.Length > 0))
				{
					entry.BestCatchWorth = Math.Max(worth, entry.BestCatchWorth);
					if (creature.Length > 0) entry.BestCatchName = creature;
				}
				_dirty = true;
			}
		}

		internal static void RecordSale(ulong steamId, string name, int worth, long nowUnix)
		{
			if (!_enabled || worth <= 0) return;
			lock (Sync)
			{
				Entry entry = Touch(steamId, name, nowUnix);
				if (entry == null) return;
				entry.Earnings += worth;
				_dirty = true;
			}
		}

		internal static void RecordBoss(ulong steamId, string name, long nowUnix)
		{
			if (!_enabled) return;
			lock (Sync)
			{
				Entry entry = Touch(steamId, name, nowUnix);
				if (entry == null) return;
				entry.Bosses++;
				_dirty = true;
			}
		}

		// Called with the connected roster on every readiness sample. Credits each present
		// player with the seconds since they were last seen present, so playtime is measured
		// by this server's clock and never by anything a client says. The first sighting after
		// a join sets the baseline and credits nothing; a player who leaves is forgotten here
		// and starts a fresh baseline on return.
		internal static void ObservePlaytime(IList<ulong> steamIds, IList<string> names, long nowUnix)
		{
			if (!_enabled || steamIds == null) return;
			lock (Sync)
			{
				HashSet<ulong> present = new HashSet<ulong>();
				for (int i = 0; i < steamIds.Count; i++)
				{
					ulong id = steamIds[i];
					if (id == 0UL) continue;
					present.Add(id);
					string name = names != null && i < names.Count ? names[i] : null;
					long last;
					if (LastObservedUnix.TryGetValue(id, out last))
					{
						long delta = nowUnix - last;
						// A stalled sampler (a paused world, a long frame) must not credit hours
						// in one step; anything beyond a generous sample gap is treated as a gap.
						if (delta > 0 && delta <= 60)
						{
							Entry entry = Touch(id, name, nowUnix);
							if (entry != null)
							{
								entry.PlaytimeSeconds += delta;
								_dirty = true;
							}
						}
						else if (delta > 60)
						{
							Touch(id, name, nowUnix);
						}
					}
					else
					{
						Touch(id, name, nowUnix);
					}
					LastObservedUnix[id] = nowUnix;
				}
				if (LastObservedUnix.Count > present.Count)
				{
					List<ulong> gone = new List<ulong>();
					foreach (ulong id in LastObservedUnix.Keys)
					{
						if (!present.Contains(id)) gone.Add(id);
					}
					foreach (ulong id in gone) LastObservedUnix.Remove(id);
				}
			}
		}

		// ------------------------------------------------------------------
		// Reading
		// ------------------------------------------------------------------

		internal static Entry Get(ulong steamId)
		{
			lock (Sync)
			{
				Entry entry;
				return Entries.TryGetValue(steamId, out entry) ? entry.Copy() : null;
			}
		}

		// Ranked by earnings, then catches, then bosses, then playtime; the name breaks the
		// last tie so the order is stable across two reads.
		internal static List<Entry> Top(int count)
		{
			count = Math.Max(1, Math.Min(100, count));
			List<Entry> all;
			lock (Sync)
			{
				all = new List<Entry>(Entries.Count);
				foreach (Entry entry in Entries.Values) all.Add(entry.Copy());
			}
			all.Sort(Compare);
			if (all.Count > count) all.RemoveRange(count, all.Count - count);
			return all;
		}

		// 1-based rank of a player, or 0 when they have no row.
		internal static int RankOf(ulong steamId)
		{
			List<Entry> all;
			lock (Sync)
			{
				if (!Entries.ContainsKey(steamId)) return 0;
				all = new List<Entry>(Entries.Count);
				foreach (Entry entry in Entries.Values) all.Add(entry.Copy());
			}
			all.Sort(Compare);
			for (int i = 0; i < all.Count; i++)
			{
				if (all[i].SteamId == steamId) return i + 1;
			}
			return 0;
		}

		private static int Compare(Entry a, Entry b)
		{
			int byEarnings = b.Earnings.CompareTo(a.Earnings);
			if (byEarnings != 0) return byEarnings;
			int byCatches = b.Catches.CompareTo(a.Catches);
			if (byCatches != 0) return byCatches;
			int byBosses = b.Bosses.CompareTo(a.Bosses);
			if (byBosses != 0) return byBosses;
			int byPlaytime = b.PlaytimeSeconds.CompareTo(a.PlaytimeSeconds);
			if (byPlaytime != 0) return byPlaytime;
			return string.CompareOrdinal(a.Name, b.Name);
		}

		// The JSON array the readiness document and /status carry. Built here so the two
		// serializers cannot disagree about the shape. Ids are included: this reaches only the
		// loopback/signed status surface, never the public /players route.
		internal static string TopJson(int count)
		{
			List<Entry> top = Top(count);
			StringBuilder builder = new StringBuilder("[");
			for (int i = 0; i < top.Count; i++)
			{
				if (i > 0) builder.Append(',');
				Entry entry = top[i];
				builder.Append(Json.Object()
					.Add("rank", i + 1)
					.Add("steamId", entry.SteamId.ToString(CultureInfo.InvariantCulture))
					.Add("name", entry.Name)
					.Add("catches", entry.Catches)
					.Add("earnings", entry.Earnings)
					.Add("bosses", entry.Bosses)
					.Add("playtimeSeconds", entry.PlaytimeSeconds)
					.Add("bestCatch", entry.BestCatchName)
					.Add("bestCatchWorth", entry.BestCatchWorth)
					.Add("lastSeenUnix", entry.LastSeenUnix)
					.Close());
			}
			builder.Append(']');
			return builder.ToString();
		}

		// ------------------------------------------------------------------
		// Persistence. Atomic (temp file, then move) so a crash mid-write leaves the previous
		// file, never half of a new one. Best-effort by contract: a full disk costs the
		// leaderboard, never the world.
		// ------------------------------------------------------------------

		internal static bool Dirty
		{
			get { lock (Sync) return _dirty; }
		}

		internal static string FlushIfDirty()
		{
			lock (Sync)
			{
				if (!_enabled || !_dirty) return null;
				string problem = WriteNow();
				if (problem == null) _dirty = false;
				return problem;
			}
		}

		internal static string Flush()
		{
			lock (Sync)
			{
				if (!_enabled) return null;
				string problem = WriteNow();
				if (problem == null) _dirty = false;
				return problem;
			}
		}

		private static string WriteNow()
		{
			try
			{
				string directory = Path.GetDirectoryName(_path);
				if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
				string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
				try
				{
					File.WriteAllText(temporary, Serialize(), new UTF8Encoding(false));
					if (File.Exists(_path)) File.Delete(_path);
					File.Move(temporary, _path);
				}
				finally
				{
					try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
				}
				return null;
			}
			catch (Exception exception)
			{
				return "the leaderboard could not be written (" + exception.GetType().Name + ": " + exception.Message + ")";
			}
		}

		// One row per player, tab-separated, a # header so the file explains itself to a
		// customer who opens it over FTP. Columns are positional; a future column goes on the
		// END so an older file still parses.
		internal static string Serialize()
		{
			StringBuilder builder = new StringBuilder();
			builder.Append("# Driftwood catch leaderboard for world \"").Append(_world).Append("\"\n");
			builder.Append("# steamid64\tname\tcatches\tearnings\tbosses\tplaytime_seconds\tbest_catch\tbest_catch_worth\tfirst_seen_unix\tlast_seen_unix\n");
			List<Entry> rows;
			lock (Sync)
			{
				rows = new List<Entry>(Entries.Values);
			}
			rows.Sort(Compare);
			foreach (Entry entry in rows)
			{
				builder.Append(entry.SteamId.ToString(CultureInfo.InvariantCulture)).Append('\t')
					.Append(entry.Name).Append('\t')
					.Append(entry.Catches.ToString(CultureInfo.InvariantCulture)).Append('\t')
					.Append(entry.Earnings.ToString(CultureInfo.InvariantCulture)).Append('\t')
					.Append(entry.Bosses.ToString(CultureInfo.InvariantCulture)).Append('\t')
					.Append(entry.PlaytimeSeconds.ToString(CultureInfo.InvariantCulture)).Append('\t')
					.Append(entry.BestCatchName).Append('\t')
					.Append(entry.BestCatchWorth.ToString(CultureInfo.InvariantCulture)).Append('\t')
					.Append(entry.FirstSeenUnix.ToString(CultureInfo.InvariantCulture)).Append('\t')
					.Append(entry.LastSeenUnix.ToString(CultureInfo.InvariantCulture)).Append('\n');
			}
			return builder.ToString();
		}

		internal static List<Entry> Parse(string text, out int badLines)
		{
			badLines = 0;
			List<Entry> entries = new List<Entry>();
			if (string.IsNullOrEmpty(text)) return entries;
			foreach (string raw in text.Split('\n'))
			{
				string line = raw.TrimEnd('\r');
				if (line.Length == 0 || line[0] == '#') continue;
				string[] columns = line.Split('\t');
				ulong id;
				if (columns.Length < 6 ||
					!ulong.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0UL)
				{
					badLines++;
					continue;
				}
				Entry entry = new Entry { SteamId = id, Name = Clean(columns[1], NameCap) };
				int catches, bosses, bestWorth;
				long earnings, playtime, first, last;
				if (!int.TryParse(columns[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out catches) ||
					!long.TryParse(columns[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out earnings) ||
					!int.TryParse(columns[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bosses) ||
					!long.TryParse(columns[5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out playtime))
				{
					badLines++;
					continue;
				}
				entry.Catches = Math.Max(0, catches);
				entry.Earnings = Math.Max(0, earnings);
				entry.Bosses = Math.Max(0, bosses);
				entry.PlaytimeSeconds = Math.Max(0, playtime);
				if (columns.Length > 6) entry.BestCatchName = Clean(columns[6], CreatureNameCap);
				if (columns.Length > 7 && int.TryParse(columns[7], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bestWorth))
					entry.BestCatchWorth = Math.Max(0, bestWorth);
				if (columns.Length > 8 && long.TryParse(columns[8], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out first))
					entry.FirstSeenUnix = first;
				if (columns.Length > 9 && long.TryParse(columns[9], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out last))
					entry.LastSeenUnix = last;
				entries.Add(entry);
			}
			return entries;
		}

		// Under the lock. Finds or creates the row, refreshes the display name (a real name
		// beats a placeholder, and a newer real name beats an older one) and the last-seen
		// stamp. Returns null for ids that must never become rows.
		private static Entry Touch(ulong steamId, string name, long nowUnix)
		{
			if (steamId < IdentityClaimRules.FirstRealSteamId) return null;
			Entry entry;
			if (!Entries.TryGetValue(steamId, out entry))
			{
				if (Entries.Count >= MaxEntries)
				{
					if (!_capWarned)
					{
						_capWarned = true;
						LogWarning?.Invoke("The catch leaderboard holds " + MaxEntries +
							" players and will not add more. That is far beyond any real server; if this is one, the file can be trimmed by hand.");
					}
					return null;
				}
				entry = new Entry { SteamId = steamId, FirstSeenUnix = nowUnix };
				Entries[steamId] = entry;
				_dirty = true;
			}
			string clean = Clean(name, NameCap);
			if (clean.Length > 0 && !IsPlaceholder(clean, steamId) && clean != entry.Name)
			{
				entry.Name = clean;
				_dirty = true;
			}
			else if (entry.Name.Length == 0 && clean.Length > 0)
			{
				entry.Name = clean;
				_dirty = true;
			}
			if (nowUnix > entry.LastSeenUnix)
			{
				entry.LastSeenUnix = nowUnix;
				// Not marked dirty on its own: the playtime credit that accompanies it does.
			}
			return entry;
		}

		// Mirrors DriftwoodIdentity.Placeholder without binding that class (see HostSteamId).
		private static bool IsPlaceholder(string name, ulong steamId)
		{
			return name == "Player" || name == "Player-" + (steamId % 10000UL).ToString("D4", CultureInfo.InvariantCulture);
		}

		// Tabs and newlines are this file's structure, and every name here is player-chosen.
		private static string Clean(string value, int cap)
		{
			if (string.IsNullOrEmpty(value)) return string.Empty;
			string clean = SteamProfileParser.Sanitize(value, cap);
			return clean.Replace("(Clone)", string.Empty).Trim();
		}
	}
}
