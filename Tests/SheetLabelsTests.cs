using System.Linq;
using System.Collections.Generic;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// Checks the export half of the row table: the row of the player's sheet a
// time of srs's (scope, name) is written to.
public class SheetLabelsTests {
    // a row whose two documents differ carries a Target, which wins over the
    // default; the default's tab and chapter are pinned row by row below
    [Fact]
    public void ATargetOverridesTheDefault() {
        SheetRowRef elsewhere = new("A Sides", "1a", "Crossing (renamed)");
        SheetRow overridden = Row("1a", "Crossing") with { Target = elsewhere };

        Assert.Equal(elsewhere, SheetRows.TargetOf(overridden));
    }

    // the entry tab files an A-side IL under the bare chapter, not under "1a IL"
    [Fact]
    public void AnIlIsWrittenUnderItsBareChapter() {
        SheetRow[] ils = [.. SheetRows.All.Where(row => row.SheetChapter.EndsWith(" IL"))];

        Assert.NotEmpty(ils);
        Assert.All(ils, row => Assert.Equal(new SheetRowRef("A Sides", row.Scope, row.Label), SheetRows.TargetOf(row)));
    }

    [Fact]
    public void FarewellsIlsAreWrittenWithoutTheirSuffix() {
        AssertRow("Farewell", "DTS IL", "Farewell", "", "DTS");
        AssertRow("Farewell", "No DTS IL", "Farewell", "", "No DTS");
    }

    [Fact]
    public void NoTwoRowsShareATarget() {
        // two rows on one target would write one cell, and the export
        // summary would name the first for both
        Assert.Equal(SheetRows.All.Length, SheetRows.All.Select(SheetRows.TargetOf).Distinct().Count());
    }

    [Fact]
    public void UnknownSegmentIsNotExported() {
        Assert.False(SheetRows.TryFind("9a", "Nowhere", out _));
    }

    [Fact]
    public void OnlyTheFourWritableTabsAreTargeted() {
        HashSet<string> tabs = [.. SheetRows.All.Select(row => SheetRows.TargetOf(row).Tab)];

        Assert.Equal(["A Sides", "B+C Sides", "Farewell", "ARB/Full Clear"], tabs);
        Assert.Equal("A Sides", SheetLabels.TabASides);
        Assert.Equal("B+C Sides", SheetLabels.TabBCSides);
        Assert.Equal("Farewell", SheetLabels.TabFarewell);
        Assert.Equal("ARB/Full Clear", SheetLabels.TabArbFullClear);
    }

    // the berry tab's chapter cell has no " CP" to drop: it is the entry tab's
    // cell as it stands, and a chapter row is matched on that cell twice
    [Fact]
    public void ABerryRowIsWrittenUnderItsChapterCell() {
        const string berry = "\U0001F353";
        AssertRow("1a", "ARB Start", "ARB/Full Clear", $"1a {berry}", "Start");
        AssertRow("1a", "ARB Start to Heart", "ARB/Full Clear", $"1a {berry}", "Start to Heart");
        AssertRow("2a", "ARB Start", "ARB/Full Clear", $"2a {berry}", "2a Start");
        AssertRow("4a", "ARB Cliff Face (from RTM)", "ARB/Full Clear", $"4a {berry}", "Cliff Face (from RTM)");
        AssertRow("5a", "ARB Depths", "ARB/Full Clear", $"5a {berry}", "Depths");
        AssertRow("5a", "ARB Start", "ARB/Full Clear", $"5a {berry}", "5a Start");
        AssertRow("1a", "ARB Heart to RTM", "ARB/Full Clear", $"1a {berry}", "Heart to RTM");
        AssertRow("4a", "ARB Old Trail (to RTM)", "ARB/Full Clear", $"4a {berry}", "Old Trail (to RTM)");
        AssertRow("7a", "ARB 2500m-2", "ARB/Full Clear", $"7a {berry}", "2500m-2");
        AssertRow("7a", "ARB 2500m-full", "ARB/Full Clear", $"7a {berry}", "2500m-full");
        AssertRow("8a", "ARB HotM Vertical", "ARB/Full Clear", $"8a {berry}", "HotM Vertical");
        AssertRow("2a", "ARB IL", "ARB/Full Clear", $"2a {berry}", $"2a {berry}");
        Assert.All(SheetRows.All.Where(row => row.Tab == StandardsTab.Arb), row => Assert.Null(row.Target));
    }

    [Fact]
    public void EveryRowNamesItsGameAnchor() {
        Assert.All(SheetRows.All, row => Assert.False(string.IsNullOrEmpty(row.Anchor)));
        Assert.Equal("Hollows", Row("6a", "Hollows Tape RTM").Anchor);
        Assert.Equal("Start", Row("Farewell", "Start DTS").Anchor);
        Assert.Equal("Reflection", Row("6b", "Falling").Anchor);
        Assert.Equal("Heart of the Mountain", Row("8a", "HotM Vertical").Anchor);
        Assert.Equal("HotM Horizontal", Row("8a", "HotM Horizontal").Anchor);
        Assert.Equal("500 M", Row("7a", "500m").Anchor);
    }

