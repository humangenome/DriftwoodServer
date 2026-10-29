using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace DriftwoodHost
{
	// The Steam half of the ticket check: one GET to ISteamUserAuth/AuthenticateUserTicket on
	// api.steampowered.com with the Web API key the name resolver already loads. Rules and
	// parsing live in SteamTicketRules.cs. Runs on the HTTP thread, before the main-thread claim.
	//
	// Never logs the key or the ticket. A client retries its claim within a few seconds, so an
	// answer is cached per ticket for a minute: one Steam call per join, not one per retry.
	internal static class SteamTicketCheck
	{
		// Inside the app's per-attempt budget, so the first attempt usually gets its answer and a
		// retry after a slow Steam hits the cache.
		private const int RequestTimeoutMs = 2000;
		private const int MaxResponseBytes = 64 * 1024;
		private const int CacheSeconds = 60;
		private const int MaxCache = 256;

		private sealed class Cached
		{
			public DateTime At;
			public TicketCheck Check;
			public ulong SteamId;
			public string Detail;
		}

		private static readonly object Sync = new object();
		private static readonly Dictionary<string, Cached> Cache = new Dictionary<string, Cached>(StringComparer.Ordinal);
		private static bool _warnedKeyRefused;

		internal static TicketCheck Check(string apiKey, string ticketHex, out ulong steamId, out string detail)
		{
			steamId = 0UL;
			detail = null;
			if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(ticketHex)) return TicketCheck.NotChecked;

			lock (Sync)
			{
				Cached hit;
				if (Cache.TryGetValue(ticketHex, out hit) && (DateTime.UtcNow - hit.At).TotalSeconds < CacheSeconds)
				{
					steamId = hit.SteamId;
					detail = hit.Detail;
					return hit.Check;
				}
			}

			TicketCheck check;
			try
			{
				string body = Fetch(apiKey, ticketHex);
				check = SteamTicketRules.ParseAuthenticateResponse(body, out steamId, out detail);
			}
			catch (WebException exception)
			{
				HttpWebResponse response = exception.Response as HttpWebResponse;
				int status = response == null ? 0 : (int)response.StatusCode;
				check = TicketCheck.Unavailable;
				detail = status == 0 ? exception.Status.ToString() : "HTTP " + status.ToString(CultureInfo.InvariantCulture);
				if ((status == 401 || status == 403) && !_warnedKeyRefused)
				{
					_warnedKeyRefused = true;
					Plugin.Log?.LogWarning("Steam refused the Web API key for ticket checks (" + detail +
						"); identity claims fall back to the same-address rule.");
				}
			}
			catch (Exception exception)
			{
				check = TicketCheck.Unavailable;
				detail = exception.GetType().Name;
			}

			// Only real answers are cached; an unavailable Steam is asked again on the retry.
			if (check != TicketCheck.Unavailable)
			{
				lock (Sync)
				{
					if (Cache.Count >= MaxCache) Cache.Clear();
					Cache[ticketHex] = new Cached { At = DateTime.UtcNow, Check = check, SteamId = steamId, Detail = detail };
				}
			}
			return check;
		}

		private static string Fetch(string apiKey, string ticketHex)
		{
			// Unity's Mono answers modern TLS only when asked; asking twice is harmless.
			try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
				"https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/?key=" + apiKey +
				"&appid=" + SteamTicketRules.AppId.ToString(CultureInfo.InvariantCulture) +
				"&ticket=" + ticketHex +
				"&identity=" + SteamTicketRules.Identity);
			request.Method = "GET";
			request.Timeout = RequestTimeoutMs;
			request.ReadWriteTimeout = RequestTimeoutMs;
			request.UserAgent = "Driftwood/" + Plugin.Version;

			using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
			using (Stream stream = response.GetResponseStream())
			using (MemoryStream buffer = new MemoryStream())
			{
				byte[] chunk = new byte[8 * 1024];
				while (buffer.Length < MaxResponseBytes)
				{
					int read = stream.Read(chunk, 0, chunk.Length);
					if (read <= 0) break;
					buffer.Write(chunk, 0, read);
				}
				return Encoding.UTF8.GetString(buffer.ToArray());
			}
		}
	}

	// Process-wide state for the ticket check: the verified-id list and the counts the readiness
	// file publishes. Initialised at boot with the host's state directory.
	internal static class IdentityTicketState
	{
		internal static VerifiedIds Ids = new VerifiedIds(null);
		internal static long Verified;
		internal static long Unverified;
		internal static long Refused;

		internal static void Initialise(string stateDirectory)
		{
			try
			{
				Ids = new VerifiedIds(string.IsNullOrEmpty(stateDirectory) ? null : Path.Combine(stateDirectory, "verified-steam-ids.txt"));
			}
			catch
			{
				Ids = new VerifiedIds(null);
			}
		}
	}
}
