using System;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// The time format is not pinned here: SpeedrunTool's own formatter is the
// authority, and this project cannot reference it. TimeFormat owns only the
// delta and the form of an hour or more.
public class TimeFormatTests {
    // Speed Run Tool's format has no hours
    [Theory]
    [InlineData(3600.0, "1:00:00.000")]
    [InlineData(3903.2509, "1:05:03.250")]
    [InlineData(36000 + 59 * 60 + 59.9999, "10:59:59.999")]
    public void AnHourOrMoreCarriesItsHours(double seconds, string shown) {
        Assert.Equal(shown, TimeFormat.FromTicks(TimeSpan.FromSeconds(seconds).Ticks));
    }

    [Fact]
    public void UnderAnHourIsLeftToSpeedRunTool() {
        long ticks = TimeSpan.TicksPerHour - 1;

        Assert.Equal(TimeFormat.Format(ticks), TimeFormat.FromTicks(ticks));
    }

    [Fact]
    public void ADeltaIsATimeWithItsSign() {
        long ticks = TimeSpan.FromSeconds(73.5).Ticks;

        Assert.Equal("-" + TimeFormat.FromTicks(ticks), TimeFormat.Delta(-ticks));
        Assert.Equal("+" + TimeFormat.FromTicks(ticks), TimeFormat.Delta(ticks));
    }

    [Theory]
    // whole frames, as two game times differ
    [InlineData(-0.051, "-3f")]
    [InlineData(0.017, "+1f")]
    [InlineData(-0.085, "-5f")]
    [InlineData(0, "+0f")]
    // a hand-typed sheet time: the nearest frame, never 0f for a difference
    [InlineData(-0.025, "-1f")]
    [InlineData(0.026, "+2f")]
    [InlineData(-0.003, "-1f")]
    public void UnderATenthADeltaIsInFrames(double seconds, string shown) {
        Assert.Equal(shown, TimeFormat.Delta(TimeSpan.FromSeconds(seconds).Ticks));
    }

    [Fact]
    public void FromATenthADeltaIsATime() {
        long ticks = TimeSpan.FromSeconds(0.1).Ticks;

        Assert.Equal("-" + TimeFormat.FromTicks(ticks), TimeFormat.Delta(-ticks));
        Assert.Equal("+" + TimeFormat.FromTicks(ticks), TimeFormat.Behind(ticks));
    }

    [Theory]
    // strictly under the threshold: a tie is one frame short
    [InlineData(0, "+1f")]
    [InlineData(0.015, "+1f")]
    [InlineData(0.017, "+2f")]
    [InlineData(0.034, "+3f")]
    [InlineData(0.099, "+6f")]
    public void BehindAThresholdCountsTheFramesToSave(double seconds, string shown) {
        Assert.Equal(shown, TimeFormat.Behind(TimeSpan.FromSeconds(seconds).Ticks));
    }

    [Fact]
    public void ADeltaOfAnHourOrMoreCarriesItsHours() {
        Assert.Equal("-1:04:47.491", TimeFormat.Delta(-new TimeSpan(0, 1, 4, 47, 491).Ticks));
    }
}
