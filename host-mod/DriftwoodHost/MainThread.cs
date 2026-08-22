using System;
using System.Collections.Generic;
using System.Threading;

namespace DriftwoodHost
{
	// Run a piece of work on Unity's main thread and wait for it, with a bound.
	//
	// WHY THIS EXISTS: the HTTP API answers on its own threads, and almost everything worth
	// asking it about is a Unity object. Touching one off the main thread is either an
	// immediate "can only be called from the main thread" exception or, worse, an
	// intermittent one - which is how a save that works on a quiet box fails on a busy one.
	//
	// The save route did exactly this before: HostHttpApi called WorldLifecycle.SaveNow()
	// straight off the listener thread. It is the one call in the product whose silent
	// failure loses a customer's world, so it is the first thing routed through here.
	//
	// EVERY wait is bounded. A main thread that is not pumping is precisely the state a
	// wedged server is in, and an HTTP handler that blocks forever on it turns one stuck
	// world into a listener with no free threads - the diagnostic dies with the patient.
	internal static class MainThread
	{
		private sealed class WorkItem
		{
			internal Action Work;
			internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
			internal Exception Error;
		}

		private static readonly Queue<WorkItem> Pending = new Queue<WorkItem>();
		private static readonly object Sync = new object();

		// Set once Pump() has run at least once. Until then there is no main thread to
		// dispatch to and every caller must be told so rather than waiting out its timeout.
		private static volatile bool _pumping;

		internal static bool Pumping => _pumping;

		// Drained from Plugin.Update(). Bounded per frame so a flood of API calls can never
		// starve the frame loop - which at IdleFrameRate is only five frames a second.
		internal static void Pump()
		{
			_pumping = true;
			for (int drained = 0; drained < 8; drained++)
			{
				WorkItem item;
				lock (Sync)
				{
					if (Pending.Count == 0) return;
					item = Pending.Dequeue();
				}
				try { item.Work(); }
				catch (Exception exception) { item.Error = exception; }
				finally { item.Done.Set(); }
			}
		}

		// Returns true when the work ran to completion on the main thread. False means it
		// never ran, or threw - and the caller must report that as a failure rather than as
		// a success with no effect.
		internal static bool Run(Action work, int timeoutMilliseconds, out string failure)
		{
			failure = null;
			if (work == null) { failure = "nothing to run"; return false; }
			if (!_pumping)
			{
				failure = "this server is not running its frame loop yet";
				return false;
			}

			WorkItem item = new WorkItem { Work = work };
			lock (Sync)
			{
				// A queue that is already this deep means the main thread is not draining it,
				// and adding to it would only make the next caller wait longer for the same
				// answer. Refuse now, with a reason.
				if (Pending.Count >= 32)
				{
					failure = "this server is busy and could not take another request";
					return false;
				}
				Pending.Enqueue(item);
			}

			if (!item.Done.Wait(Math.Max(100, timeoutMilliseconds)))
			{
				failure = "this server did not respond in time";
				return false;
			}
			if (item.Error != null)
			{
				failure = item.Error.GetType().Name + ": " + item.Error.Message;
				return false;
			}
			return true;
		}

		// Convenience for a bool-returning game call. The out-parameter distinguishes
		// "ran and answered false" from "never ran", which are different problems.
		internal static bool Run(Func<bool> work, int timeoutMilliseconds, out string failure)
		{
			bool result = false;
			Action wrapped = () => { result = work(); };
			if (!Run(wrapped, timeoutMilliseconds, out failure)) return false;
			if (!result) failure = "the game refused the request";
			return result;
		}
	}
}
