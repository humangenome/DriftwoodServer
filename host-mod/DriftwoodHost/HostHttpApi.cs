using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DriftwoodHost
{
	// THE one network surface this product has besides the gameplay socket, on port + 1, TCP.
	//
	// It serves two different audiences and that is the whole design:
	//
	//   THE PANEL, on loopback. /status and /save. The panel's poll, the version detector, the
	//   supervisor's refusal check and the save-before-stop all run ON the game box and reach
	//   this over 127.0.0.1.
	//
	//   THE PLAYER'S LAUNCHER, from the open internet. /health, /players, /manifest, /console,
	//   /snapshots. How to Fish runs no Steam interface, so there is NO A2S responder and no
	//   query port anywhere - this endpoint IS the query surface, and a launcher that cannot
	//   reach it reports every healthy server as Offline. That is the bug this file was
	//   rewritten to fix.
	//
	// ---------------------------------------------------------------------------------------
	// THE RULE THAT DECIDES EVERY ROUTE: a SteamID64 never leaves this box unauthenticated.
	//
	// /status publishes the roster as "<steamid64>:<name>" because the panel needs it and the
	// panel is the customer's own account. /players publishes names and connect durations and
	// nothing else, because it is public. They are two payloads built by two serializers, not
	// one payload behind a flag - a flag can be wrong, a serializer that never receives the id
	// cannot leak it.
	//
	// /status is LOOPBACK-ONLY unless the caller signs. Not "authenticated": loopback-only, so
	// that every existing panel caller keeps working untouched and the id-bearing payload
	// cannot reach a remote caller even if a future route table is edited wrongly.
	// ---------------------------------------------------------------------------------------
	//
	// AUTH IS HMAC, AND ONLY HMAC. There used to be a second scheme here - a static token in
	// X-Driftwood-Auth - while the launcher signed every request the family way. Two schemes
	// on one API is how one of them ends up unimplemented, which is exactly what had happened:
	// the launcher's console could not have authenticated to this server at all.
	//
	//   X-Driftwood-Timestamp: <unix seconds>
	//   X-Driftwood-Signature: hex(HMACSHA256(SHA256(AuthToken), "METHOD\n<path>\n<ts>\n<sha256hex(body)>"))
	//
	// Same construction as Beacon, Lantern, Hearth, Cauldron and Lodestone, and the launcher
	// already implements it byte for byte in DriftwoodHttpClient.BuildSignedRequest.
	//
	// WRITTEN ON A RAW TcpListener, NOT HttpListener, and that is not a style choice. An
	// HttpListener wildcard prefix needs a URL ACL (netsh urlacl) and without one it throws -
	// the old code then fell back to a loopback-only prefix and carried on, logging success.
	// On an open port that fallback is invisible and total: every launcher on earth reads the
	// server as Offline while the panel, on loopback, sees a perfectly healthy host. Lodestone
	// hit the same wall on the same class of Unity host and made the same choice.
	internal sealed class HostHttpApi : IDisposable
	{
		public const int UnknownPlayers = -1;

		internal const string TimestampHeader = ApiSignature.TimestampHeader;
		internal const string SignatureHeader = ApiSignature.SignatureHeader;
		internal const string ShaHeader = "X-Driftwood-Sha256";

		private const int MaxHeaderBytes = 16 * 1024;
		private const int MaxJsonBodyBytes = 64 * 1024;
		private const int MaxConnections = 16;
		private const int ReplayWindowSeconds = ApiSignature.ReplayWindowSeconds;

		private readonly Readiness _readiness;
		private readonly HostConfig _config;
		private readonly byte[] _authKey;
		private readonly bool _authUsable;
		private readonly int _port;

		private readonly Dictionary<string, long> _seenSignatures = new Dictionary<string, long>();
		private readonly object _seenLock = new object();

		private TcpListener _listener;
		private Thread _accept;
		private volatile bool _running;
		private int _connections;

		internal HostHttpApi(HostConfig config, Readiness readiness)
		{
			_config = config;
			_readiness = readiness;
			_port = config.EffectiveHttpPort;
			string token = config.AuthToken ?? string.Empty;
			_authUsable = token.Length > 0;
			_authKey = ApiSignature.KeyFromToken(token);
		}

		public bool Start()
		{
			try
			{
				// IPAddress.Any, and it is a real decision. The launcher a customer's players run
				// reaches this from the internet; the panel reaches it on loopback. One listener
				// serves both and the ROUTE TABLE, not the bind address, decides who may see
				// what.
				_listener = new TcpListener(IPAddress.Any, _port);
				_listener.Start();
			}
			catch (Exception exception)
			{
				Plugin.Log?.LogError("The status API could not listen on port " + _port + ": " + exception.Message);
				return false;
			}

			_running = true;
			_accept = new Thread(AcceptLoop) { IsBackground = true, Name = "Driftwood.HttpApi" };
			_accept.Start();
			Plugin.Log?.LogInfo("Status and launcher API listening on 0.0.0.0:" + _port + ".");
			if (!_authUsable)
			{
				// Fail-closed and LOUD. Every signed route refuses, which means the launcher's
				// console and every snapshot action refuse, and the panel could not save before a
				// stop either. Silent would be far worse.
				Plugin.Log?.LogError("No API token is configured, so every authenticated route on this server will refuse. " +
					"The panel writes this value; an empty one means the config was never written or was written wrongly.");
			}
			return true;
		}

		private void AcceptLoop()
		{
			int consecutiveFailures = 0;
			while (_running)
			{
				TcpClient client;
				try
				{
					client = _listener.AcceptTcpClient();
					consecutiveFailures = 0;
				}
				catch (Exception)
				{
					if (!_running) return;
					// A listener whose socket has died throws on every accept, and a loop that
					// only sleeps and retries spins forever against it. The panel decides whether
					// this server is up by asking this endpoint, so a permanently dead status
					// port is a running server the panel reports as down and restarts - which is
					// the failure mode the refuse-on-bind-failure path at boot exists to prevent,
					// arriving later by a different road. Rebind rather than spin.
					Thread.Sleep(250);
					if (++consecutiveFailures < 8) continue;
					consecutiveFailures = 0;
					try
					{
						_listener.Stop();
						_listener = new TcpListener(IPAddress.Any, _port);
						_listener.Start();
						Plugin.Log?.LogWarning("The status API listener stopped accepting and has been rebound on port " + _port + ".");
					}
					catch (Exception exception)
					{
						Plugin.Log?.LogError("The status API listener could not be rebound on port " + _port + ": " + exception.Message);
						Thread.Sleep(5000);
					}
					continue;
				}

				// A hard connection cap is the whole DoS story on a public port, and it is the
				// same one the sibling ships. Anything past it is closed immediately rather than
				// queued, so a flood costs a socket accept and nothing else - in particular it
				// never costs the frame loop.
				if (Interlocked.Increment(ref _connections) > MaxConnections)
				{
					Interlocked.Decrement(ref _connections);
					try { client.Close(); } catch { }
					continue;
				}

				Thread session = new Thread(() =>
				{
					try { Serve(client); }
					catch (Exception exception) { Plugin.Log?.LogDebug("API session: " + exception.Message); }
					finally
					{
						Interlocked.Decrement(ref _connections);
						try { client.Close(); } catch { }
					}
				});
				session.IsBackground = true;
				session.Name = "Driftwood.HttpApi.Session";
				session.Start();
			}
		}

		// ------------------------------------------------------------------
		// THE ROUTE TABLE.
		//
		// Every route states its audience HERE and nowhere else, and the dispatcher reads the
		// tier off this table before it reaches a handler. There is deliberately no default:
		// a path that matches no row is 404, never "allowed because nobody said otherwise".
		//
		// It is a table rather than a chain of ifs so that the tier is DATA - which means the
		// cross-repo contract test can read it and assert that every route the launcher signs
		// is a route this server requires a signature for. That assertion is the one that
		// would have caught the break this file exists to fix: the launcher HMAC-signed
		// /console while this server accepted a static token on a route that did not exist.
		// ------------------------------------------------------------------
		internal enum AuthTier
		{
			// Anyone. This game has no A2S responder, so these three ARE the query surface,
			// and everything on them is what an A2S query publishes for every other game on
			// the fleet. No ids, ever.
			Public,
			// The panel and the supervisor, on 127.0.0.1 - or any caller that can sign. Carries
			// SteamID64s, which is why remote-and-unsigned is refused.
			LoopbackOrSigned,
			// A signature, always. Everything a remote caller can DRIVE.
			Signed
		}

		private sealed class RouteSpec
		{
			internal string Method;
			internal string Pattern;
			internal AuthTier Tier;
		}

		internal const string SnapshotImportPattern = "/api/v1/snapshots/import-restore";

		private static readonly RouteSpec[] Routes =
		{
			new RouteSpec { Method = "GET",  Pattern = "/api/v1/health",   Tier = AuthTier.Public },
			new RouteSpec { Method = "GET",  Pattern = "/api/v1/players",  Tier = AuthTier.Public },
			new RouteSpec { Method = "GET",  Pattern = "/api/v1/manifest", Tier = AuthTier.Public },
			new RouteSpec { Method = "GET",  Pattern = "/api/v1/status",   Tier = AuthTier.LoopbackOrSigned },
			new RouteSpec { Method = "POST", Pattern = "/api/v1/save",     Tier = AuthTier.LoopbackOrSigned },
			new RouteSpec { Method = "POST", Pattern = "/api/v1/console",  Tier = AuthTier.Signed },
			new RouteSpec { Method = "GET",  Pattern = "/api/v1/snapshots", Tier = AuthTier.Signed },
			new RouteSpec { Method = "POST", Pattern = "/api/v1/snapshots", Tier = AuthTier.Signed },
			new RouteSpec { Method = "POST", Pattern = SnapshotImportPattern, Tier = AuthTier.Signed },
			new RouteSpec { Method = "GET",  Pattern = "/api/v1/snapshots/{id}/download", Tier = AuthTier.Signed },
			new RouteSpec { Method = "POST", Pattern = "/api/v1/snapshots/{id}/restore",  Tier = AuthTier.Signed }
		};

		// ------------------------------------------------------------------
		// Request
		// ------------------------------------------------------------------
		private sealed class Request
		{
			internal string Method = "";
			internal string Path = "";
			internal string Query = "";
			internal readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			internal byte[] Body = new byte[0];
			// Set instead of Body when the payload was streamed to disk (a snapshot import).
			internal string BodyFile;
			internal string BodySha = string.Empty;
			internal bool IsLoopback;

			internal string Header(string name)
			{
				string value;
				return Headers.TryGetValue(name, out value) ? value : null;
			}
		}

		private void Serve(TcpClient client)
		{
			client.NoDelay = true;
			client.ReceiveTimeout = 30000;
			client.SendTimeout = 120000;
			NetworkStream stream = client.GetStream();

			bool loopback = false;
			try
			{
				IPEndPoint endpoint = client.Client.RemoteEndPoint as IPEndPoint;
				loopback = endpoint != null && IPAddress.IsLoopback(endpoint.Address);
			}
			catch { }

			Request request = null;
			try
			{
				request = ReadRequest(stream, loopback);
			}
			catch (Exception exception)
			{
				Plugin.Log?.LogDebug("API request read failed: " + exception.Message);
			}

			if (request == null)
			{
				try { Write(stream, 400, "application/json", Error("bad request")); } catch { }
				return;
			}

			try
			{
				Route(stream, request);
			}
			catch (Exception exception)
			{
				Plugin.Log?.LogWarning("API " + request.Method + " " + request.Path + " failed: " + exception.Message);
				try { Write(stream, 500, "application/json", Error("internal error")); } catch { }
			}
			finally
			{
				if (request.BodyFile != null)
				{
					try { if (File.Exists(request.BodyFile)) File.Delete(request.BodyFile); } catch { }
				}
			}
		}

		private Request ReadRequest(NetworkStream stream, bool loopback)
		{
			MemoryStream head = new MemoryStream();
			byte[] one = new byte[1];
			byte b0 = 0, b1 = 0, b2 = 0, b3 = 0;
			bool complete = false;
			while (head.Length < MaxHeaderBytes)
			{
				int read;
				try { read = stream.Read(one, 0, 1); }
				catch { return null; }
				if (read <= 0) return null;
				head.WriteByte(one[0]);
				b0 = b1; b1 = b2; b2 = b3; b3 = one[0];
				if (b0 == (byte)'\r' && b1 == (byte)'\n' && b2 == (byte)'\r' && b3 == (byte)'\n')
				{
					complete = true;
					break;
				}
			}
			if (!complete) return null;

			string text = Encoding.UTF8.GetString(head.ToArray());
			string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
			if (lines.Length == 0) return null;
			string[] parts = lines[0].Split(' ');
			if (parts.Length < 2) return null;

			Request request = new Request { Method = parts[0].ToUpperInvariant(), IsLoopback = loopback };
			string target = parts[1];
			int question = target.IndexOf('?');
			if (question >= 0)
			{
				request.Path = target.Substring(0, question);
				request.Query = target.Substring(question + 1);
			}
			else
			{
				request.Path = target;
			}
			// Trailing slash is not a different route. The panel has historically called both
			// "/api/v1/status" and "/api/v1/status/", so normalise once here rather than listing
			// every route twice and eventually forgetting one.
			if (request.Path.Length > 1 && request.Path.EndsWith("/", StringComparison.Ordinal))
			{
				request.Path = request.Path.Substring(0, request.Path.Length - 1);
			}

			for (int i = 1; i < lines.Length; i++)
			{
				if (lines[i].Length == 0) break;
				int colon = lines[i].IndexOf(':');
				if (colon <= 0) continue;
				request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
			}

			// Chunked bodies are refused rather than mis-read. Nothing that talks to this API
			// sends one - the launcher sets an explicit ContentLength even on a streamed file
			// upload - and treating a chunked body as "no body" would fail the signature check
			// instead, which presents as a mysterious 401 rather than as an unsupported
			// encoding.
			string transferEncoding = request.Header("Transfer-Encoding");
			if (transferEncoding != null &&
				transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				request.Path = "/chunked-unsupported";
				return request;
			}

			string contentLength = request.Header("Content-Length");
			long length;
			if (contentLength == null || !long.TryParse(contentLength, out length) || length <= 0)
			{
				request.BodySha = Sha256Hex(new byte[0]);
				return request;
			}

			bool upload = IsUploadRoute(request.Method, request.Path);
			long cap = upload ? SnapshotStore.MaxImportBytes : MaxJsonBodyBytes;
			if (length > cap) return null;

			if (!upload)
			{
				byte[] body = new byte[(int)length];
				int got = 0;
				while (got < body.Length)
				{
					int read;
					try { read = stream.Read(body, got, body.Length - got); }
					catch { return null; }
					if (read <= 0) return null;
					got += read;
				}
				request.Body = body;
				request.BodySha = Sha256Hex(body);
				return request;
			}

			// Streamed to disk, hashed on the way past. A world archive must never be held in
			// the game process's heap: this runs inside Unity, and a body-sized allocation on a
			// box hosting a hundred instances is a memory spike nobody asked for.
			string temporary = Path.Combine(Path.GetTempPath(), "driftwood-upload-" + Guid.NewGuid().ToString("N") + ".zip");
			try
			{
				using (SHA256 sha = SHA256.Create())
				using (FileStream file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
				{
					byte[] buffer = new byte[64 * 1024];
					long remaining = length;
					while (remaining > 0)
					{
						int want = (int)Math.Min(buffer.Length, remaining);
						int read;
						try { read = stream.Read(buffer, 0, want); }
						catch { read = -1; }
						if (read <= 0)
						{
							try { file.Dispose(); } catch { }
							try { File.Delete(temporary); } catch { }
							return null;
						}
						sha.TransformBlock(buffer, 0, read, null, 0);
						file.Write(buffer, 0, read);
						remaining -= read;
					}
					sha.TransformFinalBlock(new byte[0], 0, 0);
					request.BodySha = ApiSignature.HexEncode(sha.Hash);
				}
			}
			catch
			{
				try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
				return null;
			}
			request.BodyFile = temporary;
			return request;
		}

		// Decided before routing, because the body has to be read before the route's handler
		// exists - and a snapshot archive must be streamed to disk rather than held in the
		// game process's heap. Derived from the same pattern constant the table uses.
		private static bool IsUploadRoute(string method, string path)
		{
			return string.Equals(method, "POST", StringComparison.Ordinal) &&
				string.Equals(path, SnapshotImportPattern, StringComparison.OrdinalIgnoreCase);
		}

		// ------------------------------------------------------------------
		// Routing
		// ------------------------------------------------------------------
		private void Route(NetworkStream stream, Request request)
		{
			if (request.Path == "/chunked-unsupported")
			{
				Write(stream, 411, "application/json", Error("this API needs a Content-Length; chunked bodies are not accepted"));
				return;
			}

			string id;
			RouteSpec spec = Match(request.Method, request.Path, out id);
			if (spec == null)
			{
				Write(stream, 404, "application/json", Error("not found"));
				return;
			}
			if (!Allowed(spec.Tier, request))
			{
				Refused(stream);
				return;
			}
			Dispatch(stream, request, spec, id);
		}

		private static RouteSpec Match(string method, string path, out string id)
		{
			id = string.Empty;
			for (int i = 0; i < Routes.Length; i++)
			{
				RouteSpec spec = Routes[i];
				if (!string.Equals(spec.Method, method, StringComparison.Ordinal)) continue;
				int placeholder = spec.Pattern.IndexOf("{id}", StringComparison.Ordinal);
				if (placeholder < 0)
				{
					if (string.Equals(spec.Pattern, path, StringComparison.OrdinalIgnoreCase)) return spec;
					continue;
				}
				string prefix = spec.Pattern.Substring(0, placeholder);
				string suffix = spec.Pattern.Substring(placeholder + 4);
				if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
				if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
				int length = path.Length - prefix.Length - suffix.Length;
				if (length <= 0) continue;
				id = path.Substring(prefix.Length, length);
				return spec;
			}
			return null;
		}

		private bool Allowed(AuthTier tier, Request request)
		{
			switch (tier)
			{
				case AuthTier.Public: return true;
				case AuthTier.LoopbackOrSigned: return request.IsLoopback || Authorised(request);
				default: return Authorised(request);
			}
		}

		private void Dispatch(NetworkStream stream, Request request, RouteSpec spec, string id)
		{
			switch (spec.Pattern)
			{
				case "/api/v1/health": Health(stream); return;
				case "/api/v1/players": PublicPlayers(stream); return;
				case "/api/v1/manifest": Manifest(stream); return;
				case "/api/v1/status": Write(stream, 200, "application/json", StatusJson()); return;

				case "/api/v1/save":
				{
					string failure;
					bool saved = MainThread.Run(WorldLifecycle.SaveNow, 15000, out failure);
					Write(stream, saved ? 200 : 500, "application/json",
						Json.Object().Add("saved", saved).Add("error", saved ? null : failure).Close());
					return;
				}

				case "/api/v1/console":
				{
					string command = JsonRead.String(request.Body, "command");
					string output;
					// The actor is TRANSPORT-DERIVED, never claimed: "panel" is a loopback
					// caller (the hosting panel or the supervisor on this box), "console" is a
					// remote caller that proved itself with this server's signed API secret.
					// It exists for the owner-action audit trail and is not a permission level -
					// the route's auth tier already decided who may be here.
					bool ok = ConsoleCommands.Execute(command,
						request.IsLoopback ? "panel" : "console", _readiness, _config, out output);
					// 200 with ok=false on a refused or unknown command, by contract with the
					// launcher: it prints the reason instead of an HTTP status, and a status code
					// is not a sentence anybody can act on.
					Write(stream, 200, "application/json",
						Json.Object().Add("ok", ok).Add("output", output).Close());
					return;
				}

				case "/api/v1/snapshots":
					if (spec.Method == "GET") SnapshotList(stream);
					else SnapshotCreate(stream);
					return;

				case SnapshotImportPattern: SnapshotImportRestore(stream, request); return;
				case "/api/v1/snapshots/{id}/download": SnapshotDownload(stream, id); return;
				case "/api/v1/snapshots/{id}/restore": SnapshotRestore(stream, id); return;
			}

			// Unreachable while the table and this switch agree, and a loud 500 rather than a
			// quiet fall-through if they ever stop agreeing.
			Plugin.Log?.LogError("Route " + spec.Pattern + " is in the route table with no handler.");
			Write(stream, 500, "application/json", Error("internal error"));
		}

		private void SnapshotList(NetworkStream stream)
		{
			List<SnapshotStore.Summary> list = SnapshotStore.List();
			StringBuilder items = new StringBuilder("[");
			for (int i = 0; i < list.Count; i++)
			{
				if (i > 0) items.Append(',');
				items.Append(Json.Object()
					.Add("id", list[i].Id)
					.Add("taken_unix", list[i].TakenUnix)
					.Add("size_bytes", list[i].SizeBytes)
					.Add("sha256", list[i].Sha256)
					.Close());
			}
			items.Append(']');
			Write(stream, 200, "application/json",
				Json.Object().AddRaw("snapshots", items.ToString()).Close());
		}

		private void SnapshotCreate(NetworkStream stream)
		{
			string id, failure;
			if (!SnapshotStore.Create("manual", out id, out failure))
			{
				Write(stream, 500, "application/json", Error(failure));
				return;
			}
			Write(stream, 200, "application/json",
				Json.Object().AddRaw("snapshot", Json.Object().Add("id", id).Close()).Close());
		}

		private void SnapshotImportRestore(NetworkStream stream, Request request)
		{
			if (request.BodyFile == null)
			{
				Write(stream, 400, "application/json", Error("no archive was uploaded"));
				return;
			}
			string id, failure;
			if (!SnapshotStore.Adopt(request.BodyFile, out id, out failure))
			{
				Write(stream, 400, "application/json", Error(failure));
				return;
			}
			if (!SnapshotStore.Restore(id, out failure))
			{
				Write(stream, 500, "application/json", Error(failure));
				return;
			}
			Write(stream, 200, "application/json", Json.Object().Add("ok", true).Close());
			SnapshotStore.EndProcessForRestore();
		}

		private void SnapshotDownload(NetworkStream stream, string id)
		{
			string file = SnapshotStore.ResolvePath(id);
			if (file == null) { Write(stream, 404, "application/json", Error("not found")); return; }
			WriteFile(stream, file, SnapshotStore.HashOf(file));
		}

		private void SnapshotRestore(NetworkStream stream, string id)
		{
			string failure;
			if (!SnapshotStore.Restore(id, out failure))
			{
				Write(stream, 500, "application/json", Error(failure));
				return;
			}
			Write(stream, 200, "application/json", Json.Object().Add("ok", true).Close());
			SnapshotStore.EndProcessForRestore();
		}

		// ------------------------------------------------------------------
		// Payloads
		// ------------------------------------------------------------------
		// 200 ONLY WHEN A PLAYER COULD ACTUALLY JOIN. A server whose world is still loading, or
		// wedged, is not joinable, and answering 200 for it would put an Online badge on a
		// server nobody can get into. 503 makes the launcher say Offline, which is the honest
		// word for "not right now".
		//
		// The consequence worth stating: because this is 200-only-when-running, player_count on
		// a 200 is always a REAL number. The -1 unknown sentinel belongs to /status, whose
		// callers reap empty servers and must be able to tell empty from unknown.
		private void Health(NetworkStream stream)
		{
			bool running = _readiness.WorldRunning;
			string body = Json.Object()
				.Add("ok", running)
				.Add("instance", _readiness.WorldName)
				.Add("server_name", string.IsNullOrEmpty(_config.ServerName) ? _readiness.WorldName : _config.ServerName)
				// The launcher reads driftwood_version as its liveness proof and treats an absent
				// or empty value as Offline, so this field must never be blank.
				.Add("driftwood_version", string.IsNullOrEmpty(_readiness.PluginVersion) ? Plugin.Version : _readiness.PluginVersion)
				.Add("driftwood_build", _readiness.GameVersion)
				.Add("gameplay_port", _readiness.Port)
				.Add("max_players", _readiness.Slots)
				.Add("player_count", running ? _readiness.Players : UnknownPlayers)
				// v1 servers have no join password: the panel does not emit one and HostConfig
				// REFUSES TO START when one is configured. Published explicitly as false so the
				// launcher's pre-flight prompt stays off rather than sitting on "unknown".
				.Add("password_protected", false)
				.Add("phase", _readiness.Phase.ToString())
				.Close();
			Write(stream, running ? 200 : 503, "application/json", body);
		}

		// PUBLIC. Names and durations, the same facts an A2S player query publishes for every
		// other game on this fleet. No ids - see the file header.
		private void PublicPlayers(NetworkStream stream)
		{
			List<PlayerDirectory.Row> rows = _readiness.WorldRunning
				? PlayerDirectory.Snapshot()
				: new List<PlayerDirectory.Row>();
			StringBuilder items = new StringBuilder("[");
			for (int i = 0; i < rows.Count; i++)
			{
				if (i > 0) items.Append(',');
				// POSITIONAL placeholder, not DriftwoodIdentity.Placeholder. That one derives
				// its digits from steamId % 10000, which publishes the last four digits of a
				// SteamID64 - a small leak, but this is the one route in the product where an
				// id must not appear in any form. The panel's roster keeps the real placeholder
				// because it already carries the whole id.
				Json entry = Json.Object()
					.Add("name", string.IsNullOrEmpty(rows[i].Name) ? "Player " + (i + 1) : rows[i].Name)
					.Add("connected_seconds", rows[i].ConnectedSeconds);
				// Absent, not zero, when this build of FishNet cannot measure it. The launcher
				// renders the row without a ping; a fabricated number would be worse.
				if (rows[i].PingMs.HasValue) entry.Add("ping_ms", rows[i].PingMs.Value);
				items.Append(entry.Close());
			}
			items.Append(']');
			Write(stream, 200, "application/json", Json.Object()
				.Add("instance", _readiness.WorldName)
				.Add("count", rows.Count)
				.AddRaw("players", items.ToString())
				.Close());
		}

		// PUBLIC. A player needs to know what a server runs before they hold any credential for
		// it. The curated lists are empty on a hosted instance because this product
		// ships no mod picker for this game; the launcher hides an empty curated section rather
		// than rendering a card that apologises for itself.
		private void Manifest(NetworkStream stream)
		{
			List<ModManifest.Entry> plugins = ModManifest.LoadedPlugins();
			StringBuilder items = new StringBuilder("[");
			for (int i = 0; i < plugins.Count; i++)
			{
				if (i > 0) items.Append(',');
				items.Append(Json.Object()
					.Add("id", plugins[i].Id)
					.Add("name", plugins[i].Name)
					.Add("version", plugins[i].Version)
					.Add("ours", plugins[i].Ours)
					.Close());
			}
			items.Append(']');
			Write(stream, 200, "application/json", Json.Object()
				.Add("manifest_version", 1)
				.Add("instance", _readiness.WorldName)
				.Add("driftwood_version", string.IsNullOrEmpty(_readiness.PluginVersion) ? Plugin.Version : _readiness.PluginVersion)
				.Add("generated_unix", PlayerDirectory.NowUnix())
				.AddRaw("server_mods", items.ToString())
				.AddRaw("required", "[]")
				.AddRaw("recommended", "[]")
				.AddRaw("blocked", "[]")
				.Close());
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
				// Empty in the normal case. Non-empty means a world restore was accepted and is
				// waiting for the start that applies it - which is a world that has NOT changed
				// yet, and is exactly the state that must never be invisible.
				.Add("pending_restore", SnapshotStore.PendingRestoreId)
				.AddStrings("roster", _readiness.Roster())
				// THE WORLD BLOCK AND PLAYER POSITIONS. Same tier as the roster ids - loopback or
				// signed, never the public routes - because where a person is standing is the
				// same class of fact as who they are. The panel relays it to the owner's map;
				// nothing on the open /players route ever receives a coordinate.
				.Add("island", worldRunning ? _readiness.IslandCurrent : 0)
				.Add("islandTotal", _readiness.IslandTotal)
				.Add("islandUnlocked", worldRunning ? _readiness.IslandUnlocked : 0)
				.Add("islandChanging", worldRunning && _readiness.IslandChanging)
				.Add("wallet", worldRunning ? _readiness.Wallet : -1L)
				.AddRaw("islandCentre", (worldRunning && _readiness.IslandCentreKnown)
					? "[" + _readiness.IslandCentreX.ToString("0.##", CultureInfo.InvariantCulture) + "," + _readiness.IslandCentreZ.ToString("0.##", CultureInfo.InvariantCulture) + "]"
					: "null")
				.Add("islandRadius", worldRunning ? _readiness.IslandRadius : 0d)
				.Add("uptimeSeconds", _readiness.UptimeSeconds)
				.AddRaw("positions", worldRunning ? PositionsJson() : "[]")
				.Close();
		}

		// One entry per connected player: the id (this is the identified tier), the display
		// name, and x/y/z in world units when the game gave a transform on the last sample.
		// A row the sampler could not place is published WITHOUT coordinates, so the map
		// draws nothing for it rather than a dot at the origin.
		private static string PositionsJson()
		{
			List<PlayerDirectory.Row> rows = PlayerDirectory.Snapshot();
			StringBuilder items = new StringBuilder("[");
			for (int i = 0; i < rows.Count; i++)
			{
				if (i > 0) items.Append(',');
				Json entry = Json.Object()
					.Add("id", rows[i].SteamId.ToString(CultureInfo.InvariantCulture))
					.Add("name", string.IsNullOrEmpty(rows[i].Name) ? DriftwoodIdentity.Placeholder(rows[i].SteamId) : rows[i].Name)
					.Add("connected_seconds", rows[i].ConnectedSeconds);
				if (rows[i].HasPosition)
				{
					entry.Add("x", Math.Round((double)rows[i].X, 2))
						.Add("y", Math.Round((double)rows[i].Y, 2))
						.Add("z", Math.Round((double)rows[i].Z, 2));
				}
				items.Append(entry.Close());
			}
			items.Append(']');
			return items.ToString();
		}

		// ------------------------------------------------------------------
		// Auth
		// ------------------------------------------------------------------
		private bool Authorised(Request request)
		{
			if (!_authUsable) return false;
			string timestampHeader = request.Header(TimestampHeader);
			string signatureHeader = request.Header(SignatureHeader);
			if (string.IsNullOrEmpty(timestampHeader) || string.IsNullOrEmpty(signatureHeader)) return false;

			long timestamp;
			if (!long.TryParse(timestampHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out timestamp)) return false;
			long now = PlayerDirectory.NowUnix();
			if (Math.Abs(now - timestamp) > ReplayWindowSeconds) return false;

			string canonical = ApiSignature.Canonical(request.Method, request.Path, timestamp, request.BodySha);
			string expected = ApiSignature.Compute(_authKey, canonical);

			string supplied = signatureHeader.Trim().ToLowerInvariant();
			if (!ApiSignature.ConstantTimeEquals(expected, supplied)) return false;

			// Replay guard. Without it a captured signature is good for the whole window, which
			// on a restore route means anybody who saw one request can roll a world back.
			lock (_seenLock)
			{
				long cutoff = now - ReplayWindowSeconds;
				if (_seenSignatures.Count > 0)
				{
					List<string> stale = new List<string>();
					foreach (KeyValuePair<string, long> pair in _seenSignatures)
					{
						if (pair.Value < cutoff) stale.Add(pair.Key);
					}
					foreach (string key in stale) _seenSignatures.Remove(key);
				}
				if (_seenSignatures.ContainsKey(supplied)) return false;
				_seenSignatures[supplied] = timestamp;
			}
			return true;
		}

		private void Refused(NetworkStream stream)
		{
			Write(stream, 401, "application/json", Error(_authUsable
				? "unauthorised"
				: "this server has no API token configured, so it cannot authenticate anything"));
		}

		// ------------------------------------------------------------------
		// Response
		// ------------------------------------------------------------------
		private void Write(NetworkStream stream, int status, string contentType, string body)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
			StringBuilder head = new StringBuilder();
			head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
			head.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
			head.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
			head.Append("Cache-Control: no-store\r\n");
			// The launcher's Mods tab can be opened from a browser-hosted surface in this
			// family; the sensitive routes are signed, so a permissive read origin costs
			// nothing that the open port has not already granted.
			head.Append("Access-Control-Allow-Origin: *\r\n");
			head.Append("Connection: close\r\n\r\n");
			byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
			stream.Write(headBytes, 0, headBytes.Length);
			if (bytes.Length > 0) stream.Write(bytes, 0, bytes.Length);
			stream.Flush();
		}

		private void WriteFile(NetworkStream stream, string path, string sha256)
		{
			FileInfo info = new FileInfo(path);
			StringBuilder head = new StringBuilder();
			head.Append("HTTP/1.1 200 OK\r\n");
			head.Append("Content-Type: application/zip\r\n");
			head.Append("Content-Length: ").Append(info.Length).Append("\r\n");
			head.Append("Content-Disposition: attachment; filename=\"").Append(info.Name).Append("\"\r\n");
			// The launcher verifies the download against this header, so it is part of the
			// contract and not a convenience.
			head.Append(ShaHeader).Append(": ").Append(sha256).Append("\r\n");
			head.Append("Cache-Control: no-store\r\n");
			head.Append("Connection: close\r\n\r\n");
			byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
			stream.Write(headBytes, 0, headBytes.Length);

			using (FileStream file = File.OpenRead(path))
			{
				byte[] buffer = new byte[64 * 1024];
				while (true)
				{
					int read = file.Read(buffer, 0, buffer.Length);
					if (read <= 0) break;
					stream.Write(buffer, 0, read);
				}
			}
			stream.Flush();
		}

		private static string Error(string message)
		{
			return Json.Object().Add("error", message ?? "error").Close();
		}

		private static string StatusText(int status)
		{
			switch (status)
			{
				case 200: return "OK";
				case 400: return "Bad Request";
				case 401: return "Unauthorized";
				case 404: return "Not Found";
				case 405: return "Method Not Allowed";
				case 411: return "Length Required";
				case 500: return "Internal Server Error";
				case 503: return "Service Unavailable";
				default: return "OK";
			}
		}

		private static string Truncate(string value, int max) =>
			string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);

		private static string Sha256Hex(byte[] bytes) => ApiSignature.Sha256Hex(bytes);

		public void Dispose()
		{
			_running = false;
			try { _listener?.Stop(); } catch { }
		}
	}

	// One string field out of a small JSON object. The mod deliberately does not bind the
	// game's Newtonsoft (build-churn liability) and the game's Mono has no System.Text.Json,
	// and the only bodies this API accepts are {"command": "..."} - so this is the whole
	// parser it needs rather than a dependency it does not.
	internal static class JsonRead
	{
		internal static string String(byte[] body, string name)
		{
			if (body == null || body.Length == 0) return string.Empty;
			string text;
			try { text = Encoding.UTF8.GetString(body); }
			catch { return string.Empty; }

			string needle = "\"" + name + "\"";
			int at = text.IndexOf(needle, StringComparison.Ordinal);
			if (at < 0) return string.Empty;
			int colon = text.IndexOf(':', at + needle.Length);
			if (colon < 0) return string.Empty;
			int i = colon + 1;
			while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
			if (i >= text.Length || text[i] != '"') return string.Empty;
			i++;

			StringBuilder value = new StringBuilder();
			while (i < text.Length)
			{
				char c = text[i];
				if (c == '\\')
				{
					i++;
					if (i >= text.Length) break;
					char escaped = text[i];
					switch (escaped)
					{
						case 'n': value.Append('\n'); break;
						case 'r': value.Append('\r'); break;
						case 't': value.Append('\t'); break;
						case 'b': value.Append('\b'); break;
						case 'f': value.Append('\f'); break;
						case 'u':
							if (i + 4 < text.Length)
							{
								int code;
								if (int.TryParse(text.Substring(i + 1, 4), NumberStyles.HexNumber,
									CultureInfo.InvariantCulture, out code))
								{
									value.Append((char)code);
								}
								i += 4;
							}
							break;
						default: value.Append(escaped); break;
					}
					i++;
					continue;
				}
				if (c == '"') break;
				value.Append(c);
				i++;
			}
			return value.ToString();
		}
	}
}
