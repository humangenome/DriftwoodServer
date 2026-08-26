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

			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = 30;

			StartCoroutine(Run());
		}

		private IEnumerator Run()
		{
			yield return new WaitForSecondsRealtime(_startDelay.Value);

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

			StartCoroutine(ChatScript());

			while (true)
			{
				yield return new WaitForSecondsRealtime(5f);
				Logger.LogInfo("BENCH alive players=" + (PlayerManager.Players?.Count ?? -1) +
					" pos=" + (Player.LocalPlayer != null ? Player.LocalPlayer.Transform.position.ToString("F1") : "?") +
					" hops=" + _hops);
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
					Server.Instance.SendChatMessage(id, line);
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
