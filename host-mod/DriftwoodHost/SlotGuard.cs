using System;
using System.Reflection;
using FishNet.Transporting;
using HarmonyLib;

namespace DriftwoodHost
{
	// SLOT ENFORCEMENT - playbook 8 gate 1b, the gate that exists because Lodestone advertised,
	// displayed and enforced-nowhere a four slot limit and five people got in.
	//
	// The good news, read out of FishyUnityTransport: the transport DOES gate on the real remote
	// join path. HandleRemoteConnectionState(Started, clientId) checks
	// m_TransportIdToClientIdMap.Count >= GetMaximumClients() and disconnects over the limit.
	// That is more than Lodestone had. There are three traps around it.
	//
	// TRAP 1 - SetMaximumClients is a NO-OP once the server is running:
	//     if (m_ServerState.IsStartingOrStarted()) { LogWarning("Cannot set maximum clients when
	//     server is running."); } else { m_MaximumClients = value; }
	//   and the default is 4095. So a host that sets slots after starting silently runs with 4095
	//   slots and one warning line nobody reads. The GAME'S OWN Steam host path does exactly that
	//   (ConnectionManager.CreateOnlineLobby starts the connection, then calls SetMaximumClients).
	//   Driftwood sets it BEFORE StartConnection and then READS IT BACK, because "we wrote the
	//   setting" and "the setting is in force" are not the same sentence.
	//
	// TRAP 2 - the ghost host consumes a transport slot. In host mode StartClient() takes the
	//   loopback branch while the server is Started, and that branch calls the same private
	//   HandleRemoteConnectionState(Started, 0), which inserts into the map. So the host's own
	//   connection occupies one slot and a server sold as 4 would admit only 3 paying players.
	//   That is Lodestone's bug in the mirror - underselling instead of overselling, and just as
	//   invisible. The transport is therefore configured with Slots + 1, and the extra is never
	//   sold, never displayed, and asserted separately.
	//
	// TRAP 3 - the refusal carries no reason. The over-limit path calls m_Driver.Disconnect with
	//   no payload, and FishNet's own KickReason never reaches the client either. The joiner does
	//   get a prompt, clean bounce (ConnectionManager handles Stopped while a connection was
	//   expected and returns to the menu) rather than a silent timeout - but nothing says "full".
	//   The place that CAN say it is the Driftwood launcher, from the supervisor's health
	//   endpoint, which publishes players/slots/full. That is a launcher-lane contract item and
	//   it is recorded as one rather than left as an assumption.
	internal static class SlotGuard
	{
		internal static int SoldSlots { get; private set; }
		internal static int ConfiguredMaxClients { get; private set; }
		internal static bool HostSlotReserved { get; private set; }
		internal static long RefusedJoins { get; private set; }

		// Returns null on success, or one plain sentence naming what went wrong.
		internal static string Configure(Transport transport, int soldSlots, bool hostMode)
		{
			if (transport == null) return "No transport was available to apply the slot limit to.";
			SoldSlots = soldSlots;
			HostSlotReserved = hostMode;
			int wanted = hostMode ? soldSlots + 1 : soldSlots;

			transport.SetMaximumClients(wanted);

			int actual;
			try
			{
				actual = transport.GetMaximumClients();
			}
			catch (Exception exception)
			{
				return "The slot limit could not be read back from the transport (" + exception.GetType().Name + "), so this server cannot prove it is enforcing " + soldSlots + " slots.";
			}

			ConfiguredMaxClients = actual;
			if (actual != wanted)
			{
				return "This server was told to allow " + soldSlots + " players but the game is enforcing " + actual +
					" connections, so the slot limit is not in force. The limit has to be set before the server starts listening; setting it afterwards is silently ignored.";
			}
			return null;
		}

		// Counts refusals so a full server is visible in the readiness file rather than only in a
		// warning line inside a 40 MB log.
		internal static void RecordRefusal() => RefusedJoins++;

		internal static PatchTarget RefusalCounterTarget()
		{
			return new PatchTarget
			{
				TypeName = "FishNet.Transporting.UTP.UnityTransport",
				MethodName = "HandleRemoteConnectionState",
				Parameters = new[] { typeof(RemoteConnectionState), typeof(ulong) },
				Kind = PatchKind.Custom,
				Necessity = PatchNecessity.Optional,
				Group = "slot-refusal-telemetry",
				Prefix = AccessTools.Method(typeof(SlotGuard), nameof(HandleRemoteConnectionStatePrefix)),
				Why = "Counts refused joins so a full server is visible, and logs the refusal with the real numbers."
			};
		}

		// Observe only. The transport's own limit check stays the enforcement point - wrapping it
		// would put OUR arithmetic on the join path, which is precisely the class of mistake this
		// gate exists to catch.
		private static void HandleRemoteConnectionStatePrefix(object __instance, RemoteConnectionState state, ulong clientId)
		{
			if (state != RemoteConnectionState.Started) return;
			try
			{
				FieldInfo map = AccessTools.Field(__instance.GetType(), "m_TransportIdToClientIdMap");
				object value = map?.GetValue(__instance);
				PropertyInfo count = value?.GetType().GetProperty("Count");
				if (count == null) return;
				int connected = (int)count.GetValue(value, null);
				if (connected < ConfiguredMaxClients) return;
				RecordRefusal();
				Plugin.Log?.LogWarning(
					"Refused a join: this server is full. " + Visible(connected) + "/" + SoldSlots +
					" players connected (transport " + connected + "/" + ConfiguredMaxClients + ").");
			}
			catch
			{
				// Telemetry must never be able to break admission.
			}
		}

		// The number a customer should see: transport connections minus the host's own.
		internal static int Visible(int transportConnections) =>
			Math.Max(0, transportConnections - (HostSlotReserved ? 1 : 0));
	}
}
