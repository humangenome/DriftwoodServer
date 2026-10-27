using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DriftwoodHost
{
	// A game bug, not a headless one: ProjectileManager.UpdateProjectileScan (read in game 1.1.3)
	// works out the projectile's distance from its owner BEFORE it checks that the owner exists:
	//
	//     bool flag = (projectile.Position - projectile.Owner.Transform.position).sqrMagnitude > _sqrMaxProjRange;
	//     if (!projectile.Owner || flag) { AddToRemoveQueue(projectile); return; }
	//
	// Once the player who fired or threw it is gone (died and respawned as a new object, or left),
	// the first line throws, so the second line - the one meant to remove exactly that projectile -
	// never runs. The projectile stays in the list and throws again on every physics step, and the
	// throw also aborts the rest of FixedUpdate's loop, so every projectile after it in that loop
	// stops moving and hitting for as long as somebody is aboard. Measured on canary 219989
	// 2026-10-27: after a player died in their own dynamite blast, 13,968 copies of the error in
	// about three minutes (about 100 a second, 4.2 MB of log), stopping only when the world paused.
	//
	// The prefix does what the game's own check intends: when the owner (or its transform) no longer
	// exists, queue the projectile for removal through the game's AddToRemoveQueue and skip the scan.
	// A projectile with a live owner runs the original untouched.
	internal static class ProjectileOwnerGuard
	{
		private static MethodInfo _addToRemoveQueue;
		private static long _removed;
		private static long _queueFailures;

		internal static long Removed => System.Threading.Interlocked.Read(ref _removed);

		public static IEnumerable<PatchTarget> Targets()
		{
			_addToRemoveQueue = AccessTools.Method(typeof(ProjectileManager), "AddToRemoveQueue");
			yield return new PatchTarget
			{
				TypeName = "ProjectileManager",
				MethodName = "UpdateProjectileScan",
				Kind = PatchKind.Custom,
				// Optional: without it the server hosts exactly as before (the bug stays, the log
				// guard keeps the log small).
				Necessity = PatchNecessity.Optional,
				Group = "projectile-owner-guard",
				Prefix = AccessTools.Method(typeof(ProjectileOwnerGuard), nameof(UpdateProjectileScanPrefix)),
				Why = "The game reads a projectile's owner before checking it exists; a projectile whose owner is gone threw on every physics step and stopped the other projectiles."
			};
		}

		private static bool UpdateProjectileScanPrefix(ProjectileManager __instance, Projectile projectile)
		{
			if (projectile == null) return true;
			Player owner = projectile.Owner;
			// Unity's own existence test: false for null AND for a destroyed object.
			if (owner && owner.Transform) return true;
			if (_addToRemoveQueue == null) return true;
			try
			{
				_addToRemoveQueue.Invoke(__instance, new object[] { projectile });
			}
			catch (Exception exception)
			{
				// Could not queue it: let the game run its own (throwing) path rather than skip a
				// projectile nobody will ever remove.
				if (System.Threading.Interlocked.Increment(ref _queueFailures) == 1)
				{
					Plugin.Log?.LogWarning("Could not remove a projectile whose owner has left (" +
						exception.GetType().Name + "); the game's own error will repeat.");
				}
				return true;
			}
			long removed = System.Threading.Interlocked.Increment(ref _removed);
			if (removed <= 3 || removed % 100 == 0)
			{
				Plugin.Log?.LogInfo("Removed a projectile whose owner has left the world (" + removed +
					" so far). The game would have thrown on it every physics step.");
			}
			return false;
		}
	}
}
