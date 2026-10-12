namespace Celeste.Mod.SpeedrunSheet;

/// One reviewable line of the export screen.
public sealed class PendingUpdate {
    public SheetRowRef Row { get; private init; }
    public string Label { get; private init; }
    public long LocalTicks { get; private init; }
    public string LocalText { get; private init; }
    public string RemoteText { get; private init; }
    /// the cell exactly as the script read it, kept alongside the parsed value:
    /// it is what the write compares against, and the sheet displays "1:36.9"
    /// where RemoteText reformats to "1:36.900"
    public string RemoteCell { get; private init; }
    public string DeltaText { get; private init; }
    public bool Selected { get; set; }
    /// the band the cell was read from, sent back so the script searches it alone
    public string Band { get; private init; }
    /// a key the sheet holds twice: listed, never written
    public bool Duplicate { get; private init; }
    /// which way the delta goes; null when there is nothing to compare or the
    /// times are equal at the millisecond
    public bool? Ahead { get; private init; }
    /// the value srs sets the Timing cell to; null leaves it alone
    public string Timing { get; private init; }
    /// what the Timing cell held when read; null when Timing is null
    public string ExpectTiming { get; private init; }
    /// the buffer the sheet time was judged through; null unless the cell holds a
    /// readable time entered as FileTime
    public double? ExpectBuffer { get; private init; }
    /// the sync group of the Timing cell, null when tied to nothing
    public string Group { get; private init; }
    /// the sheet's time converted to the basis srs measures (a FileTime cell), or
    /// the session time shifted into the basis the standard is graded in (1A
    /// chapter rows); empty otherwise
    public string AdjustedText { get; private init; } = "";
    /// the cell holds a time entered as FileTime
    public bool SheetIsFileTime { get; private init; }

    /// a duplicate, or a FileTime cell srs has no value to switch: listed, never
    /// ticked, never sent
    public bool Writable => !Duplicate && !(SheetIsFileTime && Timing == null);

    /// remote is the row as the script read it, never a parsed time. ⚠️ An
    /// unreadable cell is not an empty one: counted as empty it ticks the row
    /// and overwrites it, which a Google locale writing 8,704 does on every
    /// row. 0:00.000 counts as empty, the sheet's own idiom for "no time yet".
    public static PendingUpdate Create(SheetRowRef row, string label, long localTicks, RemoteRow remote,
        SaveAndQuitNote note, bool duplicate, long? rankOffsetTicks) {
        string localText = TimeFormat.FromTicks(localTicks);
        string band = remote?.Band ?? "";
        if (duplicate) {
            return new PendingUpdate {
                Row = row, Label = label, Band = band, Duplicate = true,
                LocalTicks = localTicks, LocalText = localText,
                RemoteText = "", RemoteCell = "", DeltaText = ExportProtocol.Localize("SRS_EXPORT_DUPLICATE"),
            };
        }

        string remoteCell = remote?.Time;
        (bool cellUnreadable, long? cellTicks) = SheetTiming.ReadCell(remoteCell);
        (bool unreadable, long? remoteTicks) = SheetTiming.Comparable(remote);
        bool fileTime = remote?.Timing == SheetTiming.FileTime;

        // compared as shown, to the millisecond, as the sheet and its script
        // compare: a finer time reads as behind the very cell it was written to
        long shown = SheetData.TryParseTime(localText)?.Ticks ?? localTicks;

        // an unreadable cell is never an improvement: we cannot tell, and the
        // safe default is to leave a time we do not understand alone
        bool improves = !unreadable && (remoteTicks == null || shown < remoteTicks.Value);

        string delta = "";
        if (unreadable) {
            delta = "?";
        } else if (remoteTicks != null) {
            delta = TimeFormat.Delta(shown - remoteTicks.Value);
        }

        string timing = SheetTiming.Choice(remote?.TimingOptions, note);
        bool writable = !(fileTime && timing == null);
        if (!writable) {
            delta = "?";
        }

        string adjusted = "";
        if (fileTime) {
            adjusted = remoteTicks == null ? (cellTicks == null ? "" : "?")
                : TimeFormat.FromTicks(remoteTicks.Value) + (timing == null ? "" : " " + timing);
        } else if (rankOffsetTicks is { } shift && shift != 0) {
            adjusted = TimeFormat.FromTicks(localTicks + shift);
        }

        return new PendingUpdate {
            Row = row,
            Label = label,
            Band = band,
            LocalTicks = localTicks,
            LocalText = localText,
            // show the cell as it stands rather than nothing: the player is the
            // only one who can tell a locale from a typo from a note
            RemoteText = cellUnreadable ? remoteCell.Trim()
                       : cellTicks == null ? "" : TimeFormat.FromTicks(cellTicks.Value),
            RemoteCell = remoteCell ?? "",
            DeltaText = delta,
            Ahead = unreadable || remoteTicks == null || shown == remoteTicks.Value ? null : shown < remoteTicks.Value,
            Selected = improves && writable,
            Timing = timing,
            ExpectTiming = timing == null ? null : remote.Timing,
            ExpectBuffer = fileTime && cellTicks != null ? remote.FileTimeBuffer : null,
            Group = remote?.TimingGroup,
            AdjustedText = adjusted,
            SheetIsFileTime = fileTime,
        };
    }
}
