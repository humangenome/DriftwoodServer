using System.Diagnostics;
using System.Threading;
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

		internal static double MeasuredSleepMs { get; private set; }

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

		private void LateUpdate()
		{
			if (_targetFrameRate <= 0) return;
			double period = 1000.0 / _targetFrameRate;
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
