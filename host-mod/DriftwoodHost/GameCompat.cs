using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DriftwoodHost
{
	// ONE DLL FOR EVERY SUPPORTED GAME BUILD.
	//
	// Game 1.1.0 (the Islet update, Steam build 25604825 as 1.1.3) changed the shape of five
	// members this host used to bind directly, and a direct reference to a member that no longer
	// exists is not a compile error on the fleet - it is a MissingFieldException or a
	// MissingMethodException the first time the method holding it is compiled at run time:
	//
	//   IslandManager.TotalIslands      public static FIELD -> auto PROPERTY. It sat inside the
	//                                   readiness sampler, so on 1.1.x every sample threw and the
	//                                   world never reported itself as running.
	//   MoneyManager.AddMoney/Remove    (int, Player) -> (int, Player, Currency): the owner's
	//                                   "money add/remove" command.
	//   SpawnManager.PlayerSpawnPos/Rot static fields removed; 1.1.x asks
	//                                   GetPlayerSpawnPos(Vector3)/GetPlayerSpawnRot(Vector3),
	//                                   which also knows about the new mini islands: !stuck.
	//
	// Customer servers move from the old build to the new one at their own pace, so the host
	// must run on both. Every member that differs between supported builds is therefore reached
	// HERE, by reflection, old shape and new shape, resolved once. Anything the game does not
	// have in either shape is reported as a plain sentence, never thrown at the caller.
	//
	// Found and proven with the member diff (tools/memberdiff), which resolves every member
	// this DLL binds against a given game build. Run it against each new build before pinning.
	internal static class GameCompat
	{
		// ---- islands -----------------------------------------------------------------------

		private static bool _islandsResolved;
		private static FieldInfo _totalIslandsField;
		private static MethodInfo _totalIslandsGetter;
		private static MethodInfo _isMainIsland;

		private static void ResolveIslands()
		{
			if (_islandsResolved) return;
			_islandsResolved = true;
			Type type = typeof(IslandManager);
			_totalIslandsField = QuietField(type, "TotalIslands");
			if (_totalIslandsField != null && !_totalIslandsField.IsStatic) _totalIslandsField = null;
			_totalIslandsGetter = QuietProperty(type, "TotalIslands")?.GetGetMethod(true);
			// 1.1.0+: mini islands live in the same scene list as the main ones, so the scene
			// count alone no longer says how many islands a crew can sail between.
			_isMainIsland = QuietMethod(type, "IsMainIsland", new[] { typeof(byte) });
		}

		// The game's own island scene count (build scenes minus the base scene). -1 when this
		// build exposes it in neither shape.
		internal static int TotalIslandScenes()
		{
			ResolveIslands();
			try
			{
				if (_totalIslandsField != null) return (int)_totalIslandsField.GetValue(null);
				if (_totalIslandsGetter != null) return (int)_totalIslandsGetter.Invoke(null, null);
			}
			catch (Exception exception)
			{
				WarnOnce("islands", "The island count could not be read (" + Unwrap(exception) + ").");
			}
			return -1;
		}

		// The islands a crew can actually stand on and the owner's island commands may name,
		// counted the way the game's own next-island command walks them on each build: every
		// scene below TotalIslands - 1, and on 1.1.0+ only the ones the game calls main islands
		// (the mini islands that drift in at sea are not destinations).
		internal static int PlayableIslands()
		{
			int total = TotalIslandScenes();
			if (total <= 0) return 0;
			if (_isMainIsland == null) return Math.Max(0, total - 1);
			int count = 0;
			try
			{
				for (int i = 0; i < total - 1 && i < byte.MaxValue; i++)
				{
					if ((bool)_isMainIsland.Invoke(null, new object[] { (byte)i })) count++;
				}
			}
			catch (Exception exception)
			{
				WarnOnce("main-islands", "The main islands could not be counted (" + Unwrap(exception) + "); falling back to the scene count.");
				return Math.Max(0, total - 1);
			}
			return count;
		}

		// ---- the shared wallet -------------------------------------------------------------

		private static bool _moneyResolved;
		private static MethodInfo _addMoney;
		private static MethodInfo _removeMoney;

		private static MethodInfo FindMoneyMethod(string name)
		{
			foreach (MethodInfo method in typeof(MoneyManager).GetMethods(BindingFlags.Public | BindingFlags.Static))
			{
				if (method.Name != name) continue;
				ParameterInfo[] parameters = method.GetParameters();
				if (parameters.Length < 2) continue;
				if (parameters[0].ParameterType != typeof(int)) continue;
				if (parameters[1].ParameterType != typeof(Player)) continue;
				bool restOptional = true;
				for (int i = 2; i < parameters.Length; i++)
				{
					if (!parameters[i].IsOptional) { restOptional = false; break; }
				}
				if (restOptional) return method;
			}
			return null;
		}

		// MAIN THREAD. Moves the crew's DOLLAR wallet through the game's own AddMoney/RemoveMoney
		// (which also play the coin sound and clamp at zero), on either signature. Returns null on
		// success or one sentence saying why not.
		internal static string ChangeMoney(int amount, bool add, Player near)
		{
			if (!_moneyResolved)
			{
				_moneyResolved = true;
				_addMoney = FindMoneyMethod("AddMoney");
				_removeMoney = FindMoneyMethod("RemoveMoney");
			}
			MethodInfo method = add ? _addMoney : _removeMoney;
			if (method == null)
				return "this game build has no MoneyManager." + (add ? "AddMoney" : "RemoveMoney") + "(amount, player), so the wallet cannot be changed from here.";
			ParameterInfo[] parameters = method.GetParameters();
			object[] args = new object[parameters.Length];
			args[0] = amount;
			args[1] = near;
			for (int i = 2; i < parameters.Length; i++)
			{
				args[i] = DefaultFor(parameters[i]);
			}
			try
			{
				method.Invoke(null, args);
				return null;
			}
			catch (Exception exception)
			{
				return "the game refused the wallet change (" + Unwrap(exception) + ").";
			}
		}

		// The declared default of an optional parameter, as the parameter's own type. An enum
		// default can come back from metadata as its underlying integer, and Invoke wants the enum.
		private static object DefaultFor(ParameterInfo parameter)
		{
			Type type = parameter.ParameterType;
			object value = parameter.HasDefaultValue ? parameter.DefaultValue : null;
			if (type.IsEnum) return Enum.ToObject(type, value ?? 0);
			if (value == null && type.IsValueType) return Activator.CreateInstance(type);
			return value;
		}

		// ---- where "shore" is --------------------------------------------------------------

		private static bool _spawnResolved;
		private static FieldInfo _playerSpawnPosField;
		private static FieldInfo _playerSpawnRotField;
		private static MethodInfo _playerSpawnPosMethod;
		private static MethodInfo _playerSpawnRotMethod;
		private static FieldInfo _boatSpawnPosField;
		private static FieldInfo _boatSpawnRotField;
		private static MethodInfo _boatSpawnPosMethod;
		private static MethodInfo _boatSpawnRotMethod;

		private static void ResolveSpawn()
		{
			if (_spawnResolved) return;
			_spawnResolved = true;
			Type type = typeof(SpawnManager);
			Type[] byPosition = { typeof(Vector3) };
			_playerSpawnPosField = StaticField(type, "PlayerSpawnPos", typeof(Vector3));
			_playerSpawnRotField = StaticField(type, "PlayerSpawnRot", typeof(float));
			_playerSpawnPosMethod = QuietMethod(type, "GetPlayerSpawnPos", byPosition);
			_playerSpawnRotMethod = QuietMethod(type, "GetPlayerSpawnRot", byPosition);
			_boatSpawnPosField = StaticField(type, "BoatSpawnPos", typeof(Vector3));
			_boatSpawnRotField = StaticField(type, "BoatSpawnRot", typeof(Quaternion));
			_boatSpawnPosMethod = QuietMethod(type, "GetBoatSpawnPos", byPosition);
			_boatSpawnRotMethod = QuietMethod(type, "GetBoatSpawnRot", byPosition);
		}

		private static FieldInfo StaticField(Type type, string name, Type fieldType)
		{
			FieldInfo field = QuietField(type, name);
			return field != null && field.IsStatic && field.FieldType == fieldType ? field : null;
		}

		// TRUE on builds where the game itself decides the crew's arrival point by asking where
		// they are (1.1.0+, which added mini islands). On those builds the game also gates its
		// own crew teleport on Player.LocalPlayer, which a headless host does not have.
		internal static bool SpawnIsPositional
		{
			get { ResolveSpawn(); return _playerSpawnPosField == null && _playerSpawnPosMethod != null; }
		}

		// MAIN THREAD. The island's authored arrival point for a player standing at `near`:
		// the same point the game uses for a fresh spawn, a respawn and an island change.
		internal static bool TryPlayerSpawn(Vector3 near, out Vector3 position, out float rotation)
		{
			ResolveSpawn();
			position = Vector3.zero;
			rotation = 0f;
			try
			{
				if (_playerSpawnPosField != null && _playerSpawnRotField != null)
				{
					position = (Vector3)_playerSpawnPosField.GetValue(null);
					rotation = (float)_playerSpawnRotField.GetValue(null);
					return true;
				}
				if (_playerSpawnPosMethod != null && _playerSpawnRotMethod != null)
				{
					position = (Vector3)_playerSpawnPosMethod.Invoke(null, new object[] { near });
					rotation = (float)_playerSpawnRotMethod.Invoke(null, new object[] { near });
					return true;
				}
			}
			catch (Exception exception)
			{
				WarnOnce("player-spawn", "The island spawn point could not be read (" + Unwrap(exception) + ").");
			}
			return false;
		}

		internal static bool TryBoatSpawn(Vector3 near, out Vector3 position, out Quaternion rotation)
		{
			ResolveSpawn();
			position = Vector3.zero;
			rotation = Quaternion.identity;
			try
			{
				if (_boatSpawnPosField != null && _boatSpawnRotField != null)
				{
					position = (Vector3)_boatSpawnPosField.GetValue(null);
					rotation = (Quaternion)_boatSpawnRotField.GetValue(null);
					return true;
				}
				if (_boatSpawnPosMethod != null && _boatSpawnRotMethod != null)
				{
					position = (Vector3)_boatSpawnPosMethod.Invoke(null, new object[] { near });
					rotation = (Quaternion)_boatSpawnRotMethod.Invoke(null, new object[] { near });
					return true;
				}
			}
			catch (Exception exception)
			{
				WarnOnce("boat-spawn", "The boat's spawn point could not be read (" + Unwrap(exception) + ").");
			}
			return false;
		}

		// ---- the host has no local player --------------------------------------------------
		//
		// 1.1.0 added `&& Player.LocalPlayer` to two server paths that used to run without it,
		// because on a listen server the host always has a player. This host suppresses its
		// own player on purpose (GhostHost.cs), so on 1.1.x both paths quietly stop:
		//
		//   IslandManager.ProcessRequest  after an island change that asked for it
		//                                 (TeleportPlayers: the owner's island commands, the
		//                                 game's own end-of-game return), the crew is no longer
		//                                 teleported to the new island and the boat stays put -
		//                                 players are left in open water where the old island was.
		//   Server.RespawnPlayer          when the last player alive gives up and respawns, the
		//                                 game dereferences Player.LocalPlayer to move the boat
		//                                 home: a NullReferenceException, and the boat stays at
		//                                 sea.
		//
		// Both are completed here, with the game's own calls and the game's own points, only
		// when the game skipped them for want of a local player.

		private static float _islandTeleportArmedAt = -1f;
		private const float IslandTeleportGiveUpSeconds = 120f;

		// MAIN THREAD. Called by the owner's island commands right after they ask the game to
		// change island. Does nothing on builds where the game still moves the crew itself.
		internal static void ArmIslandTeleport()
		{
			if (!SpawnIsPositional) return;
			_islandTeleportArmedAt = Time.realtimeSinceStartup;
		}

		// MAIN THREAD, from the readiness sampler (about once a second). Finishes an armed island
		// change once the new island is the loaded one.
		internal static void ServiceIslandTeleport()
		{
			if (_islandTeleportArmedAt < 0f) return;
			try
			{
				if (Time.realtimeSinceStartup - _islandTeleportArmedAt > IslandTeleportGiveUpSeconds)
				{
					_islandTeleportArmedAt = -1f;
					Plugin.Log?.LogWarning("An island change asked for by an owner command did not finish loading within " +
						(int)IslandTeleportGiveUpSeconds + " seconds, so the crew was not moved to it.");
					return;
				}
				if (Player.LocalPlayer != null)
				{
					// Something gave this process a local player; the game moves the crew itself.
					_islandTeleportArmedAt = -1f;
					return;
				}
				if (IslandManager.IsLoading) return;
				OnlineIslandManager manager = OnlineIslandManager.Instance;
				Island island = Island.CurIsland;
				if (manager == null || island == null || Server.Instance == null || !Server.Instance.IsServerInitialized) return;
				// The game's own "this island is the one that is loaded" test.
				if (island.gameObject.scene.buildIndex != manager._curIsland.Value + 1) return;

				_islandTeleportArmedAt = -1f;
				int moved = 0;
				Vector3 target = Vector3.zero;
				foreach (Player player in new List<Player>(PlayerManager.AlivePlayers))
				{
					if (player == null || player.IsDeinitializing || player.Owner == null || !player.Owner.IsValid) continue;
					Vector3 position;
					float rotation;
					if (!TryPlayerSpawn(player.Transform.position, out position, out rotation) || position == Vector3.zero) continue;
					// The order the game's own Server.TeleportPlayer ends in: a target RPC the
					// player's own client carries out (movement is client-owned in this game).
					player.RPCTeleport(player.Owner, position, rotation);
					target = position;
					moved++;
				}
				bool boat = MoveBoatHome();
				Plugin.Log?.LogInfo("Island change finished: moved " + Wording.Count(moved, "player") + (boat ? " and the boat" : "") +
					" to island " + (manager._curIsland.Value + 1) + " (spawn " + target.x.ToString("0.#") + ", " + target.y.ToString("0.#") + ", " + target.z.ToString("0.#") +
					"), a step this game build leaves to a local player the server does not have.");
			}
			catch (Exception exception)
			{
				_islandTeleportArmedAt = -1f;
				WarnOnce("island-teleport", "Moving the crew after an island change failed (" + Unwrap(exception) + ").");
			}
		}

		// MAIN THREAD. The game's own boat reset: its spawn point on the loaded island.
		internal static bool MoveBoatHome()
		{
			BoatManager boats = BoatManager.Instance;
			if (boats == null) return false;
			Vector3 near = Island.CurIsland != null ? Island.CurIsland.transform.position : Vector3.zero;
			Vector3 position;
			Quaternion rotation;
			if (!TryBoatSpawn(near, out position, out rotation) || position == Vector3.zero) return false;
			boats.TryMoveBoat(position, rotation);
			return true;
		}

		internal static IEnumerable<PatchTarget> Targets()
		{
			yield return new PatchTarget
			{
				TypeName = "Server",
				MethodName = "RpcLogic___RespawnPlayer___*",
				MethodNamePrefix = "RpcLogic___RespawnPlayer___",
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = "headless-crew-respawn",
				Finalizer = AccessTools.Method(typeof(GameCompat), nameof(RespawnFinalizer)),
				Why = "1.1.0 moves the boat home after a whole-crew wipe through Player.LocalPlayer, which a headless host does not have; the respawn then ends in a NullReferenceException with the boat left at sea."
			};
		}

		// The game's body has already dropped every inventory and reset every vital by the time
		// it reaches the boat line, which is its LAST statement - so finishing that one line and
		// stopping the escape is exactly the game's behaviour. Anything else is passed through
		// untouched, and every completion is counted like any other swallow.
		private static Exception RespawnFinalizer(Exception __exception, MethodBase __originalMethod)
		{
			if (__exception == null) return null;
			if (!(__exception is NullReferenceException) || Player.LocalPlayer != null || !SpawnIsPositional) return __exception;
			try
			{
				MoveBoatHome();
			}
			catch (Exception exception)
			{
				WarnOnce("respawn-boat", "Moving the boat home after a crew wipe failed (" + Unwrap(exception) + ").");
			}
			SwallowCounter.Record(__originalMethod, __exception);
			return null;
		}

		// ---- plumbing ----------------------------------------------------------------------

		// Probes for members whose ABSENCE is normal on some supported build. HarmonyX's
		// AccessTools logs a warning for every miss, which would put a false alarm in every
		// healthy log; plain reflection asks the same question quietly.
		private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

		internal static FieldInfo QuietField(Type type, string name)
		{
			try { return type?.GetField(name, Any); } catch { return null; }
		}

		internal static MethodInfo QuietMethod(Type type, string name, Type[] parameters)
		{
			try { return type?.GetMethod(name, Any, null, parameters, null); } catch { return null; }
		}

		internal static PropertyInfo QuietProperty(Type type, string name)
		{
			try { return type?.GetProperty(name, Any); } catch { return null; }
		}


		private static readonly HashSet<string> Warned = new HashSet<string>();

		private static void WarnOnce(string key, string message)
		{
			lock (Warned)
			{
				if (!Warned.Add(key)) return;
			}
			Plugin.Log?.LogWarning(message + " This is logged once.");
		}

		internal static string Unwrap(Exception exception)
		{
			Exception inner = exception is TargetInvocationException && exception.InnerException != null ? exception.InnerException : exception;
			return inner.GetType().Name + ": " + inner.Message;
		}
	}
}
