using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DriftwoodHost;
using FishNet;
using FishNet.Transporting;
using FishNet.Transporting.Multipass;
using FishNet.Transporting.UTP;
using HarmonyLib;
using UnityEngine;

namespace DriftwoodBenchClient
{
	// A headless load generator. NOT part of the product - see bench/NOT-SHIPPED.md.
	//
	// It joins a Driftwood server the same way the game's own JoinOfflineLobby does (the only
	// difference is the address, which the game hardcodes to "localhost"), waits for its player to
	// spawn, and then drives PlayerMovement's private _moveInput field so the character actually
	// WALKS. Driving the input field rather than teleporting matters: this is a physics game, so
	// the cost we are trying to measure is the host simulating and replicating a moving rigidbody,
	// and a teleport skips exactly that.
	[BepInPlugin("com.humangenome.driftwood.benchclient", "Driftwood Bench Client", "0.1.0")]
	public class BenchClientPlugin : BaseUnityPlugin
	{
		internal static ManualLogSource L;

		private ConfigEntry<string> _address;
		private ConfigEntry<int> _port;
		private ConfigEntry<bool> _walk;
		private ConfigEntry<float> _turnSeconds;
		private ConfigEntry<float> _startDelay;
		private ConfigEntry<string> _chatLines;
		private ConfigEntry<float> _chatStart;
		private ConfigEntry<float> _chatGap;
		private ConfigEntry<string> _steamId;
		private ConfigEntry<string> _personaName;
		private ConfigEntry<string> _joinVia;

		private FieldInfo _moveInputField;
		private FieldInfo _sprintInputField;
		private object _movement;
		private float _nextTurn;
		private float _nextHop;
		private Vector3 _origin;
		private Vector3 _spawnSeen;
		private int _hops;
		private Vector2 _direction = Vector2.up;
		private readonly System.Random _random = new System.Random();

