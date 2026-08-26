using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DriftwoodHost
{
	// Where the catch leaderboard gets its facts. Every hook here is a SERVER-SIDE event in the
	// game's own flow, established from the decompiled build:
	//
	//   BITE     CreatureManager.HookItem runs only on the server (it is driven from the
	//            server-only tick subscription in CreatureManager.OnStartServer). It creates
	//            the fish and calls Item.SetAttachedRod(bait.FishingRod) on it before spawning
	//            it, and the rod's Holder is the Player whose bait was in the water. This hook
	//            listens on SetAttachedRod with a non-null rod: the moment the server decided
	//            WHO hooked WHAT. No client is consulted - the server rolled the bite.
	//
	//   LANDED   A hooked fish is a catch only once a player has it in hand. The server's
	//            holder write is Item.SetSyncedHolder (a SyncVar only the server may set, called
	//            from Server.SetItemHolder when a client grabs the item and the server accepts).
	//            A fish that snaps the line and swims off, or is eaten by a bird, is never
	//            landed and never counted. Credit goes to the angler who hooked it, not to
	//            whoever's hand closed on it - co-op lets a friend grab your fish off the line.
	//
	//   SOLD     SellBox.OnTriggerStay runs on the server and calls MoneyManager.SellItem, which
	//            adds item.TotalWorth to the crew's one shared wallet. The sale is credited to
	//            the angler who hooked the item when this server saw the bite; otherwise to the
	//            item's LastHolder (the game's own attribution for the coin sound); otherwise
	//            it goes to the wallet unattributed and appears on no row.
	//
	//   BOSS     Server.HitCreature is the server-side damage path. When the hit that arrives
	//            leaves a boss at zero HP, the player it names is the one who finished it. The
	//            owner console's `killboss` credits nobody, deliberately.
	//
	// What is NOT attributable and is therefore not claimed: a boss killed by an explosion
	// (that path carries no player), and a fish that died of a fall or a bird before anyone
	// held it. Half a stat is worse than none, so those are simply absent.
	//
	// All hooks run on the main thread inside the game's own call; none may throw past it.
	// The bookkeeping is a dictionary keyed on the item object itself, bounded and pruned so
	// a long-running server never grows it.
	internal static class CatchHooks
	{
		internal const string GroupName = "Leaderboard";

		private sealed class Hooked
		{
			internal ulong SteamId;
			internal string Name = string.Empty;
			internal string Creature = string.Empty;
			internal bool IsCreature;
			internal bool Landed;
			internal float At;
		}

		private const int MaxTracked = 512;
		private const float ForgetAfterSeconds = 30 * 60;

		// Keyed on the item OBJECT by reference - not on Unity's instance id, which this engine
		// version deprecates - with a comparer that never calls into the engine, so a destroyed
		// item is still a valid (and soon pruned) key.
		private static readonly Dictionary<Item, Hooked> ByInstance = new Dictionary<Item, Hooked>(new ReferenceComparer<Item>());
		private static readonly HashSet<Creature> CreditedBosses = new HashSet<Creature>(new ReferenceComparer<Creature>());

		private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
		{
			public bool Equals(T a, T b) => ReferenceEquals(a, b);
			public int GetHashCode(T value) => value == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
		}
		private static volatile string _state = "off (not started)";
		private static bool _warnedOnce;

		internal static string State => _state;

		internal static void Configure(bool enabled, string ledgerProblem)
		{
			if (!enabled)
			{
				_state = "off (disabled in this server's configuration)";
				return;
			}
			_state = ledgerProblem != null
				? "off (" + ledgerProblem + ")"
				: "off (the catch hooks are not in force)";
		}

		internal static void OnPatched(bool applied)
		{
			if (!CatchLedger.Enabled) return;
			_state = applied
				? "on (" + CatchLedger.Count + " player(s) on the board)"
				: "off (the game build no longer has the catch path this hooks)";
		}

		internal static void RefreshState()
		{
			if (_state.StartsWith("on (", StringComparison.Ordinal))
				_state = "on (" + CatchLedger.Count + " player(s) on the board)";
		}

		internal static IEnumerable<PatchTarget> Targets()
		{
			yield return new PatchTarget
			{
				TypeName = "Item",
				MethodName = "SetAttachedRod",
				Parameters = new[] { typeof(FishingRod) },
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = GroupName,
				Postfix = AccessTools.Method(typeof(CatchHooks), nameof(AttachedRodPostfix)),
				Why = "The server's own bite: which angler's rod the new fish was put on."
			};
			yield return new PatchTarget
			{
				TypeName = "Item",
				MethodName = "SetSyncedHolder",
				Parameters = new[] { typeof(Player), typeof(bool) },
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = GroupName,
				Postfix = AccessTools.Method(typeof(CatchHooks), nameof(SyncedHolderPostfix)),
				Why = "The server's holder write: a hooked fish becomes a catch when somebody has it in hand."
			};
			yield return new PatchTarget
			{
				TypeName = "MoneyManager",
				MethodName = "SellItem",
				Parameters = new[] { typeof(Item) },
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = GroupName,
				Prefix = AccessTools.Method(typeof(CatchHooks), nameof(SellItemPrefix)),
				Why = "The server-side sale: what the crew's wallet was paid for, credited to the angler."
			};
			yield return new PatchTarget
			{
				TypeName = "Server",
				MethodName = "RpcLogic___HitCreature___215526726",
				Parameters = new[] { typeof(Creature), typeof(Player), typeof(int), typeof(Vector3), typeof(Vector3) },
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = GroupName,
				Postfix = AccessTools.Method(typeof(CatchHooks), nameof(HitCreaturePostfix)),
				Why = "The server-side damage path: who landed the hit that finished a boss."
			};
		}

		// ------------------------------------------------------------------
		// Harmony entry points. Prefix returns true always: nothing here ever changes what
		// the game does, it only watches.
		// ------------------------------------------------------------------

		private static void AttachedRodPostfix(Item __instance, FishingRod __0)
		{
			try
			{
				if (!CatchLedger.Enabled || __0 == null || __instance == null) return;
				if (!ServerRunning()) return;
				Player angler = null;
				try { angler = __0.Holder ?? __0.SyncedHolder; } catch { }
				if (angler == null) return;
				ulong steamId = 0UL;
				try { steamId = angler.SteamID; } catch { }
				if (DriftwoodIdentity.IsSynthetic(steamId)) return;

				Prune();
				ByInstance[__instance] = new Hooked
				{
					SteamId = steamId,
					Name = NameOf(steamId, angler),
					Creature = ItemName(__instance),
					IsCreature = IsCreature(__instance),
					At = Time.realtimeSinceStartup
				};
			}
			catch (Exception exception) { WarnOnce("bite", exception); }
		}

		private static void SyncedHolderPostfix(Item __instance, Player __0)
		{
			try
			{
				if (!CatchLedger.Enabled || __0 == null || __instance == null) return;
				if (!ServerRunning()) return;
				// The game refuses some holder writes silently (a dead holder, a bird's prey);
				// only a write that took is a landing.
				Player holder = null;
				try { holder = __instance.SyncedHolder; } catch { }
				if (!ReferenceEquals(holder, __0)) return;

				Hooked hooked;
				if (!ByInstance.TryGetValue(__instance, out hooked) || hooked.Landed) return;
				hooked.Landed = true;
				if (!hooked.IsCreature) return;
				int worth = 0;
				try { worth = __instance.TotalWorth; } catch { }
				CatchLedger.RecordCatch(hooked.SteamId, hooked.Name, hooked.Creature, worth, PlayerDirectory.NowUnix());
				RefreshState();
			}
			catch (Exception exception) { WarnOnce("landing", exception); }
		}

		private static bool SellItemPrefix(Item __0)
		{
			try
			{
				if (!CatchLedger.Enabled || __0 == null) return true;
				if (!ServerRunning()) return true;
				int worth = 0;
				try { worth = __0.TotalWorth; } catch { }
				if (worth <= 0) return true;

				Hooked hooked;
				ulong steamId = 0UL;
				string name = string.Empty;
				if (ByInstance.TryGetValue(__0, out hooked))
				{
					steamId = hooked.SteamId;
					name = hooked.Name;
					ByInstance.Remove(__0);
				}
				else
				{
					Player last = null;
					try { last = __0.LastHolder; } catch { }
					if (last != null)
					{
						try { steamId = last.SteamID; } catch { }
						name = NameOf(steamId, last);
					}
				}
				if (DriftwoodIdentity.IsSynthetic(steamId)) return true;
				CatchLedger.RecordSale(steamId, name, worth, PlayerDirectory.NowUnix());
				RefreshState();
			}
			catch (Exception exception) { WarnOnce("sale", exception); }
			return true;
		}

		private static void HitCreaturePostfix(Creature __0, Player __1)
		{
			try
			{
				if (!CatchLedger.Enabled || __0 == null || __1 == null) return;
				if (!ServerRunning()) return;
				if (__0.BossType == BossType.None) return;
				int hp = 1;
				try { hp = __0._hp.Value; } catch { return; }
				if (hp > 0) return;
				if (CreditedBosses.Contains(__0)) return;
				if (CreditedBosses.Count > 64) CreditedBosses.Clear();
				CreditedBosses.Add(__0);
				ulong steamId = 0UL;
				try { steamId = __1.SteamID; } catch { }
				if (DriftwoodIdentity.IsSynthetic(steamId)) return;
				CatchLedger.RecordBoss(steamId, NameOf(steamId, __1), PlayerDirectory.NowUnix());
				RefreshState();
			}
			catch (Exception exception) { WarnOnce("boss", exception); }
		}

		// ------------------------------------------------------------------

		private static bool ServerRunning()
		{
			try { return Server.Instance != null && Server.Instance.IsServerInitialized; }
			catch { return false; }
		}

		private static bool IsCreature(Item item)
		{
			try { return item.Creature != null; } catch { return false; }
		}

		// The localized display name when the game can give one, else the prefab name. Both
		// are display text; the ledger cleans them.
		private static string ItemName(Item item)
		{
			try
			{
				string name = item.GetName();
				if (!string.IsNullOrEmpty(name)) return name;
			}
			catch { }
			try { return item.name ?? string.Empty; } catch { return string.Empty; }
		}

		private static string NameOf(ulong steamId, Player player)
		{
			string known = DriftwoodIdentity.KnownNameOrNull(steamId);
			if (!string.IsNullOrEmpty(known)) return known;
			try
			{
				if (player != null && !string.IsNullOrEmpty(player.SteamName)) return player.SteamName;
			}
			catch { }
			return DriftwoodIdentity.Placeholder(steamId);
		}

		private static void Prune()
		{
			if (ByInstance.Count < MaxTracked) return;
			float now = Time.realtimeSinceStartup;
			List<Item> stale = new List<Item>();
			foreach (KeyValuePair<Item, Hooked> pair in ByInstance)
			{
				if (now - pair.Value.At > ForgetAfterSeconds) stale.Add(pair.Key);
			}
			foreach (Item key in stale) ByInstance.Remove(key);
			// Still full after the age sweep: a server with 512 live hooked fish is not a real
			// state, so drop the oldest half rather than grow.
			if (ByInstance.Count >= MaxTracked)
			{
				List<KeyValuePair<Item, Hooked>> all = new List<KeyValuePair<Item, Hooked>>(ByInstance);
				all.Sort((a, b) => a.Value.At.CompareTo(b.Value.At));
				for (int i = 0; i < all.Count / 2; i++) ByInstance.Remove(all[i].Key);
			}
		}

		private static void WarnOnce(string what, Exception exception)
		{
			if (_warnedOnce) return;
			_warnedOnce = true;
			Plugin.Log?.LogWarning("The catch leaderboard's " + what + " hook failed (" + exception.GetType().Name + ": " +
				exception.Message + "). The game was not affected; further failures are not logged.");
		}
	}
}
