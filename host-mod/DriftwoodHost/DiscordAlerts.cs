using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace DriftwoodHost
{
	// Discord alerts for the owner's server channel: joins and leaves (with real names once the
	// Steam Web API key is provisioned), boss kills, island advances, and blocked players who
	// tried to come back. The pattern is ValheimOne's, proven on that fleet: a bounded queue, one
	// background thread, a short debounce so a burst becomes one message, and FAIL-SOFT
	// EVERYWHERE - a dead webhook drops alerts with one warning and touches nothing else. No
	// page render, join, or frame ever waits on Discord.
	//
	// WHERE THE URL COMES FROM, in order:
	//
	//   1. <instance root>\Driftwood\discord-webhook.txt - the CUSTOMER'S file, beside
	//      blocklist.txt: inside their FTP jail on a managed server (their webhook is their own
	//      secret, unlike the fleet's Steam key), outside the game tree SteamCMD owns, and
	//      outside the config file the panel rewrites on every start. First non-empty,
	//      non-# line is the URL. Read at boot; changing it takes a restart.
	//   2. [Discord] WebhookUrl in the plugin config - a self-hoster's convenience, and the
	//      hook the panel can write through later when this becomes a panel field.
	//
	// Only a real Discord webhook URL is accepted (https, a discord.com or discordapp.com host,
	// an /api/webhooks/ path). This host runs on a machine full of other people's servers, and a
	// "webhook" field that will POST to any URL an FTP user types is a server-side
	// request-forgery primitive wearing a feature's name.
	//
	// This file deliberately has NO Unity, BepInEx, or game dependency, so the xunit suite links
	// it and proves the URL gate, the payload shape, and the join/leave diff without a running
	// game. Logging goes through the settable delegates below; Plugin wires them at boot.
	internal static class DiscordAlerts
	{
		internal const string WebhookFileName = "discord-webhook.txt";
		private const int QueueCap = 64;
		private const int DebounceMs = 2000;
		private const int RequestTimeoutMs = 5000;
		// Discord caps content at 2000 characters; staying under it means a batch is never
		// rejected for length. Longer batches split into several posts.
		internal const int MaxContentChars = 1900;
		private const int BackoffFloorSeconds = 30;
		private const int BackoffCeilingSeconds = 900;

		// Wired by Plugin at boot; null-safe so the linked tests run without a logger. The
		// version string is passed in rather than read from Plugin, which references BepInEx
		// and would drag it into the linked test build.
		// Explicitly null-initialised: in the linked test build nothing ever assigns them, and
		// the strict compile treats "never assigned" as an error.
		internal static Action<string> LogWarning = null;
		internal static Action<string> LogInfo = null;
		private static string _pluginVersion = "0";

		private static readonly object Sync = new object();
		private static readonly Queue<string> Pending = new Queue<string>();
		private static Thread _worker;
		private static volatile bool _running;
		private static string _webhookUrl = string.Empty;
		private static string _username = "Driftwood";
		private static int _backoffSeconds = BackoffFloorSeconds;
		private static int _sent;
		private static bool _failureWarned;

		// One plain sentence for the readiness document and the console's `status`, so "why are
		// there no alerts" is answerable from the panel instead of from a log dive.
		private static volatile string _state = "off (no Discord webhook is configured)";
		internal static string State => _state;
		internal static bool Enabled => _running;

		// Join/leave diffing. Main-thread only (called from the readiness sampler), so no lock.
		private static Dictionary<ulong, string> _lastRoster;
		private static int _lastIslandOneBased = -1;

		internal static void Initialise(HostConfig config, string instanceRoot, string pluginVersion)
		{
			try
			{
				if (!string.IsNullOrEmpty(pluginVersion)) _pluginVersion = pluginVersion;
				string source;
				string url = ResolveUrl(config, instanceRoot, out source);
				if (url.Length == 0) return;
				if (!LooksLikeWebhookUrl(url))
				{
					_state = "off (the configured value is not a Discord webhook URL)";
					LogWarning?.Invoke("The Discord webhook from " + source + " is not a Discord webhook URL " +
						"(expected https://discord.com/api/webhooks/...), so alerts are OFF. The URL itself is not logged.");
					return;
				}
				_webhookUrl = url;
				if (!string.IsNullOrEmpty(config?.ServerName)) _username = config.ServerName;
				_running = true;
				_worker = new Thread(WorkLoop) { IsBackground = true, Name = "Driftwood.DiscordAlerts" };
				_worker.Start();
				_state = "ok (0 sent this run)";
				LogInfo?.Invoke("Discord alerts are ON (webhook from " + source +
					"): joins, leaves, boss kills, island moves and blocked-join attempts will be posted.");
			}
			catch (Exception exception)
			{
				_state = "off (failed to start: " + exception.GetType().Name + ")";
				LogWarning?.Invoke("Discord alerts could not start (" + exception.Message +
					"); nothing else is affected.");
			}
		}

		// Bounded flush for the shutdown path: give the worker a moment to post what is queued,
		// then let the process go. Never blocks longer than the cap.
		internal static void StopAndFlush(int timeoutMs)
		{
			if (!_running) return;
			_running = false;
			try { _worker?.Join(Math.Max(0, Math.Min(2000, timeoutMs))); } catch { }
		}

		// ------------------------------------------------------------------
		// Event sources. Every one is cheap, main-thread safe, and a no-op while alerts are off.
		// ------------------------------------------------------------------

		// Called from the readiness sampler with the connected roster. Diffs against the last
		// sample: a new id is a join, a missing one is a leave. The FIRST sample after boot is
		// the baseline and emits joins normally - a fresh host cannot have players before its
		// first sample, so those joins are real.
		internal static void ObserveRoster(IList<ulong> ids, IList<string> names, int slots)
		{
			if (!_running || ids == null) return;
			Dictionary<ulong, string> current = new Dictionary<ulong, string>();
			for (int i = 0; i < ids.Count; i++)
			{
				if (ids[i] == 0UL) continue;
				current[ids[i]] = names != null && i < names.Count && !string.IsNullOrEmpty(names[i])
					? names[i]
					: ids[i].ToString(CultureInfo.InvariantCulture);
			}

			Dictionary<ulong, string> previous = _lastRoster;
			_lastRoster = current;
			foreach (string line in RosterChanges(previous, current, slots))
			{
				Enqueue(line);
			}
		}

		// Pure and linked into the test suite: previous may be null (first sample after boot).
		internal static List<string> RosterChanges(
			Dictionary<ulong, string> previous, Dictionary<ulong, string> current, int slots)
		{
			List<string> lines = new List<string>();
			if (previous == null) previous = new Dictionary<ulong, string>();
			string aboard = " (" + current.Count + " of " + slots + " aboard).";
			foreach (KeyValuePair<ulong, string> entry in current)
			{
				if (previous.ContainsKey(entry.Key)) continue;
				lines.Add(entry.Value + " joined the server" + aboard);
			}
			foreach (KeyValuePair<ulong, string> entry in previous)
			{
				if (current.ContainsKey(entry.Key)) continue;
				lines.Add(entry.Value + " left the server" + aboard);
			}
			return lines;
		}

		// Called from the sampler with the crew's current island, 1-based. The first
		// observation is the baseline (a server coming up on island 3 did not just sail there).
		internal static void ObserveIsland(int islandOneBased, int totalPlayable)
		{
			if (!_running || islandOneBased < 1) return;
			if (_lastIslandOneBased < 1)
			{
				_lastIslandOneBased = islandOneBased;
				return;
			}
			if (islandOneBased == _lastIslandOneBased) return;
			_lastIslandOneBased = islandOneBased;
			Enqueue("The crew moved to island " + islandOneBased +
				(totalPlayable > 0 ? " of " + totalPlayable : string.Empty) + ".");
		}

		internal static void BossDefeated(string bossName)
		{
			if (!_running) return;
			string name = string.IsNullOrEmpty(bossName) ? "The boss" : bossName;
			Enqueue(name + " was defeated.");
		}

		internal static void BlockedPlayerRejected(ulong steamId, string name)
		{
			if (!_running) return;
			string label = string.IsNullOrEmpty(name)
				? steamId.ToString(CultureInfo.InvariantCulture)
				: name + " (" + steamId.ToString(CultureInfo.InvariantCulture) + ")";
			Enqueue("Blocked player " + label + " tried to join and was removed.");
		}

		// ------------------------------------------------------------------
		// The pipe.
		// ------------------------------------------------------------------

		private static void Enqueue(string line)
		{
			lock (Sync)
			{
				if (Pending.Count >= QueueCap) return; // alerts are a courtesy; drop, never block
				Pending.Enqueue(line);
			}
		}

		private static void WorkLoop()
		{
			try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
			while (_running)
			{
				bool any;
				lock (Sync) any = Pending.Count > 0;
				if (!any)
				{
					Thread.Sleep(500);
					continue;
				}
				// Let a burst (a group joining, a boss kill plus its island move) coalesce
				// into one message instead of hitting Discord's rate limit one line at a time.
				Thread.Sleep(DebounceMs);

				List<string> lines;
				lock (Sync)
				{
					lines = new List<string>(Pending);
					Pending.Clear();
				}
				if (lines.Count == 0) continue;

				bool ok = true;
				foreach (string payload in BuildPayloads(_username, lines))
				{
					if (!Post(payload)) { ok = false; break; }
				}

				if (ok)
				{
					_backoffSeconds = BackoffFloorSeconds;
					_failureWarned = false;
					_state = "ok (" + _sent + " sent this run)";
					continue;
				}

				// The batch is DROPPED, not requeued: alerts describe moments, and a joined/left
				// pair delivered ten minutes late reads as happening now. Back off so a dead
				// webhook is asked about occasionally, not hammered.
				int wait = _backoffSeconds;
				_backoffSeconds = Math.Min(BackoffCeilingSeconds, _backoffSeconds * 2);
				for (int slept = 0; slept < wait && _running; slept++) Thread.Sleep(1000);
			}

			// Shutdown flush: one quick attempt at whatever is still queued, bounded by the
			// request timeout, so a stop broadcastable moment (the last leave) can still land.
			List<string> remaining;
			lock (Sync)
			{
				remaining = new List<string>(Pending);
				Pending.Clear();
			}
			if (remaining.Count > 0)
			{
				foreach (string payload in BuildPayloads(_username, remaining))
				{
					if (!Post(payload)) break;
				}
			}
		}

		private static bool Post(string payload)
		{
			try
			{
				// HttpWebRequest, deliberately: this file runs on the game's Unity Mono runtime,
				// where it is the same proven path SteamNameResolver uses. The obsoletion notice
				// is a .NET-8 opinion that only surfaces in the linked test build.
#pragma warning disable SYSLIB0014
				HttpWebRequest request = (HttpWebRequest)WebRequest.Create(_webhookUrl);
#pragma warning restore SYSLIB0014
				request.Method = "POST";
				request.ContentType = "application/json";
				request.Timeout = RequestTimeoutMs;
				request.ReadWriteTimeout = RequestTimeoutMs;
				request.UserAgent = "Driftwood/" + _pluginVersion;
				byte[] body = Encoding.UTF8.GetBytes(payload);
				request.ContentLength = body.Length;
				using (Stream stream = request.GetRequestStream())
				{
					stream.Write(body, 0, body.Length);
				}
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
				{
					int code = (int)response.StatusCode;
					if (code < 200 || code > 299) throw new WebException("HTTP " + code);
				}
				_sent++;
				return true;
			}
			catch (Exception exception)
			{
				string reason = exception is WebException web && web.Response is HttpWebResponse http
					? "HTTP " + (int)http.StatusCode
					: exception.GetType().Name;
				try { (exception as WebException)?.Response?.Close(); } catch { }
				_state = "failing (" + reason + "); alerts are dropped until the webhook answers again";
				if (!_failureWarned)
				{
					_failureWarned = true;
					LogWarning?.Invoke("Discord alert delivery failed (" + reason +
						"). Alerts are dropped while the webhook is unreachable; retrying with backoff. This message is not repeated per retry.");
				}
				return false;
			}
		}

		// ------------------------------------------------------------------
		// The pure parts, linked into the test suite.
		// ------------------------------------------------------------------

		// Only a genuine Discord webhook URL passes: https, a Discord host, the webhook path.
		// Everything else is refused so a customer-editable file can never point this server's
		// outbound POST at an arbitrary machine.
		internal static bool LooksLikeWebhookUrl(string candidate)
		{
			if (string.IsNullOrWhiteSpace(candidate)) return false;
			Uri uri;
			if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out uri)) return false;
			if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
			string host = uri.Host;
			bool discordHost =
				string.Equals(host, "discord.com", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(host, "discordapp.com", StringComparison.OrdinalIgnoreCase) ||
				host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
				host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);
			if (!discordHost) return false;
			return uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
		}

		// Splits a batch into as few posts as fit Discord's content cap. allowed_mentions is
		// pinned empty because half of every line is a player-chosen name, and "@everyone" is a
		// perfectly legal Steam persona.
		internal static List<string> BuildPayloads(string username, List<string> lines)
		{
			List<string> payloads = new List<string>();
			StringBuilder content = new StringBuilder();
			foreach (string raw in lines)
			{
				string line = SteamProfileParser.Sanitize(raw ?? string.Empty, 300);
				if (line.Length == 0) continue;
				if (content.Length > 0 && content.Length + 1 + line.Length > MaxContentChars)
				{
					payloads.Add(Payload(username, content.ToString()));
					content.Length = 0;
				}
				if (content.Length > 0) content.Append('\n');
				content.Append(line);
			}
			if (content.Length > 0) payloads.Add(Payload(username, content.ToString()));
			return payloads;
		}

		private static string Payload(string username, string content)
		{
			return Json.Object()
				.Add("username", Truncate(username, 80))
				.Add("content", Truncate(content, MaxContentChars))
				.AddRaw("allowed_mentions", "{\"parse\":[]}")
				.Close();
		}

		private static string Truncate(string value, int cap)
		{
			value = value ?? string.Empty;
			return value.Length <= cap ? value : value.Substring(0, cap);
		}

		private static string ResolveUrl(HostConfig config, string instanceRoot, out string source)
		{
			source = "nowhere";
			// The customer's file first: it is theirs, it survives the panel's config rewrite,
			// and it is how a managed customer turns this on today.
			if (!string.IsNullOrWhiteSpace(instanceRoot))
			{
				string path = Path.Combine(instanceRoot.Trim(), "Driftwood", WebhookFileName);
				try
				{
					if (File.Exists(path))
					{
						foreach (string raw in File.ReadAllLines(path))
						{
							string line = raw.Trim();
							if (line.Length == 0 || line[0] == '#') continue;
							source = WebhookFileName;
							return line;
						}
					}
				}
				catch (Exception exception)
				{
					LogWarning?.Invoke("The Discord webhook file could not be read (" +
						exception.GetType().Name + "); checking the config instead.");
				}
			}
			string inline = (config?.DiscordWebhookUrl ?? string.Empty).Trim();
			if (inline.Length > 0)
			{
				source = "the plugin config";
				return inline;
			}
			return string.Empty;
		}

		// Test seam: the diff state is static, and two tests observing rosters must not see
		// each other's baseline.
		internal static void ResetForTests()
		{
			_lastRoster = null;
			_lastIslandOneBased = -1;
			lock (Sync) Pending.Clear();
		}
	}
}
