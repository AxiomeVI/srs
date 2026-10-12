using System;
using System.Collections.Generic;
using System.Linq;

namespace Celeste.Mod.SpeedrunSheet;

/// What srs decides about a row's Timing cell: the value to set, the sheet's
/// time in the basis srs measures, and the updates that go out. Game-free.
internal static class SheetTiming {
    public const string FileTime = "FileTime";

    /// The value srs sets the row's Timing cell to, or null to leave it alone.
    public static string Choice(IReadOnlyList<string> options, SaveAndQuitNote note) {
        if (options == null) {
            return null;
        }

        bool with = options.Contains("S&Q", StringComparer.Ordinal);
        bool without = options.Contains("No S&Q", StringComparer.Ordinal);
        if (with && without) {
            return note switch {
                SaveAndQuitNote.With => "S&Q",
                SaveAndQuitNote.Without => "No S&Q",
                _ => null,
            };
        }

        if (with) {
            return null;
        }

        if (options.Contains("IL", StringComparer.Ordinal)) {
            return "IL";
        }

        return options.Contains("SRT", StringComparer.Ordinal) ? "SRT" : null;
    }

    /// The time a sheet cell holds, or null when it holds none srs can read:
    /// empty, 0:00.000 or unreadable. Unreadable says which of the two.
    public static (bool Unreadable, long? Ticks) ReadCell(string cell) {
        long? parsed = SheetData.TryParseTime(cell)?.Ticks;
        bool unreadable = parsed == null && !string.IsNullOrWhiteSpace(cell);
        return (unreadable, unreadable || parsed == 0 ? null : parsed);
    }

    /// A sheet offset in seconds as ticks, to the millisecond, the grain of the
    /// sheet. Null for one no sheet gives: not a number, negative, over a day.
    public static long? SecondsToTicks(double seconds) =>
        !double.IsFinite(seconds) || seconds < 0 || seconds > 86400 ? null
        : (long) Math.Round(seconds * 1000, MidpointRounding.AwayFromZero) * TimeSpan.TicksPerMillisecond;

    /// The sheet's time for the row in the basis srs measures. A time entered
    /// as FileTime is converted by the buffer; with no buffer, or one not
    /// smaller than the time, it is unreadable: never an improvement. An empty
    /// cell is no time whatever its Timing says, and always an improvement.
    public static (bool Unreadable, long? Ticks) Comparable(RemoteRow row) {
        if (row == null) {
            return (false, null);
        }

        (bool unreadable, long? ticks) = ReadCell(row.Time);
        if (unreadable) {
            return (true, null);
        }

        if (ticks == null || row.Timing != FileTime) {
            return (false, ticks);
        }

        if (row.FileTimeBuffer is not { } buffer || SecondsToTicks(buffer) is not { } offset) {
            return (true, null);
        }

        return offset < ticks.Value ? (false, ticks.Value - offset) : (true, null);
    }

    /// The updates an export sends, in the order it sends them. Results pair
    /// with this list, so it is the one list the sender, the log and what srs
    /// holds afterwards all use.
    public static List<ExportUpdate> Updates(IReadOnlyList<PendingUpdate> selected) {
        List<(PendingUpdate Source, ExportUpdate Update)> built = [];
        foreach (PendingUpdate u in selected) {
            // no time reaches a FileTime cell without its switch
            if (u.SheetIsFileTime && u.Timing == null) {
                continue;
            }

            built.Add((u, new ExportUpdate {
                Tab = u.Row.Tab,
                Band = u.Band,
                Chapter = u.Row.Chapter,
                Cp = u.Row.Cp,
                Time = TimeFormat.FromTicks(u.LocalTicks),
                // the raw cell, never a reformat: see PendingUpdate.RemoteCell
                Expect = u.RemoteCell,
                Timing = u.Timing,
                ExpectTiming = u.ExpectTiming,
                ExpectBuffer = u.ExpectBuffer,
            }));
        }

        // a sync group holds one choice: a later row that chose otherwise
        // keeps its time and sends no Timing, unless its cell is FileTime: that
        // time needs its switch
        Dictionary<string, string> fixedValue = [];
        foreach ((PendingUpdate source, ExportUpdate update) in built) {
            string group = source.Group;
            if (update.Timing == null || string.IsNullOrEmpty(group)) {
                continue;
            }

            if (!fixedValue.TryAdd(group, update.Timing) && fixedValue[group] != update.Timing
                && !source.SheetIsFileTime) {
                update.Timing = null;
                update.ExpectTiming = null;
            }
        }

        // a Timing write can move another row's buffer through the selector
        // sync, so rows judged through a buffer go before it
        return built.OrderBy(b => b.Update.ExpectBuffer == null).Select(b => b.Update).ToList();
    }
}
