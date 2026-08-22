using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using FishNet;
using FishNet.Transporting;
using FishNet.Transporting.Multipass;
using FishNet.Transporting.UTP;
using HarmonyLib;
using UnityEngine;

namespace DriftwoodHost
{
	// Driftwood - the How to Fish dedicated host.
	//
	// The game already ships a complete dedicated-server code path: offline/singleplayer starts a
	// real FishNet server on FishNet.Transporting.UTP.UnityTransport (raw UDP) through Multipass
	// and connects a local client to it. We are not building multiplayer, not swapping a
	// transport, and not defeating DRM. We are changing a hardcoded address, and then doing all
	// the productisation the shipped menu never had to do.
	[BepInPlugin(Guid, "Driftwood Host", Version)]
	public class Plugin : BaseUnityPlugin
	{
		public const string Guid = "com.humangenome.driftwood.host";
		public const string Version = "0.1.0";

		internal static ManualLogSource Log;

		private HostConfig _config;
		private Readiness _readiness;
		private Transport _transport;
		private HostHttpApi _api;
		private bool _settingsApplied;
		private bool _stopping;
		private string _stopFilePath;
		private readonly List<string> _installedGuards = new List<string>();
		private readonly FrameStats _frames = new FrameStats();

		private void Awake()
		{
			try
			{
				Boot();
			}
			catch (Exception exception)
			{
				// Anything unexpected in boot is still a refusal with a readable sentence, not a
				// stack trace in a 40 MB log. The gameplay port is never presented either way.
				Logger.LogError(exception.ToString());
				Refuse("This server could not start because Driftwood failed while setting it up (" +
					exception.GetType().Name + ": " + exception.Message + ").");
			}
		}

