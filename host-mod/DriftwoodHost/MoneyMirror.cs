using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DriftwoodHost
{
	// THE BALANCE THE SERVER APPROVES PURCHASES AGAINST.
	//
	// How to Fish keeps the crew's money TWICE.
	//
	//   MoneyManager._money is a SyncVar<int> and is the real balance. OnStartServer loads it
	//   from the world save; SellItem, AddMoney and RemoveMoney are the only things that move
	//   it; it replicates to every client.
	//
	//   MoneyManager.Money is a plain STATIC int - a mirror of that SyncVar, written in exactly
	//   three places and every one of them is CLIENT-side: OnStartClient (Money = _money.Value),
	//   OnStopClient (Money = 0), and the `!asServer` half of the SyncVar's OnChange callback.
	//
	// Two things READ the static, and both of them run on the SERVER:
	//
	//   MoneyManager.CanAfford(cost) => cost <= Money. That is the gate inside every purchase
	//   ServerRpc - BuyItem, BuyBait, UnlockPocket, the attachment/bullet/sharpness upgrades,
	//   BuyBoatMotor, BuyBoatRadar. A refused purchase returns silently; there is no message.
	//
	//   SaveManager.SaveServer writes `Money = MoneyManager.Money` into the world save.
	//
	// On a LISTEN server the host is also a client, so the static tracks and nobody ever notices.
	// On a DEDICATED server the loopback client's OnStartClient runs once, seeding the static
	// from the save, and the `!asServer` OnChange never fires again - so the static FREEZES at
	// the value the world was loaded with. A brand new world loads zero, and from that moment
	// every purchase on that server is checked against zero and dropped: the client's own copy
	// of the balance is correct, so the client sends the request, and the server refuses it in
	// silence. The player sees money that never goes down and an item that never arrives. The
	// same frozen number is then written back over the save, so a session's earnings never
	// persist and the next boot re-freezes at the same wrong figure.
	//
	// It fails in the permissive direction too: a world whose save happens to hold a large
	// figure lets every purchase under it through for ever, no matter what the crew has spent.
	//
	// MEASURED on a customer server 2026-08-25 (game 1.0.9, host 0.1.5, ticket 765633):
	// _money.Value = 3 while the game's own SaveServer wrote "Money":0 into the world file.
	//
	// The fix is to keep the static in step with the SyncVar on the server - exactly what the
	// client half of the game does for itself. Nothing else changes: the SyncVar is still the
	// only thing that moves money, every purchase is still decided server-side, and no decision
	// is taken from anything a client asserts.
	internal static class MoneyMirror
	{
		internal const string GroupName = "economy-mirror";

		// The static's setter is private, so it is reached by reflection. Resolved once, on
		// first use, and a failure is reported once rather than on every sale.
		private static MethodInfo _setter;
		private static bool _resolved;
		private static bool _warned;

		internal static IEnumerable<PatchTarget> Targets()
		{
			// All five are OPTIONAL and share one group. If the game renames any of them the
			// whole mirror stands down and the server still hosts - degraded to the frozen-
			// balance defect, which the readiness document then names in featuresStoodDown -
			// rather than refusing to host over a shop.
			yield return Seed("OnStartServer",
				"Seeds the static balance the server's own purchase gate reads, from the SyncVar the save was loaded into.");
			yield return Mutator("AddMoney",
				"Keeps the server's purchase gate in step after money is granted.");
			yield return Mutator("SellItem",
				"Keeps the server's purchase gate in step after a sale at the sell box - without it a sale raises the real balance and the gate never sees it.");
			yield return Mutator("RemoveMoney",
				"Keeps the server's purchase gate in step after a purchase is paid for - without it spent money is never debited from the figure the gate reads.");

			// The save writes the static, not the SyncVar, and OnStopClient zeroes the static on
			// the way down - so a shutdown save can persist a zero over a real balance. Mirror
			// immediately before the game builds the save object.
			yield return new PatchTarget
			{
				TypeName = "SaveManager",
				MethodName = "SaveServer",
				Parameters = new[] { typeof(bool) },
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = GroupName,
				Prefix = AccessTools.Method(typeof(MoneyMirror), nameof(MirrorPrefix)),
				Why = "SaveManager.SaveServer persists MoneyManager.Money, the client-side mirror, which on a dedicated server is frozen at the figure the world was loaded with."
			};
		}

		private static PatchTarget Seed(string method, string why) => Mutator(method, why);

		private static PatchTarget Mutator(string method, string why) => new PatchTarget
		{
			TypeName = "MoneyManager",
			MethodName = method,
			Kind = PatchKind.Custom,
			Necessity = PatchNecessity.Optional,
			Group = GroupName,
			Postfix = AccessTools.Method(typeof(MoneyMirror), nameof(MirrorPostfix)),
			Why = why
		};

		// Both hooks sit on the live gameplay path, so neither may throw past the game's own
		// call. A failure here costs the mirror, never the sale.
		private static void MirrorPostfix() { Mirror(); }
		private static void MirrorPrefix() { Mirror(); }

		private static void Mirror()
		{
			try
			{
				MoneyManager manager = MoneyManager.Instance;
				if (manager == null || !manager.IsServerInitialized) return;
				MethodInfo setter = Setter();
				if (setter == null) return;
				setter.Invoke(null, new object[] { manager._money.Value });
			}
			catch (Exception exception)
			{
				if (_warned) return;
				_warned = true;
				Plugin.Log?.LogWarning("The shared balance could not be mirrored onto the figure the game's purchase gate reads (" +
					exception.GetType().Name + ": " + exception.Message +
					"). Purchases on this server may be refused even when the crew can afford them.");
			}
		}

		private static MethodInfo Setter()
		{
			if (_resolved) return _setter;
			_resolved = true;
			_setter = AccessTools.PropertySetter(typeof(MoneyManager), "Money");
			if (_setter == null)
			{
				Plugin.Log?.LogWarning("MoneyManager.Money has no setter in this game build, so the server's purchase gate cannot be kept in step with the crew's real balance.");
			}
			return _setter;
		}
	}
}
