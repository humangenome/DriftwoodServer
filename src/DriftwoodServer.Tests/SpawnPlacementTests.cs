using DriftwoodHost;
using Xunit;

namespace DriftwoodServer.Tests;

// Where the owner's spawn command drops an item. A player who has not moved since joining sits
// at the world origin on the server, and the item used to land there, in the middle of the map.
public class SpawnPlacementTests
{
	[Theory]
	[InlineData(0f, 0f, 0f, false)]          // never reported: the server copy's starting point
	[InlineData(0.3f, 0.2f, -0.1f, false)]   // still easing in on the origin
	[InlineData(508.9f, 2.1f, -104.3f, true)] // a real island position
	[InlineData(0f, 1.2f, 0f, true)]
	public void IsReported_TreatsTheOriginAsNotReported(float x, float y, float z, bool expected) =>
		Assert.Equal(expected, SpawnPlacement.IsReported(x, y, z));

	[Fact]
	public void NobodyConnected_Refuses()
	{
		Assert.Equal(SpawnPlacement.Spot.Nobody, SpawnPlacement.Choose(System.Array.Empty<bool>(), true, out int named));
		Assert.Equal(-1, named);
	}

	[Fact]
	public void AMovedPlayer_IsPreferredOverOneWhoHasNotMoved()
	{
		Assert.Equal(SpawnPlacement.Spot.Player, SpawnPlacement.Choose(new[] { false, true, true }, true, out int named));
		Assert.Equal(1, named);
	}

	[Fact]
	public void TheFirstMovedPlayer_KeepsTheOldOrder()
	{
		Assert.Equal(SpawnPlacement.Spot.Player, SpawnPlacement.Choose(new[] { true, true }, false, out int named));
		Assert.Equal(0, named);
	}

	[Fact]
	public void NobodyHasMoved_UsesTheIslandArrivalPoint()
	{
		Assert.Equal(SpawnPlacement.Spot.IslandArrival, SpawnPlacement.Choose(new[] { false, false }, true, out int named));
		Assert.Equal(0, named);
	}

	[Fact]
	public void TheHostLog_SaysWhichPlaceWasUsed()
	{
		Assert.Contains("arrival point", SpawnPlacement.Describe(SpawnPlacement.Spot.IslandArrival));
		Assert.Contains("last reported position", SpawnPlacement.Describe(SpawnPlacement.Spot.Player));
		Assert.Contains("could not be read", SpawnPlacement.Describe(SpawnPlacement.Spot.PlayerUnreported));
	}

	[Fact]
	public void NobodyHasMovedAndNoArrivalPoint_FallsBackToThePlayer()
	{
		Assert.Equal(SpawnPlacement.Spot.PlayerUnreported, SpawnPlacement.Choose(new[] { false }, false, out int named));
		Assert.Equal(0, named);
	}
}