		private void Boot()
		{
			Log = Logger;

			// FIRST, before anything can make a sound. -batchmode does NOT guarantee silence: on
			// a box with an audio device FMOD finds it and plays, which leaked game audio onto the
			// operator's desktop during a probe run.
			Silence.Install();

			// Read the panel's file directly rather than through BepInEx's binder, so a key the
			// panel writes under a different section or a near-miss name still lands, and any key
			// that matched NOTHING is reported instead of silently ignored.
			_config = HostConfig.Load(Path.Combine(Paths.ConfigPath, Guid + ".cfg"));
			string gameRoot = Path.GetDirectoryName(Paths.GameRootPath ?? Paths.BepInExRootPath) ?? Paths.BepInExRootPath;
			BootMarkers.Prepare(_config.ResolveLogsDirectory(Paths.GameRootPath ?? gameRoot));
			string stateDirectory = _config.ResolveStateDirectory(
				Path.Combine(Paths.ConfigPath, "driftwood-state"));
			_readiness = new Readiness(stateDirectory)
			{
				PluginVersion = Version,
				GameVersion = Application.version
			};
			_readiness.UnrecognisedConfigKeys = _config.UnrecognisedKeys;
			_stopFilePath = Path.Combine(stateDirectory, "stop.requested");
			TryDelete(_stopFilePath);

			if (!_config.Loaded)
			{
				Logger.LogWarning("No Driftwood configuration file was found at " + _config.SourcePath +
					"; running on built-in defaults. On a customer server this means the panel never wrote one.");
			}
			foreach (string key in _config.UnrecognisedKeys)
			{
				// Never silent. A key nobody reads is how a slot limit or a save path ends up
				// configured and not in force.
				Logger.LogWarning("Configuration key \"" + key + "\" was not recognised and has been IGNORED.");
			}

			if (!_config.Enabled)
			{
				Refuse("Driftwood hosting is switched off in this server's configuration.");
				return;
			}

			string invalid = _config.Validate();
			if (invalid != null)
			{
				Refuse(invalid);
				return;
			}

			// The status surface comes up BEFORE anything that can refuse, so a server that will
			// not host can still be asked WHY over HTTP instead of only leaving a file behind.
			_readiness.Port = _config.Port;
			_readiness.Slots = _config.MaxPlayers;
			_readiness.WorldName = _config.WorldName;
			_readiness.EffectiveBindAddress = _config.BindAddress;
			_api = new HostHttpApi(_config.EffectiveHttpPort, _readiness, _config.AuthToken, WorldLifecycle.SaveNow);
			if (!_api.Start())
			{
				// The panel decides whether this server is up by asking this endpoint. Hosting
				// without it would produce a server that works and reports as down, which the
				// panel would then restart, forever. Refusing with a reason is the kinder failure.
				Refuse("This server could not open its status port " + _config.EffectiveHttpPort +
					". Something else is using it, so the panel would never be able to tell whether this server was running.");
				return;
			}

			// Saves go into this server's own folder BEFORE anything writes one. The game's
			// default is a per-user folder every instance on the box would share.
			string configuredSaveDirectory = (_config.SaveRoot ?? string.Empty).Trim();
			if (configuredSaveDirectory.Length > 0)
			{
				string failure = WorldLifecycle.RedirectSaveFolder(configuredSaveDirectory);
				if (failure != null)
				{
					// Conditionally required, the same shape as a join password: harmless when
					// nobody configured one, absolutely required the moment somebody does -
					// because the alternative is two servers silently overwriting each other's
					// world.
					Refuse(failure);
					return;
				}
			}
			_readiness.SaveDirectory = WorldLifecycle.SaveDirectoryRedirected
				? WorldLifecycle.EffectiveSaveDirectory
				: Application.persistentDataPath + "/Saves/";
			// The panel asserts on this. It names what the mod ACTUALLY resolved, not what it was
			// asked for, and it lives outside the customer's FTP jail so it cannot be forged.
			BootMarkers.WriteSaveRoot(_readiness.SaveDirectory);

			GhostHost.Suppress = _config.SuppressGhostHost;
			DriftwoodIdentity.HostDisplayName =
				string.IsNullOrEmpty(_config.ServerName) ? "Server" : _config.ServerName;

			// Resolve EVERY patch target before applying ANY of them, report every miss in one
			// block, and assert afterwards that the patch library actually patched what we asked
			// for. On a rebuilt game this turns nine boot-fix-boot cycles into a single boot that
			// names everything that moved.
			List<PatchTarget> targets = new List<PatchTarget>();
			targets.AddRange(SteamGuards.Targets());
			targets.AddRange(HeadlessPatches.Targets());
			targets.AddRange(GhostHost.Targets());
			targets.Add(SlotGuard.RefusalCounterTarget());

			if (!string.IsNullOrWhiteSpace(_config.SimulateMissingPatch))
			{
				PatchPlan.SimulatedMissing = _config.SimulateMissingPatch
					.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
				for (int i = 0; i < PatchPlan.SimulatedMissing.Length; i++)
				{
					PatchPlan.SimulatedMissing[i] = PatchPlan.SimulatedMissing[i].Trim();
				}
				Logger.LogWarning("FAULT INJECTION IS ON. These patch targets are being treated as missing: " +
					string.Join(", ", PatchPlan.SimulatedMissing) + ". Never leave this set on a customer server.");
			}

			Harmony harmony = new Harmony(Guid);
			PatchReport report = PatchPlan.Apply(harmony, targets, Logger.LogInfo, Logger.LogWarning);
			_readiness.PatchesApplied = report.Applied;
			_readiness.PatchesMissing = report.MissingRequired.Count > 0
				? report.MissingRequired
				: report.MissingOptional;
			_readiness.PatchesFailed = report.FailedToApply;
			_readiness.FeaturesStoodDown = report.StoodDownGroups;

			// Only guards that ACTUALLY installed go in the marker. A patch whose target was not
			// found must never appear there, because the panel reads that list as the truth about
			// what is in force.
			_installedGuards.AddRange(report.Applied);
			BootMarkers.WriteGuards(_installedGuards);

			if (!report.CanHost)
			{
				// FAIL CLOSED. A server that cannot host must not present a port.
				Refuse(report.Reason());
				return;
			}

			ApplyFrameCap();

			_readiness.Phase = HostPhase.Starting;
			_readiness.Reason = "Loading the world";
			_readiness.Write();

			StartCoroutine(RunHost());
			StartCoroutine(WatchStopFile());
			StartCoroutine(WatchSwallowRate());
		}

		// A headless Unity build runs its loop as fast as it can and burns a whole core doing it.
		// Playbook 2b measured a 2.8x density difference from the cap alone on the sibling
		// product. Set it explicitly and record what actually took effect; the game itself never
		// touches Application.targetFrameRate, so nothing competes for it.
		private void ApplyFrameCap()
		{
			int wanted = _config.TargetFrameRate;
			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = wanted <= 0 ? -1 : wanted;
			_readiness.EffectiveTargetFrameRate = Application.targetFrameRate;

			// The physics step is wall-clock driven, so no frame cap touches it. Only change it on
			// a deliberate decision - it is simulation fidelity, not overhead.
			if (_config.PhysicsStepSeconds > 0f)
			{
				Time.fixedDeltaTime = _config.PhysicsStepSeconds;
				if (!Mathf.Approximately(Time.fixedDeltaTime, _config.PhysicsStepSeconds))
				{
					Logger.LogWarning("Physics step did not take: asked for " + _config.PhysicsStepSeconds +
						"s, the engine reports " + Time.fixedDeltaTime + "s.");
				}
			}
			_readiness.EffectivePhysicsStepSeconds = Time.fixedDeltaTime;
			if (wanted > 0 && Application.targetFrameRate != wanted)
			{
				Logger.LogWarning("Frame cap did not take: asked for " + wanted +
					", the engine reports " + Application.targetFrameRate + ".");
			}
		}

