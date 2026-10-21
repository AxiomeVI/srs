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
        Assert.Equal("+" + TimeFormat.FromTicks(0), TimeFormat.Delta(0));
    }

    [Fact]
    public void ADeltaOfAnHourOrMoreCarriesItsHours() {
        Assert.Equal("-1:04:47.491", TimeFormat.Delta(-new TimeSpan(0, 1, 4, 47, 491).Ticks));
    }
}
