using System;
using System.Collections.Generic;
using Steamworks;

namespace DriftwoodHost
{
	// The host runs with no Steam client and no Steam account, so every Steam identity and name
	// lookup has to be answered by us. Two separate jobs, and conflating them is exactly the
	// "default leaking across a boundary" bug (playbook 1d mechanism 5), so they are kept apart:
	//
	//   1. The HOST's own identity. Used only for the loopback connection. Must be stable across
	//      restarts, because SaveManager.GetSavedPlayer(steamID) keys the per-player save on this
	//      ulong - a value derived from the process id would orphan a save on every restart.
	//      It must NEVER be sold as a player or written into a roster.
	//
	//   2. A REMOTE player's display name. The host cannot resolve a SteamID to a name without
	//      Steam. Real clients can and do resolve each other locally, so this only matters where
	//      the SERVER's copy of a name escapes to a client - see DisplayNameLeak below.
	internal static class DriftwoodIdentity
	{
		// Reserved host placeholder. Inside the individual-account SteamID space but far below
		// any issued account id, so it can never collide with a real player.
		public const ulong HostSteamId = 76561190000000001UL;

		// The first id Valve ever issued. Everything below it is one of OURS - the host
		// placeholder or a per-connection spawn identity (see SpawnIdentity) - so the Steam
		// Web API must never be asked about it and no ban/roster logic may treat it as a
		// real account.
		public const ulong FirstRealSteamId = 76561197960265729UL;
		public static bool IsSynthetic(ulong id) => id < FirstRealSteamId;

		private static readonly object Sync = new object();
		private static readonly Dictionary<ulong, string> Names = new Dictionary<ulong, string>();

		// True once every connected player's display name came from a real source rather than a
		// placeholder. Published in the readiness file so the gap is visible instead of silent.
		public static bool AllNamesResolved { get; private set; } = true;

		public static void SetKnownName(ulong steamId, string displayName)
		{
			if (steamId == 0UL || string.IsNullOrEmpty(displayName)) return;
			lock (Sync) Names[steamId] = displayName;
		}

		public static void Forget(ulong steamId)
		{
			lock (Sync) Names.Remove(steamId);
		}

		// True when a REAL name is held for this id - one a client supplied or the Steam Web
		// API resolved - as opposed to the synthetic placeholder ResolveName falls back to.
		public static bool HasKnownName(ulong steamId)
		{
			lock (Sync) return Names.ContainsKey(steamId);
		}

		// The known name or null, never a placeholder. The name cache writer uses this so a
		// placeholder can never be persisted and later served as though it were real.
		public static string KnownNameOrNull(ulong steamId)
		{
			lock (Sync) return Names.TryGetValue(steamId, out string known) ? known : null;
		}

		public static string HostDisplayName = "Server";

		public static string ResolveName(CSteamID id)
		{
			ulong raw = id.m_SteamID;
			if (raw == HostSteamId) return HostDisplayName;
			lock (Sync)
			{
				if (Names.TryGetValue(raw, out string known)) return known;
			}
			AllNamesResolved = false;
			return Placeholder(raw);
		}

		// Deliberately NOT a plausible-looking name. If this string ever reaches a player, it
		// should read as obviously wrong rather than as somebody's actual handle.
		public static string Placeholder(ulong steamId) =>
			steamId == 0UL ? "Player" : "Player-" + (steamId % 10000UL).ToString("D4");
	}
}