		private void Awake()
		{
			L = Logger;
			Silence.Install();

			_address = Config.Bind("Bench", "Address", "127.0.0.1", "Server address to dial.");
			_port = Config.Bind("Bench", "Port", 7801, "Server port.");
			_walk = Config.Bind("Bench", "Walk", true, "Drive the player's movement input so the host has to simulate a moving body.");
			_turnSeconds = Config.Bind("Bench", "TurnSeconds", 4f, "How often to pick a new direction.");
			_startDelay = Config.Bind("Bench", "StartDelaySeconds", 12f, "Seconds before dialling.");
			// The chat harness. The product feature under test is server-side player chat commands
			// (!stuck, !playtime, !top, !help): a vanilla client types them into the game's own chat
			// and the host answers. There is no way to prove that path from the server alone - it
			// needs a real connection, a real Player and a real ServerRpc - so this rig sends the
			// lines itself, exactly as ChatManager.SendTypedMessage does, and logs what comes back.
			_chatLines = Config.Bind("Bench", "ChatLines", "", "Semicolon-separated chat lines to send once spawned. Empty = send nothing.");
			_chatStart = Config.Bind("Bench", "ChatStartSeconds", 10f, "Seconds after spawn before the first chat line.");
			_chatGap = Config.Bind("Bench", "ChatGapSeconds", 6f, "Seconds between chat lines. Keep above the server's per-player reply gap.");

			// The identity-claim rig (DriftwoodConnect 0.1.4+, loaded beside this plugin). A headless
			// client has no Steam, so the Steam guard below answers the host placeholder - which the
			// claim correctly refuses to send. SteamId gives this process a real account id to answer
			// with instead, so the shipped DriftwoodConnect reads it through the same
			// SteamUser.GetSteamID call it makes on a retail client. Empty = the old behaviour.
			_steamId = Config.Bind("Bench", "SteamId", "", "SteamID64 this headless client reports as signed in. Empty = the host placeholder (no claim).");
			_personaName = Config.Bind("Bench", "PersonaName", "", "Steam persona name this headless client reports. Empty = unchanged.");
			// "direct" dials the transport itself (the original rig). "menu" types the address into the
			// game's own Join Game lobby box and presses its Join button, which is DriftwoodConnect's
			// menu-join path end to end.
			_joinVia = Config.Bind("Bench", "JoinVia", "direct", "direct = dial the transport; menu = type the address into the Join Game box and press Join (needs DriftwoodConnect).");
			ulong benchId;
			if (ulong.TryParse((_steamId.Value ?? "").Trim(), out benchId) && benchId != 0UL)
			{
				SteamGuards.CurrentIdentity = () => benchId;
				Logger.LogInfo("BENCH_STEAMID " + benchId);
			}
			if (!string.IsNullOrWhiteSpace(_personaName.Value)) DriftwoodIdentity.HostDisplayName = _personaName.Value.Trim();

			// The client is headless too, so it needs the same guards. In particular
			// SteamFriends.GetPersonaName / GetFriendPersonaName: without them Player.OnStartClient
			// throws, the throw escapes into FishNet's shared spawn loop and the whole spawn batch
			// aborts, so the peer never receives its own Client object.
			List<PatchTarget> targets = new List<PatchTarget>();
			targets.AddRange(SteamGuards.Targets());
			targets.AddRange(HeadlessPatches.Targets());
			PatchReport report = PatchPlan.Apply(new Harmony("com.humangenome.driftwood.benchclient"),
				targets, Logger.LogInfo, Logger.LogWarning);
			if (!report.CanHost)
			{
				Logger.LogError("BENCH CLIENT WILL NOT RUN: " + report.Reason());
				return;
			}

			// A freshly-joined player is put through the intro and the tutorial, and the game blocks
			// movement input while that runs - so the first bench run produced a spawned player
			// standing perfectly still. For a LOAD GENERATOR that is the wrong measurement: the
			// cost we are after is the host simulating and replicating a MOVING rigidbody.
			try
			{
				Harmony unblock = new Harmony("com.humangenome.driftwood.benchclient.unblock");
				MethodInfo getter = AccessTools.PropertyGetter(typeof(Player), "BlockInputs");
				if (getter != null)
				{
					unblock.Patch(getter, postfix: new HarmonyMethod(
						AccessTools.Method(typeof(BenchClientPlugin), nameof(NeverBlockInputs))));
				}
			}
			catch (Exception exception)
			{
				Logger.LogWarning("Could not unblock player input: " + exception.Message);
			}

			// Log every chat line this client RECEIVES, and swallow it. The observers broadcast
			// lands in OnlineChatManager's RPC logic, which hands off to ChatManager - a UI
			// component that does not exist headless - so a prefix that returns false both keeps
			// the client alive and gives the run a transcript of the server's answers.
			try
			{
				Harmony chat = new Harmony("com.humangenome.driftwood.benchclient.chat");
				MethodInfo received = AccessTools.Method(
					AccessTools.TypeByName("OnlineChatManager"), "RpcLogic___SendChatMessage___3264264606",
					new[] { typeof(ulong), typeof(string) });
				if (received != null)
				{
					chat.Patch(received, prefix: new HarmonyMethod(
						AccessTools.Method(typeof(BenchClientPlugin), nameof(ChatReceivedPrefix))));
					Logger.LogInfo("BENCH_CHAT_HOOK ok");
				}
				else Logger.LogWarning("BENCH_CHAT_HOOK missing - cannot record the server's replies.");
			}
			catch (Exception exception)
			{
				Logger.LogWarning("BENCH_CHAT_HOOK failed: " + exception.Message);
			}

			InstallLabHooks();

			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = 30;

			StartCoroutine(Run());
		}

		private IEnumerator Run()
		{
			yield return new WaitForSecondsRealtime(_startDelay.Value);

			if (string.Equals((_joinVia.Value ?? "").Trim(), "menu", StringComparison.OrdinalIgnoreCase))
			{
				if (!MenuJoin()) yield break;
				yield return new WaitForSecondsRealtime(1f);
				MainMenuManager.InstantCrash();
				yield return WaitForSpawnThenWalk();
				yield break;
			}

			ConnectionManager connectionManager = ConnectionManager.Instance;
			Multipass multipass =
				AccessTools.Field(typeof(ConnectionManager), "_multipass")?.GetValue(connectionManager) as Multipass;
			if (multipass == null)
			{
				Logger.LogError("No Multipass; cannot dial.");
				yield break;
			}
			multipass.SetClientTransport<UnityTransport>();
			ConnectionManager.IsUsingSteam = false;

			Transport transport = multipass.ClientTransport;
			transport.SetClientAddress(_address.Value);
			transport.SetPort((ushort)_port.Value);
			bool dialled = transport.StartConnection(false);
			Logger.LogInfo("BENCH dialling " + _address.Value + ":" + _port.Value + " -> " + dialled);
			if (!dialled) yield break;

			// JoinOfflineLobby ends here too. Leaving the menu is what lets the island load and
			// stops Client.SendSpawnPlayer waiting on Island.CurIsland forever.
			MainMenuManager.CrashAnimation();
			yield return new WaitForSecondsRealtime(1f);
			MainMenuManager.InstantCrash();

			yield return WaitForSpawnThenWalk();
		}

