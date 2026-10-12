using System;
using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet;

/// What the tier rows say: the time and its tier, the gain on the player's
/// sheet time when it is a PB, and the delta to the next tier. Game-free, so
/// tested.
internal readonly record struct TierRows(string Time, string Tier, string Pb, string Gap);

internal static class TierLine {
    /// `sheetTicks` is the player's own time for the row, null when unknown.
    /// The time is ranked as Speed Run Tool shows it, to the millisecond, which
    /// is also the time the export writes and the sheet ranks. `ranked: false`
    /// shows the time alone: no tier, so no gap.
    public static TierRows Build(List<string> columns, List<TimeSpan?> thresholds, long ticks, long? sheetTicks,
        bool ranked = true) {
        string shownText = TimeFormat.FromTicks(ticks);
        TimeSpan shown = SheetData.TryParseTime(shownText) ?? TimeSpan.FromTicks(ticks);
        string tier = ranked ? SheetData.TierOf(columns, thresholds, shown) : "";

        string pb = sheetTicks is { } sheet && shown.Ticks < sheet ? $"PB {TimeFormat.Delta(shown.Ticks - sheet)}" : null;

        string gap = SheetData.NextTier(columns, thresholds, tier) is { } next
            ? $"{TimeFormat.Delta(shown.Ticks - next.Threshold.Ticks)} to {next.Column}"
            : null;
        return new TierRows(shownText, tier, pb, gap);
    }

    /// Whether the row's standard can rank the time srs measures. 1A's two
    /// chapter rows are graded in file time, and srs sums chapter time.
    public static bool Ranks(SegmentRule rule) => !(rule.SpansLeaves && rule.Scope == "1a");

    /// "1a Crossing", "6a Lake": the scope, unless the name is it ("1c",
    /// Farewell's "Farewell").
    public static string NameOf(string scope, string name) =>
        name == scope ? name : $"{scope} {name}";
}
