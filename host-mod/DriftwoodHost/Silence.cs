using UnityEngine;

namespace DriftwoodHost
{
	// -batchmode does NOT guarantee silence. On a box with no audio device FMOD falls back to
	// "nosound output"; on a real workstation it finds a device and PLAYS. That leaked game audio
	// onto the operator's desktop during a probe run on 2026-08-21. Never rely on -batchmode.
	//
	// Installed before anything else, and it holds the listener at zero every frame because the
	// game restores volume from PlayerPrefs when its AudioManager initialises.
	//
	// >>> NOTHING HERE MAY WRITE PlayerPrefs, AND THIS FILE USED TO.
	//
	// Unity keys PlayerPrefs per (company, product) under HKCU - per WINDOWS USER, not per install
	// directory - so a write here lands on every copy of the game that Windows account can run,
	// including a retail client. The client mod bans this in so many words
	// (DriftwoodConnect/Plugin.cs) because a test run that zeroed the volume prefs rewrote a real
	// person's audio settings, and the canary flow runs this host and a retail client under one
	// account. Setting five volume keys to zero here was the identical incident waiting to happen,
	// on the machine of whoever ran a host next to their own game.
	//
	// It was also redundant: the every-frame AudioListener clamp below already guarantees silence
	// in this process, and it is the only part that CANNOT leak, because it touches nothing that
	// outlives the process. Runtime state, never a stored preference.
	//
	// Note this is silence, not saving. Playbook 2b: a muted audio engine still decodes the music
	// stream and still mixes every ambient loop into a buffer that is thrown away - "muted is not
	// off", and it cost Lodestone ~5.2% of a core. Refusing to START audio events is a separate,
	// larger win and is tracked as its own measurement.
	internal class Silence : MonoBehaviour
	{
		private static Silence _instance;

		internal static bool Installed => _instance != null;

		internal static void Install()
		{
			if (_instance != null) return;
			AudioListener.pause = true;
			AudioListener.volume = 0f;

			GameObject go = new GameObject("Driftwood.Silence");
			Object.DontDestroyOnLoad(go);
			go.hideFlags = HideFlags.HideAndDontSave;
			_instance = go.AddComponent<Silence>();
		}

		// The other half of MuteAudio. Silence is installed unconditionally at Boot, BEFORE the
		// config is read, because the window between process start and config load is exactly when
		// FMOD finds a device and starts playing - a config-gated install would be a config-gated
		// leak. So the key is honoured by RELEASING afterwards rather than by not installing.
		//
		// Without this, MuteAudio was a key the mod read and never consumed: a support person
		// could set it to false, see it accepted, see it echoed back as recognised, and hear
		// nothing change. That is the silently-ignored-config shape this product exists to refuse.
		// The bench rig LINKS this file and has no Plugin type, so the warning goes through a
		// delegate the host mod sets at boot rather than a direct Plugin.Log call. A direct
		// reference here compiled in the host mod and broke the bench build in silence.
		internal static System.Action<string> LogWarning = null;

		internal static void Release()
		{
			if (_instance == null) return;
			Object.Destroy(_instance.gameObject);
			_instance = null;
			AudioListener.pause = false;
			AudioListener.volume = 1f;
			LogWarning?.Invoke("MuteAudio is false, so the audio engine has been released. On a host with a real audio device this server will make NOISE on the machine it runs on.");
		}

		private void Update()
		{
			if (AudioListener.volume != 0f) AudioListener.volume = 0f;
			if (!AudioListener.pause) AudioListener.pause = true;
		}
	}
}