		// The Join Game screen, used the way a player uses it: the address goes into the real
		// lobby-ID box (its own change handler runs) and the real Join button's onClick fires,
		// so the game's ButtonManager.JoinByIDButton and whatever DriftwoodConnect hangs on it
		// run exactly as on a retail client.
		private bool MenuJoin()
		{
			ButtonManager menu = UnityEngine.Object.FindObjectOfType<ButtonManager>(true);
			if (menu == null) { Logger.LogError("BENCH_MENU no ButtonManager in the scene"); return false; }
			var field = AccessTools.Field(typeof(ButtonManager), "_lobbyIdInputField")?.GetValue(menu) as TMPro.TMP_InputField;
			var button = AccessTools.Field(typeof(ButtonManager), "_joinByIDButton")?.GetValue(menu) as UnityEngine.UI.Button;
			if (field == null || button == null) { Logger.LogError("BENCH_MENU lobby box or Join button missing"); return false; }

			string address = _address.Value + ":" + _port.Value;
			field.text = address;
			menu.OnLobbyIDInputFieldChange();
			Logger.LogInfo("BENCH_MENU typed '" + field.text + "' joinEnabled=" + button.enabled + " interactable=" + button.interactable);
			if (!button.enabled) { Logger.LogError("BENCH_MENU the Join button stayed disabled for an address"); return false; }
			button.onClick.Invoke();
			Logger.LogInfo("BENCH_MENU pressed Join");
			return true;
		}

		private IEnumerator WaitForSpawnThenWalk()
		{
			float deadline = Time.realtimeSinceStartup + 180f;
			while (Player.LocalPlayer == null)
			{
				if (Time.realtimeSinceStartup > deadline)
				{
					Logger.LogError("BENCH no local player after 180s; not generating load.");
					yield break;
				}
				yield return new WaitForSecondsRealtime(1f);
			}

			_movement = Player.LocalPlayer.Movement;
			Type movementType = _movement.GetType();
			_moveInputField = AccessTools.Field(movementType, "_moveInput");
			_sprintInputField = AccessTools.Field(movementType, "_sprintInput");
			Logger.LogInfo("BENCH_SPAWNED localPlayer=true moveInputField=" + (_moveInputField != null));

			// Only when asked: the chat script binds Server.SendChatMessage at compile time, and a
			// game build that changed that signature throws MissingMethodException at JIT.
			if (!string.IsNullOrWhiteSpace(_chatLines.Value)) StartCoroutine(ChatScript());

			Vector3 spawnedAt = Player.LocalPlayer.Transform.position;
			float furthest = 0f;
			int respawns = 0;
			while (true)
			{
				yield return new WaitForSecondsRealtime(5f);
				// A fresh character can spawn dead (the water at the wreck); a dead body ignores
				// input and teleports, which reads as a join that cannot move. Do what a player
				// does: press Respawn - PlayerDying.LocalRespawn, the handler the button reaches,
				// which round-trips Server.RespawnPlayer. Reflection-only across game builds.
				bool dead = BenchIsDead();
				if (dead && respawns < 5)
				{
					respawns++;
					Logger.LogInfo("BENCH_DEAD at " + (Player.LocalPlayer != null ? Player.LocalPlayer.Transform.position.ToString("F1") : "?") +
						"; respawning (attempt " + respawns + ")");
					BenchRespawn();
					yield return new WaitForSecondsRealtime(3f);
					if (Player.LocalPlayer != null) spawnedAt = Player.LocalPlayer.Transform.position;
					dead = BenchIsDead();
				}
				if (Player.LocalPlayer != null)
				{
					furthest = Mathf.Max(furthest, Vector3.Distance(spawnedAt, Player.LocalPlayer.Transform.position));
				}
				Logger.LogInfo("BENCH alive players=" + (PlayerManager.Players?.Count ?? -1) +
					" pos=" + (Player.LocalPlayer != null ? Player.LocalPlayer.Transform.position.ToString("F1") : "?") +
					" hops=" + _hops + " dead=" + dead + " furthest=" + furthest.ToString("F1") + "m respawns=" + respawns);
			}
		}

