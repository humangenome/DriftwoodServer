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
	// Note this is silence, not saving. Playbook 2b: a muted audio engine still decodes the music
	// stream and still mixes every ambient loop into a buffer that is thrown away - "muted is not
	// off", and it cost Lodestone ~5.2% of a core. Refusing to START audio events is a separate,
	// larger win and is tracked as its own measurement.
	internal class Silence : MonoBehaviour
	{
		private static readonly string[] VolumeKeys = { "Master", "FX", "Music", "Proxy", "Gain" };

		internal static void Install()
		{
			foreach (string key in VolumeKeys) PlayerPrefs.SetFloat(key, 0f);
			PlayerPrefs.Save();
			AudioListener.pause = true;
			AudioListener.volume = 0f;

			GameObject go = new GameObject("Driftwood.Silence");
			Object.DontDestroyOnLoad(go);
			go.hideFlags = HideFlags.HideAndDontSave;
			go.AddComponent<Silence>();
		}

		private void Update()
		{
			if (AudioListener.volume != 0f) AudioListener.volume = 0f;
			if (!AudioListener.pause) AudioListener.pause = true;
		}
	}
}