		private IEnumerator RunHost()
		{
			yield return new WaitForSeconds(_config.StartDelaySeconds);

			ConnectionManager connectionManager = ConnectionManager.Instance;
			if (connectionManager == null)
			{
				Refuse("The game's connection manager was not available, so this server could not start listening.");
				yield break;
			}

			Multipass multipass =
				AccessTools.Field(typeof(ConnectionManager), "_multipass")?.GetValue(connectionManager) as Multipass;
			if (multipass == null)
			{
				Refuse("The game's transport selector has moved, so this server could not choose the direct-UDP transport.");
				yield break;
			}

			// Offline/singleplayer already runs client-server over UnityTransport; select the
			// same one rather than adding a transport of our own.
			multipass.SetClientTransport<UnityTransport>();
			ConnectionManager.IsUsingSteam = false;

			_transport = multipass.ClientTransport;
			if (_transport == null)
			{
				Refuse("The game returned no direct-UDP transport, so this server could not start listening.");
				yield break;
			}

			// The world must be selected BEFORE the server starts: MoneyManager, NPCManager,
			// OnlineIslandManager, BoatManager and EndGameManager all read the selected save
			// during their own server init.
			string worldFailure = WorldLifecycle.LoadOrCreateWorld(_config.WorldName);
			if (worldFailure != null)
			{
				Refuse(worldFailure);
				yield break;
			}
			Logger.LogInfo((WorldLifecycle.CreatedNewWorld ? "Created" : "Loaded") +
				" world \"" + WorldLifecycle.SelectedWorld + "\" in " + _readiness.SaveDirectory);

			// Slots BEFORE StartConnection - afterwards the setter is silently ignored.
			// HostMode is a LOCKED INVARIANT, not a setting: Client.OnStartServer builds the world
			// only for a local client, so a server without one presents a port and an empty
			// universe. It is honoured as a config key because the panel writes it, but it is
			// never obeyed downwards.
			if (!_config.HostMode)
			{
				Logger.LogWarning("HostMode was set to false. It is being ignored: this game only builds its world for a local client, so a server without one would bind a port and host nothing.");
			}
			// The loopback connection occupies a transport slot either way. The only question is
			// whether it is SOLD, and the answer is no unless somebody explicitly asked for it.
			string slotFailure = SlotGuard.Configure(_transport, _config.MaxPlayers, hostMode: !_config.CountHostPlayer);
			if (slotFailure != null)
			{
				Refuse(slotFailure);
				yield break;
			}
			_readiness.TransportMaxClients = SlotGuard.ConfiguredMaxClients;

			// An EMPTY ServerListenAddress resolves to NetworkEndpoint.LoopbackIpv4 - reachable
			// only from this box. Always set it explicitly.
			_transport.SetServerBindAddress(_config.BindAddress, IPAddressType.IPv4);
			_transport.SetClientAddress("127.0.0.1");
			_transport.SetPort((ushort)_config.Port);

			GameInfo.GenerateSeed();

			if (!_transport.StartConnection(true))
			{
				Refuse("This server could not bind UDP port " + _config.Port +
					". Another process is probably already using it.");
				yield break;
			}
			_readiness.ServerStarted = true;
			_readiness.Write();

			// The loopback client is a LOCKED INVARIANT, not an optional extra: the game only
			// builds its world for a local client. Its player avatar is suppressed separately.
			if (!_transport.StartConnection(false))
			{
				_transport.StopConnection(true);
				Refuse("This server started listening but its own internal client could not connect, so the world would never have been built. The port has been closed.");
				yield break;
			}
			_readiness.LocalClientStarted = true;
			_readiness.Write();

			float deadline = Time.realtimeSinceStartup + _config.WorldReadyTimeoutSeconds;
			while (!_stopping)
			{
				Sample();

				if (_readiness.WorldRunning)
				{
					if (!_settingsApplied) ApplyWorldSettings();
					if (_readiness.Phase != HostPhase.Hosting)
					{
						_readiness.Phase = HostPhase.Hosting;
						_readiness.BootAssertionsPassed = true;
						_readiness.Reason = "Hosting \"" + WorldLifecycle.SelectedWorld + "\" on port " + _config.Port;
						Logger.LogInfo("DRIFTWOOD_READY port=" + _config.Port +
							" slots=" + _config.MaxPlayers +
							" world=" + WorldLifecycle.SelectedWorld);
					}
				}
				else if (_readiness.Phase != HostPhase.Hosting && Time.realtimeSinceStartup > deadline)
				{
					// The port binds before the world exists, so a bound port is not a hosted
					// world. If the world never arrives, CLOSE THE PORT - a server that reports
					// as down is far better than a healthy-looking server with nothing behind it.
					CloseTransport();
					Refuse("The world did not finish loading within " +
						(int)_config.WorldReadyTimeoutSeconds +
						" seconds, so the gameplay port has been closed. This server reports as down rather than as a healthy server with nothing behind it.");
					yield break;
				}

				_readiness.Write();
				yield return new WaitForSeconds(2f);
			}
		}