		// The client's chat ServerRpc changed shape in game 1.0.10: SendChatMessage(ulong, string)
		// became SendChatMessage(string, NetworkConnection = null). Call whichever this build has,
		// by reflection, so the rig binds to neither at compile time.
		private static void BenchSendChat(ulong id, string line)
		{
			object server = Server.Instance;
			MethodInfo byConnection = AccessTools.Method(typeof(Server), "SendChatMessage", new[] { typeof(string), typeof(FishNet.Connection.NetworkConnection) });
			if (byConnection != null) { byConnection.Invoke(server, new object[] { line, null }); return; }
			MethodInfo byId = AccessTools.Method(typeof(Server), "SendChatMessage", new[] { typeof(ulong), typeof(string) });
			if (byId != null) { byId.Invoke(server, new object[] { id, line }); return; }
			throw new MissingMethodException("Server", "SendChatMessage");
		}

		private static Component BenchDying()
		{
			try { return Player.LocalPlayer != null ? Player.LocalPlayer.GetComponent("PlayerDying") : null; }
			catch { return null; }
		}

		private static bool BenchIsDead()
		{
			try
			{
				Component dying = BenchDying();
				PropertyInfo property = dying?.GetType().GetProperty("IsDead");
				return property != null && (bool)property.GetValue(dying);
			}
			catch { return false; }
		}

