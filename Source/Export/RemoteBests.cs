using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Celeste.Mod.SpeedrunSheet;

/// Where the times held came from: nothing, the copy saved last session, or
/// the sheet itself this session.
public enum HeldSource { None, Saved, Fresh }

/// The player's sheet as srs last read it. Deliberately NOT registered with
/// SpeedrunTool.SaveLoad: a savestate load must not wipe downloaded data.
/// No file and no log here: SheetReader does both, and this file stays game-free.
public static class RemoteBests {
    private readonly record struct Key(string Tab, string Chapter, string Cp);

    /// Everything a reader needs, published in one assignment, and every writer
    /// publishes by compare-and-swap: separate fields could be seen out of
    /// order, and a plain write could undo a concurrent one. AcceptedAt is on
    /// the monotonic clock, SavedAtUtc on the wall clock: a fresh answer's age
    /// never crosses a session, a saved copy's always does.
    private sealed record Snapshot(
        HeldSource Source,
        IReadOnlyList<RemoteRow> Rows,
        Dictionary<Key, RemoteRow> Index,
        HashSet<Key> Duplicates,
        DateTime SavedAtUtc,
        long AcceptedAt,
        string Error,
        int Accepts);

    private static volatile Snapshot current = Empty(0);

    public static HeldSource Source => current.Source;
    public static bool IsResolved => current.Source != HeldSource.None;

    /// Why the last read failed; null since the last answer taken in.
    public static string Error => current.Error;

    /// Counts the answers taken in and the cells written, so a screen can tell its table is older.
    public static int Accepts => current.Accepts;

    /// Every row as the sheet sent it, duplicates included: what the copy saves.
    public static IReadOnlyList<RemoteRow> Rows => current.Rows;
    public static DateTime SavedAtUtc => current.SavedAtUtc;

    public static TimeSpan Age {
        get {
            Snapshot seen = current;
            return seen.Source switch {
                HeldSource.Fresh => TimeSpan.FromMilliseconds(Environment.TickCount64 - seen.AcceptedAt),
                HeldSource.Saved => AgeSince(seen.SavedAtUtc, DateTime.UtcNow),
                _ => TimeSpan.MaxValue,
            };
        }
    }

    /// A clock that went back shows the copy as just saved.
    public static TimeSpan AgeSince(DateTime savedAtUtc, DateTime nowUtc) =>
        nowUtc > savedAtUtc ? nowUtc - savedAtUtc : TimeSpan.Zero;

    public static void Reset() => Publish(seen => Empty(seen.Accepts));

    /// Takes the sheet's answer in. Returns the rows whose key it carries more
    /// than once, for the caller to log: those are never exported.
    public static List<RemoteRow> AcceptFresh(IReadOnlyList<RemoteRow> rows) {
        (Dictionary<Key, RemoteRow> index, HashSet<Key> duplicates) = Build(rows);
        Publish(seen => new Snapshot(HeldSource.Fresh, rows, index, duplicates,
            DateTime.UtcNow, Environment.TickCount64, null, seen.Accepts + 1));
        return rows.Where(row => duplicates.Contains(KeyOf(row))).ToList();
    }

    /// The copy saved last session, taken only when nothing is held: it never
    /// replaces an answer.
    public static bool TryAcceptSaved(IReadOnlyList<RemoteRow> rows, DateTime savedAtUtc) {
        (Dictionary<Key, RemoteRow> index, HashSet<Key> duplicates) = Build(rows);
        Snapshot seen;
        do {
            seen = current;
            if (seen.Source != HeldSource.None) {
                return false;
            }
        } while (Interlocked.CompareExchange(ref current,
                     new Snapshot(HeldSource.Saved, rows, index, duplicates, savedAtUtc, 0, null, seen.Accepts + 1),
                     seen) != seen);

        return true;
    }

    /// Keeps what is held.
    public static void Fail(string error) => Publish(seen => seen with { Error = error });

