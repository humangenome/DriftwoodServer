using System.Collections.Generic;

namespace DriftwoodHost
{
	// Where the owner's `spawn` drops an item. The pure half, with no Unity or game type, so the
	// choice is proven in the test project; OwnerGameplay.SpawnItem reads the positions and
	// carries the choice out.
	//
	// WHY A PLAYER'S POSITION CAN BE UNKNOWN: movement is client-owned (see PlayerRescue). The
	// server's copy of a player (OtherPlayer) eases its transform towards the last position that
	// player's client reported, and that position starts as the world origin. A client reports
	// only once it moves, so a player who has stood still since joining sits at the origin on
	// the server, while on their own screen they stand at the island's arrival point. Spawning
	// "next to" that copy dropped the item in the middle of the map (10/23 lab).
	//
	// THE RULE: the first connected player whose position the server has heard wins. When nobody
	// has moved yet, everyone is still where the game put them on arrival, so the island's
	// arrival point (GameCompat.TryPlayerSpawn, the same point PlayerRescue sends a stuck player
	// to) is next to them. Only when that point cannot be read either does the old behaviour
	// stand.
	internal static class SpawnPlacement
	{
		// A reported position this close to the origin is treated as not reported. No island's
		// standing ground is at the exact origin, and an unmoved copy converges on it.
		internal const float UnreportedRadius = 1f;

		internal enum Spot
		{
			// Nobody connected: the command refuses.
			Nobody,
			// Next to the named player, at their server position.
			Player,
			// At the island's arrival point, where the named (unmoved) player stands.
			IslandArrival,
			// The old behaviour: the named player's server position, though it was never reported.
			PlayerUnreported
		}

		// The host log's half sentence for each choice (staff and self-hosters read it; the owner's reply is unchanged).
		internal static string Describe(Spot spot)
		{
			switch (spot)
			{
				case Spot.Player: return "next to the player's last reported position";
				case Spot.IslandArrival: return "the island's arrival point, because no connected player has moved since joining";
				case Spot.PlayerUnreported: return "the player's position as this server has it, because nobody has moved since joining and the island's arrival point could not be read";
				default: return "nowhere, because nobody is connected";
			}
		}

		internal static bool IsReported(float x, float y, float z) =>
			x * x + y * y + z * z >= UnreportedRadius * UnreportedRadius;

		// reported[i] says whether connected player i (roster order) has a reported position.
		// `named` is the index of the player the reply names ("Spawned X next to <name>."),
		// or -1 when nobody is connected.
		internal static Spot Choose(IReadOnlyList<bool> reported, bool islandArrivalKnown, out int named)
		{
			named = -1;
			if (reported == null || reported.Count == 0) return Spot.Nobody;
			for (int i = 0; i < reported.Count; i++)
			{
				if (!reported[i]) continue;
				named = i;
				return Spot.Player;
			}
			named = 0;
			return islandArrivalKnown ? Spot.IslandArrival : Spot.PlayerUnreported;
		}
	}
}