		private void BenchRespawn()
		{
			try
			{
				Component dying = BenchDying();
				MethodInfo respawn = dying?.GetType().GetMethod("LocalRespawn", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
				if (respawn == null) { Logger.LogWarning("BENCH respawn: LocalRespawn not found"); return; }
				respawn.Invoke(dying, respawn.GetParameters().Length == 0 ? null : new object[respawn.GetParameters().Length]);
			}
			catch (Exception exception)
			{
				Logger.LogWarning("BENCH respawn failed: " + exception.GetType().Name + ": " + exception.Message);
			}
		}

		private static void NeverBlockInputs(ref bool __result) => __result = false;

		// false = do not run the game's own handler (ChatManager is UI and absent headless).
		private static bool ChatReceivedPrefix(ulong __0, string __1)
		{
			L?.LogInfo("BENCH_CHAT_RECV from=" + __0 + " text=" + (__1 ?? string.Empty));
			return false;
		}

		// Sends each configured line through the game's OWN ServerRpc - the same call
		// ChatManager.SendTypedMessage makes when a player presses Enter - so the server sees a
		// genuine inbound chat message on a genuine connection. Position is logged around each
		// line so a teleport the server orders is visible as a real move.
		private IEnumerator ChatScript()
		{
			string configured = (_chatLines.Value ?? string.Empty).Trim();
			if (configured.Length == 0) yield break;
			try { if (Player.LocalPlayer != null) _spawnSeen = Player.LocalPlayer.Transform.position; } catch { }
			L?.LogInfo("BENCH_CHAT_START spawnSeen=" + _spawnSeen.ToString("F1"));
			string[] lines = configured.Split(';');

			yield return new WaitForSecondsRealtime(_chatStart.Value);

			for (int i = 0; i < lines.Length; i++)
			{
				string line = lines[i].Trim();
				if (line.Length == 0) continue;

				// "@move" is a harness step, not a chat line: walk the character away from the
				// island spawn so a server-ordered teleport back to it is a MEASURABLE move.
				// A player that is already standing on the spawn proves nothing about !stuck.
				// Issue #1 harness steps (see LabSteps below): "@bomb[:seconds]", "@boss[:seconds]",
				// "@wait:seconds", "@spawnables".
				if (line.StartsWith("@bomb", StringComparison.OrdinalIgnoreCase))
				{
					yield return BombStep(StepSeconds(line, 120f));
					continue;
				}
				if (line.StartsWith("@boss", StringComparison.OrdinalIgnoreCase))
				{
					// Runs beside the rest of the script: the owner may spawn and kill the boss at any time.
					StartCoroutine(BossStep(StepSeconds(line, 300f)));
					continue;
				}
				if (line.StartsWith("@wait", StringComparison.OrdinalIgnoreCase))
				{
					float wait = StepSeconds(line, 10f);
					L?.LogInfo("BENCH_WAIT " + wait.ToString("F0") + "s");
					yield return new WaitForSecondsRealtime(wait);
					continue;
				}
				if (line.StartsWith("@spawnables", StringComparison.OrdinalIgnoreCase))
				{
					LogSpawnables();
					continue;
				}

				if (line.StartsWith("@move", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#move", StringComparison.OrdinalIgnoreCase))
				{
					try
					{
						if (Player.LocalPlayer != null)
						{
							if (_spawnSeen == Vector3.zero) _spawnSeen = Player.LocalPlayer.Transform.position;
							Vector3 away = _spawnSeen + new Vector3(60f, 0f, 60f);
							Player.LocalPlayer.LocalTeleport(away, 0f);
							L?.LogInfo("BENCH_CHAT_MOVE to=" + away.ToString("F1") + " spawnSeen=" + _spawnSeen.ToString("F1"));
						}
					}
					catch (Exception exception) { L?.LogError("BENCH_CHAT_MOVE failed: " + exception.Message); }
					yield return new WaitForSecondsRealtime(3f);
					continue;
				}

				ulong id = 0UL;
				Vector3 before = Vector3.zero;
				try
				{
					if (Player.LocalPlayer == null) { L?.LogWarning("BENCH_CHAT_SEND skipped - no local player."); continue; }
					id = Player.LocalPlayer.SteamID;
					before = Player.LocalPlayer.Transform.position;
					L?.LogInfo("BENCH_CHAT_SEND from=" + id + " pos=" + before.ToString("F1") + " text=" + line);
					BenchSendChat(id, line);
				}
				catch (Exception exception)
				{
					L?.LogError("BENCH_CHAT_SEND failed: " + exception);
				}

				yield return new WaitForSecondsRealtime(_chatGap.Value);

				try
				{
					if (Player.LocalPlayer != null)
					{
						Vector3 after = Player.LocalPlayer.Transform.position;
						L?.LogInfo("BENCH_CHAT_AFTER text=" + line + " pos=" + after.ToString("F1") +
							" moved=" + Vector3.Distance(before, after).ToString("F1") +
							" distToSpawnSeen=" + (_spawnSeen == Vector3.zero ? "?" : Vector3.Distance(_spawnSeen, after).ToString("F1")));
					}
				}
				catch { }
			}
			L?.LogInfo("BENCH_CHAT_DONE");
		}

		// ---- Issue #1 lab steps (DriftwoodServer issue #1: bombs with no explosion, boss death hang) ----
		//
		// What a player saw in that issue is decided on the server. The server plays an explosion's
		// effects, then sends ObserverExplode to every client and despawns the spent explosive; when the
		// effects step threw (a dedicated host has no local player), neither the message nor the despawn
		// happened. So the proof is what reaches a real connection:
		//   @bomb  waits for an explosive near this player (the owner drops one with `spawn <name>`),
		//          lights it through Server.ActivateExplosive (the ServerRpc a player's click sends), and
		//          logs whether the server's explosion message arrived and the explosive left the world.
		//   @boss  waits for a boss fight (the owner's `spawn <boss>`), then for its death (`killboss`),
		//          then for the dead boss to leave the world here. A boss that dies and never leaves is
		//          the death scene the issue says players were stuck in.
		// Both read the game by reflection, so the rig binds to no member that a game update can move.
		private static int _explodeReceived;
		private static int _bossDied;

		private void InstallLabHooks()
		{
			try
			{
				Harmony lab = new Harmony("com.humangenome.driftwood.benchclient.lab");
				Type explosionManager = AccessTools.TypeByName("ExplosionManager");
				MethodInfo observerExplode = null;
				if (explosionManager != null)
				{
					foreach (MethodInfo method in explosionManager.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
					{
						if (method.Name.StartsWith("RpcLogic___ObserverExplode", StringComparison.Ordinal)) { observerExplode = method; break; }
					}
				}
				if (observerExplode != null)
				{
					lab.Patch(observerExplode, prefix: new HarmonyMethod(
						AccessTools.Method(typeof(BenchClientPlugin), nameof(ObserverExplodePrefix))));
					Logger.LogInfo("BENCH_LAB_HOOK explode ok (" + observerExplode.Name + ")");
				}
				else Logger.LogWarning("BENCH_LAB_HOOK explode missing: @bomb cannot see the server's explosion message.");

				EventInfo death = AccessTools.TypeByName("BossManager")?.GetEvent("OnGlobalBossDeath",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (death != null)
				{
					death.AddEventHandler(null, new Action(() => { _bossDied++; L?.LogInfo("BENCH_BOSS_EVENT death"); }));
					Logger.LogInfo("BENCH_LAB_HOOK boss death ok");
				}
				else Logger.LogWarning("BENCH_LAB_HOOK boss death event missing.");
			}
			catch (Exception exception)
			{
				Logger.LogWarning("BENCH_LAB_HOOK failed: " + exception.GetType().Name + ": " + exception.Message);
			}
		}

		// true = the game's own handler still runs.
		private static bool ObserverExplodePrefix(object __0)
		{
			_explodeReceived++;
			string name = "?";
			try { UnityEngine.Object item = __0 as UnityEngine.Object; if (item != null) name = item.name; } catch { }
			L?.LogInfo("BENCH_EXPLODE_RECV item=" + name + " count=" + _explodeReceived);
			return true;
		}

		private static float StepSeconds(string line, float fallback)
		{
			int colon = line.IndexOf(':');
			float value;
			if (colon > 0 && float.TryParse(line.Substring(colon + 1).Trim(), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out value) && value > 0f) return value;
			return fallback;
		}

		private IEnumerator BombStep(float seconds)
		{
			L?.LogInfo("BENCH_BOMB_WAIT up to " + seconds.ToString("F0") + "s for an explosive within 25 m");
			Type explosiveType = AccessTools.TypeByName("Explosive");
			if (explosiveType == null) { L?.LogError("BENCH_BOMB_RESULT FAIL no Explosive type in this build"); yield break; }
			// A player that has not moved since spawning has never sent the server its position, so
			// the server still has it at the world origin and an owner `spawn` lands there. The
			// game's own teleport sends the position on the next tick (Server.UpdatePlayerPosRot).
			try
			{
				if (Player.LocalPlayer != null)
				{
					Player.LocalPlayer.LocalTeleport(Player.LocalPlayer.Transform.position, 0f);
					L?.LogInfo("BENCH_BOMB_SYNC sent position " + Player.LocalPlayer.Transform.position.ToString("F1"));
				}
			}
			catch (Exception exception) { L?.LogWarning("BENCH_BOMB_SYNC failed: " + exception.Message); }
			float deadline = Time.realtimeSinceStartup + seconds;
			float nextReport = 0f;
			float startedAt = Time.realtimeSinceStartup;
			bool walked = false;
			Component bomb = null;
			while (bomb == null && Time.realtimeSinceStartup < deadline)
			{
				bomb = NearestOf(explosiveType, 25f);
				if (bomb != null) break;
				// One landed somewhere else (the server's idea of where this player stood): walk to it
				// the way a player would, by the game's own teleport, then pick it up on the next pass.
				Component far = walked ? null : NearestOf(explosiveType, 5000f);
				if (far != null && Time.realtimeSinceStartup - startedAt > 3f)
				{
					walked = true;
					try
					{
						Vector3 next = far.transform.position + new Vector3(2f, 0.5f, 0f);
						Player.LocalPlayer.LocalTeleport(next, 0f);
						L?.LogInfo("BENCH_BOMB_GOTO " + far.name + " at " + far.transform.position.ToString("F1"));
					}
					catch (Exception exception) { L?.LogWarning("BENCH_BOMB_GOTO failed: " + exception.Message); }
					yield return new WaitForSecondsRealtime(2f);
					continue;
				}
				if (Time.realtimeSinceStartup >= nextReport)
				{
					nextReport = Time.realtimeSinceStartup + 10f;
					L?.LogInfo("BENCH_BOMB_SEARCH " + DescribeAll(explosiveType));
				}
				yield return new WaitForSecondsRealtime(1f);
			}
			if (bomb == null) { L?.LogError("BENCH_BOMB_RESULT FAIL no explosive appeared within 25 m: " + DescribeAll(explosiveType)); yield break; }
			string name = bomb.name;
			L?.LogInfo("BENCH_BOMB_FOUND item=" + name + " dist=" + DistanceTo(bomb).ToString("F1"));
			yield return new WaitForSecondsRealtime(2f);
			if (bomb == null) { L?.LogError("BENCH_BOMB_RESULT FAIL item=" + name + " left the world before it was lit"); yield break; }

			int before = _explodeReceived;
			string failure = ActivateExplosive(bomb);
			if (failure != null) { L?.LogError("BENCH_BOMB_RESULT FAIL could not light it: " + failure); yield break; }
			L?.LogInfo("BENCH_BOMB_LIT item=" + name + " (Server.ActivateExplosive)");
			float litAt = Time.realtimeSinceStartup;
			bool gone = false;
			while (Time.realtimeSinceStartup - litAt < 30f)
			{
				yield return new WaitForSecondsRealtime(0.5f);
				gone = bomb == null;
				if (gone && _explodeReceived > before) break;
			}
			bool message = _explodeReceived > before;
			L?.LogInfo("BENCH_BOMB_RESULT " + (message && gone ? "PASS" : "FAIL") + " item=" + name +
				" explosion_message=" + message + " left_world=" + gone +
				" seconds_after_lit=" + (Time.realtimeSinceStartup - litAt).ToString("F1"));
		}

		private IEnumerator BossStep(float seconds)
		{
			L?.LogInfo("BENCH_BOSS_WAIT up to " + seconds.ToString("F0") + "s for a boss fight");
			float deadline = Time.realtimeSinceStartup + seconds;
			Component boss = null;
			while (boss == null && Time.realtimeSinceStartup < deadline)
			{
				boss = CurrentBoss();
				if (boss == null) yield return new WaitForSecondsRealtime(0.5f);
			}
			if (boss == null) { L?.LogError("BENCH_BOSS_RESULT FAIL no boss fight started"); yield break; }
			string name = boss.name;
			int died = _bossDied;
			L?.LogInfo("BENCH_BOSS_SEEN boss=" + name + " dist=" + DistanceTo(boss).ToString("F1"));

			while (_bossDied == died && boss != null && Time.realtimeSinceStartup < deadline)
				yield return new WaitForSecondsRealtime(0.5f);
			if (_bossDied == died)
			{
				L?.LogError("BENCH_BOSS_RESULT FAIL boss=" + name + " never died here (left_world=" + (boss == null) + ")");
				yield break;
			}
			float diedAt = Time.realtimeSinceStartup;
			L?.LogInfo("BENCH_BOSS_DIED boss=" + name);
			// A dead boss leaves the world through its despawn; its OnStopClient plays the death burst.
			// 90 s is far longer than the death scene; a boss still here then is the hang.
			while (boss != null && Time.realtimeSinceStartup - diedAt < 90f)
				yield return new WaitForSecondsRealtime(0.5f);
			bool gone = boss == null;
			L?.LogInfo("BENCH_BOSS_RESULT " + (gone ? "PASS" : "FAIL") + " boss=" + name + " died=true left_world=" + gone +
				" seconds_after_death=" + (Time.realtimeSinceStartup - diedAt).ToString("F1"));
		}

		private static Component CurrentBoss()
		{
			try
			{
				PropertyInfo property = AccessTools.TypeByName("BossManager")?.GetProperty("Boss",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				return property?.GetValue(null) as Component;
			}
			catch { return null; }
		}

		private static Component NearestOf(Type type, float range)
		{
			if (Player.LocalPlayer == null) return null;
			Component best = null;
			float bestDistance = range;
			foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsOfType(type, true))
			{
				Component component = found as Component;
				if (component == null) continue;
				float distance = DistanceTo(component);
				if (distance >= 0f && distance <= bestDistance) { best = component; bestDistance = distance; }
			}
			return best;
		}

		private static string DescribeAll(Type type)
		{
			try
			{
				List<string> parts = new List<string>();
				foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsOfType(type, true))
				{
					Component component = found as Component;
					if (component == null) continue;
					parts.Add(component.name + "@" + DistanceTo(component).ToString("F0") + "m" + (component.gameObject.activeInHierarchy ? "" : "(inactive)"));
					if (parts.Count >= 8) break;
				}
				string me = Player.LocalPlayer != null ? Player.LocalPlayer.Transform.position.ToString("F1") : "?";
				return "me=" + me + " explosives=" + parts.Count + (parts.Count > 0 ? " [" + string.Join(", ", parts.ToArray()) + "]" : "");
			}
			catch (Exception exception) { return "search failed: " + exception.Message; }
		}

		private static float DistanceTo(Component component)
		{
			try { return Vector3.Distance(Player.LocalPlayer.Transform.position, component.transform.position); }
			catch { return -1f; }
		}

		// Server.ActivateExplosive(Explosive, uint tick, bool forced = false, bool instant = false,
		// Player playerWhoForced = null) on the builds read so far; filled by position and type.
		private static string ActivateExplosive(Component bomb)
		{
			try
			{
				if (Server.Instance == null) return "no Server instance";
				MethodInfo activate = null;
				foreach (MethodInfo method in typeof(Server).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (method.Name == "ActivateExplosive") { activate = method; break; }
				}
				if (activate == null) return "Server.ActivateExplosive is not in this build";
				ParameterInfo[] parameters = activate.GetParameters();
				object[] args = new object[parameters.Length];
				for (int i = 0; i < parameters.Length; i++)
				{
					Type type = parameters[i].ParameterType;
					if (i == 0) args[i] = bomb;
					else if (type == typeof(uint)) args[i] = InstanceFinder.TimeManager.Tick;
					else if (parameters[i].HasDefaultValue) args[i] = parameters[i].DefaultValue;
					else args[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
				}
				activate.Invoke(Server.Instance, args);
				return null;
			}
			catch (Exception exception)
			{
				Exception inner = exception.InnerException ?? exception;
				return inner.GetType().Name + ": " + inner.Message;
			}
		}

		// Every name the owner's `spawn` command accepts (the game's own spawnable catalogue).
		private static void LogSpawnables()
		{
			try
			{
				FieldInfo field = AccessTools.Field(AccessTools.TypeByName("GameInfo"), "_nameToSpawnable");
				System.Collections.IDictionary map = field?.GetValue(null) as System.Collections.IDictionary;
				if (map == null) { L?.LogWarning("BENCH_SPAWNABLES none (catalogue not found)"); return; }
				List<string> names = new List<string>();
				foreach (object key in map.Keys) names.Add(key as string ?? "?");
				names.Sort(StringComparer.Ordinal);
				L?.LogInfo("BENCH_SPAWNABLES " + names.Count + ": " + string.Join(",", names.ToArray()));
			}
			catch (Exception exception)
			{
				L?.LogWarning("BENCH_SPAWNABLES failed: " + exception.Message);
			}
		}

		private void FixedUpdate()
		{
			// THREE escalating ways to make the character move, because two of them did not work.
			//
			// 1. Drive _moveInput (in Update). Blocked while the intro and tutorial hold the player.
			// 2. Push the rigidbody directly. Still produced a stationary player - the movement
			//    component appears to be disabled or to zero the velocity while the intro runs.
			// 3. Use the game's OWN teleport, which is what this does. Player.LocalTeleport sets
			//    _sendTeleport, and the next tick sends the new position and rotation to the server
			//    through Server.UpdatePlayerPosRot. That guarantees the host processes real position
			//    updates and moves a real body in the world, which is the load being measured.
			//
			// A bench rig that measures a stationary player is measuring the wrong thing, and it
			// says so in the log rather than reporting a number nobody can interpret.
			if (!_walk.Value || Player.LocalPlayer == null) return;
			try
			{
				Rigidbody body = Player.LocalPlayer.Rigidbody;
				if (body != null)
				{
					Vector3 wanted = new Vector3(_direction.x, 0f, _direction.y) * 4f;
					Vector3 current = body.linearVelocity;
					if (new Vector2(current.x, current.z).sqrMagnitude < 1f)
					{
						body.linearVelocity = new Vector3(wanted.x, current.y, wanted.z);
					}
				}

				if (Time.realtimeSinceStartup >= _nextHop)
				{
					_nextHop = Time.realtimeSinceStartup + 0.5f;
					Vector3 from = _origin == Vector3.zero ? Player.LocalPlayer.Transform.position : _origin;
					if (_origin == Vector3.zero) _origin = from;
					Vector3 to = _origin + new Vector3(_direction.x, 0f, _direction.y) * (float)(_random.NextDouble() * 12.0 + 2.0);
					Player.LocalPlayer.LocalTeleport(to, (float)(_random.NextDouble() * 360.0));
					_hops++;
				}
			}
			catch
			{
			}
		}

		private void Update()
		{
			if (!_walk.Value || _movement == null || _moveInputField == null) return;
			if (Time.realtimeSinceStartup >= _nextTurn)
			{
				_nextTurn = Time.realtimeSinceStartup + Mathf.Max(0.5f, _turnSeconds.Value);
				double angle = _random.NextDouble() * Math.PI * 2.0;
				_direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
			}
			try
			{
				_moveInputField.SetValue(_movement, _direction);
				_sprintInputField?.SetValue(_movement, true);
			}
			catch
			{
				// A bench rig must never be able to change what it is measuring by throwing.
			}
		}
	}
}