    /// The cells the sheet just wrote, as srs sent them: the script writes the
    /// string into a text cell, so it displays exactly that. Source and save
    /// date stay: the rest of what is held is as old as it was. Builds new
    /// rows, never edits held ones, which a save may be serialising. Moves
    /// Accepts: a table reopened during the write was built on the old cells.
    public static void ApplyWritten(IReadOnlyList<(SheetRowRef Row, string Time, string Timing)> written) {
        if (written.Count == 0) {
            return;
        }

        Dictionary<Key, (string Time, string Timing)> changes = [];
        foreach ((SheetRowRef row, string time, string timing) in written) {
            changes[KeyOf(row)] = (time, timing);
        }

        Publish(seen => {
            List<RemoteRow> rows = seen.Rows.Select(row => {
                if (!changes.TryGetValue(KeyOf(row), out var change) || seen.Duplicates.Contains(KeyOf(row))) {
                    return row;
                }

                // a Timing write makes the cell readable, so its buffer no
                // longer applies. Rows the sync moved keep what was read: the
                // read after every export brings the new values
                return new RemoteRow {
                    Tab = row.Tab, Band = row.Band, Chapter = row.Chapter, Cp = row.Cp,
                    Time = change.Time ?? row.Time,
                    Timing = change.Timing ?? row.Timing,
                    TimingOptions = row.TimingOptions,
                    TimingGroup = row.TimingGroup,
                    FileTimeBuffer = change.Timing != null ? null : row.FileTimeBuffer,
                    RankOffset = row.RankOffset,
                };
            }).ToList();
            return seen with { Rows = rows, Index = Build(rows).Index, Accepts = seen.Accepts + 1 };
        });
    }

    public static bool TryGet(SheetRowRef row, out RemoteRow value) {
        Snapshot seen = current;
        Key key = KeyOf(row);
        if (seen.Duplicates.Contains(key)) {
            value = null;
            return false;
        }

        return seen.Index.TryGetValue(key, out value);
    }

    public static bool IsDuplicate(SheetRowRef row) => current.Duplicates.Contains(KeyOf(row));

    /// Whether an answer holds any row srs writes. A sheet whose band headers
    /// broke answers with none, and taking that in would empty a good copy.
    public static bool HoldsAnyOf(IReadOnlyList<RemoteRow> rows, IEnumerable<SheetRowRef> targets) {
        HashSet<Key> keys = rows.Select(KeyOf).ToHashSet();
        return targets.Any(target => keys.Contains(KeyOf(target)));
    }

    private static void Publish(Func<Snapshot, Snapshot> change) {
        Snapshot seen;
        do {
            seen = current;
        } while (Interlocked.CompareExchange(ref current, change(seen), seen) != seen);
    }

    private static Snapshot Empty(int accepts) => new(HeldSource.None, [], [], [], default, 0, null, accepts);

    private static (Dictionary<Key, RemoteRow> Index, HashSet<Key> Duplicates) Build(IReadOnlyList<RemoteRow> rows) {
        Dictionary<Key, RemoteRow> index = [];
        HashSet<Key> duplicates = [];
        foreach (RemoteRow row in rows) {
            Key key = KeyOf(row);
            if (!index.TryAdd(key, row)) {
                duplicates.Add(key);
            }
        }

        return (index, duplicates);
    }

    private static Key KeyOf(RemoteRow row) => new(Normalize(row.Tab), Normalize(row.Chapter), Normalize(row.Cp));
    private static Key KeyOf(SheetRowRef row) => new(Normalize(row.Tab), Normalize(row.Chapter), Normalize(row.Cp));

    /// Same rule as the Apps Script's norm(): NFC, collapsed whitespace, and the
    /// U+FE0F variation selector dropped. Emoji are NOT stripped: they are what
    /// tells "7a Start" from "7a Start \U0001F48E".
    private static string Normalize(string value) {
        if (string.IsNullOrEmpty(value)) {
            return "";
        }

        string withoutSelectors = value.Replace("\uFE0F", "");
        string collapsed = Regex.Replace(withoutSelectors.Normalize(NormalizationForm.FormC), @"\s+", " ");
        return collapsed.Trim().ToLowerInvariant();
    }
}
