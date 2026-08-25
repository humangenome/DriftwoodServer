using System;
using FishNet.Connection;
using UnityEngine;

namespace DriftwoodHost
{
	// "Teleport the player back to shore" - the one movement primitive the game actually has,
	// used the way the game itself uses it.
	//
	// HOW MOVEMENT WORKS IN THIS GAME, because it decides what "server-authoritative" can mean
	// here: a player's position is CLIENT-OWNED. The local client drives its own Rigidbody
	// (PlayerMovement) and reports position to the server every tick (Server.UpdatePlayerPosRot),
	// which the server relays to everyone else's proxy of that player (OtherPlayer). The server
	// never simulates a player and cannot move one by writing a transform - the client would
	// overwrite it on the next tick. What the game DOES ship is a server-to-owner order:
	// Player.RPCTeleport, a TargetRpc the owning client obeys by running its own
	// LocalTeleport (PlayerMovement.Teleport -> rigidbody move, camera yaw, then a position
	// report flagged `teleport` so every other client snaps its proxy instead of lerping).
	// That is exactly how an island change moves the whole crew (IslandManager ->
	// Server.TeleportPlayer -> RPCTeleport), so the mechanism is the game's own and every
	// vanilla client already honours it. The server decides whether and where; the client
	// carries it out. A modified client could ignore the order, but a modified client can
	// already fly - movement is client-owned with or without us.
	//
	// WHERE "SHORE" IS: the island's authored spawn transform, SpawnManager.PlayerSpawnPos /
	// PlayerSpawnRot, read on this server from the loaded island scene. It is the same point
	// the game uses for a fresh spawn (PlayerMovement.TeleportToLand), for a respawn after
	// death (PlayerDying.ResurrectEffect) and for arrival on a new island - the shoreline by
	// the wreck. Nothing here invents a position.
	//
	// EVERY method touches Unity objects and therefore RUNS ON THE MAIN THREAD ONLY. The chat
	// hook is already on the main thread (FishNet delivers RPCs there); the console reaches
	// this through MainThread.Run. Decisions are made on server state; nothing a client
	// asserts is consulted for any refusal.
	internal static class PlayerRescue
	{
		// MAIN THREAD. Returns null on success, or one sentence addressed to the player saying
		// why not. `where` describes the destination for the audit line.
		internal static string ToShore(Player player, out string where)
		{
			where = string.Empty;
			try
			{
				if (Server.Instance == null || !Server.Instance.IsServerInitialized)
					return "the world is not running.";
				if (player == null || player.IsDeinitializing)
					return "you are not in the world right now.";
				NetworkConnection owner = player.Owner;
				if (owner == null || !owner.IsValid)
					return "your connection is already gone.";

				// The island must be settled. During a change the spawn point belongs to the
				// island that is on its way out, and the game itself refuses island triggers for
				// five seconds after a swap for the same reason.
				if (IslandManager.IsLoading || OnlineIslandManager.TeleportPlayers)
					return "the crew is moving islands right now - try again in a few seconds.";
				if (Time.time - OnlineIslandManager.TimeWhenSwappingIsland < 5f)
					return "the island just changed - try again in a few seconds.";
				if (Island.CurIsland == null)
					return "no island is loaded right now.";
				Vector3 spawn = SpawnManager.PlayerSpawnPos;
				if (spawn == Vector3.zero)
					return "this island has no spawn point to send you to.";

				// A downed player already has a way back to shore - the game's own respawn -
				// and teleporting a body would fight the death camera and the dead-player
				// object the server spawned for them.
				bool downed = false;
				try { downed = player.Vitals != null && player.Vitals.Health <= 0; } catch { }
				try { downed = downed || (player.Dying != null && player.Dying.IsDead); } catch { }
				if (downed)
					return "you are down - hold the mouse button to give up and respawn instead.";

				// The game blocks giving up during a boss fight; a teleport to shore is the
				// same escape hatch by another name. Mirror the game's rule.
				Creature boss = null;
				try { boss = BossManager.Boss; } catch { }
				if (boss != null)
				{
					bool bossDead = false;
					try { bossDead = boss._hp.Value <= 0; } catch { }
					if (!bossDead)
						return "a boss fight is on - !stuck is off until it ends.";
				}

				// The driver is glued to the wheel: the client snaps the driver's body to the
				// boat's driver seat every frame (PlayerMovement.MoveToBoatDriverPos), so a
				// teleport would be undone within a frame and read as "it did nothing".
				Boat boat = null;
				try { boat = BoatManager.Boat; } catch { }
				if (boat != null)
				{
					Player driver = null;
					try { driver = boat.Driver; } catch { }
					if (driver == player)
						return "you are driving the boat - step away from the wheel first.";
				}

				float rotation = SpawnManager.PlayerSpawnRot;
				where = "island spawn (" + spawn.x.ToString("0.#") + ", " + spawn.y.ToString("0.#") + ", " + spawn.z.ToString("0.#") + ")";
				player.RPCTeleport(owner, spawn, rotation);
				return null;
			}
			catch (Exception exception)
			{
				return "the teleport failed (" + exception.GetType().Name + ": " + exception.Message + ").";
			}
		}
	}
}
