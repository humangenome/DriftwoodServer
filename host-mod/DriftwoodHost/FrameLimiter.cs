using System.Diagnostics;
using System.Threading;
using FishNet;
using UnityEngine;

namespace DriftwoodHost
{
	// A REAL frame limiter, because Application.targetFrameRate is a request the batch-mode player
	// ignores.
	//
	// Measured on this game: with targetFrameRate set to 30, vSync off, and the engine reading the
	// value back as 30, the server actually ran at 440 fps. That is why the capped and uncapped
	// runs were indistinguishable - both were uncapped. Playbook 2b warns about exactly this shape
	// ("the engine's frame-rate target is silently discarded... anyone who changes one line will
	// measure no improvement and conclude the whole finding was wrong"); the mechanism here is
	// batch mode rather than vSync, and the symptom is identical.
	//
	// So the frame is padded by hand. LateUpdate runs last in the frame, so sleeping there sleeps
	// the whole loop.
	//
	// What this can and cannot reach, from the decompile: it reduces the Update / LateUpdate bucket
	// only. FixedUpdate and the netcode tick are wall-clock driven - Unity will simply run more
	// FixedUpdates per frame to catch up - so this cannot touch the physics cost. Expect a partial
	// saving and measure it rather than assuming the sibling's number transfers.
	internal sealed class FrameLimiter : MonoBehaviour
	{
		private static FrameLimiter _instance;
		private readonly Stopwatch _clock = Stopwatch.StartNew();
		private double _nextFrameMs;
		private int _targetFrameRate;
		private int _idleFrameRate;
		private bool _idling;
		private double _frozenWithPlayerSince;

		// Occupancy as the READINESS SAMPLER sees it, which is the only place that knows the
		// difference between a transport connection and a player. Pushed rather than pulled so the
		// limiter, which runs every frame, never touches a game type.
		private static int _observedPlayers = -1;
		private static bool _observedHostClient;
		private static double _observedAt;

		internal static double MeasuredSleepMs { get; private set; }
		// True while the loop is running at the reduced empty-server rate.
		internal static bool Idling { get; private set; }
		// Proof the limiter is installed at all. A configured cap with this false means the server
		// is UNCAPPED, whatever the engine reports.
		internal static bool Active => _instance != null;
		internal static int IdleTransitions { get; private set; }

		internal static void SetIdleFrameRate(int idleFrameRate)
		{
			if (_instance != null) _instance._idleFrameRate = idleFrameRate;
		}

		// Called from the readiness sampler every couple of seconds.
		//
		// `realPlayers` already excludes the host's own loopback connection, and `hostClientPresent`
		// is the thing the old transport-count heuristic silently assumed: it decided "empty" as
		// `transportClients <= 1`, which is only correct while the loopback client is one of those
		// connections. If it ever dropped while a remote player stayed, one real player counted as
		// one connection, the server read EMPTY, and the loop dropped to 5 fps with somebody on it.
		internal static void ObserveOccupancy(int realPlayers, bool hostClientPresent)
		{
			_observedPlayers = realPlayers;
			_observedHostClient = hostClientPresent;
			_observedAt = Time.realtimeSinceStartupAsDouble;
		}

		internal static void Apply(int targetFrameRate)
		{
			if (_instance == null)
			{
				GameObject go = new GameObject("Driftwood.FrameLimiter");
				Object.DontDestroyOnLoad(go);
				go.hideFlags = HideFlags.HideAndDontSave;
				_instance = go.AddComponent<FrameLimiter>();
			}
			_instance._targetFrameRate = targetFrameRate;
			_instance._nextFrameMs = _instance._clock.Elapsed.TotalMilliseconds;
			Plugin.Log?.LogInfo(targetFrameRate > 0
				? "Frame limiter active at " + targetFrameRate + " fps. Application.targetFrameRate is ignored in batch mode, so the frame is padded by hand."
				: "Frame limiter off; the server runs its loop as fast as it can.");
		}

