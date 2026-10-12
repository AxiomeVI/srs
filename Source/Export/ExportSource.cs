using System;
using System.Collections.Generic;
using System.Linq;

namespace Celeste.Mod.SpeedrunSheet;

/// Turns this session's bests into the export screen's rows: the side being
/// played first, then the sheet's order, a side's berry rows right under that
/// side's own. Not SpeedrunTool's PbTimes, which are cut on the player's setting
/// and describe no sheet segment. Mapped through srs's own row table, so a
/// session with no standards still exports.
internal static class ExportSource {
    public static List<PendingUpdate> Collect(IEnumerable<RunBook.Run> runs, string playing = null) {
        List<((int Side, int Row) Order, PendingUpdate Update)> found = [];
        foreach (RunBook.Run run in runs) {
            if (!SheetRows.TryFind(run.Scope, run.Name, out SheetRow sheetRow)) {
                continue;
            }

            SheetRowRef row = SheetRows.TargetOf(sheetRow);
            // the whole row, not a parsed time: PendingUpdate has to tell an
            // empty cell from one it cannot read, and only the cell says which
            RemoteBests.TryGet(row, out RemoteRow remote);
            int side = sheetRow.Scope == playing ? -1
                : Array.FindIndex(SheetRows.All, other => other.Scope == sheetRow.Scope);
            found.Add(((side, Array.IndexOf(SheetRows.All, sheetRow)), PendingUpdate.Create(row, DisplayName(run),
                run.Ticks, remote, run.Note, RemoteBests.IsDuplicate(row), RankOffsetOf(run, remote))));
        }

        found.Sort((a, b) => a.Order.CompareTo(b.Order));
        return found.ConvertAll(entry => entry.Update);
    }

    private static long? RankOffsetOf(RunBook.Run run, RemoteRow remote) =>
        TierLine.RankOffsetTicks(
            SegmentRules.All.First(rule => rule.Scope == run.Scope && rule.Name == run.Name), remote);

    /// srs names a berry row "ARB Start" and a Full Clear one "FC Start". On
    /// screen the group header says it, and dropping it collides with nothing
    /// within a group.
    private static string DisplayName(RunBook.Run run) {
        foreach (string prefix in GroupPrefixes) {
            if (run.Name.StartsWith(prefix, StringComparison.Ordinal)) {
                return run.Name[prefix.Length..];
            }
        }

        return run.Name;
    }

    private static readonly string[] GroupPrefixes = ["ARB ", "FC "];
}
