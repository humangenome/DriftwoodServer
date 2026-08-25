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
		public const string Version = "0.1.3";

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
		private bool _warnedAboutCapBelowTick;

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
			// gameDir is where the executable lives. instanceRoot is its PARENT, because the install
			// nests as <instance root>\How to Fish\How to Fish.exe. Saves, Logs\ and the boot
			// markers all live under the instance root, outside anything SteamCMD owns.
			string gameDir = Paths.GameRootPath ?? Paths.BepInExRootPath;
			string instanceRoot = _config.ResolveInstanceRoot(gameDir);
			BootMarkers.Prepare(_config.ResolveLogsDirectory(gameDir));
			string stateDirectory = _config.ResolveStateDirectory(
				Path.Combine(Paths.ConfigPath, "driftwood-state"));
			_readiness = new Readiness(stateDirectory)
			{
				PluginVersion = Version,
				GameVersion = Application.version
			};
			_readiness.UnrecognisedConfigKeys = _config.UnrecognisedKeys;
			_readiness.GameDir = gameDir;
			_readiness.InstanceRoot = instanceRoot;
			_readiness.LogsDirectory = BootMarkers.LogsDirectory;
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

			// MuteAudio is consumed HERE, not at Install() time. Silence goes on before the config
			// is read because the gap between process start and config load is exactly when FMOD
			// finds a device and starts playing; honouring the key therefore means releasing
			// afterwards. Read-and-never-consumed is the shape this product refuses.
			if (!_config.MuteAudio) Silence.Release();

			// The status surface comes up BEFORE anything that can refuse, so a server that will
			// not host can still be asked WHY over HTTP instead of only leaving a file behind.
			_readiness.Port = _config.Port;
			_readiness.Slots = _config.MaxPlayers;
			_readiness.WorldName = _config.WorldName;
			_readiness.EffectiveBindAddress = _config.BindAddress;
			_api = new HostHttpApi(_config, _readiness);
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

			// World snapshots live under the INSTANCE root, beside Logs\ and outside the game
			// tree SteamCMD owns, so a validate cannot take a customer's backups. Initialised
			// here because this is the first moment both halves - the resolved save directory
			// and the world name - are known.
			SnapshotStore.Initialise(_readiness.SaveDirectory, instanceRoot, _config.WorldName);

			// APPLY A STAGED WORLD RESTORE HERE, AND NOWHERE ELSE.
			//
			// This is the earliest moment the save directory and the world name are both
			// resolved, and it is long before WorldLifecycle.LoadOrCreateWorld - so at the
			// instant the files are swapped there is no world in memory that could be written
			// over them. Doing it at the other end, just before the process exits, is what
			// this fixes: the game's own SaveManager.OnApplicationQuit wrote the live world
			// straight back over the restored files and the restore was silently a no-op.
			SnapshotStore.ApplyPending();

			// The owner layer, in dependency order: the audit record first (so even an
			// enforcement that fires during boot can be recorded), then the block list it
			// enforces, then the name resolver that labels its entries. Each is fail-soft by
			// contract - a missing key or an unreadable file degrades to placeholders or an
			// empty list, loudly, and never stops the boot.
			OwnerAudit.Initialise(BootMarkers.LogsDirectory);
			string blocklistProblem = Blocklist.Initialise(instanceRoot, stateDirectory);
			if (blocklistProblem != null)
			{
				Logger.LogWarning("Blocklist: " + blocklistProblem + ".");
			}
			else if (Blocklist.Count > 0)
			{
				Logger.LogInfo("Blocklist loaded: " + Blocklist.Count + " blocked player(s) will be kept out.");
			}
			SteamNameResolver.Initialise(_config, stateDirectory);

			// Discord alerts, after the audit/blocklist/name layers they report on. Fail-soft by
			// contract: no webhook (or a bad one) means alerts are off with one explanatory line,
			// and nothing else changes. The boss-kill hook is wired in its own method so a game
			// build that moved BossManager degrades to "no boss alerts" instead of a failed boot.
			DiscordAlerts.LogWarning = message => Log?.LogWarning(message);
			DiscordAlerts.LogInfo = message => Log?.LogInfo(message);
			DiscordAlerts.Initialise(_config, instanceRoot, Version);
			if (DiscordAlerts.Enabled)
			{
				try
				{
					SubscribeBossAlerts();
				}
				catch (Exception exception)
				{
					Logger.LogWarning("Boss-kill alerts could not be wired (" + exception.GetType().Name +
						"); joins, leaves and the other alerts still work.");
				}
			}

			GhostHost.Suppress = _config.SuppressGhostHost;
			EmptyWorldPause.Enabled = _config.PauseWorldWhenEmpty;
			if (EmptyWorldPause.Enabled)
			{
				Logger.LogWarning("PauseWorldWhenEmpty is ON. The world will stand still while nobody is connected. This is a behaviour change, not just an optimisation - verify a real client can join, spawn and move on this server before relying on it.");
			}
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

			// The declared required set and the plan must agree. If a target is renamed or demoted,
			// this refuses rather than letting a server come up without a guard the rest of the
			// system assumes is in force.
			foreach (string requiredId in SteamGuards.RequiredGuardIds)
			{
				bool present = targets.Exists(t =>
					string.Equals(t.Id, requiredId, StringComparison.OrdinalIgnoreCase) &&
					t.Necessity == PatchNecessity.Required);
				if (present) continue;
				Refuse("This server will not host because " + requiredId +
					" is not registered as a required guard. Without it the player-spawn path throws and no player can ever appear, while the port stays open.");
				return;
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
			// Set for completeness, but this is NOT what caps the loop: the batch-mode player
			// IGNORES it and still reads the value back as though it took. Measured: 30 requested,
			// 30 read back, 440-500 fps actually running.
			Application.targetFrameRate = wanted <= 0 ? -1 : wanted;
			// The hand-padded limiter is what actually caps it.
			FrameLimiter.Apply(wanted);
			FrameLimiter.SetIdleFrameRate(_config.IdleFrameRate);
			_readiness.EffectiveTargetFrameRate = wanted;
			_readiness.FrameLimiterActive = FrameLimiter.Active;

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
			// The engine's own value is never trusted as evidence here - actualFrameRate in the
			// readiness document is the only thing that proves a cap is in force.
			if (wanted > 0 && !FrameLimiter.Active)
			{
				Logger.LogError("A frame cap of " + wanted + " was configured but the limiter is not running, so this server is UNCAPPED. Check actualFrameRate before believing any cap.");
			}
		}

		private IEnumerator RunHost()
		{
			yield return new WaitForSecondsRealtime(_config.StartDelaySeconds);

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
			if (_config.CountHostPlayer)
			{
				// There is no host avatar to count. With the ghost suppressed this makes an empty
				// server report one player, which means it is never seen as empty - and an
				// occupied server is never reaped. It exists because the config contract has the
				// key, not because it is ever the right answer.
				Logger.LogWarning("CountHostPlayer is on. This server's own internal connection will be counted as a player and sold as a slot, so an empty server will report one player and will never be seen as empty.");
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
				// REALTIME, not WaitForSeconds: a scaled wait never completes while the empty-world
				// pause holds timeScale at zero, which would freeze this loop and strand the server.
				yield return new WaitForSecondsRealtime(2f);
			}
		}

		private void ApplyWorldSettings()
		{
			_settingsApplied = true;
			ApplyNetworkTickRate();
			// Called HERE and nowhere else: the tick rate is only knowable once the NetworkManager
			// exists, so this is the first moment the comparison is possible. It was defined and
			// never called - the same defect class as the frame limiter that shipped unwired, and
			// in the same file.
			WarnIfCapIsBelowTheTick();
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
			// Drain anything the HTTP API asked the game to do. Every Unity call the API makes
			// - saving the world, taking a snapshot, quitting after a restore - is queued to
			// here rather than run on a listener thread, because touching a Unity object off
			// the main thread is an exception on a busy box and silence on a quiet one.
			MainThread.Pump();
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

		// A frame cap below the netcode tick rate does not break anything, but it batches sends:
		// FishNet ticks on wall-clock time and will run two ticks in one frame to keep up. In a
		// game whose whole feel is objects moving, that is a smoothness cost paid for CPU, and it
		// should be a decision rather than an accident.
		private void WarnIfCapIsBelowTheTick()
		{
			if (_warnedAboutCapBelowTick) return;
			int cap = _config.TargetFrameRate;
			int tick = _readiness.EffectiveNetworkTickRate;
			if (cap <= 0 || tick <= 0 || cap >= tick) return;
			_warnedAboutCapBelowTick = true;
			Logger.LogWarning("The frame cap (" + cap + " fps) is below this game's network tick rate (" +
				tick + " Hz), so the server will run more than one tick in some frames and batch its sends. " +
				"That is a smoothness cost, not an error - set the cap at or above the tick rate unless it was a deliberate trade.");
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
				//
				// Pushed into PlayerDirectory rather than formatted here, because there are now
				// TWO consumers with different rights: the panel's /status (ids included) and
				// the public /players (names and durations only). One place builds both, and the
				// public one never receives an id to leak.
				List<ulong> rosterIds = new List<ulong>();
				List<string> rosterNames = new List<string>();
				List<object> rosterConnections = new List<object>();
				List<float[]> rosterPositions = new List<float[]>();
				if (PlayerManager.Players != null)
				{
					foreach (Player player in PlayerManager.Players)
					{
						if (player == null) continue;
						ulong steamId = player.SteamID;
						if (steamId == DriftwoodIdentity.HostSteamId) continue;
						rosterIds.Add(steamId);
						// Identity map FIRST, the game's cached copy second. The game resolves
						// SteamName exactly once, at join, through our patched
						// GetFriendPersonaName - which answers with a placeholder until the
						// Steam Web API lookup lands seconds later. Reading the map on every
						// sample is what lets a name arrive AFTER the join and still reach the
						// roster; the game's copy alone would hold the placeholder forever.
						rosterNames.Add(DriftwoodIdentity.KnownNameOrNull(steamId)
							?? player.SteamName
							?? DriftwoodIdentity.Placeholder(steamId));
						rosterConnections.Add(null);
						// Where they stand, for the owner's map. The body transform when the game
						// exposes one, the object's own otherwise; a throw here costs this row its
						// dot and nothing else.
						float[] where = null;
						try
						{
							Transform body = null;
							try { body = player.Transform; } catch { }
							if (body == null) body = player.transform;
							if (body != null)
							{
								Vector3 at = body.position;
								if (!float.IsNaN(at.x) && !float.IsNaN(at.y) && !float.IsNaN(at.z)) where = new[] { at.x, at.y, at.z };
							}
						}
						catch { }
						rosterPositions.Add(where);
					}
				}
				PlayerDirectory.Observe(rosterIds, rosterNames, rosterConnections, rosterPositions);
				SampleWorld();
				// Hand the connected ids to the name resolver (a set-add under a lock, nothing
				// slower - the actual HTTP happens on its own thread), and sweep the block list.
				// Both live in the sampler because it is the one recurring main-thread walk that
				// already knows who is connected.
				SteamNameResolver.Request(rosterIds);
				OwnerActions.EnforceBlocklist();
				// Discord join/leave and island-move alerts ride the same walk: a set diff and a
				// byte compare on the main thread, with the HTTP on the alert pipe's own thread.
				DiscordAlerts.ObserveRoster(rosterIds, rosterNames, _config.MaxPlayers);
				OnlineIslandManager islandManager = OnlineIslandManager.Instance;
				if (_readiness.WorldRunning && islandManager != null)
				{
					DiscordAlerts.ObserveIsland(islandManager._curIsland.Value + 1,
						Math.Max(0, IslandManager.TotalIslands - 1));
				}
				_readiness.DiscordAlertsState = DiscordAlerts.State;
				_readiness.SteamNameResolution = SteamNameResolver.State;
				_readiness.BlockedPlayers = Blocklist.Count;
				List<string> roster = PlayerDirectory.IdentifiedRoster();
				_readiness.SetRoster(roster);
				_readiness.ServerStarted = InstanceFinder.NetworkManager?.IsServerStarted ?? false;
				_readiness.LocalClientStarted = InstanceFinder.NetworkManager?.IsClientStarted ?? false;

				// The limiter runs every frame and must never touch a game type, so occupancy is
				// PUSHED to it from here - the one place that knows the difference between a
				// transport connection and a player. Two independent counts, and the larger wins:
				// SlotGuard's arithmetic on the transport, and the roster the game itself holds.
				// Either alone is a single point of failure for a decision that can drop a server
				// to 5 fps with somebody on it.
				FrameLimiter.ObserveOccupancy(
					Math.Max(_readiness.Players, roster.Count),
					_readiness.LocalClientStarted);

				EmptyWorldPause.Update(_readiness.WorldRunning, _readiness.Players);
				_readiness.WorldPaused = EmptyWorldPause.Paused;
				_readiness.LoopIdling = FrameLimiter.Idling;
				_readiness.IdleTransitions = FrameLimiter.IdleTransitions;
				_readiness.WorldResumeCount = EmptyWorldPause.ResumeCount;
				_readiness.WorldSavesOnPause = EmptyWorldPause.SavesOnPause;
				_readiness.WorldSaveOnPauseFailures = EmptyWorldPause.FailedSavesOnPause;
			}
			catch (Exception exception)
			{
				Logger.LogWarning("Readiness sample failed: " + exception.GetType().Name + " " + exception.Message);
			}
		}

		// The world block behind the panel's map and console: island, progression, wallet,
		// the current island's authored centre and radius, uptime. Its own try so a game build
		// that moves one of these managers costs the world block, never the roster sample.
		private void SampleWorld()
		{
			_readiness.UptimeSeconds = Math.Round((double)Time.realtimeSinceStartup, 1);
			if (!_readiness.WorldRunning)
			{
				_readiness.IslandCurrent = 0;
				_readiness.IslandUnlocked = 0;
				_readiness.IslandChanging = false;
				_readiness.Wallet = -1;
				_readiness.IslandCentreKnown = false;
				return;
			}
			try
			{
				int playable = Math.Max(0, IslandManager.TotalIslands - 1);
				_readiness.IslandTotal = playable;
				_readiness.IslandChanging = IslandManager.IsLoading;
				OnlineIslandManager islands = OnlineIslandManager.Instance;
				if (islands != null)
				{
					_readiness.IslandCurrent = islands._curIsland.Value + 1;
					_readiness.IslandUnlocked = Math.Min(islands._maxIslandUnlocked.Value + 1, Math.Max(playable, 1));
				}
				Island island = Island.CurIsland;
				if (island != null)
				{
					Vector3 centre = Island.IslandPos;
					_readiness.IslandCentreKnown = true;
					_readiness.IslandCentreX = Math.Round((double)centre.x, 2);
					_readiness.IslandCentreZ = Math.Round((double)centre.z, 2);
					_readiness.IslandRadius = Math.Round((double)Island.IslandSize, 1);
				}
				else
				{
					_readiness.IslandCentreKnown = false;
				}
			}
			catch (Exception exception)
			{
				Logger.LogDebug("Island sample failed: " + exception.Message);
			}
			try
			{
				MoneyManager money = MoneyManager.Instance;
				_readiness.Wallet = (money != null && money.IsServerInitialized) ? money._money.Value : -1;
			}
			catch (Exception exception)
			{
				_readiness.Wallet = -1;
				Logger.LogDebug("Wallet sample failed: " + exception.Message);
			}
		}

		// Catching is not fixing. A swallow firing thousands of times a second is a broken feature
		// wearing a seatbelt, and on the sibling product the garbage from exactly that froze the
		// world for 100-180 ms every second while every check reported health.
		private IEnumerator WatchSwallowRate()
		{
			while (true)
			{
				yield return new WaitForSecondsRealtime(30f);
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
				yield return new WaitForSecondsRealtime(1f);
				if (!File.Exists(_stopFilePath)) continue;

				_stopping = true;
				_readiness.Phase = HostPhase.Stopping;
				_readiness.Reason = "Saving and shutting down";
				_readiness.Write();

				// Tell the crew before the lights go out. The stop file cannot say whether this
				// is a stop or a restart, so the line covers both. The short realtime wait after
				// a successful broadcast is what lets the chat RPC actually reach the clients -
				// the transport closes moments later - and it only costs anything when somebody
				// was connected to hear it. Bounded well inside the supervisor's graceful window.
				bool crewWarned = false;
				try
				{
					if (PlayerDirectory.Snapshot().Count > 0)
					{
						crewWarned = OwnerActions.Broadcast(
							"Saving the world and shutting down - if this is a restart, the server will be back in about a minute.") == null;
					}
				}
				catch (Exception exception)
				{
					Logger.LogWarning("Shutdown broadcast failed: " + exception.Message);
				}
				if (crewWarned) yield return new WaitForSecondsRealtime(1.5f);

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
				EmptyWorldPause.ForceResume();
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
				PlayerDirectory.Clear();
				_readiness.SetRoster(new List<string>());
				try { _readiness.Write(); } catch (Exception exception) { Logger.LogWarning("Final readiness write failed: " + exception.Message); }

				TryDelete(_stopFilePath);
				// Give any queued Discord lines one bounded chance to land before the process ends.
				try { DiscordAlerts.StopAndFlush(1000); } catch { }
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
		//
		// A refusal is TERMINAL and the process ENDS. It did not used to, and after the port had
		// already been bound - a world-load timeout is the reachable case - that left the process
		// alive running the menu scene with the socket closed. Uncapped that is most of a core,
		// and the supervisor's steady loop only checked whether the process existed, so the zombie
		// burned until the next panel action. There is nothing for a refused host to do: the
		// reason is on disk in host-ready.json, the panel reads it from there, and relaunching
		// would refuse again for the same reason.
		private void Refuse(string reason)
		{
			Logger.LogError("DRIFTWOOD WILL NOT HOST: " + reason);
			if (_readiness != null)
			{
				_readiness.Phase = HostPhase.WillNotHost;
				_readiness.Reason = reason;
				_readiness.BootAssertionsPassed = false;
				_readiness.ServerStarted = false;
				_readiness.LocalClientStarted = false;
				_readiness.WorldObjectPresent = false;
				_readiness.IslandLoaded = false;
				// UNKNOWN, not zero. Zero is what marks a server EMPTY, and an empty server gets
				// reaped; this file states that rule two hundred lines up and then broke it here.
				// Wire-safe by accident today (the HTTP layer recomputes -1 when the world is not
				// running) but a rule that only holds because something downstream re-derives it
				// is not a rule.
				_readiness.Players = HostHttpApi.UnknownPlayers;
				PlayerDirectory.Clear();
				_readiness.SetRoster(new List<string>());
				try { _readiness.Write(); } catch (Exception exception) { Logger.LogWarning("Refusal readiness write failed: " + exception.Message); }
			}

			// Close anything already open before leaving, so a refusal never leaves a bound port
			// behind it, and restore the world clock so nothing inherits a frozen one. The
			// GAMEPLAY port closes immediately - that is the half a player could otherwise
			// connect to - while the status API stays up for the grace window below.
			_stopping = true;
			try { EmptyWorldPause.ForceResume(); } catch { }
			try { CloseTransport(); } catch (Exception exception) { Logger.LogWarning("Refusal transport close failed: " + exception.Message); }

			try { StartCoroutine(QuitAfterRefusal()); }
			catch (Exception exception)
			{
				// If a coroutine cannot even be started, do not stay alive to think about it.
				Logger.LogError("Could not schedule the post-refusal shutdown (" + exception.Message + "); quitting now.");
				HardExit();
			}
		}

		// Seconds a refused host keeps its STATUS port open before ending the process.
		//
		// Not zero, on purpose. The status API is deliberately brought up before anything that
		// can refuse, so a host that will not host can still be ASKED why rather than only
		// leaving a file behind - and the supervisor polls that endpoint every 10 seconds and
		// stops a refused host cleanly when it sees WillNotHost. Quitting instantly would take
		// that channel away and turn every refusal into three blind relaunch attempts.
		//
		// So: close the gameplay port at once, answer "why" for one supervisor poll interval with
		// margin, then end. Either the supervisor stops us first or this does.
		private const float RefusalGraceSeconds = 25f;

		private IEnumerator QuitAfterRefusal()
		{
			yield return new WaitForSecondsRealtime(RefusalGraceSeconds);
			Logger.LogError("DRIFTWOOD WILL NOT HOST: ending the process. Leaving it alive would run the menu scene for nothing, which on an uncapped loop is most of a core.");
			try { _api?.Dispose(); } catch { }
			Application.Quit(1);
			// Application.Quit is a REQUEST - it takes effect at the end of the frame and a
			// batch-mode player with a stuck scene load has been seen to ignore it. This is the
			// zombie this whole path exists to remove, so it does not get to survive a polite
			// request.
			yield return new WaitForSecondsRealtime(15f);
			Logger.LogError("The process did not end after Application.Quit; forcing it.");
			HardExit();
		}

		// NOT INLINED, so the BossManager type only loads inside the caller's try. If a future
		// game build moves or reshapes BossManager, the TypeLoadException lands in that catch
		// and costs boss alerts - never the boot.
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		private static void SubscribeBossAlerts()
		{
			BossManager.OnGlobalBossDeath += () =>
			{
				// This handler runs inside the game's own boss-death path; nothing may escape it.
				try
				{
					string name = null;
					try { name = BossManager.Boss != null ? BossManager.Boss.GetName() : null; } catch { }
					DiscordAlerts.BossDefeated(name);
				}
				catch { }
			};
		}

		private static void HardExit()
		{
			try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
			catch (Exception exception) { Plugin.Log?.LogError("Could not force the process to exit: " + exception.Message); }
		}

		private static void TryDelete(string path)
		{
			try { if (File.Exists(path)) File.Delete(path); } catch { }
		}
	}
}
