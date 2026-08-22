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
		private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

			config.Enabled = config.Bool(config.Enabled, "Enabled", "AutoStart");
			config.BindAddress = config.String(config.BindAddress, "BindAddress", "ServerBindAddress", "ListenAddress");
			config.Port = config.Int(config.Port, "Port", "GamePort", "ServerPort");
			config.HttpPort = config.Int(config.HttpPort, "HttpPort", "AdminPort", "ApiPort");
			config.MaxPlayers = config.Int(config.MaxPlayers, "MaxPlayers", "Slots", "MaxClients");
			config.StartDelaySeconds = config.Float(config.StartDelaySeconds, "StartDelaySeconds", "StartDelay");
			config.WorldReadyTimeoutSeconds = config.Float(config.WorldReadyTimeoutSeconds, "WorldReadyTimeoutSeconds", "WorldTimeoutSeconds");
			config.ServerName = config.String(config.ServerName, "ServerName", "HostName");
			config.JoinPassword = config.String(config.JoinPassword, "JoinPassword", "Password");
			config.AuthToken = config.String(config.AuthToken, "AuthToken", "AdminToken", "PassRcon", "RconPassword");

			config.WorldName = config.String(config.WorldName, "WorldName", "world_name", "SaveName");
			config.SaveRoot = config.String(config.SaveRoot, "SaveRoot", "SaveDirectory", "SavePath");
			config.AutoSaveMinutes = config.Float(config.AutoSaveMinutes, "AutoSaveMinutes", "auto_save_minutes");
			config.FriendlyFire = config.Bool(config.FriendlyFire, "FriendlyFire", "friendly_fire");
			config.OneShotKills = config.Bool(config.OneShotKills, "OneShotKills", "OneShot", "one_shot");

			config.MuteAudio = config.Bool(config.MuteAudio, "MuteAudio", "Mute");
			config.HostMode = config.Bool(config.HostMode, "HostMode");
			config.CountHostPlayer = config.Bool(config.CountHostPlayer, "CountHostPlayer");
			config.SuppressGhostHost = config.Bool(config.SuppressGhostHost, "SuppressGhostHost", "HideHostPlayer");
			config.TargetFrameRate = config.Int(config.TargetFrameRate, "TargetFrameRate", "FrameRate", "Fps");

			config.SimulateMissingPatch = config.String(config.SimulateMissingPatch, "SimulateMissingPatch");
			config.StateDirectory = config.String(config.StateDirectory, "StateDirectory", "StateRoot");
			config.InstanceRoot = config.String(config.InstanceRoot, "InstanceRoot", "ServerRoot", "GameServerRoot");

			foreach (string key in config._values.Keys)
			{
				if (!config._consumed.Contains(key)) config.UnrecognisedKeys.Add(key);
			}
			return config;
		}

		private void ReadFile(string path)
		{
			if (!File.Exists(path)) return;
			Loaded = true;
			foreach (string raw in File.ReadAllLines(path))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
				int equals = line.IndexOf('=');
				if (equals <= 0) continue;
				string key = line.Substring(0, equals).Trim();
				string value = line.Substring(equals + 1).Trim();
				// Section is deliberately ignored: the two lanes have moved keys between [Server],
				// [World] and [Performance], and a value in the "wrong" section is still the
				// operator's clear intent. A duplicate key across sections is reported below.
				if (_values.ContainsKey(key)) continue;
				_values[key] = value;
			}
		}

		private string Raw(params string[] names)
		{
			foreach (string name in names)
			{
				if (!_values.TryGetValue(name, out string value)) continue;
				_consumed.Add(name);
				return value;
			}
			foreach (string name in names) _consumed.Add(name);
			return null;
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

		public int EffectiveHttpPort => HttpPort > 0 ? HttpPort : Port + 1;

		public string ResolveStateDirectory(string fallback) =>
			string.IsNullOrWhiteSpace(StateDirectory) ? fallback : StateDirectory.Trim();

		// Logs\ under the instance root - outside the customer's FTP jail, so a marker there
		// cannot be forged or deleted by the customer.
		public string ResolveLogsDirectory(string gameRoot)
		{
			string root = string.IsNullOrWhiteSpace(InstanceRoot)
				? Path.GetDirectoryName(Path.GetFullPath(gameRoot.TrimEnd('/', '\\')))
				: InstanceRoot.Trim();
			return string.IsNullOrEmpty(root) ? Path.Combine(gameRoot, "Logs") : Path.Combine(root, "Logs");
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
			if (!string.IsNullOrWhiteSpace(SaveRoot) && !Path.IsPathRooted(SaveRoot.Trim()))
				return "SaveRoot must be an absolute path.";
			if (StartDelaySeconds < 0f || StartDelaySeconds > 300f)
				return "StartDelaySeconds is outside the supported range of 0 to 300.";
			if (WorldReadyTimeoutSeconds < 30f || WorldReadyTimeoutSeconds > 1800f)
				return "WorldReadyTimeoutSeconds is outside the supported range of 30 to 1800.";
			if (TargetFrameRate < 0 || TargetFrameRate > 1000)
				return "TargetFrameRate is outside the supported range of 0 to 1000.";
			if (AutoSaveMinutes < 1f || AutoSaveMinutes > 60f)
				return "AutoSaveMinutes is outside the range the game accepts, which is 1 to 60.";
			return null;
		}
	}
}
