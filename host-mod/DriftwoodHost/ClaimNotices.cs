using System;
using System.Collections.Generic;
using FishNet.Connection;

namespace DriftwoodHost
{
	// Tells a refused player why they are on a temporary character. A claim runs before the player
	// spawns, so a line sent then is lost: it waits here until the player is in the world, plus a
	// few seconds for the chat to be up. The line goes to the whole crew (the game has no private
	// server chat), so at most one per connection and one per ten seconds across the server, and
	// only a caller that proved its game connection can queue one. MAIN THREAD throughout.
	internal static class ClaimNotices
	{
		private const double SettleSeconds = 5;
		private const double GapSeconds = 10;
		private const double GiveUpSeconds = 120;
		private const int MaxWaiting = 16;

		private sealed class Waiting
		{
			internal NetworkConnection Connection;
			internal ClaimNotice Notice;
			internal DateTime QueuedAt;
			internal DateTime SpawnedAt;
		}

		private static readonly List<Waiting> Pending = new List<Waiting>();
		private static readonly HashSet<NetworkConnection> Told = new HashSet<NetworkConnection>();
		private static DateTime _lastSent = DateTime.MinValue;

		internal static void Queue(NetworkConnection connection, ClaimNotice notice)
		{
			if (connection == null || notice == ClaimNotice.None) return;
			if (Told.Contains(connection) || Pending.Count >= MaxWaiting) return;
			foreach (Waiting waiting in Pending) if (ReferenceEquals(waiting.Connection, connection)) return;
			Pending.Add(new Waiting { Connection = connection, Notice = notice, QueuedAt = DateTime.UtcNow, SpawnedAt = DateTime.MinValue });
		}

		// From the readiness sampler, every ~2 seconds.
		internal static void Service()
		{
			if (Told.Count > 0) Told.RemoveWhere(c => c == null || !c.IsActive);
			if (Pending.Count == 0) return;
			DateTime now = DateTime.UtcNow;
			List<OwnerActions.Found> aboard = OwnerActions.ConnectedPlayers();
			for (int i = Pending.Count - 1; i >= 0; i--)
			{
				Waiting waiting = Pending[i];
				if (!waiting.Connection.IsActive || (now - waiting.QueuedAt).TotalSeconds > GiveUpSeconds)
				{
					Pending.RemoveAt(i);
					continue;
				}
				OwnerActions.Found player = null;
				foreach (OwnerActions.Found candidate in aboard)
				{
					if (candidate.Player != null && ReferenceEquals(candidate.Player.Owner, waiting.Connection)) { player = candidate; break; }
				}
				if (player == null) continue;
				if (waiting.SpawnedAt == DateTime.MinValue) waiting.SpawnedAt = now;
				if ((now - waiting.SpawnedAt).TotalSeconds < SettleSeconds || (now - _lastSent).TotalSeconds < GapSeconds) continue;

				string failure = OwnerActions.Broadcast(SteamTicketRules.NoticeText(waiting.Notice, player.Name));
				if (failure != null) Plugin.Log?.LogDebug("Claim notice not sent: " + failure);
				_lastSent = now;
				Told.Add(waiting.Connection);
				Pending.RemoveAt(i);
			}
		}
	}
}