		private void ApplyWorldSettings()
		{
			_settingsApplied = true;
			ApplyNetworkTickRate();
			if (!WorldLifecycle.SetAutoSaveInterval(_config.AutoSaveMinutes))
			{
				Logger.LogWarning("Could not set the auto-save interval; the game's own default stays in force.");
			}
			if (!WorldLifecycle.ApplyServerSettings(_config.FriendlyFire, _config.OneShotKills))
			{
				Logger.LogWarning("Could not apply the friendly-fire / one-shot settings; the game's defaults stay in force.");
			}
		}

		private void Update()
		{
			_frames.Sample(Time.unscaledDeltaTime);
		}

		// The netcode tick is also wall-clock driven and also invisible to a frame cap. It can only
		// be set once the NetworkManager exists, which is why it happens here rather than at boot.
		private void ApplyNetworkTickRate()
		{
			try
			{
				object timeManager = InstanceFinder.TimeManager;
				if (timeManager == null) return;
				if (_config.NetworkTickRate > 0)
				{
					AccessTools.Method(timeManager.GetType(), "SetTickRate")
						?.Invoke(timeManager, new object[] { (ushort)_config.NetworkTickRate });
				}
				object current = AccessTools.Property(timeManager.GetType(), "TickRate")?.GetValue(timeManager, null);
				if (current != null) _readiness.EffectiveNetworkTickRate = Convert.ToInt32(current);
				if (_config.NetworkTickRate > 0 && _readiness.EffectiveNetworkTickRate != _config.NetworkTickRate)
				{
					Logger.LogWarning("Network tick rate did not take: asked for " + _config.NetworkTickRate +
						", the game reports " + _readiness.EffectiveNetworkTickRate + ".");
				}
			}
			catch (Exception exception)
			{
				Logger.LogWarning("Could not read or set the network tick rate: " + exception.Message);
			}
		}

		private void Sample()
		{
			try
			{
				double fps, meanMs, p95Ms, worstMs;
				_frames.Snapshot(out fps, out meanMs, out p95Ms, out worstMs);
				_readiness.ActualFrameRate = fps;
				_readiness.FrameTimeMeanMs = meanMs;
				_readiness.FrameTimeP95Ms = p95Ms;
				_readiness.FrameTimeWorstMs = worstMs;
				_readiness.WorldObjectPresent = Server.Instance != null;
				_readiness.IslandLoaded = Island.CurIsland != null;
				_readiness.IslandLoading = IslandManager.IsLoading;
				_readiness.GhostHostSuppressed = GhostHost.Suppressed;
				_readiness.DisplayNamesResolved = DriftwoodIdentity.AllNamesResolved;
				int transportClients = InstanceFinder.ServerManager?.Clients?.Count ?? 0;
				_readiness.ConnectedTransportClients = transportClients;
				// What a customer should see: the host's own loopback connection is never a player.
				_readiness.Players = SlotGuard.Visible(transportClients);

				// The real roster. Names come from our own map when the client supplied one and
				// from an obviously-synthetic placeholder otherwise - never from a guess that
				// could pass for somebody's actual handle.
				List<string> roster = new List<string>();
				if (PlayerManager.Players != null)
				{
					foreach (Player player in PlayerManager.Players)
					{
						if (player == null) continue;
						ulong steamId = player.SteamID;
						if (steamId == DriftwoodIdentity.HostSteamId) continue;
						roster.Add(steamId.ToString() + ":" + (player.SteamName ?? DriftwoodIdentity.Placeholder(steamId)));
					}
				}
				_readiness.SetRoster(roster);
				_readiness.ServerStarted = InstanceFinder.NetworkManager?.IsServerStarted ?? false;
				_readiness.LocalClientStarted = InstanceFinder.NetworkManager?.IsClientStarted ?? false;
			}
			catch (Exception exception)
			{
				Logger.LogWarning("Readiness sample failed: " + exception.GetType().Name + " " + exception.Message);
			}
		}

