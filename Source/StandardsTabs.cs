namespace Celeste.Mod.SpeedrunSheet;

/// The Standards tab a row is read from; each is written to an entry tab.
public enum StandardsTab {
    ASides,
    BSides,
    CSides,
    Farewell,
    Arb,
    Fc,
}

/// One Standards tab. ImplicitChapter is set when the tab has no Chapter column.
internal readonly record struct StandardsTabInfo(
    StandardsTab Tab, string LogName, string Gid, string CacheFile, string EntryTab,
    string ImplicitChapter = null);

/// The tabs srs reads, described once. Game-free.
internal static class StandardsTabs {
    // in the enum's order: Of indexes by it, and SheetData.Parse merges the
    // tabs in this order
    internal static readonly StandardsTabInfo[] All = [
        new(StandardsTab.ASides, "A Sides", "1796170425", "asides.csv", SheetLabels.TabASides),
        new(StandardsTab.BSides, "B Sides", "1885706573", "bsides.csv", SheetLabels.TabBCSides),
        new(StandardsTab.CSides, "C Sides", "1027544212", "csides.csv", SheetLabels.TabBCSides),
        new(StandardsTab.Farewell, "Farewell", "1826331297", "farewell.csv", SheetLabels.TabFarewell,
            ImplicitChapter: "Farewell"),
        new(StandardsTab.Arb, "ARB", "1460343170", "arb.csv", SheetLabels.TabArbFullClear),
        new(StandardsTab.Fc, "FC", "1408391498", "fc.csv", SheetLabels.TabArbFullClear),
    ];

    internal static StandardsTabInfo Of(StandardsTab tab) => All[(int)tab];

    /// The tab's edit URL in the reference workbook, the default of its setting.
    internal static string DefaultUrl(StandardsTab tab) => SheetUrls.EditUrlPrefix + Of(tab).Gid;
}
