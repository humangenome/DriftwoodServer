using System;
using System.Collections.Generic;
using System.Reflection;

namespace DriftwoodHost
{
	// The roster, kept in TWO shapes on purpose.
	//
	// The panel's /api/v1/status roster is "<steamid64>:<name>" - it is loopback-only, the
	// panel is the customer's own account, and support needs the id to tie a report to a
	// person. The launcher's /api/v1/players roster is names and connect durations and
	// NOTHING ELSE, because that route is public on an open port.
	//
	// They are two payloads, not one payload behind a flag. A flag is a thing that can be
	// wrong; a serializer that never receives the id cannot leak it.
	//
	// The public shape matches the rest of the family byte for byte (Beacon's
	// BeaconHttpService /api/v1/players: name, connected_seconds, ping_ms), because the
	// launcher parsing it is a re-skin of theirs and a field name is a contract.
	internal static class PlayerDirectory
	{
		internal sealed class Row
		{
			internal ulong SteamId;
			internal string Name = string.Empty;
			internal long ConnectedSeconds;
			// Null when this server cannot measure it. See the reflection note below: the
			// launcher renders the row without a ping, and an invented number would be worse
			// than an absent one.
			internal int? PingMs;
		}

		private static readonly object Sync = new object();
		private static readonly Dictionary<ulong, long> FirstSeenUnix = new Dictionary<ulong, long>();
		private static List<Row> _rows = new List<Row>();

		// Resolved once, from whatever the shipped FishNet actually exposes. FishNet's server
		// has no documented per-connection round-trip time, and different builds have carried
		// different names for it, so this looks and then either publishes a real measurement
		// or publishes nothing at all.
		private static bool _pingProbeDone;
		private static PropertyInfo _pingProperty;

		internal static long NowUnix()
		{
			return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
		}

		// Called from the readiness sampler, which is the one place that knows a transport
		// connection from a player. entries carry the raw id and the resolved display name;
		// connection objects are optional and used only for a ping if one can be read.
		internal static void Observe(IList<ulong> steamIds, IList<string> names, IList<object> connections)
		{
			long now = NowUnix();
			List<Row> rows = new List<Row>();
			lock (Sync)
			{
				HashSet<ulong> present = new HashSet<ulong>();
				for (int i = 0; i < steamIds.Count; i++)
				{
					ulong id = steamIds[i];
					present.Add(id);
					long since;
					if (!FirstSeenUnix.TryGetValue(id, out since))
					{
						since = now;
						FirstSeenUnix[id] = since;
					}
					rows.Add(new Row
					{
						SteamId = id,
						Name = i < names.Count ? (names[i] ?? string.Empty) : string.Empty,
						ConnectedSeconds = Math.Max(0, now - since),
						PingMs = connections != null && i < connections.Count ? ReadPing(connections[i]) : null
					});
				}

				// Forget anybody who left, so a returning player's timer restarts instead of
				// reading as though they never disconnected.
				if (FirstSeenUnix.Count > present.Count)
				{
					List<ulong> gone = new List<ulong>();
					foreach (KeyValuePair<ulong, long> pair in FirstSeenUnix)
					{
						if (!present.Contains(pair.Key)) gone.Add(pair.Key);
					}
					foreach (ulong id in gone) FirstSeenUnix.Remove(id);
				}

				_rows = rows;
			}
		}

		// UNKNOWN, not empty. Called on every refusal and every non-running phase, so that a
		// server which is not hosting cannot publish a roster at all.
		internal static void Clear()
		{
			lock (Sync)
			{
				FirstSeenUnix.Clear();
				_rows = new List<Row>();
			}
		}

		internal static List<Row> Snapshot()
		{
			lock (Sync) return new List<Row>(_rows);
		}

		// The panel's shape. Unchanged from what /status has always published.
		internal static List<string> IdentifiedRoster()
		{
			List<string> roster = new List<string>();
			foreach (Row row in Snapshot())
			{
				roster.Add(row.SteamId.ToString() + ":" +
					(string.IsNullOrEmpty(row.Name) ? DriftwoodIdentity.Placeholder(row.SteamId) : row.Name));
			}
			return roster;
		}

		private static int? ReadPing(object connection)
		{
			if (connection == null) return null;
			if (!_pingProbeDone)
			{
				_pingProbeDone = true;
				try
				{
					Type type = connection.GetType();
					// Only accept an int-like property with an unambiguous name. Anything
					// fuzzier risks publishing some unrelated counter as a millisecond figure.
					string[] candidates = { "Ping", "PingMs", "RoundTripTime", "Rtt" };
					foreach (string name in candidates)
					{
						PropertyInfo property = type.GetProperty(name,
							BindingFlags.Public | BindingFlags.Instance);
						if (property == null) continue;
						if (property.PropertyType != typeof(int) &&
							property.PropertyType != typeof(long) &&
							property.PropertyType != typeof(uint) &&
							property.PropertyType != typeof(ushort)) continue;
						_pingProperty = property;
						Plugin.Log?.LogInfo("Per-player ping is available from FishNet's " +
							type.Name + "." + name + " and will be published.");
						break;
					}
					if (_pingProperty == null)
					{
						Plugin.Log?.LogInfo("This build of FishNet exposes no per-connection round-trip time, " +
							"so player rows are published WITHOUT a ping rather than with a made-up one.");
					}
				}
				catch (Exception exception)
				{
					Plugin.Log?.LogDebug("Ping probe failed: " + exception.Message);
				}
			}

			if (_pingProperty == null) return null;
			try
			{
				object value = _pingProperty.GetValue(connection, null);
				if (value == null) return null;
				long ms = Convert.ToInt64(value);
				if (ms <= 0 || ms > 60000) return null;
				return (int)ms;
			}
			catch { return null; }
		}
	}
}