    private static SheetRow Row(string scope, string name) {
        Assert.True(SheetRows.TryFind(scope, name, out SheetRow row), $"{scope}/{name}");
        return row;
    }

    // the chapter echo srs strips from its own names is back on the sheet
    [Theory]
    [InlineData("1a", "1a Start")]
    [InlineData("2a", "2a Start")]
    [InlineData("3a", "3a Start")]
    [InlineData("4a", "4a Start")]
    [InlineData("8a", "8a Start")]
    public void StartRowsCarryTheChapterEcho(string chapter, string cp) {
        AssertRow(chapter, "Start", "A Sides", chapter, cp);
    }

    [Fact]
    public void EmojiRowsUseTheSheetSpelling() {
        AssertRow("3a", "Huge Mess Heart", "A Sides", "3a", "Huge Mess \U0001F499");
        AssertRow("4a", "Shrine Heart Clear", "A Sides", "4a", "Shrine \U0001F499 Clear");
        AssertRow("5a", "Depths Tape RTM", "A Sides", "5a", "Depths \U0001F4FC RTM");
        AssertRow("6a", "Hollows Tape RTM", "A Sides", "6a", "Hollows \U0001F4FC RTM");
    }

    // three rows the sheet has spelled differently before. srs's name and the
    // sheet's label are the same there: an edit putting an old spelling back
    // would export nowhere
    [Fact]
    public void RenamedRowsKeepTheSheetsSpelling() {
        AssertRow("5a", "Unravelling", "A Sides", "5a", "Unravelling");
        AssertRow("5b", "Through the Mirror", "B+C Sides", "5b", "Through the Mirror");
        AssertRow("Farewell", "Stubbornness", "Farewell", "", "Stubbornness");
    }

    [Fact]
    public void EachSideGoesToItsOwnTab() {
        AssertRow("5a", "Start", "A Sides", "5a", "5a Start");
        AssertRow("5b", "Mix Master", "B+C Sides", "5b", "Mix Master");
        AssertRow("6a", "Rock Bottom", "A Sides", "6a", "Rock Bottom");
        AssertRow("6b", "Rock Bottom", "B+C Sides", "6b", "Rock Bottom");
        AssertRow("5b", "IL", "B+C Sides", "5b", "5b Clear");
        AssertRow("1b", "IL", "B+C Sides", "1b", "1b Clear");
    }

    // the C-side band has no label column: the sheet matches the chapter twice
    [Fact]
    public void ACSideIsWrittenUnderItsChapter() {
        AssertRow("1c", "1c", "B+C Sides", "1c", "1c");
        AssertRow("8c", "8c", "B+C Sides", "8c", "8c");
    }

    [Fact]
    public void FarewellRowsHaveNoChapter() {
        AssertRow("Farewell", "Start", "Farewell", "", "Start");
        AssertRow("Farewell", "Determination DTS", "Farewell", "", "Determination DTS");
        AssertRow("Farewell", "Reconciliation", "Farewell", "", "Reconciliation");
    }

    private static void AssertRow(string scope, string name, string tab, string chapter, string cp) {
        Assert.Equal(new SheetRowRef(tab, chapter, cp), SheetRows.TargetOf(Row(scope, name)));
    }

    // Full Clear: the chapter cell as it stands, a chapter row on that cell
    // twice; 8A's row is typed on the A-side tab, and Farewell's two chapter
    // rows share one cell
    [Fact]
    public void AFullClearRowIsWrittenWhereTheEntryTabHasIt() {
        AssertRow("1a", "FC Heart to RTM", "ARB/Full Clear", "1afc", "Heart to RTM");
        AssertRow("1a", "FC Chasm-2", "ARB/Full Clear", "1afc", "Chasm-2");
        AssertRow("2a", "FC Start", "ARB/Full Clear", "2afc", "2a Start");
        AssertRow("7a", "FC Downdraft", "ARB/Full Clear", "7afc", "Downdraft");
        AssertRow("7a", "FC IL", "ARB/Full Clear", "7afc", "7afc");
        AssertRow("Farewell", "FC Moon Berry", "ARB/Full Clear", "Fw FC", "Moon Berry");
        AssertRow("Farewell", "FC DTS IL", "ARB/Full Clear", "Fw FC", "DTS");
        AssertRow("Farewell", "FC No DTS IL", "ARB/Full Clear", "Fw FC", "No DTS");
        AssertRow("8a", "HotM Horizontal Tape", "A Sides", "8a", "HotM Horizontal \U0001F4FC");
        AssertRow("7a", "1500m Gem Tape", "A Sides", "7a", "1500m \U0001F48E+\U0001F4FC");
    }
}