		// THE EMPTY-SERVER RATE. Measured: cutting physics steps 3.3x and the netcode tick 2.5x
		// together buy 4 points out of 49, so ~90% of an idle server's cost is the FRAME LOOP - and
		// the loop runs unbounded at 440-497 fps because Unity ignores targetFrameRate in batch
		// mode. Freezing the simulation clock therefore cannot deliver the "empty server costs
		// approximately nothing" shape; slowing the LOOP can.
		//
		// The netcode ticks inside this same loop, so the rate cannot go to zero - a join has to
		// still be processed. At 5 fps the server still services connections five times a second
		// and returns to full rate within one frame of anybody arriving.
		private int CurrentTarget()
		{
			if (_idleFrameRate <= 0) return _targetFrameRate;

			bool empty;
			// The sampler's answer, while it is fresh. Its interval is 2 s; 10 s of tolerance
			// covers a slow frame or a world load without letting a DEAD sampler pin the decision
			// to a stale reading forever.
			if (_observedPlayers >= 0 && Time.realtimeSinceStartupAsDouble - _observedAt < 10.0)
			{
				// If the host's own loopback client is gone, the server is in a state nobody
				// designed for. Idling is the wrong guess there: run at full rate and let the rest
				// of the machinery notice. Costing a core is recoverable; a player on a 5 fps
				// server is what a customer feels.
				empty = _observedHostClient && _observedPlayers <= 0;
			}
			else
			{
				int transportClients;
				try { transportClients = InstanceFinder.ServerManager?.Clients?.Count ?? 0; }
				catch { return _targetFrameRate; }
				// Fallback only, and deliberately CONSERVATIVE: with no sampler to say which
				// connection is the host's, treat anything at all as occupied rather than reading
				// one remote player as an empty server.
				empty = transportClients <= 0;
			}

			if (empty != _idling)
			{
				_idling = empty;
				Idling = empty;
				IdleTransitions++;
				Plugin.Log?.LogInfo(empty
					? "Nobody is connected: dropping the server loop to " + _idleFrameRate + " fps."
					: "A player arrived: restoring the server loop to " + (_targetFrameRate > 0 ? _targetFrameRate + " fps" : "full rate") + ".");
			}
			return empty ? _idleFrameRate : _targetFrameRate;
		}

		private void LateUpdate()
		{
			// SAFETY NET for the empty-world freeze. That freeze is applied and released from the
			// readiness sampler, which lives in a coroutine - and a coroutine that dies leaves the
			// world stopped with the port still open and the last status file still saying
			// "Hosting". That is a fail-OPEN: a server that looks up and is frozen solid.
			//
			// LateUpdate runs every frame regardless of timeScale, so this is the one place that
			// cannot stop running. If the clock is stopped while a real client is connected, put it
			// back - whatever the reason.
			if (Time.timeScale == 0f)
			{
				int connected;
				try { connected = InstanceFinder.ServerManager?.Clients?.Count ?? 0; } catch { connected = 0; }
				if (connected > 1)
				{
					// Anything connected beyond the host also means we must not be idling. The
					// watchdog used to restore timeScale only, so a server whose sampler had died
					// could come back to a running clock at 5 fps.
					if (_idling)
					{
						_idling = false;
						Idling = false;
						_observedPlayers = -1;
						Plugin.Log?.LogWarning("The loop was idling with a client connected. Restored the full rate.");
					}
					// This runs every frame; the readiness sampler that normally releases the pause
					// runs every two seconds. So on a NORMAL join this fires first, and saying
					// "something failed" every single time would train everyone to ignore the one
					// case that matters. Resume immediately either way, but only WARN once the
					// clock has been stopped with a player on it for longer than the sampler's own
					// interval - which is the only version of this that means something is wrong.
					if (_frozenWithPlayerSince <= 0.0) _frozenWithPlayerSince = Time.realtimeSinceStartupAsDouble;
					bool overdue = Time.realtimeSinceStartupAsDouble - _frozenWithPlayerSince > 3.0;
					Time.timeScale = 1f;
					EmptyWorldPause.ForceResume();
					if (overdue)
					{
						Plugin.Log?.LogWarning("The world clock stayed stopped for over three seconds with a player connected. Restored it. Something that should have resumed the world did not.");
					}
				}
				else
				{
					_frozenWithPlayerSince = 0.0;
				}
			}
			else
			{
				_frozenWithPlayerSince = 0.0;
			}

			int target = CurrentTarget();
			if (target <= 0) return;
			double period = 1000.0 / target;
			double now = _clock.Elapsed.TotalMilliseconds;
			_nextFrameMs += period;
			// If we fell behind - a long frame, a world load, a GC pause - do not try to catch up by
			// running hot. Resynchronise to now, so a hiccup cannot turn into a burst.
			if (_nextFrameMs < now)
			{
				_nextFrameMs = now;
				MeasuredSleepMs = 0;
				return;
			}
			double sleepMs = _nextFrameMs - now;
			MeasuredSleepMs = sleepMs;
			// Sleep in whole milliseconds and leave the remainder to the next frame. Spinning the
			// last fraction of a millisecond would give back exactly the CPU this exists to save.
			int wholeMs = (int)sleepMs;
			if (wholeMs > 0) Thread.Sleep(wholeMs);
		}
	}
}
