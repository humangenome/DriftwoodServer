using System;
using System.Collections.Generic;
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
		// Up to 1.0.11 the path fields are static readonly strings set by the type initialiser, so
		// rewriting them by reflection before first use is deterministic. ALL FOUR are derived
		// once and none follows a later change to another: set every one that exists. (0.1.7 set
		// only the first two, so the local save's .tmp/.backup still pointed at LocalLow.)
		//
		// GAME 1.0.12 CHANGED THE CONTRACT, and not setting it up right is data loss. Init() now
		// does three things, all of them wrong on a hosted server:
		//
		//   1. calls SteamUser.GetSteamID(). With no Steam client that throws - and 0.1.7 invoked
		//      Init from here, BEFORE the Steam guards are installed, so every 1.0.12 server
		//      refused to host;
		//   2. recomputes _saveFolder as persistentDataPath\Saves\<steamid>\ - the one folder
		//      every server on the box shares - OVERWRITING this redirect. SaveManager.Awake calls
		//      Init after the guards are in, so on 1.0.12 the redirect would silently undo itself;
		//   3. MOVES every *.txt and *.backup it finds in the save root into that subfolder, so
		//      even pointed at the right root it would carry a customer's world away from where
		//      the panel's backups, restores and snapshots look for it.
		//
		// So this method no longer calls Init at all, and InitPrefix (a Harmony prefix on
		// SaveSystem.Init) re-applies the redirect and skips the game's body whenever a save root
		// is configured. The on-disk layout stays flat - <SaveRoot>\<World>.txt - on every build.
		private static readonly string[] SavePathFields =
		{
			"_saveFolder", "_fullLocalSavePath", "_localBackupPath", "_localTempPath"
		};

		private static Type _saveSystem;

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
			string failure = ApplySavePaths(saveSystem, normalised);
			if (failure != null) return failure;

			_saveSystem = saveSystem;
			EffectiveSaveDirectory = normalised;
			SaveDirectoryRedirected = true;
			return null;
		}

		// Writes every path field this build has, then reads the two that matter back. Never
		// trust the write (playbook 1d, silently-ignored config).
		private static string ApplySavePaths(Type saveSystem, string normalisedFolder)
		{
			string local = normalisedFolder + "local.txt";
			foreach (string name in SavePathFields)
			{
				// Plain reflection, not AccessTools.Field: the last two fields do not exist before
				// 1.0.11, and AccessTools logs a warning for every miss, which reads like a fault.
				FieldInfo field = saveSystem.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (field == null) continue;
				string value;
				switch (name)
				{
					case "_saveFolder": value = normalisedFolder; break;
					case "_fullLocalSavePath": value = local; break;
					case "_localBackupPath": value = local + ".backup"; break;
					default: value = local + ".tmp"; break;
				}
				field.SetValue(null, value);
			}

			string readBack = AccessTools.Field(saveSystem, "_saveFolder")?.GetValue(null) as string;
			string localBack = AccessTools.Field(saveSystem, "_fullLocalSavePath")?.GetValue(null) as string;
			if (!string.Equals(readBack, normalisedFolder, StringComparison.Ordinal) ||
				!string.Equals(localBack, local, StringComparison.Ordinal))
			{
				return "The game's save folder did not accept this server's own save path, so saves would be shared with every other server on this machine.";
			}
			return null;
		}

		// Harmony prefix on SaveSystem.Init. With a save root configured, the game's own Init
		// never runs: it would ask Steam who we are, point the save folder back at the shared
		// per-user directory and move the world into a per-account subfolder (see above). The
		// redirect is re-applied instead, so an Init from anywhere - SaveManager.Awake included -
		// leaves this server's saves exactly where the panel expects them.
		private static bool InitPrefix()
		{
			if (!SaveDirectoryRedirected || _saveSystem == null) return true;
			try
			{
				Directory.CreateDirectory(EffectiveSaveDirectory);
				string failure = ApplySavePaths(_saveSystem, EffectiveSaveDirectory);
				if (failure != null) Plugin.Log?.LogError("SaveSystem.Init: " + failure);
			}
			catch (Exception exception)
			{
				// Never let this escape into SaveManager.Awake. VerifySaveFolder, run before the
				// world loads, is what turns a redirect that did not hold into a refusal.
				Plugin.Log?.LogError("SaveSystem.Init: the save redirect could not be re-applied (" +
					exception.GetType().Name + ": " + exception.Message + ").");
			}
			return false;
		}

		internal static IEnumerable<PatchTarget> Targets()
		{
			yield return new PatchTarget
			{
				TypeName = "SaveSystem",
				MethodName = "Init",
				Kind = PatchKind.Custom,
				// Required exactly when a save root is configured: then the game's Init would undo
				// the redirect (1.0.12+). A self-hoster with no save root keeps the game's own Init.
				Necessity = SaveDirectoryRedirected ? PatchNecessity.Required : PatchNecessity.Optional,
				Prefix = AccessTools.Method(typeof(WorldLifecycle), nameof(InitPrefix)),
				Why = "Game 1.0.12 made SaveSystem.Init ask Steam for the account id, repoint the save folder at the per-user directory every server on the box shares, and move world files into a per-account subfolder."
			};
		}

		// The last word before a world is loaded or created: the game's save folder must still be
		// this server's own. Anything the game ran between the redirect and now (SaveManager.Awake
		// on 1.0.12 is the known one) had its chance to move it. Returns null when it held.
		internal static string VerifySaveFolder()
		{
			if (!SaveDirectoryRedirected || _saveSystem == null) return null;
			string current = AccessTools.Field(_saveSystem, "_saveFolder")?.GetValue(null) as string;
			if (!string.Equals(current, EffectiveSaveDirectory, StringComparison.Ordinal))
			{
				return "The game moved its save folder away from this server's own (" + (current ?? "unset") +
					"), so saves would be shared with every other server on this machine.";
			}
			return null;
		}

		// Parameters the game added to a method we call that we did not know about, named so the
		// drift is visible in the readiness document instead of only in a log line nobody reads.
		internal static readonly List<string> GameApiDrift = new List<string>();

		// THE ARGUMENT LIST IS BUILT FROM THE METHOD THE GAME ACTUALLY SHIPPED, never hard-coded.
		//
		// This is not defensive decoration. Retail 1.0.5 added a third parameter to
		// SaveManager.CreateServer (string serverName, bool useSteam, BOOL ISPUBLIC), and the
		// two-argument Invoke this file used to carry threw TargetParameterCountException inside
		// the boot coroutine. Nothing caught it, so the coroutine died and the host was left in
		// the worst state it has: process alive, HTTP listener answering, gameplay socket never
		// bound, world never loaded, phase stuck on "Loading the world" forever. That is what the
		// first ever paying customer got, because the host installs Steam latest and every proof
		// this product had was taken on 1.0.4.
		//
		// A parameter we know is passed by NAME. A parameter we do not know takes its own default
		// if it has one, and otherwise the zero value for its type - and is recorded, so a build
		// that changes behaviour behind a new argument shows up as drift rather than as a mystery.
		private static object[] BuildArguments(MethodInfo method, Dictionary<string, object> known)
		{
			ParameterInfo[] parameters = method.GetParameters();
			if (parameters.Length == 0) return null;

			object[] arguments = new object[parameters.Length];
			for (int i = 0; i < parameters.Length; i++)
			{
				ParameterInfo parameter = parameters[i];
				object value;
				if (known.TryGetValue(parameter.Name, out value))
				{
					arguments[i] = value;
					continue;
				}
				if (parameter.IsOptional && parameter.DefaultValue != DBNull.Value)
				{
					arguments[i] = parameter.DefaultValue;
					continue;
				}
				arguments[i] = parameter.ParameterType.IsValueType
					? Activator.CreateInstance(parameter.ParameterType)
					: null;
				string note = method.DeclaringType?.Name + "." + method.Name + "(" +
					parameter.ParameterType.Name + " " + parameter.Name + ")";
				if (!GameApiDrift.Contains(note)) GameApiDrift.Add(note);
				Plugin.Log?.LogWarning("The game added a parameter this host does not know about: " + note +
					". It is being passed its zero value. Check the game's release notes before trusting it.");
			}
			return arguments;
		}

		// Reproduces what the main menu does, in the order the menu does it.
		//
		// NOTHING HERE MAY THROW. This runs inside the boot coroutine; an escaping exception kills
		// the coroutine and hangs the host instead of refusing it, which is strictly worse than
		// any failure it could report. Every exit is a message the supervisor can act on.
		internal static string LoadOrCreateWorld(string worldName)
		{
			try
			{
				return LoadOrCreateWorldCore(worldName);
			}
			catch (Exception exception)
			{
				Exception inner = exception is TargetInvocationException invocation && invocation.InnerException != null
					? invocation.InnerException
					: exception;
				return "This server could not load or create the world \"" + worldName + "\" (" +
					inner.GetType().Name + ": " + inner.Message + "). The game's save management has " +
					"probably changed in a build this host has not been updated for.";
			}
		}

		private static string LoadOrCreateWorldCore(string worldName)
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

			loadAll.Invoke(null, BuildArguments(loadAll, new Dictionary<string, object>()));
			select.Invoke(null, BuildArguments(select, new Dictionary<string, object> { { "name", worldName } }));

			if (current.GetValue(null, null) == null)
			{
				// useSteam: false. The saved flag decides which lobby the MENU would open; a
				// Driftwood host always runs the direct-UDP path regardless, but writing the
				// honest value keeps the save loadable in the retail client.
				//
				// isPublic (added in 1.0.5) is only ever read as "UseSteam && IsPublic", so with
				// useSteam false it is inert either way; false is the honest value.
				//
				// difficulty (added in 1.0.9) is enum Difficulty { Easy, Default, Hard }. Unknown to
				// 0.1.7, it was passed its zero value - EASY - so every world this host created on
				// 1.0.9+ started on Easy (creatures 25% less health, half the damage) instead of the
				// game's own default. Pass the game's "Default" member by NAME, so a reordered enum
				// cannot move it. Worlds already created keep the difficulty saved in them.
				Dictionary<string, object> createArguments = new Dictionary<string, object>
				{
					{ "serverName", worldName },
					{ "useSteam", false },
					{ "isPublic", false }
				};
				foreach (ParameterInfo parameter in create.GetParameters())
				{
					if (parameter.Name != "difficulty" || !parameter.ParameterType.IsEnum) continue;
					if (Enum.IsDefined(parameter.ParameterType, "Default"))
					{
						createArguments["difficulty"] = Enum.Parse(parameter.ParameterType, "Default");
					}
				}
				create.Invoke(null, BuildArguments(create, createArguments));
				CreatedNewWorld = true;
			}

			onLoaded.Invoke(null, BuildArguments(onLoaded, new Dictionary<string, object>()));

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
