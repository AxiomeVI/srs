using System;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// SheetData.TryParseTime: the sheet mixes "28", "28.1", "00:56", "1:05.5" and
// "24:06.802" in the same column, and every unparseable cell has to become a
// null threshold rather than an exception
public class TimeParsingTests {
    [Theory]
    [InlineData("28", 28.0)]
    [InlineData("28.1", 28.1)]
    [InlineData("00:56", 56.0)]
    [InlineData("1:05.5", 65.5)]
    [InlineData("24:06.802", 1446.802)]
    [InlineData("2:18.363", 138.363)]
    [InlineData("1:00:00", 3600.0)]
    [InlineData("16.151", 16.151)]
    public void ParsesTheSheetTimeFormats(string cell, double expectedSeconds) {
        TimeSpan? parsed = SheetData.TryParseTime(cell);

        Assert.NotNull(parsed);
        Assert.Equal(expectedSeconds, parsed.Value.TotalSeconds, precision: 6);
    }

    // the export asks about a cell the sheet may not hold at all, so null
    // reaches the parser as an ordinary case rather than as a CSV impossibility
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentCellParsesAsNoTimeRatherThanThrowing(string cell) {
        Assert.Null(SheetData.TryParseTime(cell));
    }

    // regression: TimeSpan.FromSeconds rounds through a double and turns
    // 90.576 s into 1:30.575, which is why the parser goes through ticks
    [Fact]
    public void KeepsMillisecondPrecisionExactly() {
        TimeSpan? parsed = SheetData.TryParseTime("1:30.576");

        Assert.Equal(TimeSpan.FromTicks(905_760_000), parsed);
    }

    // "0:00.000" fills the whole Hidden column and some WR cells: it must parse
    // as zero, not as null — TierComparison tells the two apart (a zero
    // threshold is skipped, a null cell means "no data")
    [Fact]
    public void ParsesZeroAsZeroNotNull() {
        TimeSpan? parsed = SheetData.TryParseTime("0:00.000");

        Assert.Equal(TimeSpan.Zero, parsed);
    }

    [Theory]
    [InlineData("#REF!")]
    [InlineData("1:48 ?")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("-5")]
    [InlineData("1:-30")]
    [InlineData(":")]
    [InlineData("1:75")]
    [InlineData("1:60")]
    [InlineData("1:00:60")]
    [InlineData("1.5:30")]
    // double.TryParse takes these whatever the style, and the last overflows
    // the ticks into long.MinValue
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1:NaN")]
    [InlineData("999999999999")]
    public void ReturnsNullForUnparseableCells(string cell) {
        Assert.Null(SheetData.TryParseTime(cell));
    }

    [Fact]
    public void IgnoresSurroundingWhitespace() {
        Assert.Equal(TimeSpan.FromSeconds(28.1), SheetData.TryParseTime("  28.1 "));
    }

    // the sheet is parsed with the invariant culture: a machine running a
    // comma-decimal locale must still read "28.1" as 28.1 seconds
    [Fact]
    public void UsesTheInvariantCultureForDecimals() {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            Assert.Equal(TimeSpan.FromSeconds(28.1), SheetData.TryParseTime("28.1"));
        } finally {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
