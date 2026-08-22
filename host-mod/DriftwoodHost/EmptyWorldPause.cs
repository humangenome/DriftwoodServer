using UnityEngine;

namespace DriftwoodHost
{
	// LEVER 3, the structural one - playbook 2b's "an empty server should cost approximately
	// nothing", the Icarus shape.
	//
	// Measured on this game: an idle server with nobody connected still costs ~49% of a core,
	// because the island is a live physics world. Every fish swims, every bird flies, every
	// floating item bobs and RigidbodySync replicates them, at 50 Hz, forever, for an audience of
	// nobody. Empty is the state most servers are in most of the time, so that is where the money
	// is.
	//
	// The lever is one line, and it is available because of an accident of the netcode:
	//
	//     FishNet's TimeManager drives its tick from Time.unscaledDeltaTime (verified in the
	//     decompiled TimeManager: `double num2 = Time.unscaledDeltaTime;`).
	//
	// So Time.timeScale = 0 stops FixedUpdate and the PhysX step - the largest cost bucket - while
	// the network layer keeps ticking, keeps accepting connections and keeps processing packets.
	// The world stands still; the server stays reachable.
	//
	// DEFAULTED OFF. Playbook 2b is explicit that standing something down is only safe if you prove
	// it comes back, and the proof is a real client joining a paused server, spawning, and moving.
	// Until that proof exists this stays off, and it is a switch rather than a rewrite so support
	// can turn it off without a rebuild.
	//
	// Known consequences, none of which are silent:
	//   - Coroutines on WaitForSeconds stall, including the game's AutoSaver. An empty world has
	//     nothing to save, and the world resumes before anyone can change it.
	//   - Time.time stops advancing, so SaveManager's playtime does not accrue while empty. That is
	//     arguably more correct than the alternative, but it IS a behaviour change.
	//   - Update() still runs with a zero delta, so this does not reach the Update bucket - only
	//     FixedUpdate and physics. Expect a partial saving, not a total one.
	internal static class EmptyWorldPause
	{
		internal static bool Enabled;
		internal static bool Paused { get; private set; }
		internal static int ResumeCount { get; private set; }

		// Called from the readiness sampler once the world is up. `realPlayers` excludes the host's
		// own loopback connection.
		internal static void Update(bool worldRunning, int realPlayers)
		{
			if (!Enabled)
			{
				if (Paused) Resume();
				return;
			}
			// Never pause before the world is up: the island load itself needs a running clock.
			if (!worldRunning)
			{
				if (Paused) Resume();
				return;
			}
			if (realPlayers > 0)
			{
				if (Paused) Resume();
				return;
			}
			if (!Paused) Pause();
		}

		private static void Pause()
		{
			Time.timeScale = 0f;
			Paused = true;
			Plugin.Log?.LogInfo("World paused: nobody is connected. The network layer keeps running, so this server is still joinable.");
		}

		private static void Resume()
		{
			Time.timeScale = 1f;
			Paused = false;
			ResumeCount++;
			Plugin.Log?.LogInfo("World resumed (" + ResumeCount + " time(s) so far).");
		}

		// Belt and braces for the one failure that would matter: leaving a server frozen. Anything
		// that stops the plugin restores the clock.
		internal static void ForceResume()
		{
			if (Time.timeScale != 1f) Time.timeScale = 1f;
			Paused = false;
		}
	}
}
