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
}
