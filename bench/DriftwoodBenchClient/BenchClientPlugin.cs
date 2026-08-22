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

		private FieldInfo _moveInputField;
		private FieldInfo _sprintInputField;
		private object _movement;
		private float _nextTurn;
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

			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = 30;

			StartCoroutine(Run());
		}

		private IEnumerator Run()
		{
			yield return new WaitForSeconds(_startDelay.Value);

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
			yield return new WaitForSeconds(1f);
			MainMenuManager.InstantCrash();

			float deadline = Time.realtimeSinceStartup + 180f;
			while (Player.LocalPlayer == null)
			{
				if (Time.realtimeSinceStartup > deadline)
				{
					Logger.LogError("BENCH no local player after 180s; not generating load.");
					yield break;
				}
				yield return new WaitForSeconds(1f);
			}

			_movement = Player.LocalPlayer.Movement;
			Type movementType = _movement.GetType();
			_moveInputField = AccessTools.Field(movementType, "_moveInput");
			_sprintInputField = AccessTools.Field(movementType, "_sprintInput");
			Logger.LogInfo("BENCH_SPAWNED localPlayer=true moveInputField=" + (_moveInputField != null));

			while (true)
			{
				yield return new WaitForSeconds(5f);
				Logger.LogInfo("BENCH alive players=" + (PlayerManager.Players?.Count ?? -1) +
					" pos=" + (Player.LocalPlayer != null ? Player.LocalPlayer.Transform.position.ToString("F1") : "?"));
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
