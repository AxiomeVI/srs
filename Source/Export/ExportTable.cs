using System;
using System.Collections.Generic;
using System.Linq;

namespace Celeste.Mod.SpeedrunSheet;

/// What the export screen's status line says.
internal enum ScreenStatus { None, Loading, LoadFailed, Writing, WritingChecking, WritingFailed, SavedChecking, SavedFailed, FreshFailed }

/// The export screen's decisions, game-free so they are tested; ExportMenu draws.
internal static class ExportTable {
    private static string L(string key) => ExportProtocol.Localize(key);

    /// The first state that applies wins: nothing held, then an export still
    /// out, then one that went unanswered, then a saved copy, then a fresh
    /// answer whose last read failed.
    public static ScreenStatus StatusOf(HeldSource source, bool reading, string error, bool unansweredWrite,
        bool writing) {
        if (source == HeldSource.None) {
            return error != null && !reading ? ScreenStatus.LoadFailed : ScreenStatus.Loading;
        }

        if (writing) {
            return ScreenStatus.Writing;
        }

        if (unansweredWrite) {
            return reading || error == null ? ScreenStatus.WritingChecking : ScreenStatus.WritingFailed;
        }

        if (source == HeldSource.Saved) {
            return reading || error == null ? ScreenStatus.SavedChecking : ScreenStatus.SavedFailed;
        }

        return error != null && !reading ? ScreenStatus.FreshFailed : ScreenStatus.None;
    }

    /// Minutes under an hour, hours under two days, then days.
    public static (int Value, string UnitKey) AgeOf(TimeSpan age) {
        if (age < TimeSpan.FromHours(1)) {
            return ((int) age.TotalMinutes, "SRS_AGE_MINUTES");
        }

        return age < TimeSpan.FromDays(2)
            ? ((int) age.TotalHours, "SRS_AGE_HOURS")
            : ((int) age.TotalDays, "SRS_AGE_DAYS");
    }

    /// A table rebuilt on a newer answer: rows the player pressed keep their
    /// state, the others take the new answer's; a row not Writable is never
    /// ticked.
    public static void KeepPressed(List<PendingUpdate> rebuilt, IReadOnlyDictionary<SheetRowRef, bool> pressed) {
        foreach (PendingUpdate update in rebuilt) {
            if (update.Writable && pressed.TryGetValue(update.Row, out bool ticked)) {
                update.Selected = ticked;
            }
        }
    }

    // the berry tab's chapter cell ends with this
    private const string BerryCell = "a " + SheetRows.Berry;

    /// The header over a group of rows: the chapter cell, the tab where the
    /// sheet has none (Farewell). A berry chapter cell reads "1arb": its emoji
    /// is not in the font, and its rows sit under their chapter's own
    public static string GroupLabel(SheetRowRef row) =>
        string.IsNullOrEmpty(row.Chapter) ? row.Tab
        : row.Chapter.EndsWith(BerryCell, StringComparison.Ordinal) ? row.Chapter[..^BerryCell.Length] + "arb"
        : row.Chapter;

    /// The row's srs name when it is one srs writes: an IL variant's sheet
    /// label is an emoji and a suffix, and ActiveFont drops the emoji. Else the
    /// sheet's own labels, never translated. Most checkpoint labels already
    /// carry their chapter ("1a Start"), so prefixing it again reads "1a 1a Start".
    public static string RowLabel(string tab, string chapter, string cp) {
        SheetRowRef target = new(tab, chapter, cp);
        foreach (SheetRow row in SheetRows.All) {
            if (SheetRows.TargetOf(row) == target) {
                return TierLine.NameOf(row.Scope, row.Name);
            }
        }

        string group = string.IsNullOrEmpty(chapter) ? tab : chapter;
        return cp.StartsWith(group, StringComparison.Ordinal) ? cp : $"{group} {cp}";
    }

    // an unknown status is shown as the script sent it rather than swallowed
    public static string StatusText(string status) => status switch {
        "notFound" => L("SRS_EXPORT_STATUS_NOTFOUND"),
        "ambiguous" => L("SRS_EXPORT_STATUS_AMBIGUOUS"),
        "refused" => L("SRS_EXPORT_STATUS_REFUSED"),
        "changed" => L("SRS_EXPORT_STATUS_CHANGED"),
        _ => status,
    };

    /// Breaks a line at its spaces so that no part is wider than maxWidth: a
    /// SubHeader draws every line break but never wraps by itself, and a line
    /// wider than the table widens the menu. A single word too wide stays whole.
    public static string Wrap(string line, float maxWidth, Func<string, float> width) {
        if (width(line) <= maxWidth) {
            return line;
        }

        List<string> lines = [];
        string current = "";
        foreach (string word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            string longer = current == "" ? word : current + " " + word;
            if (current != "" && width(longer) > maxWidth) {
                lines.Add(current);
                current = word;
            } else {
                current = longer;
            }
        }

        lines.Add(current);
        return string.Join("\n", lines);
    }

    // the summary has one selectable item, its Close button, so it cannot
    // scroll, and past 930 px its title leaves the screen. Measured in game:
    // both lists full, every other row and a failure line stay under it
    internal const int MaxOtherRows = 6;
    internal const int MaxNamedRows = 10;

    /// One line for the rows written, one for those already on the sheet, whose
    /// reasons only go to the log, and one per other row with the script's
    /// reason, untranslated: srs does not author it.
    public static List<string> SummaryLines(IReadOnlyList<ExportResult> results) {
        List<string> lines = [];
        string Labels(string status) {
            List<string> labels = results.Where(r => r.Status == status)
                .Select(r => RowLabel(r.Tab, r.Chapter, r.Cp)).ToList();
            return string.Join(", ", labels.Take(MaxNamedRows))
                + (labels.Count > MaxNamedRows ? $", +{labels.Count - MaxNamedRows}" : "");
        }

        if (results.Any(r => r.Status == "written")) {
            lines.Add($"{L("SRS_EXPORT_SUMMARY_WRITTEN")} {Labels("written")}");
        }

        if (results.Any(r => r.Status == "unchanged")) {
            lines.Add($"{L("SRS_EXPORT_SUMMARY_UNCHANGED")} {Labels("unchanged")}");
        }

        List<ExportResult> others = results.Where(r => r.Status is not ("written" or "unchanged")).ToList();
        foreach (ExportResult r in others.Take(MaxOtherRows)) {
            lines.Add($"{RowLabel(r.Tab, r.Chapter, r.Cp)}: {StatusText(r.Status)}"
                + (string.IsNullOrEmpty(r.Reason) ? "" : $" ({r.Reason})"));
        }

        if (others.Count > MaxOtherRows) {
            lines.Add($"+{others.Count - MaxOtherRows} {L("SRS_EXPORT_SUMMARY_MORE")}");
        }

        if (lines.Count == 0) {
            lines.Add(L("SRS_EXPORT_DONE"));
        }

        return lines;
    }
}
