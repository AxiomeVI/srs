using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the six tabs as they were last exported; refreshing them is how a
// change in the sheet becomes a failing test (see SheetConsistencyTests).
// With SRS_LIVE_SHEET set, each tab is downloaded from the default URL
// instead: `SRS_LIVE_SHEET=1 dotnet test` checks the sheet as it is today.
internal static class Fixtures {
    public static IReadOnlyDictionary<StandardsTab, string> ByTab { get; } =
        StandardsTabs.All.ToDictionary(tab => tab.Tab, tab => Load(tab));

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

    public const string LiveVariable = "SRS_LIVE_SHEET";

    private static string Load(StandardsTabInfo tab) =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LiveVariable))
            ? Read(tab.CacheFile)
            : Download(tab);

    private static string Download(StandardsTabInfo tab) {
        string url = SheetUrls.CsvUrlOf(StandardsTabs.DefaultUrl(tab.Tab));
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };
        using HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (response.Content.Headers.ContentType?.MediaType == "text/html"
            || body.TrimStart().StartsWith("<", StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"The {tab.LogName} tab came back as HTML, not CSV: the sheet is no longer shared by link.");
        }
        return body;
    }

    private static string Read(string name) =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
