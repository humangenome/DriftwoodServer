using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DriftwoodHost
{
	// The owner's gameplay levers, built on the game's OWN dev-command internals.
	//
	// How to Fish ships a host-gated cheat suite (DazedCommands: /addmoney, /nextisland,
	// /spawn, /killboss, ...) that no player can use on a dedicated server - the gate is
	// "you are the host", and on a Driftwood server the only host is this process. So the
	// managers those commands drive are a supported, shipped surface; what is NOT reusable is
	// the command layer itself, because every DazedCommands method reaches for Player.LocalPlayer
	// or the camera, and a headless host has neither. Each method here therefore invokes the
	// UNDERLYING manager with server-appropriate arguments instead:
	//
	//   money    -> MoneyManager.AddMoney / RemoveMoney. ONE SHARED WALLET (a SyncVar<int>),
	//               not per-player - that is the game's own economy model. The Player argument
	//               only positions a coin sound, and the game's own audio path no-ops on null
	//               (verified: AudioManager.PlayRandomPlayerClip checks the player first), so a
	//               connected player is passed when there is one and null is safe when not.
	//   island   -> OnlineIslandManager.TpToNextIsland / TpToSpecificIsland - the exact calls
	//               /nextisland makes. The server flips one synced byte; every client loads the
	//               island scene and every player is teleported to its spawn together.
	//   spawn    -> GameInfo.GetSpawnable + Server.Instance.Spawn, the same pair /spawn uses -
	//               but positioned at a CONNECTED PLAYER instead of at the camera, because the
	//               camera of a headless process is nowhere anybody stands.
	//   killboss -> Creature.ServerChangeHp on BossManager.Boss: the same server-side HP change
	//               a legitimate hit lands (Server.HitCreature does exactly this), so death,
	//               trophy and progression all follow the game's own kill path.
	//
	// EVERY method touches Unity objects and therefore RUNS ON THE MAIN THREAD ONLY - callers
	// reach it through MainThread.Run. Server-authoritative by construction: every decision is
	// made here on server state, nothing a client asserts is consulted. Every method returns
	// null on success or one plain sentence saying why not; refusals are loud, never silent.
	internal static class OwnerGameplay
	{
		// One command may move the wallet by at most this much. The wallet is an int and the
		// game clamps removal at zero, but an unbounded add is how a typo becomes 2.1 billion
		// - and the refusal names the cap so the owner is not left guessing.
		internal const int MoneyCapPerCommand = 1000000;

		// MAIN THREAD. The shared wallet's current value, or -1 with a refusal sentence.
		internal static string MoneyBalance(out int balance)
		{
			balance = -1;
			try
			{
				MoneyManager manager = MoneyManager.Instance;
				if (manager == null || !manager.IsServerInitialized)
					return "the world is not running, so there is no wallet to read.";
				balance = manager._money.Value;
				return null;
			}
			catch (Exception exception)
			{
				return "reading the wallet failed (" + exception.GetType().Name + ": " + exception.Message + ").";
			}
		}

		// MAIN THREAD. add=false removes; the game itself clamps the wallet at zero.
		internal static string MoneyChange(int amount, bool add, out int balance)
		{
			balance = -1;
			if (amount < 1 || amount > MoneyCapPerCommand)
				return "the amount must be between 1 and " +
					MoneyCapPerCommand.ToString("N0", CultureInfo.InvariantCulture) + ".";
			try
			{
				MoneyManager manager = MoneyManager.Instance;
				// Checked HERE because the game's own RemoveMoney dereferences its instance
				// without checking it - the null guard has to live on this side of the call.
				if (manager == null || !manager.IsServerInitialized)
					return "the world is not running, so there is no wallet to change.";
				// The Player argument only places the coin sound; the game's audio path is
				// null-safe, so an empty server just changes the number silently.
				Player nearest = FirstConnectedPlayer();
				if (add) MoneyManager.AddMoney(amount, nearest);
				else MoneyManager.RemoveMoney(amount, nearest);
				balance = manager._money.Value;
				return null;
			}
			catch (Exception exception)
			{
				return "changing the wallet failed (" + exception.GetType().Name + ": " + exception.Message + ").";
			}
		}

		// MAIN THREAD. Where the crew is, 1-based the way players count islands. Returns null
		// with the sentence in the out parameter, or a refusal.
		internal static string IslandStatus(out string sentence)
		{
			sentence = string.Empty;
			try
			{
				OnlineIslandManager manager = OnlineIslandManager.Instance;
				if (manager == null)
					return "the world is not running, so there is no island to report.";
				int playable = PlayableIslands();
				int current = manager._curIsland.Value + 1;
				int unlocked = manager._maxIslandUnlocked.Value + 1;
				sentence = "The crew is on island " + current + " of " + playable +
					" (islands unlocked: " + Math.Min(unlocked, playable) + ").";
				if (IslandManager.IsLoading) sentence += " An island change is in progress right now.";
				return null;
			}
			catch (Exception exception)
			{
				return "reading the island state failed (" + exception.GetType().Name + ": " + exception.Message + ").";
			}
		}

		// MAIN THREAD. Move the whole crew one island forward or back - the game's own
		// /nextisland with the same wrap-around. Returns the NEW 1-based island via the out
		// parameter on success.
		internal static string IslandStep(bool backwards, out int newIsland)
		{
			newIsland = 0;
			try
			{
				OnlineIslandManager manager = OnlineIslandManager.Instance;
				if (manager == null || !manager.IsServerInitialized)
					return "the world is not running, so there is nowhere to sail to.";
				if (IslandManager.IsLoading)
					return "an island change is already in progress - wait for it to finish.";
				OnlineIslandManager.TpToNextIsland(backwards);
				newIsland = manager._curIsland.Value + 1;
				return null;
			}
			catch (Exception exception)
			{
				return "the island change failed (" + exception.GetType().Name + ": " + exception.Message + ").";
			}
		}

		// MAIN THREAD. Move the whole crew to a specific island, 1-based. An island beyond the
		// crew's progression is UNLOCKED first, deliberately: the owner command exists for
		// stuck-progression rescues, and teleporting a crew onto an island their save says they
		// have not reached leaves the two disagreeing.
		internal static string IslandSet(int oneBased, out bool unlockedNew)
		{
			unlockedNew = false;
			try
			{
				OnlineIslandManager manager = OnlineIslandManager.Instance;
				if (manager == null || !manager.IsServerInitialized)
					return "the world is not running, so there is nowhere to sail to.";
				int playable = PlayableIslands();
				if (playable < 1) return "this world reports no islands, so nothing was changed.";
				if (oneBased < 1 || oneBased > playable)
					return "islands are numbered 1 to " + playable + ".";
				if (IslandManager.IsLoading)
					return "an island change is already in progress - wait for it to finish.";
				byte index = (byte)(oneBased - 1);
				if (index > manager._maxIslandUnlocked.Value)
				{
					manager.UnlockIsland(index);
					unlockedNew = true;
				}
				OnlineIslandManager.TpToSpecificIsland(index);
				return null;
			}
			catch (Exception exception)
			{
				return "the island change failed (" + exception.GetType().Name + ": " + exception.Message + ").";
			}
		}

		// MAIN THREAD. Drop one of the game's spawnable items next to a connected player - the
		// same catalogue and server spawn /spawn uses, aimed at a person instead of a camera.
		internal static string SpawnItem(string requestedName, out string spawnedName, out string nearName)
		{
			spawnedName = string.Empty;
			nearName = string.Empty;
			string normalised = (requestedName ?? string.Empty).Replace(" ", "").ToLowerInvariant();
			if (normalised.Length == 0) return "say what to spawn: spawn <item name>.";
			try
			{
				if (Server.Instance == null || !Server.Instance.IsServerInitialized)
					return "the world is not running, so nothing can be spawned.";
				Item spawnable = GameInfo.GetSpawnable(normalised);
				if (spawnable == null)
					return "How to Fish has no spawnable item called \"" + normalised +
						"\" - the name is the in-game one with spaces removed.";
				// A spawn lands next to a person, so an empty server refuses rather than
				// dropping an item at a place nobody stands and nobody can find.
				OwnerActions.Found target = null;
				foreach (OwnerActions.Found candidate in OwnerActions.ConnectedPlayers())
				{
					if (candidate.Player == null) continue;
					target = candidate;
					break;
				}
				if (target == null)
					return "nobody is connected to receive it - spawn drops the item next to a player.";
				Vector3 position = target.Player.transform.position + Vector3.up * 1.5f;
				Item item = UnityEngine.Object.Instantiate(spawnable, position, Quaternion.identity);
				if (item == null) return "the game could not create that item.";
				Server.Instance.Spawn(item.gameObject);
				spawnedName = spawnable.name;
				nearName = target.Name;
				return null;
			}
			catch (Exception exception)
			{
				return "the spawn failed (" + exception.GetType().Name + ": " + exception.Message + ").";
			}
		}

		// MAIN THREAD. End the active boss fight as a KILL - the same server-side HP change a
		// real hit lands, so the trophy and the progression follow the game's own path rather
		// than a despawn that would eat the fight.
		internal static string KillBoss(out string bossName)
		{
			bossName = "the boss";
			try
			{
				Creature boss = BossManager.Boss;
				if (boss == null) return "no boss fight is active right now.";
				try
				{
					string name = boss.GetName();
					if (!string.IsNullOrEmpty(name)) bossName = name;
				}
				catch { /* a display name is a nicety; the kill is the point */ }
				if (boss.IsDead) return "the boss is already dead.";
				if (BossManager.IsImmortal)
					return "the boss is in an invulnerable phase right now - try again in a few seconds.";
				// Large but nowhere near int overflow: hp - damage stays far above int.MinValue.
				boss.ServerChangeHp(999999999);
				if (boss._hp.Value > 0)
					return "the boss shrugged the hit off (" + boss._hp.Value +
						" hp left) - it may have entered an invulnerable phase; try again.";
				return null;
			}
			catch (Exception exception)
			{
				return "the boss kill failed (" + exception.GetType().Name + ": " + exception.Message + ").";
			}
		}

		// The number of islands a crew can actually stand on. IslandManager counts scenes;
		// build index 0 is the base scene and the game's own wrap-around in NextIslandInBuild
		// treats TotalIslands - 1 as one past the last playable island.
		private static int PlayableIslands()
		{
			return Math.Max(0, IslandManager.TotalIslands - 1);
		}

		private static Player FirstConnectedPlayer()
		{
			foreach (OwnerActions.Found candidate in OwnerActions.ConnectedPlayers())
			{
				if (candidate.Player != null) return candidate.Player;
			}
			return null;
		}
	}
}
