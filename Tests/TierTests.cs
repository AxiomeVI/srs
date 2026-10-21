using System;
using System.Collections.Generic;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// synthetic rows, never the fixtures: the case below is a WR slower than its
// Gold, and a fixture refresh after it is beaten would make the test pin nothing
public class TierTests {
    private static readonly List<string> Columns = ["Hidden", "WR", "Gold", "Pink"];

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Theory]
    [InlineData(48.0, "Gold")]
    // a tier needs a time strictly under its threshold
    [InlineData(48.5, "Pink")]
    // under the WR and not under Gold: the WR ranks nothing
    [InlineData(48.8, "Pink")]
    [InlineData(49.5, "Pink")]
    [InlineData(50.0, "Unranked")]
    [InlineData(51.0, "Unranked")]
    public void TheFirstTierInSheetOrderTheTimeBeatsWins(double seconds, string tier) {
        // a WR slower than Gold, as 5A Unravelling had it: 49.011 against 48.5
        List<TimeSpan?> thresholds = [S(0), S(49.011), S(48.5), S(50)];

        Assert.Equal(tier, SheetData.TierOf(Columns, thresholds, S(seconds)));
    }

    [Fact]
    public void ZeroAndEmptyThresholdsNeverMatch() {
        List<TimeSpan?> thresholds = [S(0), S(0), null, S(50)];

        Assert.Equal("Pink", SheetData.TierOf(Columns, thresholds, S(1)));
    }

    [Fact]
    public void ARowWithOnlyAWRRanksNothing() {
        List<TimeSpan?> thresholds = [S(0), S(40), null, null];

        Assert.Equal("Unranked", SheetData.TierOf(Columns, thresholds, S(1)));
        Assert.Null(SheetData.NextTier(Columns, thresholds, "Unranked"));
    }

    [Theory]
    [InlineData("Pink", "Gold", 45.0)]
    // Unranked aims at the slowest tier, and Hidden's zero is no target
    [InlineData("Unranked", "Pink", 50.0)]
    public void TheNextTierIsTheNearestFasterTierWithAThreshold(string tier, string next, double seconds) {
        List<TimeSpan?> thresholds = [S(0), S(40), S(45), S(50)];

        Assert.Equal((next, S(seconds)), SheetData.NextTier(Columns, thresholds, tier));
    }

    // the sheet fills its Unranked column on some rows: it is where a time
    // lands, never a time to aim at
    [Fact]
    public void TheUnrankedColumnIsNoThreshold() {
        List<string> columns = ["Hidden", "WR", "Gold", "Pink", "Unranked"];
        List<TimeSpan?> thresholds = [S(0), S(40), S(45), S(50), S(60)];

        Assert.Equal("Unranked", SheetData.TierOf(columns, thresholds, S(55)));
        Assert.Equal(("Pink", S(50)), SheetData.NextTier(columns, thresholds, "Unranked"));
    }

    [Fact]
    public void NothingIsFasterThanGold() {
        List<TimeSpan?> thresholds = [S(0), S(40), S(45), S(50)];

        Assert.Null(SheetData.NextTier(Columns, thresholds, "Gold"));
    }

    [Fact]
    public void AnEmptyTierIsSkippedForTheNextTier() {
        List<string> columns = ["Hidden", "WR", "Gold", "Pink", "Purple 1"];
        List<TimeSpan?> thresholds = [S(0), S(40), S(45), null, S(55)];

        Assert.Equal(("Gold", S(45)), SheetData.NextTier(columns, thresholds, "Purple 1"));
    }
}