		// Catching is not fixing. A swallow firing thousands of times a second is a broken feature
		// wearing a seatbelt, and on the sibling product the garbage from exactly that froze the
		// world for 100-180 ms every second while every check reported health.
		private IEnumerator WatchSwallowRate()
		{
			while (true)
			{
				yield return new WaitForSeconds(30f);
				double windowSeconds;
				List<SwallowCounter.Entry> alarming = SwallowCounter.Roll(out windowSeconds);
				foreach (SwallowCounter.Entry entry in alarming)
				{
					Logger.LogWarning("SWALLOW RATE ALARM: " + entry.Method + " threw " +
						Math.Round(entry.Total / Math.Max(1.0, windowSeconds), 1) +
						"/s in the last window (" + entry.Total + " total, last " +
						entry.LastExceptionType + "). This is a broken feature, not a handled edge case.");
				}
			}
		}

		private IEnumerator WatchStopFile()
		{
			while (!_stopping)
			{
				yield return new WaitForSeconds(1f);
				if (!File.Exists(_stopFilePath)) continue;

				_stopping = true;
				_readiness.Phase = HostPhase.Stopping;
				_readiness.Reason = "Saving and shutting down";
				_readiness.Write();

				// Save explicitly, then let the game quit cleanly - SaveManager.OnApplicationQuit
				// saves again on the way out, and Server.OnStopServer saves once more when the
				// server connection stops. Three chances, one of which does not depend on us.
				//
				// Nothing in this block may be allowed to throw past Application.Quit. A stop that
				// silently fails to stop is worse than a stop that fails to save: the supervisor
				// would sit through its whole graceful window and then force-kill, which is the
				// one path that skips the game's own quit-time save as well.
				try
				{
					if (!WorldLifecycle.SaveNow())
					{
						Logger.LogError("Could not run the game's save routine on shutdown.");
					}
				}
				catch (Exception exception)
				{
					Logger.LogError("Shutdown save failed: " + exception.Message);
				}
				try { CloseTransport(); }
				catch (Exception exception) { Logger.LogError("Shutdown transport close failed: " + exception.Message); }

				_readiness.Phase = HostPhase.Stopped;
				_readiness.Reason = "Stopped cleanly";
				_readiness.ServerStarted = false;
				_readiness.LocalClientStarted = false;
				_readiness.WorldObjectPresent = false;
				_readiness.IslandLoaded = false;
				// The world is down, so the population is UNKNOWN rather than zero - a stopped
				// server must never look like a running-but-empty one to the reaper.
				_readiness.Players = HostHttpApi.UnknownPlayers;
				_readiness.SetRoster(new List<string>());
				try { _readiness.Write(); } catch (Exception exception) { Logger.LogWarning("Final readiness write failed: " + exception.Message); }

				TryDelete(_stopFilePath);
				try { _api?.Dispose(); } catch { }
				Application.Quit(0);
			}
		}

		private void CloseTransport()
		{
			try
			{
				if (_transport == null) return;
				_transport.StopConnection(false);
				_transport.StopConnection(true);
			}
			catch (Exception exception)
			{
				Logger.LogWarning("Closing the transport failed: " + exception.Message);
			}
		}

		// One plain sentence, readable by a support person who has never seen the code, written
		// where the supervisor and the panel will both find it.
		private void Refuse(string reason)
		{
			Logger.LogError("DRIFTWOOD WILL NOT HOST: " + reason);
			if (_readiness == null) return;
			_readiness.Phase = HostPhase.WillNotHost;
			_readiness.Reason = reason;
			_readiness.BootAssertionsPassed = false;
			_readiness.ServerStarted = false;
			_readiness.LocalClientStarted = false;
			_readiness.WorldObjectPresent = false;
			_readiness.IslandLoaded = false;
			_readiness.Players = 0;
			_readiness.Write();
		}

		private static void TryDelete(string path)
		{
			try { if (File.Exists(path)) File.Delete(path); } catch { }
		}
	}
}
