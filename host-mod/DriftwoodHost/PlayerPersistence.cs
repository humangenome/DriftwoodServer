using System;
using System.Collections.Generic;
using HarmonyLib;

namespace DriftwoodHost
{
	// WHY NO CONNECTED PLAYER'S PROGRESS EVER REACHED THE WORLD SAVE.
	//
	// SaveManager.SaveServer opens by walking PlayerManager.Players and calling
	// player.Inventory.SaveInventory() on each - that call is what folds a player's
	// current inventory, held item, health, fullness, baits, pockets and tutorial
	// state into CurServerSave.Players before the file is written. PlayerManager
	// fills from Player.OnStartClient, a CLIENT-context callback that never runs on
	// a headless host for a remote player (the proven mechanism in ServerRoster.cs).
	// So on every dedicated Driftwood server the walk is a no-op: every autosave,
	// every panel save, every shutdown save writes the world WITHOUT the people in
	// it.
	//
	// What made it survivable enough to ship unnoticed: PlayerInventory.OnStopServer
	// saves a departing REMOTE player at disconnect (it is server-context, so it does
	// run headless), and the save object carries the players list forward by
	// reference (Players = CurServerSave.Players). So a player who left cleanly and
	// was followed by a later autosave did persist. What was lost, every time:
	//
	//   - everything a CONNECTED player did since their last disconnect, on every
	//     restart - the panel's own restart flow saves first, and that save cannot
	//     see them (ticket 765633: "came back from our restart as a different
	//     character" - half of that was this, the other half is the identity
	//     keying in SpawnIdentity.cs);
	//   - everyone aboard, on a crash - the disconnect save never runs
	//     (OnStopServer checks Server.IsDeinitializing and skips teardown), so the
	//     last capture of each player is however long ago they last left.
	//
	// The fix is one prefix on SaveServer: walk the server's own connection table
	// (the same ServerRoster seam the roster, the map and kick already read) and run
	// the game's own SaveInventory for every connected player the vanilla walk will
	// miss. The game then serializes the records exactly as it always intended to.
	// Offline players are untouched: their records ride along in the carried-forward
	// list, and SavePlayer updates in place by SteamID rather than rebuilding.
	//
	// LIVE FINDING (canary, 2026-08-26, game 1.0.6): on this build the server's own
	// loopback ghost client observes remote player spawns, which runs OnStartClient in
	// this process and DOES register them in PlayerManager - so the vanilla walk usually
	// covers connected players and this prefix stays silent (captured == 0, no log
	// line). It remains armed for the states where that observer path has not run or
	// has broken - the exact state the 0.1.4-era roster evidence recorded - and the
	// vanilla-set skip plus the per-id dedupe below make it a no-op when redundant.
	internal static class PlayerPersistence
	{
		internal const string GroupName = "player-persistence";

		private static bool _announced;
		private static bool _warned;

		internal static IEnumerable<PatchTarget> Targets()
		{
			// Optional: a game build that renames SaveServer stands this down and the
			// server still hosts - degraded to disconnect-only persistence, which the
			// readiness document then names in featuresStoodDown - rather than
			// refusing to host over it. Same shape and same reasoning as MoneyMirror,
			// which shares this exact method with its own prefix.
			yield return new PatchTarget
			{
				TypeName = "SaveManager",
				MethodName = "SaveServer",
				Parameters = new[] { typeof(bool) },
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = GroupName,
				Prefix = AccessTools.Method(typeof(PlayerPersistence), nameof(CapturePrefix)),
				Why = "SaveServer folds each player into the save via PlayerManager.Players, which never fills on a headless host - so no connected player's progress was ever written to the world file."
			};
		}

		// Runs on the main thread, immediately before the game builds and writes the
		// save object. MUST NOT throw past this frame: a throw here would abort the
		// game's own save. A failure costs one player's snapshot (their previous
		// record stays as it was), never the save and never another player's record.
		private static void CapturePrefix()
		{
			try
			{
				// The same early-outs the game's own SaveServer body applies. When any
				// of these hold the body writes nothing, so capturing would be wasted
				// work at best and a half-initialised walk at worst.
				if (SaveManager.CurServerSave == null) return;
				Server server = Server.Instance;
				if (server == null || !server.IsServerInitialized) return;

				// Whoever the vanilla walk will already save is skipped, so a future
				// game build that starts filling PlayerManager headless cannot make
				// this save anyone twice.
				HashSet<Player> vanilla = new HashSet<Player>();
				HashSet<ulong> savedIds = new HashSet<ulong>();
				try
				{
					foreach (Player known in PlayerManager.Players)
					{
						if (known == null) continue;
						vanilla.Add(known);
						// The ids the game's own walk is about to save. Proven live on the
						// canary (2026-08-26): a client that vanishes without a FIN can leave
						// its Player OBJECT stranded in PlayerManager while the person rejoins
						// and reclaims the same id - two objects, one id. Whatever this prefix
						// captures must never overwrite the record the vanilla walk writes for
						// a LIVE player with a stale ghost's frozen copy of the same id.
						try { savedIds.Add(known.SteamID); } catch { }
					}
				}
				catch { }

				int captured = 0;
				bool failed = false;
				foreach (Player player in ServerRoster.Connected())
				{
					if (player == null || vanilla.Contains(player)) continue;
					try
					{
						// Never persist the host's own ghost. ServerRoster already
						// skips the loopback connection; this is the second net, for
						// the merge source.
						ulong steamId = player.SteamID;
						if (steamId == 0UL || steamId == DriftwoodIdentity.HostSteamId) continue;
						// One record per id per save. The roster walks live connections first,
						// so when a stale object shares a live player's id, the live one is
						// the one this pass keeps.
						if (!savedIds.Add(steamId)) continue;
						PlayerInventory inventory = player.Inventory;
						if (inventory == null) continue;
						// The game's own capture, exactly as a listen server runs it:
						// SaveInventory -> SaveManager.SavePlayer updates this
						// player's record in CurServerSave.Players in place, or
						// appends a new one. andDestroy stays false - despawning is
						// the disconnect path's job, not the save's.
						inventory.SaveInventory(false);
						captured++;
					}
					catch (Exception exception)
					{
						failed = true;
						if (!_warned)
						{
							_warned = true;
							Plugin.Log?.LogWarning("A connected player could not be captured into the world save (" +
								exception.GetType().Name + ": " + exception.Message +
								"). Their previous record stays in force; the save itself is unaffected.");
						}
					}
				}

				if (captured > 0)
				{
					if (!_announced)
					{
						_announced = true;
						Plugin.Log?.LogInfo("Connected players are being captured into world saves. First capture: " +
							captured + " player" + (captured == 1 ? "" : "s") + ".");
					}
					else
					{
						Plugin.Log?.LogInfo("Captured " + captured + " connected player" +
							(captured == 1 ? "" : "s") + " into the world save" + (failed ? " (one or more failed - see the warning above)" : "") + ".");
					}
				}
			}
			catch (Exception exception)
			{
				if (_warned) return;
				_warned = true;
				Plugin.Log?.LogWarning("The connected-player capture failed before it could walk the roster (" +
					exception.GetType().Name + ": " + exception.Message +
					"). Saves fall back to disconnect-only persistence for this session.");
			}
		}
	}
}
