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
    /// is also the time the export writes and the sheet ranks. The tier and
    /// the gap are taken on that time plus `rankOffsetTicks`; the time and the PB
    /// are not. A null `rankOffsetTicks` gives the time alone, no tier, no gap.
    public static TierRows Build(List<string> columns, List<TimeSpan?> thresholds, long ticks, long? sheetTicks,
        long? rankOffsetTicks) {
        string shownText = TimeFormat.FromTicks(ticks);
        TimeSpan shown = SheetData.TryParseTime(shownText) ?? TimeSpan.FromTicks(ticks);

        string pb = sheetTicks is { } sheet && shown.Ticks < sheet ? $"PB {TimeFormat.Delta(shown.Ticks - sheet)}" : null;
        if (rankOffsetTicks is not { } offset) {
            return new TierRows(shownText, "", pb, null);
        }

        TimeSpan ranked = shown + TimeSpan.FromTicks(offset);
        string tier = SheetData.TierOf(columns, thresholds, ranked);
        string gap = SheetData.NextTier(columns, thresholds, tier) is { } next
            ? $"{TimeFormat.Delta(ranked.Ticks - next.Threshold.Ticks)} to {next.Column}"
            : null;
        return new TierRows(shownText, tier, pb, gap);
    }

    /// What Build adds to the time before ranking it: nothing for a standard
    /// in the basis srs measures, the row's own offset for one in file time,
    /// and null when the sheet gave none, so the row cannot be ranked.
    public static long? RankOffsetTicks(SegmentRule rule, RemoteRow remote) =>
        !StandardInFileTime(rule) ? 0
        : remote?.RankOffset is { } seconds ? SheetTiming.SecondsToTicks(seconds)
        : null;

    /// Whether the row's standard is graded in file time, a basis shifted from
    /// the chapter time srs sums: 1A's two chapter rows.
    public static bool StandardInFileTime(SegmentRule rule) => rule.SpansLeaves && rule.Scope == "1a";

    /// "1a Crossing", "6a Lake": the scope, unless the name is it ("1c",
    /// Farewell's "Farewell"). The berry and full clear rows read as players
    /// write them: "1arb Start", "1afc Start".
    public static string NameOf(string scope, string name) =>
        name == scope ? name
        : Leads(name, "ARB") ? scope + "rb" + name[3..]
        : Leads(name, "FC") ? scope + "fc" + name[2..]
        : $"{scope} {name}";

    private static bool Leads(string name, string word) =>
        name == word || name.StartsWith(word + " ", StringComparison.Ordinal);
}
