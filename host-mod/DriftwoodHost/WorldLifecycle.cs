using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DriftwoodHost
{
	// PERSISTENCE, AND THE SILENT-FAILURE LANDMINE UNDER IT.
	//
	// SaveManager.SaveServer() opens with:
	//
	//     if (CurServerSave == null || !Server.Instance || !Server.Instance.IsServerInitialized) return;
	//
	// CurServerSave is null unless something selected or created a save, and the ONLY thing in the
	// shipped game that ever does is the main menu. So a headless server started the way the
	// feasibility probe started it writes no save, ever - Server.OnStopServer() -> SaveServer()
	// returns at the first line - AND starts a fresh world on every boot, because MoneyManager,
	// NPCManager, OnlineIslandManager, BoatManager and EndGameManager each read
	// SaveManager.CurServerSave during their own server init and fall back to zero/locked when it
	// is null. Nothing logs any of it. Playbook 1d mechanism 6, sitting on the product's
	// persistence.
	//
	// ORDER MATTERS. Steps 1-3 must complete BEFORE the transport server starts, because those
	// managers read CurServerSave during server init.
	internal static class WorldLifecycle
	{
		internal static string EffectiveSaveDirectory { get; private set; } = string.Empty;
		internal static bool SaveDirectoryRedirected { get; private set; }
		internal static string SelectedWorld { get; private set; } = string.Empty;
		internal static bool CreatedNewWorld { get; private set; }

		// The game hardcodes its save folder to Application.persistentDataPath + "/Saves/", which
		// on Windows is %USERPROFILE%\AppData\LocalLow\Dazed Games\<product> - PER USER, not per
		// instance. Every server on a box would share one folder and collide by world name, which
		// is data loss, not inconvenience. Unity's known-folder lookup ignores the environment, so
		// the Necesse trick (set APPDATA=) does not work here.
		//
		// Both fields are static readonly strings on a static class, initialised in one static
		// constructor, so rewriting them by reflection before first use is deterministic. BOTH
		// must be set: _fullLocalSavePath is computed from _saveFolder in that same constructor
		// and does not follow a later change to it.
		internal static string RedirectSaveFolder(string absoluteDirectory)
		{
			if (string.IsNullOrEmpty(absoluteDirectory)) return null;
			Type saveSystem = AccessTools.TypeByName("SaveSystem");
			if (saveSystem == null) return "The game no longer contains SaveSystem, so saves cannot be redirected to this server's own folder.";

			FieldInfo folder = AccessTools.Field(saveSystem, "_saveFolder");
			FieldInfo localPath = AccessTools.Field(saveSystem, "_fullLocalSavePath");
			if (folder == null || localPath == null)
			{
				return "The game's save-path fields have moved, so saves cannot be redirected to this server's own folder and would be shared with every other server on this machine.";
			}

			string normalised = absoluteDirectory.Replace('\\', '/').TrimEnd('/') + "/";
			Directory.CreateDirectory(normalised);
			folder.SetValue(null, normalised);
			localPath.SetValue(null, normalised + "local.txt");

			// Read back. Never trust the write (playbook 1d, silently-ignored config).
			string readBack = folder.GetValue(null) as string;
			if (!string.Equals(readBack, normalised, StringComparison.Ordinal))
			{
				return "The game's save folder did not accept this server's own save path, so saves would be shared with every other server on this machine.";
			}

			// SaveManager.Awake already ran SaveSystem.Init() against the old path; run it again
			// so the new folder exists before anything writes to it.
			AccessTools.Method(saveSystem, "Init")?.Invoke(null, null);

			EffectiveSaveDirectory = normalised;
			SaveDirectoryRedirected = true;
			return null;
		}

		// Reproduces what the main menu does, in the order the menu does it.
		internal static string LoadOrCreateWorld(string worldName)
		{
			Type saveManager = AccessTools.TypeByName("SaveManager");
			if (saveManager == null) return "The game no longer contains SaveManager, so this server cannot load or create a world.";

			MethodInfo loadAll = AccessTools.Method(saveManager, "LoadAllServers");
			MethodInfo select = AccessTools.Method(saveManager, "SelectServer");
			MethodInfo create = AccessTools.Method(saveManager, "CreateServer");
			MethodInfo onLoaded = AccessTools.Method(saveManager, "OnServerLoaded");
			PropertyInfo current = AccessTools.Property(saveManager, "CurServerSave");
			if (loadAll == null || select == null || create == null || onLoaded == null || current == null)
			{
				return "The game's save-management methods have moved, so this server cannot load or create a world.";
			}

			loadAll.Invoke(null, null);
			select.Invoke(null, new object[] { worldName });

			if (current.GetValue(null, null) == null)
			{
				// useSteam: false. The saved flag decides which lobby the MENU would open; a
				// Driftwood host always runs the direct-UDP path regardless, but writing the
				// honest value keeps the save loadable in the retail client.
				create.Invoke(null, new object[] { worldName, false });
				CreatedNewWorld = true;
			}

			onLoaded.Invoke(null, null);

			if (current.GetValue(null, null) == null)
			{
				return "This server could not load or create the world \"" + worldName + "\", so nothing it does would ever be saved.";
			}

			SelectedWorld = worldName;
			return null;
		}

		// The game's own AutoSaver already saves every _intervalInMinutes while the server is
		// initialised, and SaveManager.OnApplicationQuit() saves on a clean Unity exit. Both are
		// worth keeping rather than reimplementing; only the interval needs setting.
		internal static bool SetAutoSaveInterval(float minutes)
		{
			try
			{
			Type autoSaver = AccessTools.TypeByName("AutoSaver");
			if (autoSaver == null) return false;
			UnityEngine.Object instance = UnityEngine.Object.FindAnyObjectByType(autoSaver);
			if (instance == null) return false;
			FieldInfo interval = AccessTools.Field(autoSaver, "_intervalInMinutes");
			if (interval == null) return false;
			interval.SetValue(instance, Mathf.Clamp(minutes, 1f, 60f));
			return Mathf.Approximately((float)interval.GetValue(instance), Mathf.Clamp(minutes, 1f, 60f));
			}
			catch (Exception exception)
			{
				Plugin.Log?.LogWarning("Could not set the auto-save interval: " + exception.Message);
				return false;
			}
		}

		// ServerSettings' SyncVars default to friendly fire ON and one-shot OFF, and NEITHER is
		// written to the world save. Without re-applying them every boot a customer's choice
		// silently reverts on the next restart - and the panel would keep showing the old value.
		internal static bool ApplyServerSettings(bool friendlyFire, bool oneShot)
		{
			try
			{
			Type type = AccessTools.TypeByName("ServerSettings");
			if (type == null) return false;
			object instance = AccessTools.Field(type, "Instance")?.GetValue(null);
			if (instance == null) return false;

			MethodInfo toggleFriendly = AccessTools.Method(type, "ToggleFriendlyFire");
			toggleFriendly?.Invoke(instance, new object[] { friendlyFire });

			// ToggleOneShot has no parameter - it flips. Read the current value and only call it
			// when the value actually has to change.
			PropertyInfo oneShotProperty = AccessTools.Property(type, "OneShotEnabled");
			if (oneShotProperty != null && (bool)oneShotProperty.GetValue(null, null) != oneShot)
			{
				AccessTools.Method(type, "ToggleOneShot")?.Invoke(instance, null);
			}

			PropertyInfo friendlyProperty = AccessTools.Property(type, "UseFriendlyFire");
			bool friendlyOk = friendlyProperty == null || (bool)friendlyProperty.GetValue(null, null) == friendlyFire;
			bool oneShotOk = oneShotProperty == null || (bool)oneShotProperty.GetValue(null, null) == oneShot;
			return friendlyOk && oneShotOk;
			}
			catch (Exception exception)
			{
				Plugin.Log?.LogWarning("Could not apply the friendly-fire / one-shot settings: " + exception.Message);
				return false;
			}
		}

		// SaveManager.SaveServer dereferences NPCManager.Instance, BoatManager.Boat and
		// EndGameManager.Instance with no null checks, so it CAN throw - most likely on a stop
		// that arrives before the world has finished coming up. An exception escaping here would
		// kill the coroutine that calls Application.Quit, leaving the server hung until something
		// force-kills it, which is the worst possible outcome for a save routine.
		internal static bool SaveNow()
		{
			try
			{
				Type saveManager = AccessTools.TypeByName("SaveManager");
				MethodInfo save = saveManager == null ? null : AccessTools.Method(saveManager, "SaveServer");
				if (save == null)
				{
					Plugin.Log?.LogError("The game's save routine could not be found, so nothing was saved.");
					return false;
				}
				save.Invoke(null, new object[] { true });
				return true;
			}
			catch (Exception exception)
			{
				Exception inner = exception is TargetInvocationException invocation && invocation.InnerException != null
					? invocation.InnerException
					: exception;
				Plugin.Log?.LogError("The game's save routine threw (" + inner.GetType().Name + ": " + inner.Message +
					"). This usually means the world had not finished loading. Nothing was saved.");
				return false;
			}
		}
	}
}
