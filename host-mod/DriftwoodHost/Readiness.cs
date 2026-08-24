using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DriftwoodHost
{
	internal enum HostPhase
	{
		Starting,
		Hosting,
		// A named, terminal refusal. The gameplay port is not presented.
		WillNotHost,
		Stopping,
		Stopped
	}

	// Playbook 1d requirement 3: READINESS MEANS THE WORLD IS RUNNING, NOT THAT THE PORT IS OPEN.
	//
	// Every cheap health signal we have - process alive, port listening - is equally true of a
	// server whose entire mod stack failed to apply, which is why the default failure mode of
	// these products is a fleet that passes every check and cannot be joined.
	//
	// And the near-miss to avoid: Lodestone's plugin ALREADY computed this correctly and shipped
	// it behind a disabled admin gate, so nothing read it. A correct signal that ships disabled is
	// worth exactly zero. This one is a plain file on disk, DriftwoodServer reads it every few
	// seconds and refuses to report Hosting on anything else, and the panel reads it through the
	// supervisor. Prove something consumes it.
	internal sealed class Readiness
	{
		private readonly string _path;
		private readonly object _sync = new object();

		public Readiness(string stateDirectory)
		{
			Directory.CreateDirectory(stateDirectory);
			_path = System.IO.Path.Combine(stateDirectory, "host-ready.json");
		}

		public string FilePath => _path;

		public HostPhase Phase = HostPhase.Starting;
		public string Reason = "Starting";
		public bool ServerStarted;
		public bool LocalClientStarted;
		public bool WorldObjectPresent;
		public bool IslandLoaded;
		public bool IslandLoading;
		public bool GhostHostSuppressed;
		public bool DisplayNamesResolved = true;
		// Every REQUIRED guard installed, the config validated, the save root redirected and the
		// slot limit read back in force. The panel treats a false here as a failed start.
		public bool BootAssertionsPassed;
		public int Port;
		public int Slots;
		public int TransportMaxClients;
		public int ConnectedTransportClients;
		public int Players;
		public string WorldName = string.Empty;
		public string SaveDirectory = string.Empty;
		public string GameDir = string.Empty;
		public string InstanceRoot = string.Empty;
		public string LogsDirectory = string.Empty;
		public string GameVersion = string.Empty;
		public string PluginVersion = string.Empty;
		public string EffectiveBindAddress = string.Empty;
		public int EffectiveTargetFrameRate;
		// Measured, not configured. If this sits well below the cap, the cap is not what is
		// limiting this server and no frame-cap tuning will change its cost.
		public double ActualFrameRate;
		public double FrameTimeMeanMs;
		public double FrameTimeP95Ms;
		public double FrameTimeWorstMs;
		public double EffectivePhysicsStepSeconds;
		public int EffectiveNetworkTickRate;
		public bool WorldPaused;
		public bool FrameLimiterActive;
		// One plain sentence from the Steam name resolver, so "why are the names placeholders"
		// is answerable from the panel instead of from a log dive. "off (...)" / "ok (...)" /
		// "failing (...)".
		public string SteamNameResolution = string.Empty;
		// How many SteamIDs the owner's block list currently holds. Zero is the normal state.
		public int BlockedPlayers;
		// One plain sentence from the Discord alert pipe, same contract as the name resolver's:
		// "off (...)" / "ok (N sent this run)" / "failing (...)".
		public string DiscordAlertsState = string.Empty;
		public bool LoopIdling;
		public int IdleTransitions;
		public int WorldResumeCount;
		// Proof the empty-world freeze flushes on the way in. The AutoSaver runs on scaled time,
		// so a pause that did NOT save first leaves the last session's tail dirty in memory until
		// something loses it. A rising failure count is a data-loss risk, not a curiosity.
		public int WorldSavesOnPause;
		public int WorldSaveOnPauseFailures;
		public List<string> PatchesApplied = new List<string>();
		public List<string> PatchesMissing = new List<string>();
		public List<string> PatchesFailed = new List<string>();
		public List<string> FeaturesStoodDown = new List<string>();
		public List<string> UnrecognisedConfigKeys = new List<string>();
		private readonly List<string> _roster = new List<string>();

		// The real roster, as the host knows it. Empty is a real answer only when the world is
		// running; otherwise the population is UNKNOWN and callers must not read empty as zero.
		public void SetRoster(IEnumerable<string> entries)
		{
			lock (_sync)
			{
				_roster.Clear();
				if (entries != null) _roster.AddRange(entries);
			}
		}

		public List<string> Roster()
		{
			lock (_sync) return new List<string>(_roster);
		}

		// The single field a consumer should branch on. True ONLY when the world is genuinely
		// running - not when the socket is bound.
		public bool WorldRunning =>
			ServerStarted && WorldObjectPresent && IslandLoaded && !IslandLoading;

		public void Write()
		{
			string payload;
			lock (_sync)
			{
				List<SwallowCounter.Entry> swallows = SwallowCounter.Snapshot();
				StringBuilder swallowJson = new StringBuilder("[");
				for (int i = 0; i < swallows.Count; i++)
				{
					if (i > 0) swallowJson.Append(',');
					swallowJson.Append(Json.Object()
						.Add("method", swallows[i].Method)
						.Add("total", swallows[i].Total)
						.Add("peakPerSecond", swallows[i].PeakPerSecond)
						.Add("lastException", swallows[i].LastExceptionType)
						.Close());
				}
				swallowJson.Append(']');

				payload = Json.Object()
					.Add("schema", 1)
					.Add("product", "Driftwood")
					.Add("pluginVersion", PluginVersion)
					.Add("gameVersion", GameVersion)
					.Add("timestampUtc", DateTime.UtcNow.ToString("O"))
					.Add("phase", Phase.ToString())
					.Add("reason", Reason)
					// The one field that means "a player can join and be in a world".
					.Add("worldRunning", WorldRunning)
					.Add("serverStarted", ServerStarted)
					.Add("localClientStarted", LocalClientStarted)
					.Add("worldObjectPresent", WorldObjectPresent)
					.Add("islandLoaded", IslandLoaded)
					.Add("islandLoading", IslandLoading)
					.Add("port", Port)
					.Add("slots", Slots)
					.Add("transportMaxClients", TransportMaxClients)
					.Add("connectedTransportClients", ConnectedTransportClients)
					// UNKNOWN (-1) unless the world is genuinely running. Zero is what marks a
					// server empty and an empty server gets reaped, so a loading or wedged server
					// must never publish a zero. See protocol/http-api.md.
					.Add("players", WorldRunning ? Players : -1)
					.Add("worldName", WorldName)
					.Add("saveDirectory", SaveDirectory)
					.Add("gameDir", GameDir)
					.Add("instanceRoot", InstanceRoot)
					.Add("logsDirectory", LogsDirectory)
					.Add("ghostHostSuppressed", GhostHostSuppressed)
					.Add("bootAssertionsPassed", BootAssertionsPassed)
					.AddStrings("roster", _roster)
					.AddStrings("unrecognisedConfigKeys", UnrecognisedConfigKeys)
					// Parameters the game grew on methods this host calls by reflection. Empty is
					// the normal state; a non-empty list means the game moved under us and something
					// is being passed a value nobody chose.
					.AddStrings("gameApiDrift", WorldLifecycle.GameApiDrift)
					.Add("displayNamesResolved", DisplayNamesResolved)
					.Add("steamNameResolution", SteamNameResolution)
					.Add("blockedPlayers", BlockedPlayers)
					.Add("discordAlerts", DiscordAlertsState)
					.Add("effectiveBindAddress", EffectiveBindAddress)
					.Add("effectiveTargetFrameRate", EffectiveTargetFrameRate)
					.Add("actualFrameRate", ActualFrameRate)
					.Add("frameTimeMeanMs", FrameTimeMeanMs)
					.Add("frameTimeP95Ms", FrameTimeP95Ms)
					.Add("frameTimeWorstMs", FrameTimeWorstMs)
					.Add("effectivePhysicsStepSeconds", EffectivePhysicsStepSeconds)
					.Add("effectiveNetworkTickRate", EffectiveNetworkTickRate)
					.Add("worldPaused", WorldPaused)
					.Add("frameLimiterActive", FrameLimiterActive)
					.Add("loopIdling", LoopIdling)
					.Add("idleTransitions", IdleTransitions)
					.Add("worldResumeCount", WorldResumeCount)
					.Add("worldSavesOnPause", WorldSavesOnPause)
					.Add("worldSaveOnPauseFailures", WorldSaveOnPauseFailures)
					.Add("swallowedTotal", SwallowCounter.TotalSwallowed())
					.AddRaw("swallowed", swallowJson.ToString())
					.AddStrings("patchesApplied", PatchesApplied)
					.AddStrings("patchesMissing", PatchesMissing)
					.AddStrings("patchesFailed", PatchesFailed)
					.AddStrings("featuresStoodDown", FeaturesStoodDown)
					.Close();
			}
			WriteAtomic(_path, payload);
		}

		// Never leave a half-written status file behind: a reader that catches one is exactly the
		// kind of thing that gets "handled" by falling back to a default.
		private static void WriteAtomic(string path, string content)
		{
			string directory = System.IO.Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
			string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				File.WriteAllText(temporary, content, new UTF8Encoding(false));
				if (File.Exists(path)) File.Delete(path);
				File.Move(temporary, path);
			}
			finally
			{
				try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
			}
		}
	}
}
