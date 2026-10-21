using System;
using System.Collections.Generic;
using System.Linq;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the six tabs as they were last exported; refreshing them is how a
// change in the sheet becomes a failing test (see SheetConsistencyTests)
internal static class Fixtures {
    public static IReadOnlyDictionary<StandardsTab, string> ByTab { get; } =
        StandardsTabs.All.ToDictionary(tab => tab.Tab, tab => Read(tab.CacheFile));

    public static string ASides => ByTab[StandardsTab.ASides];
    public static string BSides => ByTab[StandardsTab.BSides];
    public static string Farewell => ByTab[StandardsTab.Farewell];

    public static SheetData Parsed { get; } = SheetData.Parse(ByTab);

    /// The A Sides, B Sides and Farewell tabs, by position, for tests about them.
    public static SheetData Parse(string aSides, string bSides, string farewell = null) =>
        SheetData.Parse(new Dictionary<StandardsTab, string> {
            [StandardsTab.ASides] = aSides,
            [StandardsTab.BSides] = bSides,
            [StandardsTab.Farewell] = farewell,
        });

    public static List<SheetSegment> Imported => Parsed.Block.Segments;

    private static string Read(string name) =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
