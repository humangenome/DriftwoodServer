using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Managing.Server;

namespace DriftwoodHost
{
	// THE LIVE HALF OF IDENTITY CLAIMS - the table that binds a validated real
	// SteamID64 to one live connection, consulted by SpawnIdentity at the moment the
	// game asks who a spawning player is. The rules half (everything provable without
	// a connection) lives in IdentityClaimRules.cs; the HTTP route that feeds this
	// lives in HostHttpApi.cs.
	//
	// EVERY method here runs on the MAIN THREAD. Submit is dispatched through
	// MainThread.Run by the HTTP handler; IdentityFor is called from inside the
	// game's own RPC reader; Prune from the readiness sampler. That is what makes a
	// plain dictionary under a lock sufficient - the lock is for the HTTP thread's
	// timeout path, not for concurrency between these methods.
	//
	// A claim binds to the CONNECTION OBJECT, not to the client id number. FishNet
	// can hand a departed player's id to a later joiner, and a claim that outlived
	// its connection would then hand the newcomer somebody else's identity - and with
	// it somebody else's saved character. ReferenceEquals cannot be recycled.
	internal static class IdentityClaims
	{
		private sealed class Claim
		{
			internal NetworkConnection Connection;
			internal ulong SteamId;
		}

		private static readonly object Sync = new object();
		private static readonly Dictionary<int, Claim> ByClientId = new Dictionary<int, Claim>();

		// More live claims than the server has slots is not a crew, it is a flood.
		private const int MaxClaims = 64;

		// MAIN THREAD. Everything about the claim is attacker-controlled; the refusal
		// string is for the LOG only and never leaves this box (the HTTP route answers
		// the same way whether a claim was accepted or refused, so a probe learns
		// nothing about who is aboard).
		internal static bool Submit(int clientId, ulong steamId, string displayName, string httpAddress, out string refusal)
		{
			refusal = null;
			if (!IdentityClaimRules.IsClaimableSteamId(steamId))
			{
				refusal = "the id is not an issuable individual SteamID64";
				return false;
			}

			ServerManager server = InstanceFinder.ServerManager;
			if (server == null || server.Clients == null)
			{
				refusal = "no server";
				return false;
			}

			NetworkConnection connection;
			if (!server.Clients.TryGetValue(clientId, out connection) || connection == null || !connection.IsActive)
			{
				refusal = "no live connection with that id";
				return false;
			}
			if (connection.IsLocalClient)
			{
				refusal = "the host's own loopback connection cannot carry a claim";
				return false;
			}

			// The binding check: the claim must arrive from the same address the
			// claimed connection plays from. Fails closed when either side is
			// unreadable - degraded to the synthetic fallback, never to an unverified
			// claim.
			string transportAddress = null;
			try { transportAddress = connection.GetAddress(); } catch { }
			if (!IdentityClaimRules.AddressesMatch(transportAddress, httpAddress))
			{
				refusal = "the claim did not come from the address that connection plays from";
				return false;
			}

			lock (Sync)
			{
				PruneLocked();

				Claim existing;
				if (ByClientId.TryGetValue(clientId, out existing) && ReferenceEquals(existing.Connection, connection))
				{
					// The client retries its claim; an identical resubmit is the
					// success it already had. A DIFFERENT id on the same connection is
					// refused: first valid claim wins, for the whole connection's life,
					// so a claim cannot be swapped underneath a spawned character.
					if (existing.SteamId == steamId)
					{
						RememberName(steamId, displayName);
						return true;
					}
					refusal = "this connection already claimed a different id";
					return false;
				}

				// One person, one presence: a claim may not duplicate an id that is
				// already aboard on another live connection - claimed or spawned.
				foreach (KeyValuePair<int, Claim> pair in ByClientId)
				{
					if (pair.Value.SteamId == steamId && !ReferenceEquals(pair.Value.Connection, connection))
					{
						refusal = "that id is already claimed by another connection";
						return false;
					}
				}
				if (SpawnedElsewhere(steamId, connection))
				{
					refusal = "a player with that id is already aboard";
					return false;
				}

				if (ByClientId.Count >= MaxClaims)
				{
					refusal = "too many pending claims";
					return false;
				}

				ByClientId[clientId] = new Claim { Connection = connection, SteamId = steamId };
			}

			RememberName(steamId, displayName);

			// A claim that arrives AFTER this connection's player spawned cannot re-key
			// the spawn (the save record is already keyed; swapping identity under a
			// live character is exactly the corruption this table exists to prevent).
			// What it can still honestly do is put the person's name on the synthetic
			// identity they are playing under, so the roster shows who they are.
			try
			{
				Player already = ServerRoster.OwnedPlayer(connection);
				if (already != null)
				{
					ulong current = already.SteamID;
					if (current != 0UL && DriftwoodIdentity.IsSynthetic(current) &&
						current != DriftwoodIdentity.HostSteamId && !string.IsNullOrEmpty(displayName))
					{
						DriftwoodIdentity.SetKnownName(current, displayName);
					}
				}
			}
			catch { }

			Plugin.Log?.LogInfo("Identity claim accepted for connection " + clientId +
				(string.IsNullOrEmpty(displayName) ? "" : " (\"" + displayName + "\")") + ".");
			return true;
		}

