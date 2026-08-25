using System.Collections.Generic;
using HarmonyLib;
using Steamworks;

namespace DriftwoodHost
{
	// The rule this file exists to enforce: on a headless host, NO Steamworks method executes
	// unguarded. Guard at the SDK BOUNDARY, not at the call site.
	//
	// Why the boundary and not the call site. The feasibility run found an unguarded
	// AchievementManager.UnlockAchievement -> SteamUserStats.GetAchievement throw when a seagull
	// stole an item, which took BirdManager.Update with it and killed bird simulation for the
	// whole run with nothing in the log. The decompile audit then found the SAME defect on the
	// join path: Player.InitializePlayer calls SteamFriends.GetPersonaName /
	// GetFriendPersonaName, and an exception there aborts the rest of Player.OnStartClient -
	// including the server-side SkipTutorial / SkipIntro, so a returning customer replays the
	// tutorial and the intro on every join.
	//
	// Two instances of one defect. Patching the four call sites would leave the fifth. Patching
	// the boundary covers every present and future caller, and the boundary is also the part of
	// the surface that does NOT move when the game rebuilds (playbook 1c).
	internal static class SteamGuards
	{
		// THE REQUIRED SET. A server that cannot resolve and install every one of these refuses to
		// host, because each one aborts FishNet's shared spawn loop when it throws - taking every
		// object queued behind it with it, silently, while the socket stays up.
		//
		//   GetPersonaName / GetFriendPersonaName : Player.InitializePlayer, on the join path.
		//                                           Without them no player can ever appear.
		//   GetSteamID                            : Client.SendSpawnPlayer, and SaveManager keys
		//                                           every per-player save on the result - so an
		//                                           unguarded call breaks progression as well as
		//                                           spawning.
		//
		// Kept as a named list so a cross-repo contract audit can read it out of this file, and so
		// nothing can quietly demote a member to Optional.
		internal static readonly string[] RequiredGuardIds =
		{
			"Steamworks.SteamFriends.GetPersonaName",
			"Steamworks.SteamFriends.GetFriendPersonaName",
			"Steamworks.SteamUser.GetSteamID"
		};

		public static IEnumerable<PatchTarget> Targets()
		{
			// --- identity -----------------------------------------------------------------
			yield return new PatchTarget
			{
				TypeName = "Steamworks.SteamUser",
				MethodName = "GetSteamID",
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Required,
				Prefix = AccessTools.Method(typeof(SteamGuards), nameof(GetSteamId)),
				Why = "Client.SendSpawnPlayer and ChatManager.SendTypedMessage both call it; SaveManager keys the per-player save on the result."
			};

			// --- display names ------------------------------------------------------------
			yield return new PatchTarget
			{
				TypeName = "Steamworks.SteamFriends",
				MethodName = "GetPersonaName",
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Required,
				Prefix = AccessTools.Method(typeof(SteamGuards), nameof(GetPersonaName)),
				Why = "Player.InitializePlayer and Player.OnSteamIDChange, on the join path."
			};
			yield return new PatchTarget
			{
				TypeName = "Steamworks.SteamFriends",
				MethodName = "GetFriendPersonaName",
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Required,
				Prefix = AccessTools.Method(typeof(SteamGuards), nameof(GetFriendPersonaName)),
				Why = "Player.InitializePlayer, OtherPlayer.SetPlayerName and ChatManager.ChatMessage - every join and every chat line."
			};

			// --- stats and achievements ---------------------------------------------------
			// AchievementManager is skipped wholesale below, but a boundary stub is the safety
			// net that survives the game adding a new caller.
			yield return Stub("Steamworks.SteamUserStats", "GetAchievement", nameof(GetAchievement));
			yield return Stub("Steamworks.SteamUserStats", "SetAchievement", nameof(ReturnFalse));
			yield return Stub("Steamworks.SteamUserStats", "StoreStats", nameof(ReturnFalse));
			yield return Stub("Steamworks.SteamUserStats", "ResetAllStats", nameof(ReturnFalse));
			yield return Stub("Steamworks.SteamUserStats", "GetNumAchievements", nameof(ReturnZeroUInt));

			// --- presence and overlay -----------------------------------------------------
			yield return Stub("Steamworks.SteamFriends", "SetRichPresence", nameof(ReturnFalse));
			yield return Stub("Steamworks.SteamFriends", "ClearRichPresence", nameof(SkipVoid));

			// --- misc ---------------------------------------------------------------------
			yield return Stub("Steamworks.SteamUtils", "GetSteamUILanguage", nameof(GetUiLanguage));
			yield return Stub("Steamworks.SteamAPI", "Shutdown", nameof(SkipVoid));
		}

		private static PatchTarget Stub(string type, string method, string replacement) => new PatchTarget
		{
			TypeName = type,
			MethodName = method,
			Kind = PatchKind.Custom,
			// Optional: these are the ones the game already survives unpatched. They are guarded
			// so that a future caller on a gameplay path cannot silently abort its own loop.
			Necessity = PatchNecessity.Optional,
			Group = "steam-sdk-stubs",
			Prefix = AccessTools.Method(typeof(SteamGuards), replacement)
		};

		private static bool GetSteamId(ref CSteamID __result)
		{
			// Not a constant any more: game 1.0.6 calls this ON THE SERVER to decide who a
			// joining player IS (see SpawnIdentity). Outside a remote spawn it still answers
			// the host placeholder.
			__result = new CSteamID(SpawnIdentity.CurrentIdentity());
			return false;
		}

		private static bool GetPersonaName(ref string __result)
		{
			__result = DriftwoodIdentity.HostDisplayName;
			return false;
		}

		private static bool GetFriendPersonaName(CSteamID steamIDFriend, ref string __result)
		{
			__result = DriftwoodIdentity.ResolveName(steamIDFriend);
			return false;
		}

		private static bool GetAchievement(ref bool pbAchieved, ref bool __result)
		{
			pbAchieved = false;
			__result = false;
			return false;
		}

		private static bool ReturnFalse(ref bool __result)
		{
			__result = false;
			return false;
		}

		private static bool ReturnZeroUInt(ref uint __result)
		{
			__result = 0u;
			return false;
		}

		private static bool SkipVoid() => false;

		private static bool GetUiLanguage(ref string __result)
		{
			__result = "english";
			return false;
		}
	}
}
