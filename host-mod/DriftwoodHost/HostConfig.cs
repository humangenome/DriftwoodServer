using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DriftwoodHost
{
	// The panel writes this file; the host mod reads it. Two lanes therefore have to agree on key
	// names, and a key-name disagreement is the purest form of playbook 1d's "silently-ignored
	// config": the panel writes a slot limit, the mod never sees it, both halves look healthy and
	// the server runs with the default.
	//
	// Three things guard against that here:
	//   1. Every setting accepts a small set of ALIASES, so a reasonable name from either lane
	//      lands on the same value.
	//   2. Any key in the file that matched NOTHING is collected and reported loudly at startup
	//      and echoed into the readiness file. An ignored key is never silent.
	//   3. The effective values are published in the readiness document, so the supervisor and
	//      the panel assert what the server is ACTUALLY running rather than what they wrote.
	internal sealed class HostConfig
	{
		// Section-qualified ("Server.Port"). Authoritative.
		private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		// Bare keys, used only when no qualified name matched.
		private readonly Dictionary<string, string> _flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _consumedFlat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public string SourcePath { get; private set; } = string.Empty;
		public bool Loaded { get; private set; }
		public List<string> UnrecognisedKeys { get; } = new List<string>();

		public bool Enabled = true;
		public string BindAddress = "0.0.0.0";
		// The fleet band is 22003 with a stride of 10, NOT the game's own 7777 - nine games on this
		// fleet already default to 7777 and a collision there is a first-boot bind failure.
		public int Port = 22003;
		public int HttpPort;
		public int MaxPlayers = 8;
		public float StartDelaySeconds = 10f;
		public float WorldReadyTimeoutSeconds = 240f;
		public string ServerName = string.Empty;
		public string JoinPassword = string.Empty;
		public string AuthToken = string.Empty;

		public string WorldName = "Driftwood";
		public string SaveRoot = string.Empty;
		public float AutoSaveMinutes = 5f;
		public bool FriendlyFire = true;
		public bool OneShotKills;

		public bool MuteAudio = true;
		public bool HostMode = true;
		// Whether the host's own loopback connection is counted as a player. It is not a player,
		// and counting it would make every empty server look occupied - and an occupied server is
		// never reaped.
		public bool CountHostPlayer;
		public bool SuppressGhostHost = true;
		public int TargetFrameRate;
		// The two levers a frame cap CANNOT reach. Decompiled, the game runs 31 FixedUpdate methods
		// and 22 FishNet tick subscriptions alongside its 60 Update / 29 LateUpdate methods, and
		// physics and netcode are driven by wall-clock steps rather than by frames. So a frame cap
		// only touches part of the cost and leaves a floor underneath it.
		//
		// Both default to 0 = LEAVE THE GAME'S OWN VALUE ALONE. Neither is a free win: the physics
		// step is simulation fidelity and the tick rate is how often the world is replicated. They
		// exist so the levers are measurable without a rebuild, not so they can be turned casually.
		// Lever 3: freeze the world while nobody is connected. DEFAULT OFF until a real client has
		// been proven to join a paused server, spawn and move.
		// Run the server loop at this rate while nobody is connected. 0 = off. This is the lever the
		// measurements point at: the idle cost is the frame loop, not the simulation clock.
		public int IdleFrameRate;
		public bool PauseWorldWhenEmpty;
		public float PhysicsStepSeconds;
		public int NetworkTickRate;

		// Fault injection, defaulted off. Playbook 1d requirement 2 ends with "test this by breaking
		// it on purpose - a gate you have never seen fail is not a gate", and the gate in question
		// is the one that decides whether a server refuses to host. This makes that testable and
		// repeatable instead of a thing somebody once tried by hand.
		//
		// It can only ever push the server FURTHER towards refusing, never towards hosting, so a
		// stray value fails in the safe direction. Every use is logged loudly and published in the
		// readiness file.
		public string SimulateMissingPatch = string.Empty;

		public string StateDirectory = string.Empty;
		// The gameserver instance root. Logs\ under it is OUTSIDE the customer's FTP jail, which
		// is why the boot markers live there and not in Saves\.
		public string InstanceRoot = string.Empty;

		public static HostConfig Load(string path)
		{
			HostConfig config = new HostConfig { SourcePath = path };
			config.ReadFile(path);

			// SECTION-QUALIFIED NAMES COME FIRST in every list below, bare names last. Where the
			// same bare key legitimately exists in two sections - Port, BindAddress, Password -
			// only the qualified form is trustworthy, and getting that wrong meant an empty API
			// token and a silently rejected authenticated call on every request.
			config.Enabled = config.Bool(config.Enabled, "Server.Enabled", "Enabled", "AutoStart");
			config.BindAddress = config.String(config.BindAddress,
				"Server.BindAddress", "BindAddress", "ServerBindAddress", "ListenAddress");
			config.Port = config.Int(config.Port,
				"Server.Port", "Server.GamePort", "GamePort", "ServerPort", "Port");
			config.HttpPort = config.Int(config.HttpPort,
				"Http.Port", "Http.HttpPort", "Server.HttpPort", "HttpPort", "AdminPort", "ApiPort");
			config.MaxPlayers = config.Int(config.MaxPlayers,
				"Server.MaxPlayers", "Server.Slots", "MaxPlayers", "Slots", "MaxClients");
			config.StartDelaySeconds = config.Float(config.StartDelaySeconds,
				"Server.StartDelaySeconds", "StartDelaySeconds", "StartDelay");
			config.WorldReadyTimeoutSeconds = config.Float(config.WorldReadyTimeoutSeconds,
				"Server.WorldReadyTimeoutSeconds", "WorldReadyTimeoutSeconds", "WorldTimeoutSeconds");
			config.ServerName = config.String(config.ServerName,
				"Server.ServerName", "Server.Name", "ServerName", "HostName");
			// The JOIN password. Distinct from the API token below - they are different secrets and
			// they live in different sections.
			config.JoinPassword = config.String(config.JoinPassword,
				"Server.JoinPassword", "Server.Password", "JoinPassword");
			// The API token for every mutating HTTP route. "[Http] Password" is its canonical
			// spelling on the writing side.
			config.AuthToken = config.String(config.AuthToken,
				"Http.Password", "Http.AuthToken", "Http.Token", "Server.AuthToken",
				"AuthToken", "AdminToken", "PassRcon", "RconPassword");

			config.WorldName = config.String(config.WorldName,
				"World.Name", "World.WorldName", "Server.WorldName", "WorldName", "world_name", "SaveName");
			config.SaveRoot = config.String(config.SaveRoot,
				"Server.SaveRoot", "Paths.SaveRoot", "World.SaveRoot", "SaveRoot", "SaveDirectory", "SavePath");
			config.AutoSaveMinutes = config.Float(config.AutoSaveMinutes,
				"World.AutoSaveMinutes", "Server.AutoSaveMinutes", "AutoSaveMinutes", "auto_save_minutes");
			config.FriendlyFire = config.Bool(config.FriendlyFire,
				"Gameplay.FriendlyFire", "World.FriendlyFire", "FriendlyFire", "friendly_fire");
			config.OneShotKills = config.Bool(config.OneShotKills,
				"Gameplay.OneShotKills", "Gameplay.OneShot", "OneShotKills", "OneShot", "one_shot");

			config.MuteAudio = config.Bool(config.MuteAudio, "Server.MuteAudio", "MuteAudio", "Mute");
			config.HostMode = config.Bool(config.HostMode, "Server.HostMode", "HostMode");
			config.CountHostPlayer = config.Bool(config.CountHostPlayer,
				"Server.CountHostPlayer", "CountHostPlayer");
			config.SuppressGhostHost = config.Bool(config.SuppressGhostHost,
				"Host.SuppressGhostHost", "Server.SuppressGhostHost", "SuppressGhostHost", "HideHostPlayer");
			config.TargetFrameRate = config.Int(config.TargetFrameRate,
				"Server.TargetFrameRate", "Performance.TargetFrameRate", "TargetFrameRate", "FrameRate", "Fps");
			config.IdleFrameRate = config.Int(config.IdleFrameRate,
				"Performance.IdleFrameRate", "Server.IdleFrameRate", "IdleFrameRate", "EmptyFrameRate");
			config.PauseWorldWhenEmpty = config.Bool(config.PauseWorldWhenEmpty,
				"Performance.PauseWorldWhenEmpty", "Server.PauseWorldWhenEmpty", "PauseWorldWhenEmpty", "FreezeWhenEmpty");
			config.PhysicsStepSeconds = config.Float(config.PhysicsStepSeconds,
				"Performance.PhysicsStepSeconds", "Server.PhysicsStepSeconds", "PhysicsStepSeconds", "FixedDeltaTime");
			config.NetworkTickRate = config.Int(config.NetworkTickRate,
				"Performance.NetworkTickRate", "Server.NetworkTickRate", "NetworkTickRate", "TickRate");

			config.SimulateMissingPatch = config.String(config.SimulateMissingPatch,
				"Diagnostics.SimulateMissingPatch", "SimulateMissingPatch");
			config.StateDirectory = config.String(config.StateDirectory,
				"Paths.StateDirectory", "Paths.StateRoot", "Server.StateDirectory", "StateDirectory", "StateRoot");
			// THE INSTANCE ROOT, not the game directory. The install nests: the executable lives at
			// <instance root>\How to Fish\How to Fish.exe, so the game dir is a CHILD of the
			// instance root. Saves and the boot markers live under the instance root, OUTSIDE the
			// game dir, deliberately - so a SteamCMD validate cannot own or delete them.
			config.InstanceRoot = config.String(config.InstanceRoot,
				"Paths.InstanceRoot", "Server.InstanceRoot", "InstanceRoot", "ServerRoot", "GameServerRoot", "InstanceDir");

			foreach (string qualified in config._values.Keys)
			{
				if (config._consumed.Contains(qualified)) continue;
				int dot = qualified.IndexOf('.');
				string bare = dot >= 0 ? qualified.Substring(dot + 1) : qualified;
				// A key is only "unrecognised" if neither its qualified nor its bare form was asked
				// for by any setting.
				if (config._consumedFlat.Contains(bare)) continue;
				config.UnrecognisedKeys.Add(qualified);
			}
			return config;
		}

		private void ReadFile(string path)
		{
			if (!File.Exists(path)) return;
			Loaded = true;
			string section = string.Empty;
			foreach (string raw in File.ReadAllLines(path))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
				if (line[0] == '[')
				{
					int close = line.IndexOf(']');
					section = close > 1 ? line.Substring(1, close - 1).Trim() : string.Empty;
					continue;
				}
				int equals = line.IndexOf('=');
				if (equals <= 0) continue;
				string key = line.Substring(0, equals).Trim();
				string value = line.Substring(equals + 1).Trim();

				// SECTIONS ARE AUTHORITATIVE. An earlier version of this reader ignored them, which
				// looked tolerant and was actually a structural disagreement with the side that
				// WRITES the file: "[Http] Password" collapsed onto "[Server] Password", so the API
				// token was always empty and every authenticated call was rejected; and Port and
				// BindAddress, which legitimately appear under more than one section, silently
				// collapsed into whichever came first.
				string qualified = section.Length > 0 ? section + "." + key : key;
				if (!_values.ContainsKey(qualified)) _values[qualified] = value;

				// A bare key is still accepted as a LAST RESORT, so a value written without a
				// section is not lost - but it can never outrank a sectioned match, because
				// Resolve() asks for the qualified names first.
				if (!_flat.ContainsKey(key)) _flat[key] = value;
			}
		}

		// Qualified names first, in the order given, then bare names. A bare key can never outrank
		// a sectioned one.
		private string Raw(params string[] names)
		{
			foreach (string name in names)
			{
				if (name.IndexOf('.') < 0) continue;
				if (!_values.TryGetValue(name, out string qualifiedValue)) continue;
				MarkConsumed(names);
				return qualifiedValue;
			}
			foreach (string name in names)
			{
				if (name.IndexOf('.') >= 0) continue;
				if (!_flat.TryGetValue(name, out string flatValue)) continue;
				MarkConsumed(names);
				return flatValue;
			}
			MarkConsumed(names);
			return null;
		}

		private void MarkConsumed(string[] names)
		{
			foreach (string name in names)
			{
				_consumed.Add(name);
				int dot = name.IndexOf('.');
				_consumedFlat.Add(dot >= 0 ? name.Substring(dot + 1) : name);
			}
		}

		private string String(string fallback, params string[] names)
		{
			string value = Raw(names);
			return value ?? fallback;
		}

		private int Int(int fallback, params string[] names)
		{
			string value = Raw(names);
			return value != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
				? parsed
				: fallback;
		}

		private float Float(float fallback, params string[] names)
		{
			string value = Raw(names);
			return value != null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
				? parsed
				: fallback;
		}

		private bool Bool(bool fallback, params string[] names)
		{
			string value = Raw(names);
			if (value == null) return fallback;
			if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1") return true;
			if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0") return false;
			return fallback;
		}

		// Path.IsPathRooted answers for the RUNNING OS, so a Windows path reads as relative when the
		// same code is exercised on Linux (CI, unit tests). This host only ever runs on Windows, so
		// the check has to be about the path, not about the machine reading it.
		internal static bool IsAbsolutePath(string value)
		{
			if (string.IsNullOrWhiteSpace(value)) return false;
			// C:\... or C:/...
			if (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
				(value[2] == '\\' || value[2] == '/')) return true;
			// \\server\share
			if (value.StartsWith("\\\\", StringComparison.Ordinal)) return true;
			// POSIX, for development on a non-Windows machine.
			return value[0] == '/';
		}

		public int EffectiveHttpPort => HttpPort > 0 ? HttpPort : Port + 1;

		public string ResolveStateDirectory(string fallback) =>
			string.IsNullOrWhiteSpace(StateDirectory) ? fallback : StateDirectory.Trim();

		// THE INSTANCE ROOT vs THE GAME DIR - two different directories, and conflating them is the
		// real bug behind a whole family of path defects (the remote lane found 31 parameters named
		// $gameRoot that actually held the instance root).
		//
		//   instance root : <...>\\<dirid>            <- saves, Logs\\, boot markers, host state
		//   game dir      : <...>\\<dirid>\\How to Fish   <- the executable and everything SteamCMD owns
		//
		// The install NESTS: the exe is at <instance root>\\How to Fish\\How to Fish.exe. Saves and
		// markers live under the instance root, OUTSIDE the game dir, deliberately - a SteamCMD
		// validate must never be able to own or delete them.
		public string ResolveInstanceRoot(string gameDir)
		{
			string configured = (InstanceRoot ?? string.Empty).Trim();
			if (configured.Length > 0) return configured.TrimEnd('/', '\\');
			if (string.IsNullOrEmpty(gameDir)) return string.Empty;
			string parent = Path.GetDirectoryName(Path.GetFullPath(gameDir.TrimEnd('/', '\\')));
			return string.IsNullOrEmpty(parent) ? gameDir : parent;
		}

		// Logs\\ under the instance root - outside the customer's FTP jail, so a marker there
		// cannot be forged or deleted by the customer, and outside the game dir, so SteamCMD
		// cannot take it.
		public string ResolveLogsDirectory(string gameDir)
		{
			string root = ResolveInstanceRoot(gameDir);
			return string.IsNullOrEmpty(root) ? Path.Combine(gameDir, "Logs") : Path.Combine(root, "Logs");
		}

		// Returns null when valid, otherwise one plain sentence naming the problem.
		public string Validate()
		{
			if (Port < 1 || Port > 65535)
				return "Port " + Port + " is not a usable UDP port.";
			if (EffectiveHttpPort < 1 || EffectiveHttpPort > 65535 || EffectiveHttpPort == Port)
				return "The status port " + EffectiveHttpPort + " is not usable, or collides with the gameplay port.";
			if (MaxPlayers < 1 || MaxPlayers > 100)
				return "MaxPlayers is set to " + MaxPlayers + ", which is outside the supported range of 1 to 100.";
			if (string.IsNullOrWhiteSpace(BindAddress))
				return "BindAddress is empty, which would bind the server to loopback only and make it unreachable from the internet.";
			if (string.IsNullOrWhiteSpace(WorldName))
				return "WorldName is empty, so the server has no save to load or create.";
			if (WorldName.Trim().Equals("local", StringComparison.OrdinalIgnoreCase))
				return "WorldName cannot be \"local\" - the game reserves that name for the per-machine settings file and would overwrite it.";
			if (WorldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				return "WorldName contains characters that cannot be used in a file name.";
			// REQUIRED, not optional. Unity's persistentDataPath is per Windows USER, not per
			// instance, and no launch flag moves it - so an unset SaveRoot means every server on
			// this machine reads and writes ONE save directory and quietly overwrites each other's
			// world. There is no safe default, so there is no default.
			if (string.IsNullOrWhiteSpace(SaveRoot))
				return "SaveRoot is not set. Without it this server would save into a folder shared with every other server on this machine, and they would overwrite each other's worlds.";
			if (!IsAbsolutePath(SaveRoot.Trim()))
				return "SaveRoot must be an absolute path.";
			if (StartDelaySeconds < 0f || StartDelaySeconds > 300f)
				return "StartDelaySeconds is outside the supported range of 0 to 300.";
			if (WorldReadyTimeoutSeconds < 30f || WorldReadyTimeoutSeconds > 1800f)
				return "WorldReadyTimeoutSeconds is outside the supported range of 30 to 1800.";
			if (TargetFrameRate < 0 || TargetFrameRate > 1000)
				return "TargetFrameRate is outside the supported range of 0 to 1000.";
			if (IdleFrameRate != 0 && (IdleFrameRate < 1 || IdleFrameRate > 1000))
				return "IdleFrameRate is outside the supported range of 1 to 1000; it cannot be zero because the netcode is serviced inside the same loop and a frozen loop could never accept a join.";
			if (PhysicsStepSeconds != 0f && (PhysicsStepSeconds < 0.01f || PhysicsStepSeconds > 0.1f))
				return "PhysicsStepSeconds is outside the safe range of 0.01 to 0.1 seconds; anything coarser makes fast-moving objects tunnel through the world.";
			if (NetworkTickRate != 0 && (NetworkTickRate < 10 || NetworkTickRate > 128))
				return "NetworkTickRate is outside the supported range of 10 to 128 ticks per second.";
			if (AutoSaveMinutes < 1f || AutoSaveMinutes > 60f)
				return "AutoSaveMinutes is outside the range the game accepts, which is 1 to 60.";
			return null;
		}
	}
}
