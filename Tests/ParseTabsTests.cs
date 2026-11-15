using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// Parse takes whatever tabs the cache or the download holds
public class ParseTabsTests {
    [Fact]
    public void ATabLeftOutIsSkipped() {
        SheetData data = SheetData.Parse(new Dictionary<StandardsTab, string> {
            [StandardsTab.BSides] = Fixtures.BSides,
        });

        Assert.NotNull(data.Block.Find("5b", "Central Chamber"));
        Assert.Null(data.Block.Find("1a", "Start"));
    }

    // the tabs merge in the table's order whatever the dictionary's
    [Fact]
    public void TheTabsMergeInTheTablesOrder() {
        SheetData data = SheetData.Parse(new Dictionary<StandardsTab, string>(Fixtures.ByTab.Reverse()));

        Assert.Equal(
            Fixtures.Parsed.Block.Segments.ConvertAll(s => (s.Chapter, s.Name)),
            data.Block.Segments.ConvertAll(s => (s.Chapter, s.Name)));
    }

    // a block with no Checkpoint column names each row after its chapter
    [Fact]
    public void ABlockWithoutCheckpointsImportsItsRows() {
        SheetData data = SheetData.Parse(new Dictionary<StandardsTab, string> {
            [StandardsTab.CSides] = "Chapter,Hidden,WR,Gold\n1c,0:00.000,17,23.5\n9c,0:00.000,1,2\n",
        });

        SheetSegment row = Assert.Single(data.Block.Segments);
        Assert.Equal(("1c", "1c"), (row.Chapter, row.Name));
        Assert.Equal(System.TimeSpan.FromSeconds(23.5), row.Times[2]);
    }

    private const string Header = "Chapter,Hidden,WR,Gold,Pink\n";

    // a column added to a later tab shifts nothing of its row
    [Fact]
    public void ATabWithAnExtraColumnInTheMiddleRanksItsRowAsBefore() {
        SheetData data = SheetData.Parse(new Dictionary<StandardsTab, string> {
            [StandardsTab.BSides] = Header + "1b,0,17,20,30\n",
            [StandardsTab.CSides] = "Chapter,Hidden,WR,Extra,Gold,Pink\n1c,0,17,99,23.5,40\n",
        });

        SheetSegment row = data.Block.Find("1c", "1c");
        Assert.Equal(System.TimeSpan.FromSeconds(23.5), row.Times[2]);
        Assert.Equal(System.TimeSpan.FromSeconds(40), row.Times[3]);
    }

    // a tab without a column the block has gets null under it
    [Fact]
    public void ATabMissingAColumnGetsNullThere() {
        SheetData data = SheetData.Parse(new Dictionary<StandardsTab, string> {
            [StandardsTab.BSides] = Header + "1b,0,17,20,30\n",
            [StandardsTab.CSides] = "Chapter,Hidden,WR,Pink\n1c,0,17,40\n",
        });

        SheetSegment row = data.Block.Find("1c", "1c");
        Assert.Equal(["Hidden", "WR", "Gold", "Pink"], data.Block.Columns);
        Assert.Null(row.Times[2]);
        Assert.Equal(System.TimeSpan.FromSeconds(40), row.Times[3]);
    }

    // the differences are recorded for the log
    [Fact]
    public void ADifferingTabIsReported() {
        SheetData data = SheetData.Parse(new Dictionary<StandardsTab, string> {
            [StandardsTab.BSides] = Header + "1b,0,17,20,30\n",
            [StandardsTab.CSides] = "Chapter,Hidden,WR,Extra,Pink\n1c,0,17,99,40\n",
        });

        (StandardsTab tab, string[] missing, string[] extra) = Assert.Single(data.ColumnDifferences);
        Assert.Equal(StandardsTab.CSides, tab);
        Assert.Equal(["Gold"], missing);
        Assert.Equal(["Extra"], extra);
    }

    // a blank header cell between two tiers moves no time
    [Fact]
    public void ABlankHeaderCellBetweenTiersMovesNothing() {
        SheetData data = SheetData.Parse(new Dictionary<StandardsTab, string> {
            [StandardsTab.CSides] = "Chapter,Hidden,WR,,Gold,Pink\n1c,0,17,99,23.5,40\n",
        });

        SheetSegment row = data.Block.Find("1c", "1c");
        Assert.Equal(["Hidden", "WR", "Gold", "Pink"], data.Block.Columns);
        Assert.Equal(System.TimeSpan.FromSeconds(23.5), row.Times[2]);
        Assert.Equal(System.TimeSpan.FromSeconds(40), row.Times[3]);
    }

    [Fact]
    public void TheFixturesReportNoColumnDifference() {
        Assert.Empty(Fixtures.Parsed.ColumnDifferences);
    }
}
