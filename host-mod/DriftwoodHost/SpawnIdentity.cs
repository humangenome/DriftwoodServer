using System;
using System.Collections.Generic;
using FishNet.Connection;
using HarmonyLib;

namespace DriftwoodHost
{
	// WHO A SPAWNING PLAYER IS, on a game build that stopped asking them.
	//
	// Game 1.0.4/1.0.5: SpawnPlayer(NetworkConnection owner, ulong steamID, ...) - the CLIENT
	// sent its own SteamID64 and the server keyed the Player SyncVar, the per-player save and
	// every identity feature on it.
	//
	// Game 1.0.6 REMOVED that parameter. The server now derives identity itself:
	//
	//     if (ConnectionManager.IsUsingSteam)  -> parse the sender's transport address as a
	//                                             SteamID (FishySteamworks addresses ARE ids)
	//     else                                 -> SteamUser.GetSteamID()   // THE SERVER'S OWN
	//
	// Our servers run the non-Steam path, and DriftwoodHost guards SteamUser.GetSteamID to
	// return the reserved host placeholder. So on 1.0.6 EVERY joining player spawned as the
	// host: one shared SavedPlayer record for the whole crew, and the roster's ghost-host
	// filter (`steamId == HostSteamId -> skip`) dropped every real player. Proven live on the
	// canary 2026-08-25: a player standing in the world, count 1, roster [], and a save whose
	// only identity ever was 76561190000000001.
	//
	// The real SteamID never crosses the wire on this path any more, so the server CANNOT know
	// who a joiner really is (that needs launcher help, a separate lane). What the identity
	// features actually require is DISTINCTNESS: every connection its own id, never the
	// host's. So: while the game's own SpawnPlayer RPC reader is executing for a REMOTE
	// connection, the GetSteamID guard answers a per-connection synthetic id instead of the
	// host placeholder. The ids live in the same reserved sub-account space as the host
	// placeholder (far below any id Valve ever issued), so they can never collide with a real
	// player, and the name resolver knows to skip the whole range rather than asking the Steam
	// Web API about ids that do not exist.
	internal static class SpawnIdentity
	{
		// The connection whose SpawnPlayer request is being processed RIGHT NOW. FishNet reads
		// inbound RPCs on the main thread, so a plain field is enough; the prefix sets it and
		// the finalizer clears it even when the reader throws.
		private static NetworkConnection _spawning;

		// Base of the per-connection id range. HostSteamId is ...0000001; this starts at
		// ...0100000 so the two can never meet (ClientIds are small non-negative ints).
		private const ulong SyntheticBase = 76561190000100000UL;

		internal static IEnumerable<PatchTarget> Targets()
		{
			// The reader's name carries a codegen hash that moves when the game rebuilds
			// (596900633 on 1.0.4, 1871804056 on 1.0.6), so it is resolved by PREFIX and the
			// resolution refuses on ambiguity. Optional + grouped: without it the server still
			// hosts and players still play - identity degrades back to the shared-host-id
			// defect, which the readiness document then names in featuresStoodDown.
			yield return new PatchTarget
			{
				TypeName = "Server",
				MethodName = "RpcReader___SpawnPlayer___*",
				MethodNamePrefix = "RpcReader___SpawnPlayer___",
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = "spawn-identity",
				Prefix = AccessTools.Method(typeof(SpawnIdentity), nameof(ReaderPrefix)),
				Finalizer = AccessTools.Method(typeof(SpawnIdentity), nameof(ReaderFinalizer)),
				Why = "1.0.6 assigns a joining player's SteamID SERVER-side via SteamUser.GetSteamID; without spawn context every player becomes the host placeholder and vanishes from the roster, the map, kick and the blocklist."
			};
		}

		// Bound via __args rather than parameter names, so a codegen rename of the reader's
		// parameters cannot silently unbind the patch. Every deref guarded: this sits on the
		// live join path, where a throw would cost a player their spawn.
		private static void ReaderPrefix(object[] __args)
		{
			try
			{
				if (__args == null) return;
				for (int i = 0; i < __args.Length; i++)
				{
					NetworkConnection connection = __args[i] as NetworkConnection;
					if (connection != null)
					{
						_spawning = connection;
						return;
					}
				}
			}
			catch { }
		}

		private static Exception ReaderFinalizer(Exception __exception)
		{
			_spawning = null;
			// Never swallow the game's own exception - identity context is bookkeeping, not
			// error handling.
			return __exception;
		}

		// What SteamUser.GetSteamID should answer right now. The host placeholder, unless the
		// game is mid-way through spawning a REMOTE player - then that connection's own
		// synthetic id, so the crew stays distinguishable.
		internal static ulong CurrentIdentity()
		{
			try
			{
				NetworkConnection connection = _spawning;
				if (connection != null && !connection.IsLocalClient)
				{
					int clientId = connection.ClientId;
					if (clientId >= 0) return SyntheticBase + (ulong)clientId;
				}
			}
			catch { }
			return DriftwoodIdentity.HostSteamId;
		}
	}
}
