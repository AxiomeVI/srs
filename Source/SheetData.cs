using System;
using System.Collections.Generic;
using System.Globalization;

namespace Celeste.Mod.SpeedrunSheet;

// parsed practice sheet: one block of segments merged from the Standards tabs
public class SheetData {
    /// The one block Parse builds, the segments of every tab merged under
    /// one header; null when nothing parsed.
    public SheetBlock Block { get; private set; }

    /// The SheetRows keys none of the parsed tabs had: how a row the sheet
    /// renamed gets noticed. Covers only what was given: the rows of a tab left
    /// out of the parse are all here.
    public readonly List<(string SheetChapter, string Label)> MissingRows = [];

    public int SegmentCount => Block?.Segments.Count ?? 0;

    /// The tier a time reaches: the first column, in sheet order, whose
    /// threshold the time is strictly under, and "Unranked" past every one.
    /// The WR column is a reference, not a tier: Gold is the best one, and
    /// some WR cells are slower than their Gold. A zero threshold (Hidden) and
    /// an empty cell never match.
    internal static string TierOf(List<string> columns, List<TimeSpan?> thresholds, TimeSpan time) {
        for (int i = 0; i < thresholds.Count && i < columns.Count; i++) {
            if (Ranks(columns[i], thresholds[i]) && time < thresholds[i]) {
                return columns[i];
            }
        }

        return Unranked;
    }

    internal const string Unranked = "Unranked";

    /// The tier the smallest gain reaches from `tier`, with its threshold: the
    /// slowest threshold among the earlier tiers. For Unranked, the slowest of
    /// all. Null when nothing is faster, as from Gold.
    internal static (string Column, TimeSpan Threshold)? NextTier(
        List<string> columns, List<TimeSpan?> thresholds, string tier) {
        int count = Math.Min(columns.Count, thresholds.Count);
        int before = tier == Unranked ? count : columns.IndexOf(tier);
        (string Column, TimeSpan Threshold)? next = null;
        for (int i = 0; i < before; i++) {
            if (Ranks(columns[i], thresholds[i])
                && (next == null || thresholds[i] > next.Value.Threshold)) {
                next = (columns[i], thresholds[i]!.Value);
            }
        }

        return next;
    }

    private static bool Ranks(string column, TimeSpan? threshold) =>
        column != "WR" && column != Unranked && threshold > TimeSpan.Zero;

    // never throws on malformed content: an unparseable cell is a null time; a
    // row outside any block or off the SheetRows allowlist is skipped, and so
    // is a tab missing or blank. The tabs merge into one block in
    // StandardsTabs' order, under the header of the first tab that has one
    public static SheetData Parse(IReadOnlyDictionary<StandardsTab, string> csvByTab) {
        SheetData data = new();
        SheetBlock merged = null;
        HashSet<(StandardsTab, string, string)> imported = [];

        foreach (StandardsTabInfo tab in StandardsTabs.All) {
            if (!csvByTab.TryGetValue(tab.Tab, out string csv) || string.IsNullOrWhiteSpace(csv)) {
                continue;
            }

            foreach (SheetBlock raw in ParseBlocks(csv, tab.ImplicitChapter)) {
                if (merged == null) {
                    merged = new SheetBlock(raw.TierStart, hasCheckpoints: true);
                    merged.Columns.AddRange(raw.Columns);
                    data.Block = merged;
                }

                foreach (SheetSegment segment in raw.Segments) {
                    if (SheetRows.TryRead(tab.Tab, segment.Chapter, segment.Name, out SheetRow row)) {
                        imported.Add((tab.Tab, segment.Chapter, segment.Name));
                        merged.Segments.Add(new SheetSegment(row.Scope, row.Name,
                            Realigned(segment.Times, merged.Columns.Count)));
                    }
                }
            }
        }

        foreach (SheetRow row in SheetRows.All) {
            if (!imported.Contains((row.Tab, row.SheetChapter, row.Label))) {
                data.MissingRows.Add((row.SheetChapter, row.Label));
            }
        }

        return data;
    }

    // one segment's times, padded or cut to the merged block's columns: Times
    // must stay indexable by Columns, whatever the width of the row's own tab
    private static List<TimeSpan?> Realigned(List<TimeSpan?> times, int columns) {
        List<TimeSpan?> aligned = new(columns);
        for (int i = 0; i < columns; i++) {
            aligned.Add(i < times.Count ? times[i] : null);
        }

        return aligned;
    }

    // the raw pass: the CSV's blocks of segments, one per header row, under the
    // sheet's own names (internal for the allowlist tests). implicitChapter is
    // for the Farewell tab, which has no Chapter column
    internal static List<SheetBlock> ParseBlocks(string csvText, string implicitChapter) {
        List<SheetBlock> blocks = [];
        SheetBlock currentBlock = null;
        string currentChapter = null;

        foreach (string[] row in Csv.Parse(csvText)) {
            if (IsEmpty(row)) {
                continue;
            }

            // a header row introduces a new block: first cell is the block title
            // ("Chapter", "Chapter Times (CP)"), then an optional "Checkpoint"
            // column, then the tier column labels
            int tierStart = TierStart(row);
            if (tierStart > 0) {
                currentBlock = new SheetBlock(tierStart, row[1].Trim() == "Checkpoint");
                currentChapter = null;
                for (int i = tierStart; i < row.Length; i++) {
                    string label = row[i].Trim();
                    if (label.Length > 0) {
                        currentBlock.Columns.Add(label);
                    }
                }

                blocks.Add(currentBlock);
                continue;
            }

            if (currentBlock == null) {
                continue;
            }

            // the chapter cell is only filled on the first checkpoint of a
            // chapter (merged cells export as empty cells below), so carry it
            if (row[0].Trim().Length > 0) {
                currentChapter = row[0].Trim();
            }

            string name = currentBlock.HasCheckpoints && row.Length > 1 ? row[1].Trim() : currentChapter;
            if (string.IsNullOrEmpty(name) || currentChapter == null) {
                continue;
            }

            SheetSegment segment = new(implicitChapter ?? currentChapter, name);
            for (int i = currentBlock.TierStart; i < currentBlock.TierStart + currentBlock.Columns.Count; i++) {
                segment.Times.Add(i < row.Length ? TryParseTime(row[i]) : null);
            }

            currentBlock.Segments.Add(segment);
        }

        return blocks;
    }

