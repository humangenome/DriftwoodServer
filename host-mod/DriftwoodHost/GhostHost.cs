using System.Collections.Generic;
using HarmonyLib;

namespace DriftwoodHost
{
	// THE GHOST HOST.
	//
	// This game only builds its world for a LOCAL client: Client.OnStartServer calls
	// InitializeServer() - which instantiates and spawns GameInfo.ServerPrefab, the object that
	// carries Server and every world manager - only `if (base.Owner.IsLocalClient)`. So a pure
	// server-only process produces a bound port and an empty universe. The loopback client is a
	// LOCKED INVARIANT, exactly as Schedule I's dedicated-server project documents it
	// (GHOST_HOST_REQUIRED), and Lodestone carries the same cost.
	//
	// But the loopback CLIENT and the loopback PLAYER are two different things, and only the
	// first is required. The player is spawned by a separate, later step:
	//
	//     Client.OnStartClient -> if (Owner.IsLocalClient) InitializeLocal()
	//     InitializeLocal()    -> LocalClient = this; StartCoroutine(SendSpawnPlayer())
	//     SendSpawnPlayer()    -> waits for the island, then Server.Instance.SpawnPlayer(...)
	//
	// Suppressing the coroutine leaves the world intact and removes the phantom avatar entirely:
	// no body at spawn, no dot on any map, nobody on the roster, and PlayerManager.Players
	// genuinely reports 0 on an empty server - which playbook 8 gate 1c calls a stronger proof
	// than a screenshot, because it reads what the game believes exists.
	//
	// The trap, straight out of playbook 2b: "do not simply stop spawning it - with no local
	// player the game reaches for the first player in the world, finds nothing, and throws once
	// per frame". Here the known consumer is NPC.GetEyeDir via
	// PlayerManager.GetNearestAlivePlayer, and it is covered by the NPC.Update swallow - but a
	// swallow is not a fix, so every swallow is COUNTED and rate-alarmed, and the empty-server
	// exception rate with suppression on is a measured, published number rather than an
	// assumption. If that rate is not near zero, this switch is the thing to turn off.
	internal static class GhostHost
	{
		internal static bool Suppress;
		internal static bool Suppressed { get; private set; }

		public static IEnumerable<PatchTarget> Targets()
		{
			yield return new PatchTarget
			{
				TypeName = "Client",
				MethodName = "InitializeLocal",
				Kind = PatchKind.Custom,
				// Optional and grouped: if this cannot be applied the server still hosts, it just
				// hosts with a phantom player. Standing the feature down whole is much safer than
				// running it half-applied.
				Necessity = PatchNecessity.Optional,
				Group = "ghost-host-suppression",
				Prefix = AccessTools.Method(typeof(GhostHost), nameof(InitializeLocalPrefix)),
				Why = "Skips only the SendSpawnPlayer coroutine. The loopback client itself is required for the world to exist."
			};
		}

		private static bool InitializeLocalPrefix(Client __instance)
		{
			if (!Suppress) return true;
			// Keep the game's own bookkeeping honest - LocalClient is what the rest of Client
			// expects to be set - and skip only the avatar request.
			AccessTools.Property(typeof(Client), "LocalClient")?.SetValue(null, __instance, null);
			Suppressed = true;
			return false;
		}
	}
}
