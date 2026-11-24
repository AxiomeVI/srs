using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// how a room was loaded since the last fed frame: one LoadLevel call's kind,
// and the strongest of several landing in one gap
public class RoomLoadTests {
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ALevelFromTheLoaderIsALoaderWhateverItsIntro(bool respawnIntro, bool reloading) {
        Assert.Equal(RoomLoad.Loader, RoomLoads.Of(fromLoader: true, respawnIntro, reloading));
    }

    // a transition, or a room change a cutscene makes
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyIntroButRespawnIsAWalkIn(bool reloading) {
        Assert.Equal(RoomLoad.WalkIn, RoomLoads.Of(fromLoader: false, respawnIntro: false, reloading));
    }

    // Level.Reload is the game's only LoadLevel(Respawn)
    [Fact]
    public void ARespawnInsideReloadIsADeath() {
        Assert.Equal(RoomLoad.Respawn, RoomLoads.Of(fromLoader: false, respawnIntro: true, reloading: true));
    }

    // SpeedrunTool's room teleport, into another room or onto a summit flag of this one
    [Fact]
    public void ARespawnOutsideReloadIsATeleport() {
        Assert.Equal(RoomLoad.Teleport, RoomLoads.Of(fromLoader: false, respawnIntro: true, reloading: false));
    }

    [Theory]
    [InlineData(RoomLoad.Respawn)]
    [InlineData(RoomLoad.WalkIn)]
    [InlineData(RoomLoad.Teleport)]
    [InlineData(RoomLoad.Loader)]
    public void TheFirstLoadOfAGapIsItsKind(RoomLoad kind) {
        Assert.Equal(kind, RoomLoads.Add(RoomLoad.None, kind));
    }

    [Theory]
    [InlineData(RoomLoad.Respawn, RoomLoad.WalkIn, RoomLoad.WalkIn)]
    [InlineData(RoomLoad.WalkIn, RoomLoad.Teleport, RoomLoad.Teleport)]
    [InlineData(RoomLoad.Teleport, RoomLoad.Loader, RoomLoad.Loader)]
    [InlineData(RoomLoad.Respawn, RoomLoad.Loader, RoomLoad.Loader)]
    public void TheStrongestLoadOfAGapWinsInEitherOrder(RoomLoad weaker, RoomLoad stronger, RoomLoad expected) {
        Assert.Equal(expected, RoomLoads.Add(RoomLoads.Add(RoomLoad.None, weaker), stronger));
        Assert.Equal(expected, RoomLoads.Add(RoomLoads.Add(RoomLoad.None, stronger), weaker));
    }

    [Fact]
    public void ASecondLoadOfTheSameKindChangesNothing() {
        Assert.Equal(RoomLoad.WalkIn, RoomLoads.Add(RoomLoad.WalkIn, RoomLoad.WalkIn));
    }

    // a room change with no walk-in is a teleport: only a gap with no load or
    // a death in it walked into nothing
    [Theory]
    [InlineData(RoomLoad.None, true)]
    [InlineData(RoomLoad.Respawn, true)]
    [InlineData(RoomLoad.WalkIn, false)]
    [InlineData(RoomLoad.Teleport, false)]
    [InlineData(RoomLoad.Loader, false)]
    public void OnlyNoLoadAndADeathAreBelowAWalkIn(RoomLoad kind, bool below) {
        Assert.Equal(below, kind < RoomLoad.WalkIn);
    }
}
