using System;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

public class StandardsTabsTests {
    // Of indexes All by the enum's value, and Parse merges in All's order
    [Fact]
    public void TheTableListsEveryTabOnceInTheEnumsOrder() {
        Assert.Equal(Enum.GetValues<StandardsTab>(), StandardsTabs.All.Select(tab => tab.Tab));
        Assert.All(Enum.GetValues<StandardsTab>(), tab => Assert.Equal(tab, StandardsTabs.Of(tab).Tab));
    }

    [Fact]
    public void NoTwoTabsShareACacheFileAGidOrALogName() {
        Assert.Distinct(StandardsTabs.All.Select(tab => tab.CacheFile));
        Assert.Distinct(StandardsTabs.All.Select(tab => tab.Gid));
        Assert.Distinct(StandardsTabs.All.Select(tab => tab.LogName));
    }

    // a stored address is migrated by its workbook id, so a default must be
    // built on the same prefix the migration targets
    [Fact]
    public void ADefaultAddressIsTheReferenceWorkbooksTab() {
        Assert.All(StandardsTabs.All, tab =>
            Assert.EndsWith("gid=" + tab.Gid, SheetUrls.CsvUrlOf(StandardsTabs.DefaultUrl(tab.Tab))));
    }
}