		// MAIN THREAD, from inside the game's SpawnPlayer reader via
		// SpawnIdentity.CurrentIdentity. Zero when this connection carries no usable
		// claim - the caller then falls back to the synthetic per-connection id.
		internal static ulong IdentityFor(NetworkConnection connection)
		{
			if (connection == null) return 0UL;
			lock (Sync)
			{
				Claim claim;
				if (!ByClientId.TryGetValue(connection.ClientId, out claim)) return 0UL;
				if (!ReferenceEquals(claim.Connection, connection)) return 0UL;
				return claim.SteamId;
			}
		}

		// MAIN THREAD, from the readiness sampler. Hygiene only - correctness never
		// depends on pruning, because IdentityFor checks the connection object itself.
		internal static void Prune()
		{
			lock (Sync) PruneLocked();
		}

		internal static int Count
		{
			get { lock (Sync) return ByClientId.Count; }
		}

		// TESTS AND SHUTDOWN ONLY.
		internal static void Clear()
		{
			lock (Sync) ByClientId.Clear();
		}

		private static void PruneLocked()
		{
			if (ByClientId.Count == 0) return;
			List<int> gone = null;
			foreach (KeyValuePair<int, Claim> pair in ByClientId)
			{
				bool alive = false;
				try { alive = pair.Value.Connection != null && pair.Value.Connection.IsActive; }
				catch { }
				if (alive) continue;
				if (gone == null) gone = new List<int>();
				gone.Add(pair.Key);
			}
			if (gone == null) return;
			foreach (int clientId in gone) ByClientId.Remove(clientId);
		}

		// A spawned player already carrying this id on a DIFFERENT connection means
		// the id is taken. Walks the same roster seam everything else reads.
		private static bool SpawnedElsewhere(ulong steamId, NetworkConnection claimant)
		{
			try
			{
				ServerManager server = InstanceFinder.ServerManager;
				if (server == null || server.Clients == null) return false;
				foreach (NetworkConnection other in server.Clients.Values)
				{
					if (other == null || ReferenceEquals(other, claimant)) continue;
					Player player = null;
					try { player = ServerRoster.OwnedPlayer(other); } catch { }
					if (player == null) continue;
					try { if (player.SteamID == steamId) return true; } catch { }
				}
			}
			catch { }
			return false;
		}

		private static void RememberName(ulong steamId, string displayName)
		{
			// Display only, already sanitised by the HTTP layer. The Steam Web API
			// resolver remains the authority: when a key is configured it overwrites
			// this with the account's actual persona name on its next pass, so a
			// lying name self-corrects. Keying anything on a name stays forbidden
			// (DriftwoodIdentity's own contract).
			if (string.IsNullOrEmpty(displayName)) return;
			DriftwoodIdentity.SetKnownName(steamId, displayName);
		}
	}
}
