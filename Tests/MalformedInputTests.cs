using System;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// SheetData.Parse promises never to throw on malformed content: it runs at
// game startup on whatever sits in the cache, so a broken or truncated CSV has
// to degrade into "no segments" instead of taking Celeste down with it
public class MalformedInputTests {
    private const string Header = "Chapter,Checkpoint,Hidden,WR,Gold\n";

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "\n\n")]
    [InlineData("no,header,here\n1,2,3", "")]
    [InlineData(",,,,\n,,,,", ",,,")]
    // a sheet that stopped being shared publicly answers 200 with a sign-in
    // page; the importer rejects it, but the parser must not choke on it either
    [InlineData("<!DOCTYPE html><html><body>Sign in</body></html>", null)]
    public void ReturnsNoSegmentsInsteadOfThrowing(string aSides, string bSides) {
        SheetData data = Fixtures.Parse(aSides, bSides);

        Assert.Equal(0, data.SegmentCount);
    }

    [Fact]
    public void SurvivesRowsCutShortOfTheHeader() {
        SheetData data = Fixtures.Parse(Header + "1a CP,1a Start,0:00.000", null);

        SheetSegment segment = Assert.Single(data.Block.Segments);
        // one time per declared tier column, the missing cells reading as null
        Assert.Equal(3, segment.Times.Count);
        Assert.Equal(TimeSpan.Zero, segment.Times[0]);
        Assert.Null(segment.Times[1]);
        Assert.Null(segment.Times[2]);
    }

    // TierComparison indexes a row's times by the merged header's columns
    [Fact]
    public void PadsTheRowsOfATabNarrowerThanTheHeader() {
        SheetData data = Fixtures.Parse(Header + "1a CP,1a Start,0:00.000,10,20", null,
            "Checkpoint,Hidden,WR\nSingular,0:00.000,30");

        SheetSegment singular = data.Block.Find("Farewell", "Singular");
        Assert.Equal(3, singular.Times.Count);
        Assert.Null(singular.Times[2]);
    }

    [Fact]
    public void TurnsBrokenFormulaCellsIntoNullThresholds() {
        SheetData data = Fixtures.Parse(Header + "1a CP,1a Start,0:00.000,#REF!,#REF!", null);

        SheetSegment segment = Assert.Single(data.Block.Segments);
        Assert.Null(segment.Times[1]);
        Assert.Null(segment.Times[2]);
    }

    // any tab may be missing (first run, half-written cache, a cache written
    // before Farewell was imported at all): whatever is there still has to load
    [Fact]
    public void ParsesTheASidesTabOnItsOwn() {
        SheetData data = Fixtures.Parse(Fixtures.ASides, null);

        Assert.NotEmpty(data.Block.Segments);
        Assert.DoesNotContain(data.Block.Segments, s => s.Chapter == "5b");
    }

    [Fact]
    public void ParsesTheBSidesTabOnItsOwn() {
        SheetData data = Fixtures.Parse(null, Fixtures.BSides);

        Assert.NotEmpty(data.Block.Segments);
        Assert.Contains(data.Block.Segments, s => s.Chapter == "5b");
        Assert.DoesNotContain(data.Block.Segments, s => s.Name == "Granny");
    }

    // the Farewell tab has no Chapter column of its own, so it is the one that
    // would silently import nothing if the implicit chapter stopped being
    // passed: every row would land outside the allowlist
    [Fact]
    public void ParsesTheFarewellTabOnItsOwn() {
        SheetData data = Fixtures.Parse(null, null, Fixtures.Farewell);

        Assert.All(data.Block.Segments, s => Assert.Equal("Farewell", s.Chapter));
        Assert.Contains(data.Block.Segments, s => s.Name == "Stubbornness");
    }

    // an old cache pairs with a freshly downloaded tab all the time — the two
    // A-side chapters must not go missing because Farewell arrived
    [Fact]
    public void KeepsTheOtherTabsWhenFarewellIsMissing() {
        SheetData data = Fixtures.Parse(Fixtures.ASides, Fixtures.BSides);

        Assert.Contains(data.Block.Segments, s => s.Name == "HotM Horizontal");
        Assert.DoesNotContain(data.Block.Segments, s => s.Chapter == "Farewell");
    }

    [Fact]
    public void HasNoBlockWhenThereIsNothingToParse() {
        SheetData data = Fixtures.Parse(null, null);

        Assert.Null(data.Block);
    }

    // the cache is read with File.ReadAllText (which strips the BOM) but the
    // download is decoded straight from the response, so a byte-order mark can
    // reach the parser
    [Fact]
    public void SurvivesAByteOrderMark() {
        SheetData data = Fixtures.Parse("\uFEFF" + Header + "1a CP,1a Start,0:00.000,13.906,14.5", null);

        Assert.Equal(1, data.SegmentCount);
    }

    [Fact]
    public void SkipsRowsThatAreNotOnTheImportList() {
        // rows the sheet has and srs does not import sit right next to the imported ones
        SheetData data = Fixtures.Parse(
            Header + "7a CP,7a Start,0:00.000,13.906,14.5\n,7a Start (old),0:00.000,0:00.000,\n,1500m 💎,0:00.000,52.445,55.5", null);

        SheetSegment segment = Assert.Single(data.Block.Segments);
        Assert.Equal("Start", segment.Name);
    }
}