    // header rows are marked by the fixed first tier columns "Hidden","WR",
    // sitting at index 1 (chapter-only layout) or 2 (chapter+checkpoint layout);
    // returns the index of "Hidden", or 0 if the row is not a header
    private static int TierStart(string[] row) {
        for (int i = 1; i <= 2; i++) {
            if (row.Length > i + 1 && row[i].Trim() == "Hidden" && row[i + 1].Trim() == "WR") {
                return i;
            }
        }

        return 0;
    }

    private static bool IsEmpty(string[] row) {
        foreach (string cell in row) {
            if (cell.Trim().Length > 0) {
                return false;
            }
        }

        return true;
    }

    private const double SecondsPerDay = 24 * 60 * 60;

    // accepts the sheet's mixed formats: "28", "28.1", "00:56", "1:05.5", "24:06.802"
    public static TimeSpan? TryParseTime(string cell) {
        // no cell at all, which the export asks about, parses like an empty one
        if (string.IsNullOrWhiteSpace(cell)) {
            return null;
        }

        string text = cell.Trim();

        string[] parts = text.Split(':');
        if (parts.Length > 3) {
            return null;
        }

        // past the leading field, each one counts minutes or seconds and stays
        // under 60 ("1:75" is a typo, not 2:15); only the seconds carry a
        // fraction. NaN and Infinity parse whatever the style
        double totalSeconds = 0;
        for (int i = 0; i < parts.Length; i++) {
            bool last = i == parts.Length - 1;
            NumberStyles style = last ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
            if (!double.TryParse(parts[i], style, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value) || value < 0 || (i > 0 && value >= 60)) {
                return null;
            }

            totalSeconds = totalSeconds * 60 + value;
        }

        // a number this large overflows the ticks, and no cell of a day or more
        // is a segment time
        if (totalSeconds >= SecondsPerDay) {
            return null;
        }

        // via ticks: TimeSpan.FromSeconds rounds to milliseconds with double
        // imprecision (90.576 s would become 1:30.575)
        return TimeSpan.FromTicks((long)Math.Round(totalSeconds * TimeSpan.TicksPerSecond));
    }
}

public class SheetBlock(int tierStart, bool hasCheckpoints) {
    // column index of the first tier ("Hidden"); segment times start there too
    public readonly int TierStart = tierStart;
    // true when segments are individual checkpoints grouped under a chapter,
    // false when each segment is a whole chapter ("Chapter Times" blocks)
    public readonly bool HasCheckpoints = hasCheckpoints;
    public readonly List<string> Columns = [];
    public readonly List<SheetSegment> Segments = [];

    /// The segment of that chapter and name, or null. Callers address segments
    /// by name, never by reference: SheetImporter.Data is reassigned from a
    /// worker, and the new instances are not equal to the old ones.
    public SheetSegment Find(string chapter, string name) {
        foreach (SheetSegment segment in Segments) {
            if (segment.Chapter == chapter && segment.Name == name) {
                return segment;
            }
        }

        return null;
    }
}

public class SheetSegment(string chapter, string name, List<TimeSpan?> times = null) {
    // the scope in the merged block, the sheet's chapter cell in a raw one
    public readonly string Chapter = chapter;
    public readonly string Name = name;
    // aligned with the owning block's Columns; null = empty or unparseable cell
    public readonly List<TimeSpan?> Times = times ?? [];
}

// minimal RFC 4180 parser: quoted fields, "" escapes, \r\n or \n line ends
internal static class Csv {
    public static List<string[]> Parse(string text) {
        List<string[]> rows = [];
        List<string> fields = [];
        System.Text.StringBuilder field = new();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++) {
            char c = text[i];
            if (inQuotes) {
                if (c == '"') {
                    if (i + 1 < text.Length && text[i + 1] == '"') {
                        field.Append('"');
                        i++;
                    } else {
                        inQuotes = false;
                    }
                } else {
                    field.Append(c);
                }
            } else if (c == '"') {
                inQuotes = true;
            } else if (c == ',') {
                fields.Add(field.ToString());
                field.Clear();
            } else if (c == '\n' || c == '\r') {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') {
                    i++;
                }

                fields.Add(field.ToString());
                field.Clear();
                rows.Add(fields.ToArray());
                fields.Clear();
            } else {
                field.Append(c);
            }
        }

        if (field.Length > 0 || fields.Count > 0) {
            fields.Add(field.ToString());
            rows.Add(fields.ToArray());
        }

        return rows;
    }
}
