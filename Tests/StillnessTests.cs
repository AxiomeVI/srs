using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the bookkeeping behind "a savestate opens a Current Room row only if the
// player has not moved since appearing", frame by frame
public class StillnessTests {
    private static readonly (float, float) Spawn = (19f, 144f);

    // before any appearance: nothing a load could open from
    private static readonly Stillness Unsettled = new(0f, 0f, true);

    [Fact]
    public void NothingHasAppearedAtFirst() {
        Assert.True(Unsettled.After(false, false, Spawn).Moved);
    }

    [Fact]
    public void AnAppearanceRecordsWhereAndClearsMoved() {
        Stillness still = Unsettled.After(false, true, Spawn);

        Assert.Equal(new Stillness(19f, 144f, false), still);
        Assert.Equal(still, still.After(false, false, Spawn));
    }

    // a 1-frame tap moves the player 0.28 px: the whole-pixel position stays
    // on the spawn, the exact one does not
    [Fact]
    public void ASubPixelTapIsAMove() {
        Stillness still = Unsettled.After(false, true, Spawn);

        Assert.True(still.After(false, false, (19.28f, 144f)).Moved);
    }

    [Fact]
    public void WalkingAwayAndBackStaysMoved() {
        Stillness still = Unsettled.After(false, true, Spawn)
            .After(false, false, (28f, 144f))
            .After(false, false, Spawn);

        Assert.True(still.Moved);
    }

    // a loader with control, or a respawn that hands control back on the same
    // frame: the appearance wins over the disturbance
    [Fact]
    public void ADisturbanceComesBeforeTheAppearanceOfItsFrame() {
        Stillness still = Unsettled.After(false, true, Spawn);

        Assert.True(still.After(true, false, Spawn).Moved);
        Assert.Equal(new Stillness(400f, -352f, false), still.After(true, true, (400f, -352f)));
    }

    // a restart with control appears where its frame began; a direction held
    // into that frame moves the player before the frame ends
    [Fact]
    public void AnAppearanceThenItsFramesOwnMoveIsAMove() {
        Stillness still = Unsettled.After(true, true, Spawn).After(false, false, (19.28f, 144f));

        Assert.True(still.Moved);
        Assert.False(Unsettled.After(true, true, Spawn).After(false, false, Spawn).Moved);
    }

    // 6A's fall removes the player before the cutscene ends
    [Fact]
    public void WithoutAPlayerNothingChangesButADisturbance() {
        Stillness still = Unsettled.After(false, true, Spawn);

        Assert.Equal(still, still.After(false, true, null));
        Assert.True(still.After(true, false, null).Moved);
    }
}
