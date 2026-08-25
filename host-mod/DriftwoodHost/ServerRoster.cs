using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Managing.Server;
using FishNet.Object;

namespace DriftwoodHost
{
	// THE ONE PLACE THAT ANSWERS "WHO IS ACTUALLY ABOARD", read from the server's own
	// connection table rather than the game's PlayerManager.
	//
	// PlayerManager.Players is populated by Player.OnStartClient - a CLIENT-context
	// callback. On a headless host the loopback ghost client exists but deliberately
	// never enters the world (GhostHost suppresses its player spawn), so nothing in
	// this process ever runs the client-side registration for a REMOTE player's
	// object. The result, proven live on the canary with a person standing in the
	// world: PlayerManager.Players empty, roster empty, positions empty, kick and the
	// blocklist sweep blind - while the transport count says 1. Every identity
	// feature was reading a list that can never fill on the machine it ships to.
	//
	// The server's own table cannot have that problem. SpawnPlayer (the game's own
	// ServerRpc) instantiates the Player prefab ON THE SERVER, sets the SteamID64
	// SyncVar server-side BEFORE giving the object to the connection, and every owned
	// object lives in NetworkConnection.Objects. So walking ServerManager.Clients
	// yields exactly the connected players, each with a server-authoritative id and
	// the game's own replicated transform - no client context required.
	//
	// PlayerManager is still read as a merge source, not because the walk needs it,
	// but because a future game build could move the ownership shape; a player found
	// by either source is a player, and a duplicate is dropped by reference.
	internal static class ServerRoster
	{
		// MAIN THREAD. Every deref guarded: connections and owned objects churn
		// mid-join and mid-leave, and a throw here must cost one row, never the walk.
		internal static List<Player> Connected()
		{
			List<Player> list = new List<Player>();
			try
			{
				ServerManager server = InstanceFinder.ServerManager;
				if (server != null && server.Clients != null)
				{
					foreach (NetworkConnection connection in server.Clients.Values)
					{
						try
						{
							if (connection == null || !connection.IsActive) continue;
							// The ghost host's own loopback client. Its player spawn is
							// suppressed, and even unsuppressed it is not a customer.
							if (connection.IsLocalClient) continue;
							Player player = OwnedPlayer(connection);
							if (player != null && !list.Contains(player)) list.Add(player);
						}
						catch { }
					}
				}
			}
			catch (Exception exception)
			{
				Plugin.Log?.LogDebug("Server roster walk failed: " + exception.Message);
			}
			try
			{
				if (PlayerManager.Players != null)
				{
					foreach (Player player in PlayerManager.Players)
					{
						if (player != null && !list.Contains(player)) list.Add(player);
					}
				}
			}
			catch { }
			return list;
		}

		private static Player OwnedPlayer(NetworkConnection connection)
		{
			if (connection.Objects == null) return null;
			foreach (NetworkObject owned in connection.Objects)
			{
				if (owned == null) continue;
				Player player = null;
				try
				{
					player = owned.GetComponent<Player>();
					if (player == null) player = owned.GetComponentInChildren<Player>(true);
				}
				catch { }
				if (player != null) return player;
			}
			return null;
		}
	}
}
