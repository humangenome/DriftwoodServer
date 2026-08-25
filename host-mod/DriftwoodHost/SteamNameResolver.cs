using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace DriftwoodHost
{
	// Resolves SteamID64 -> persona name over the Steam Web API, because this host cannot do it
	// any other way: the game resolves names through SteamFriends.GetFriendPersonaName, which
	// only answers on a machine with a signed-in Steam client, and a Driftwood host runs with no
	// Steam client and no Steam account at all. The NAME IS NEVER ON THE WIRE in this game -
	// each real client resolves the replicated SteamID locally - so before this file existed the
	// roster held obviously-synthetic placeholders and nothing else.
	//
	// ISteamUser/GetPlayerSummaries is plain HTTPS with an API key: no Steam install, no
	// signed-in user, up to 100 ids per call, and persona names are public for every profile.
	//
	// THE RULES THIS FILE LIVES BY:
	//
	//   FAIL SOFT, ALWAYS. Every lookup runs on this class's own background thread. Nothing
	//   here is ever awaited by a page render, a join, or the frame loop - the roster shows the
	//   stable placeholder until a real name lands, and keeps showing it forever if the API is
	//   down, the key is missing, or the key is wrong. A 30-second panel load on this same game
	//   was caused by exactly one remote call sitting on the render path; this feature is not
	//   allowed to become the second.
	//
	//   THE NAME IS DISPLAY-ONLY. Bans, saves and every per-player decision key on the
	//   SteamID64. A persona name is user-controlled text that can change hourly and can
	//   imitate anybody; it goes into rosters and log lines and nowhere else.
	//
	//   THE KEY IS A SECRET. It is read from a file OUTSIDE the customer's FTP jail (the config
	//   carries only the file's path), it is never logged, and it never appears in any payload
	//   this host serves.
	internal static class SteamNameResolver
	{
		private const int MaxIdsPerCall = 100;
		private const int RequestTimeoutMs = 10000;
		private const int MaxResponseBytes = 512 * 1024;
		// Names rarely change; a connected player's entry is refreshed opportunistically.
		private const long RefreshSeconds = 24 * 3600;
		// An id Steam answered WITHOUT (deleted account) is not retried for this long, so a
		// dead id cannot hammer the API on every sample.
		private const long NegativeSeconds = 6 * 3600;
		private const int BackoffFloorSeconds = 30;
		private const int BackoffCeilingSeconds = 900;
		private const int CacheFileCap = 512;

		private static readonly object Sync = new object();
		private static readonly HashSet<ulong> Wanted = new HashSet<ulong>();
		private static readonly Dictionary<ulong, long> ResolvedUnix = new Dictionary<ulong, long>();
		private static readonly Dictionary<ulong, long> NegativeUnix = new Dictionary<ulong, long>();

		private static string _apiKey = string.Empty;
		private static string _cachePath = string.Empty;
		private static Thread _worker;
		private static volatile bool _running;
		private static int _backoffSeconds = BackoffFloorSeconds;

		// One plain sentence for the readiness document, so "why are the names placeholders"
		// is answerable from the panel instead of from a log dive.
		private static volatile string _state = "off (no Steam Web API key is configured)";
		internal static string State => _state;

		// Reads the key (file first, inline second), loads the persisted cache, and starts the
		// worker. Returns without a worker - placeholders forever, loudly explained - when no
		// usable key exists. Everything in here is best-effort: a throw is caught by the caller
		// (Plugin.Boot wraps everything) but this method also refuses to be the thing that
		// stops a boot, so it swallows its own IO problems into _state.
		internal static void Initialise(HostConfig config, string stateDirectory)
		{
			try
			{
				_apiKey = LoadKey(config);
				if (_apiKey.Length == 0) return;

				_cachePath = Path.Combine(stateDirectory ?? string.Empty, "steam-names.txt");
				LoadCache();

				_running = true;
				_worker = new Thread(WorkLoop) { IsBackground = true, Name = "Driftwood.SteamNames" };
				_worker.Start();
				_state = "ok (0 resolved this run)";
				Plugin.Log?.LogInfo("Steam name resolution is ON: player SteamIDs will be resolved to real persona names via the Steam Web API.");
			}
			catch (Exception exception)
			{
				_state = "off (failed to start: " + exception.GetType().Name + ")";
				Plugin.Log?.LogWarning("Steam name resolution could not start (" + exception.Message +
					"); the roster keeps its placeholder names. Nothing else is affected.");
			}
		}

		// Called from the readiness sampler with the ids currently connected. Cheap by design:
		// a set add under a lock, nothing else, because this runs inside the frame loop's
		// sample and must never wait on the network.
		internal static void Request(IList<ulong> steamIds)
		{
			if (!_running || steamIds == null || steamIds.Count == 0) return;
			long now = PlayerDirectory.NowUnix();
			lock (Sync)
			{
				foreach (ulong id in steamIds)
				{
					// The whole synthetic range, not just the host placeholder: 1.0.6 spawn
					// identities are ours too, and the Steam Web API has nothing to say about
					// ids that were never issued.
					if (id == 0UL || DriftwoodIdentity.IsSynthetic(id)) continue;
					if (NegativeUnix.TryGetValue(id, out long negativeAt) && now - negativeAt < NegativeSeconds) continue;
					if (ResolvedUnix.TryGetValue(id, out long resolvedAt) && now - resolvedAt < RefreshSeconds) continue;
					Wanted.Add(id);
				}
			}
		}

		private static void WorkLoop()
		{
			while (_running)
			{
				List<ulong> batch = null;
				lock (Sync)
				{
					if (Wanted.Count > 0)
					{
						batch = new List<ulong>(Math.Min(Wanted.Count, MaxIdsPerCall));
						foreach (ulong id in Wanted)
						{
							batch.Add(id);
							if (batch.Count >= MaxIdsPerCall) break;
						}
						foreach (ulong id in batch) Wanted.Remove(id);
					}
				}

				if (batch == null)
				{
					Thread.Sleep(2000);
					continue;
				}

				bool ok = ResolveBatch(batch);
				if (ok)
				{
					_backoffSeconds = BackoffFloorSeconds;
					Thread.Sleep(1500);
					continue;
				}

				// The batch goes back so nobody is stranded as a placeholder because their
				// lookup happened to coincide with an outage.
				lock (Sync) { foreach (ulong id in batch) Wanted.Add(id); }
				int wait = _backoffSeconds;
				_backoffSeconds = Math.Min(BackoffCeilingSeconds, _backoffSeconds * 2);
				for (int slept = 0; slept < wait && _running; slept++) Thread.Sleep(1000);
			}
		}

		private static bool ResolveBatch(List<ulong> batch)
		{
			string body;
			try
			{
				body = Fetch(batch);
			}
			catch (WebException exception)
			{
				HttpWebResponse response = exception.Response as HttpWebResponse;
				string reason = response != null ? "HTTP " + (int)response.StatusCode : exception.Status.ToString();
				try { response?.Close(); } catch { }
				Fail(reason);
				return false;
			}
			catch (Exception exception)
			{
				Fail(exception.GetType().Name);
				return false;
			}

			List<SteamProfileParser.Profile> profiles = SteamProfileParser.Parse(body);
			long now = PlayerDirectory.NowUnix();
			HashSet<ulong> answered = new HashSet<ulong>();
			foreach (SteamProfileParser.Profile profile in profiles)
			{
				answered.Add(profile.SteamId);
				DriftwoodIdentity.SetKnownName(profile.SteamId, profile.PersonaName);
				lock (Sync)
				{
					ResolvedUnix[profile.SteamId] = now;
					NegativeUnix.Remove(profile.SteamId);
				}
			}
			lock (Sync)
			{
				foreach (ulong id in batch)
				{
					if (!answered.Contains(id)) NegativeUnix[id] = now;
				}
				_state = "ok (" + ResolvedUnix.Count + " resolved this run)";
			}
			SaveCache();
			return true;
		}

		private static void Fail(string reason)
		{
			bool firstFailure = !_state.StartsWith("failing", StringComparison.Ordinal);
			_state = "failing (" + reason + "); the roster keeps its placeholder names until the API answers again";
			if (firstFailure)
			{
				Plugin.Log?.LogWarning("Steam name lookup failed (" + reason +
					"). Placeholder names stay in the roster; retrying with backoff. This message is not repeated per retry.");
			}
		}

		private static string Fetch(List<ulong> batch)
		{
			// Unity's Mono answers modern TLS only when asked; asking twice is harmless.
			try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

			StringBuilder ids = new StringBuilder();
			for (int i = 0; i < batch.Count; i++)
			{
				if (i > 0) ids.Append(',');
				ids.Append(batch[i].ToString(CultureInfo.InvariantCulture));
			}

			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
				"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key=" + _apiKey +
				"&steamids=" + ids);
			request.Method = "GET";
			request.Timeout = RequestTimeoutMs;
			request.ReadWriteTimeout = RequestTimeoutMs;
			request.UserAgent = "Driftwood/" + Plugin.Version;

			using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
			using (Stream stream = response.GetResponseStream())
			using (MemoryStream buffer = new MemoryStream())
			{
				byte[] chunk = new byte[16 * 1024];
				while (buffer.Length < MaxResponseBytes)
				{
					int read = stream.Read(chunk, 0, chunk.Length);
					if (read <= 0) break;
					buffer.Write(chunk, 0, read);
				}
				return Encoding.UTF8.GetString(buffer.ToArray());
			}
		}

		// --------------------------------------------------------------------------
		// The key. File first (a hosting provider's shape: the config names a path in a
		// machine-level secrets store outside the customer's FTP jail, so the shared secret
		// never sits in a customer-readable tree), inline second (a self-hoster's convenience -
		// their box, their call). Never logged, in any branch.
		// --------------------------------------------------------------------------
		private static string LoadKey(HostConfig config)
		{
			string file = (config.SteamWebApiKeyFile ?? string.Empty).Trim();
			if (file.Length > 0)
			{
				try
				{
					if (File.Exists(file))
					{
						string key = File.ReadAllText(file).Trim();
						if (LooksLikeKey(key)) return key;
						_state = "off (the Steam Web API key file exists but does not contain a usable key)";
						Plugin.Log?.LogWarning("The Steam Web API key file was found but its content does not look like a key; player names stay as placeholders.");
						return string.Empty;
					}
					_state = "off (the configured Steam Web API key file does not exist)";
					Plugin.Log?.LogWarning("The configured Steam Web API key file does not exist (" + file +
						"); player names stay as placeholders.");
					return string.Empty;
				}
				catch (Exception exception)
				{
					_state = "off (the Steam Web API key file could not be read)";
					Plugin.Log?.LogWarning("The Steam Web API key file could not be read (" +
						exception.GetType().Name + "); player names stay as placeholders.");
					return string.Empty;
				}
			}

			string inline = (config.SteamWebApiKey ?? string.Empty).Trim();
			if (inline.Length > 0)
			{
				if (LooksLikeKey(inline)) return inline;
				_state = "off (the inline Steam Web API key does not look like a key)";
				Plugin.Log?.LogWarning("The inline SteamWebApiKey value does not look like a Steam Web API key; player names stay as placeholders.");
				return string.Empty;
			}
			return string.Empty;
		}

		// A Steam Web API key is 32 hex characters. Refusing anything else here means a stray
		// sentence, a BOM, or a pasted URL never gets sent to Valve inside a query string.
		private static bool LooksLikeKey(string candidate)
		{
			if (string.IsNullOrEmpty(candidate) || candidate.Length != 32) return false;
			foreach (char c in candidate)
			{
				bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
				if (!hex) return false;
			}
			return true;
		}

		// --------------------------------------------------------------------------
		// Cache: "<id>\t<resolvedUnix>\t<name>" per line in the state directory, so a restart
		// does not re-ask Steam for everyone who was ever seen. Names were sanitised before
		// they got here, so the tab structure cannot be forged by a persona name.
		// --------------------------------------------------------------------------
		private static void LoadCache()
		{
			try
			{
				if (_cachePath.Length == 0 || !File.Exists(_cachePath)) return;
				long now = PlayerDirectory.NowUnix();
				foreach (string line in File.ReadAllLines(_cachePath))
				{
					string[] parts = line.Split('\t');
					if (parts.Length != 3) continue;
					if (!ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) continue;
					if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) continue;
					string name = SteamProfileParser.Sanitize(parts[2]);
					if (id == 0UL || name.Length == 0 || at > now) continue;
					DriftwoodIdentity.SetKnownName(id, name);
					lock (Sync) ResolvedUnix[id] = at;
				}
			}
			catch (Exception exception)
			{
				Plugin.Log?.LogDebug("Steam name cache load failed: " + exception.Message);
			}
		}

		private static void SaveCache()
		{
			try
			{
				if (_cachePath.Length == 0) return;
				List<KeyValuePair<ulong, long>> entries;
				lock (Sync)
				{
					entries = new List<KeyValuePair<ulong, long>>(ResolvedUnix);
				}
				// Newest first, capped, so the file cannot grow without bound on a busy server.
				entries.Sort((a, b) => b.Value.CompareTo(a.Value));
				if (entries.Count > CacheFileCap) entries.RemoveRange(CacheFileCap, entries.Count - CacheFileCap);

				StringBuilder builder = new StringBuilder();
				foreach (KeyValuePair<ulong, long> entry in entries)
				{
					string name = DriftwoodIdentity.KnownNameOrNull(entry.Key);
					if (name == null) continue;
					builder.Append(entry.Key.ToString(CultureInfo.InvariantCulture)).Append('\t')
						.Append(entry.Value.ToString(CultureInfo.InvariantCulture)).Append('\t')
						.Append(name).Append('\n');
				}

				string directory = Path.GetDirectoryName(_cachePath);
				if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
				string temporary = _cachePath + ".tmp";
				File.WriteAllText(temporary, builder.ToString(), new UTF8Encoding(false));
				if (File.Exists(_cachePath)) File.Delete(_cachePath);
				File.Move(temporary, _cachePath);
			}
			catch (Exception exception)
			{
				Plugin.Log?.LogDebug("Steam name cache save failed: " + exception.Message);
			}
		}
	}
}
