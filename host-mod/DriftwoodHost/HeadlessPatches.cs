using System.Collections.Generic;

namespace DriftwoodHost
{
	// Headless, the game throws EVERY FRAME from rendering and animation code that assumes a
	// camera and a GPU. That is not cosmetic: an exception escaping a per-frame method aborts
	// that whole update loop, which silently stops parts of the simulation.
	//
	// Measured on the feasibility rig over one 60-second two-instance run:
	//   server log 258,427 lines / 36,910 exception lines  ->  49 lines / 0
	//   client log 690,660 lines / 179,047 exception lines ->  45 lines / 0
	//
	// SKIP vs SWALLOW is a real distinction and getting it backwards breaks the game quietly:
	//   skip    - the method's whole job is drawing. A server never needs it to run.
	//   swallow - the method drives simulation and MUST keep ticking. Only its escape is stopped,
	//             and every swallow is counted and rate-alarmed (see SwallowCounter).
	internal static class HeadlessPatches
	{
		public static IEnumerable<PatchTarget> Targets()
		{
			// Graphics.DrawMeshInstanced -> "Instancing is not supported" under the null device.
			// 110k+ hits in a one-minute window; by far the largest single source.
			yield return new PatchTarget
			{
				TypeName = "InstanceManager",
				MethodName = "RenderBatches",
				Kind = PatchKind.Skip,
				Necessity = PatchNecessity.Required,
				Why = "Instanced rendering under a null graphics device. 110k+ exceptions a minute."
			};

			// First-person hand posing needs a camera. 25,583 NREs in the same window.
			yield return new PatchTarget
			{
				TypeName = "PlayerHands",
				MethodName = "LateUpdate",
				Kind = PatchKind.Skip,
				Necessity = PatchNecessity.Required,
				Why = "First-person hand posing dereferences the camera."
			};

			// NPC.GetEyeDir dereferences PlayerManager.GetNearestAlivePlayer, which is null with
			// nobody nearby - and on a Driftwood host with the ghost player suppressed, that is
			// the NORMAL state of an empty server. NPCs must keep ticking, so this swallows.
			yield return new PatchTarget
			{
				TypeName = "NPC",
				MethodName = "Update",
				Kind = PatchKind.Swallow,
				Necessity = PatchNecessity.Required,
				Why = "NPC.GetEyeDir dereferences the nearest alive player, null on an empty server. NPCs must keep ticking."
			};

			yield return new PatchTarget
			{
				TypeName = "Player",
				MethodName = "LateUpdate",
				Kind = PatchKind.Swallow,
				Necessity = PatchNecessity.Required,
				Why = "Camera and transform nulls on a headless host. The player loop must keep running."
			};

			// Achievements fire from ORDINARY GAMEPLAY, not just boot. On the feasibility run a
			// seagull stole an item, which reached UnlockAchievement -> SteamUserStats and threw,
			// and took BirdManager.Update with it - bird simulation was dead for the rest of the
			// run with nothing in the log. Skipping the whole method is cheaper than letting it
			// run against stubbed SDK calls 26 gameplay entry points deep.
			yield return new PatchTarget
			{
				TypeName = "AchievementManager",
				MethodName = "UnlockAchievement",
				Kind = PatchKind.Skip,
				Necessity = PatchNecessity.Required,
				Why = "26 gameplay entry points funnel here; an unguarded throw killed bird simulation for a whole run."
			};

			yield return new PatchTarget
			{
				TypeName = "AchievementManager",
				MethodName = "HasAchievement",
				Kind = PatchKind.SkipReturningFalse,
				Necessity = PatchNecessity.Required,
				Why = "Same class as UnlockAchievement. A server has no achievements to hold."
			};

			yield return new PatchTarget
			{
				TypeName = "AchievementManager",
				MethodName = "CheckAllAchievements",
				Kind = PatchKind.Skip,
				Necessity = PatchNecessity.Optional,
				Why = "Boot path. Covered by the two above, but skipping it saves the walk."
			};

			yield return new PatchTarget
			{
				TypeName = "AchievementManager",
				MethodName = "ToggleAllAchievements",
				Kind = PatchKind.Skip,
				Necessity = PatchNecessity.Optional,
				Why = "Reachable only from the DazedCommands cheat path, and it calls four SDK methods directly."
			};

			// Boot-path Steam components. The game survives these unpatched, so they are optional -
			// but SteamManager.Awake failing leaves a half-built singleton behind.
			yield return new PatchTarget
			{
				TypeName = "SteamManager",
				MethodName = "Awake",
				Kind = PatchKind.Skip,
				Necessity = PatchNecessity.Optional,
				Why = "Registers Steam lobby callbacks and reads SteamUser.GetSteamID. None of it is on the UnityTransport path."
			};

			yield return new PatchTarget
			{
				TypeName = "LocalizationManager",
				MethodName = "SetToSteamLanguage",
				Kind = PatchKind.Skip,
				Necessity = PatchNecessity.Optional,
				Why = "SteamUtils.GetSteamUILanguage. A server has no UI to localise."
			};
		}
	}
}
