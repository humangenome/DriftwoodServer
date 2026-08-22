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
			int transportClients;
			try { transportClients = InstanceFinder.ServerManager?.Clients?.Count ?? 0; }
			catch { return _targetFrameRate; }
			// The host's own loopback connection is not a player, so "empty" is one connection.
			bool empty = transportClients <= 1;
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
					Time.timeScale = 1f;
					EmptyWorldPause.ForceResume();
					Plugin.Log?.LogWarning("The world clock was stopped while a player was connected. Restored it. Something that should have resumed the world did not.");
				}
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
