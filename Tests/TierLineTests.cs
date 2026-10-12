using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the time format is the stand-in's, so these assert what surrounds it: the
// tier, the tier a gap names, and whether each row is there
public class TierLineTests {
    private static readonly List<string> Columns = ["Hidden", "WR", "Gold", "Pink", "Unranked"];
    private static readonly List<TimeSpan?> Thresholds = [S(0), S(40), S(45), S(50), null];

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    private static long T(double seconds) => S(seconds).Ticks;

    private static TierRows Build(double time, double? sheet = null) =>
        TierLine.Build(Columns, Thresholds, T(time), sheet is { } s ? T(s) : null);

    [Fact]
    public void TheGapAimsAtTheNextTier() {
        TierRows rows = Build(47);

        Assert.StartsWith("+", rows.Gap);
        Assert.EndsWith(" to Gold", rows.Gap);
    }

    [Theory]
    // under Gold there is nothing left to reach, under the WR or not
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(44)]
    public void AGoldTimeHasNoGap(double time) {
        TierRows rows = Build(time);

        Assert.Equal("Gold", rows.Tier);
        Assert.Null(rows.Gap);
    }

    [Fact]
    public void ATieWithGoldIsNotGold() {
        TierRows rows = Build(45);

        Assert.Equal("Pink", rows.Tier);
        Assert.EndsWith(" to Gold", rows.Gap);
    }

    [Theory]
    // faster than the sheet: a PB, with a negative gain
    [InlineData(47.0, 48.0, true)]
    // equal or slower: none
    [InlineData(48.0, 48.0, false)]
    [InlineData(49.0, 48.0, false)]
    public void APBIsATimeUnderTheSheetTime(double time, double sheet, bool pb) {
        TierRows rows = Build(time, sheet);

        Assert.Equal(pb, rows.Pb != null);
        if (pb) {
            Assert.StartsWith("PB -", rows.Pb);
        }
    }

    [Fact]
    public void NoSheetTimeNoPB() {
        Assert.Null(Build(47).Pb);
    }

    // the sheet grades 1A's two chapter rows in file time, which srs does
    // not measure: they show their time and no tier, and no other row does
    [Fact]
    public void ARowSrsCannotRankShowsItsTimeAlone() {
        TierRows rows = TierLine.Build(Columns, Thresholds, T(47), T(48), ranked: false);

        Assert.Equal(("", (string)null), (rows.Tier, rows.Gap));
        Assert.NotNull(rows.Pb);
        Assert.Equal(["1a/ARB IL", "1a/FC IL"],
            SegmentRules.All.Where(rule => !TierLine.Ranks(rule)).Select(rule => $"{rule.Scope}/{rule.Name}"));
    }

    [Theory]
    [InlineData("1a", "Crossing", "1a Crossing")]
    // the name is its scope
    [InlineData("1c", "1c", "1c")]
    [InlineData("Farewell", "Farewell", "Farewell")]
    [InlineData("1a", "ARB Start", "1a ARB Start")]
    [InlineData("5a", "ARB Depths", "5a ARB Depths")]
    [InlineData("7a", "ARB IL", "7a ARB IL")]
    public void TheNameLeadsWithItsScopeOnce(string scope, string name, string shown) {
        Assert.Equal(shown, TierLine.NameOf(scope, name));
    }
}
