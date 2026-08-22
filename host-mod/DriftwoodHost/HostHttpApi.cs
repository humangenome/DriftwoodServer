using System;
using System.Net;
using System.Text;
using System.Threading;

namespace DriftwoodHost
{
	// The status and control surface the panel's endpoint companion talks to, on port + 1.
	//
	// This game runs as no Steam game server at all, so there is NO A2S responder and no query
	// port. This endpoint is the A2S replacement, and the hosting endpoint's query companion is
	// its only intended caller.
	//
	// THE PLAYER COUNT RULE, and it is the important one: an unknown player count must stay
	// UNKNOWN. It is reported as -1, never as 0, because 0 is what marks a server empty and an
	// empty server gets reaped. A server that is still loading, or wedged, or whose world is not
	// running has an UNKNOWN population, not an empty one.
	//
	// Auth: /api/v1/status is read-only and unauthenticated by design - the firewall scopes this
	// port to loopback and the web server. EVERY mutating route requires X-Driftwood-Auth. Do not
	// relax the firewall on the assumption that the API authenticates; half of it deliberately
	// does not.
	internal sealed class HostHttpApi : IDisposable
	{
		public const string AuthHeader = "X-Driftwood-Auth";
		public const int UnknownPlayers = -1;

		private readonly HttpListener _listener = new HttpListener();
		private readonly Readiness _readiness;
		private readonly string _authToken;
		private readonly Func<bool> _save;
		private readonly int _port;
		private Thread _thread;
		private volatile bool _running;

		public HostHttpApi(int port, Readiness readiness, string authToken, Func<bool> save)
		{
			_port = port;
			_readiness = readiness;
			_authToken = authToken ?? string.Empty;
			_save = save;
		}

		public bool Start()
		{
			try
			{
				_listener.Prefixes.Add("http://+:" + _port + "/");
				_listener.Start();
			}
			catch (Exception)
			{
				// A wildcard prefix needs a URL ACL. Fall back to loopback, which is enough for a
				// same-box companion, rather than failing the whole server over a status port.
				try
				{
					_listener.Prefixes.Clear();
					_listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
					_listener.Start();
				}
				catch (Exception exception)
				{
					Plugin.Log?.LogError("The status API could not listen on port " + _port + ": " + exception.Message);
					return false;
				}
			}

			_running = true;
			_thread = new Thread(Loop) { IsBackground = true, Name = "Driftwood.HttpApi" };
			_thread.Start();
			Plugin.Log?.LogInfo("Status API listening on port " + _port + " (" + string.Join(", ", PrefixArray()) + ").");
			return true;
		}

		private string[] PrefixArray()
		{
			string[] prefixes = new string[_listener.Prefixes.Count];
			_listener.Prefixes.CopyTo(prefixes, 0);
			return prefixes;
		}

		private void Loop()
		{
			while (_running)
			{
				HttpListenerContext context;
				try
				{
					context = _listener.GetContext();
				}
				catch (Exception)
				{
					if (!_running) return;
					continue;
				}
				try
				{
					Handle(context);
				}
				catch (Exception exception)
				{
					Plugin.Log?.LogWarning("Status API request failed: " + exception.Message);
					try { context.Response.Abort(); } catch { }
				}
			}
		}

		private void Handle(HttpListenerContext context)
		{
			string path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath;
			string method = context.Request.HttpMethod ?? "GET";

			if (path.Equals("/api/v1/status", StringComparison.OrdinalIgnoreCase) ||
				path.Equals("/api/v1/status/", StringComparison.OrdinalIgnoreCase))
			{
				Write(context, 200, StatusJson());
				return;
			}

			if (path.Equals("/api/v1/save", StringComparison.OrdinalIgnoreCase) ||
				path.Equals("/api/v1/save/", StringComparison.OrdinalIgnoreCase))
			{
				if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
				{
					Write(context, 405, Json.Object().Add("error", "POST only").Close());
					return;
				}
				if (!Authorised(context))
				{
					Write(context, 401, Json.Object().Add("error", "unauthorised").Close());
					return;
				}
				bool saved = false;
				try { saved = _save != null && _save(); }
				catch (Exception exception) { Plugin.Log?.LogError("Save via the status API failed: " + exception.Message); }
				Write(context, saved ? 200 : 500, Json.Object().Add("saved", saved).Close());
				return;
			}

			Write(context, 404, Json.Object().Add("error", "not found").Close());
		}

		private bool Authorised(HttpListenerContext context)
		{
			if (_authToken.Length == 0) return false;
			string supplied = context.Request.Headers[AuthHeader];
			if (string.IsNullOrEmpty(supplied) || supplied.Length != _authToken.Length) return false;
			// Constant-time compare: this token is the customer's rcon password.
			int difference = 0;
			for (int i = 0; i < supplied.Length; i++) difference |= supplied[i] ^ _authToken[i];
			return difference == 0;
		}

		private string StatusJson()
		{
			bool worldRunning = _readiness.WorldRunning;
			// -1 (UNKNOWN), never 0, unless the world is genuinely up and genuinely empty.
			int players = worldRunning ? _readiness.Players : UnknownPlayers;
			return Json.Object()
				.Add("players", players)
				// Kept short on purpose: gameservers.reported_version is varchar(45) and truncates
				// on the way IN, where the version cron's detector cannot see it.
				.Add("gameVersion", Truncate(_readiness.GameVersion, 45))
				.Add("pluginVersion", _readiness.PluginVersion)
				.Add("phase", _readiness.Phase.ToString())
				.Add("reason", _readiness.Reason)
				.Add("worldRunning", worldRunning)
				.Add("bootAssertionsPassed", _readiness.BootAssertionsPassed)
				.Add("port", _readiness.Port)
				.Add("slots", _readiness.Slots)
				.Add("world", _readiness.WorldName)
				.AddStrings("roster", _readiness.Roster())
				.Close();
		}

		private static string Truncate(string value, int max) =>
			string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);

		private static void Write(HttpListenerContext context, int status, string body)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(body);
			context.Response.StatusCode = status;
			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = bytes.Length;
			context.Response.OutputStream.Write(bytes, 0, bytes.Length);
			context.Response.OutputStream.Close();
		}

		public void Dispose()
		{
			_running = false;
			try { _listener.Stop(); } catch { }
			try { _listener.Close(); } catch { }
		}
	}
}
