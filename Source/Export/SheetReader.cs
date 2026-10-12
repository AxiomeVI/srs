using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Celeste.Mod.SpeedrunSheet;

internal enum ReadKind { Accepted, Unreachable, NotTheScript, Refused, OutOfDate, NoRows, Cancelled }

/// What a read came to. Error is what to show the player when it failed.
internal readonly record struct ReadOutcome(ReadKind Kind, string Error = null);

/// What SheetReader needs from the game, passed in so that it compiles into
/// the test project.
internal sealed class ReaderHost {
    public Func<string, Task<(string body, string error)>> Fetch { get; init; }
    public Action<string> Info { get; init; }
    public Action<string> Warn { get; init; }
    public Func<bool> Enabled { get; init; }
    public Func<string> Url { get; init; }
    public string CopyPath { get; init; }
}

/// The URL a POST was sent to, so its answer lands only on that sheet.
internal readonly record struct WriteToken(string Url);

/// The one reader of the player's sheet, and the only writer of what srs holds
/// of it (RemoteBests and the saved copy).
///
/// ⚠️ Every decision whether an answer counts, every write of the held state
/// and every handoff of the fetch happens under `gate`; the network never
/// does. A check made outside it can pass, then lose to a URL change or a
/// write landing before the publish.
internal static class SheetReader {
    private sealed class Flight {
        public string Url;
        public int Writes;
        public string Why;
        public readonly TaskCompletionSource<ReadOutcome> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly object gate = new();
    private static ReaderHost host;
    private static Flight inFlight;
    // one read asked for while another was out that cannot answer for it;
    // shared by every caller until it starts
    private static Flight next;
    // bumped when a POST is sent and when it answers: the script serves the
    // old cells until its write ends, so a read started in between is dropped
    private static int writes;
    // POSTs sent and not yet answered: Export stays off while one is out, even
    // on a screen opened after it was sent
    private static int posting;
    private static bool unansweredWrite;

    public static void Install(ReaderHost value) {
        lock (gate) {
            host = value;
        }
    }

    /// For the tests: nothing in flight, nothing held.
    internal static void Reset() {
        lock (gate) {
            inFlight = null;
            next = null;
            writes = 0;
            posting = 0;
            unansweredWrite = false;
            RemoteBests.Reset();
        }
    }

    public static bool IsReading {
        get {
            lock (gate) {
                return inFlight != null;
            }
        }
    }

    /// A POST was sent and has not answered yet.
    public static bool IsWriting {
        get {
            lock (gate) {
                return posting > 0;
            }
        }
    }

    /// A POST went unanswered and no read since says what it wrote.
    public static bool UnansweredWrite {
        get {
            lock (gate) {
                return unansweredWrite;
            }
        }
    }

    /// The task of the read that answers for the sheet as it is now. A read in
    /// flight is joined only if it was asked for this URL since the latest
    /// write; otherwise this waits for the next one, which starts when the
    /// current one lands.
    public static Task<ReadOutcome> Refresh(string why) {
        Flight start;
        lock (gate) {
            if (host == null) {
                return Task.FromResult(new ReadOutcome(ReadKind.Cancelled));
            }

            // the URL changes on the game thread without the gate: read it once
            string current = host.Url();
            if (!host.Enabled() || string.IsNullOrWhiteSpace(current)) {
                return Task.FromResult(new ReadOutcome(ReadKind.Cancelled));
            }

            if (inFlight != null) {
                if (inFlight.Url == current && inFlight.Writes == writes) {
                    return inFlight.Done.Task;
                }

                next ??= new Flight();
                next.Why = why;
                return next.Done.Task;
            }

            start = inFlight = new Flight { Url = current, Writes = writes, Why = why };
        }

        Launch(start);
        return start.Done.Task;
    }

    private static void Launch(Flight flight) {
        host.Info("reading the sheet: " + flight.Why);
        Task<(string body, string error)> fetch;
        try {
            fetch = host.Fetch(flight.Url);
        } catch (Exception e) {
            fetch = Task.FromException<(string body, string error)>(e);
        }

        fetch.ContinueWith(task => Land(flight, task), TaskScheduler.Default);
    }

    private static void Land(Flight flight, Task<(string body, string error)> task) {
        List<RemoteRow> rows = null;
        string timing = null;
        ReadOutcome outcome;
        try {
            (string body, string error) = task.Result;
            outcome = Parse(body, error, out rows, out timing);
        } catch (Exception e) {
            host.Warn("a read could not be taken in: " + e.GetType().Name);
            outcome = new ReadOutcome(ReadKind.NotTheScript, ExportProtocol.Localize("SRS_EXPORT_UNREAD"));
        }

        Flight start = null;
        string dropped = null;
        List<RemoteRow> duplicates = null;
        lock (gate) {
            string current = null;
            try {
                current = host.Url();
                dropped = !host.Enabled() ? "the mod was switched off"
                    : flight.Url != current ? "the sheet URL changed"
                    : flight.Writes != writes ? "an export was sent since it was asked"
                    : null;
                if (dropped == null && outcome.Kind == ReadKind.Accepted) {
                    duplicates = RemoteBests.AcceptFresh(rows);
                    unansweredWrite = false;
                    SaveCopyLocked(flight.Url);
                } else if (dropped == null) {
                    RemoteBests.Fail(outcome.Error);
                }
            } finally {
                // never skipped: a read left in flight would join every later
                // caller to an answer that never comes
                inFlight = null;
                if (next != null && host.Enabled() && !string.IsNullOrWhiteSpace(current)) {
                    start = next;
                    next = null;
                    start.Url = current;
                    start.Writes = writes;
                    inFlight = start;
                } else if (next != null) {
                    next.Done.TrySetResult(new ReadOutcome(ReadKind.Cancelled));
                    next = null;
                }

                if (dropped == null) {
                    flight.Done.TrySetResult(outcome);
                } else if (start != null) {
                    Flight following = start;
                    following.Done.Task.ContinueWith(t => flight.Done.TrySetResult(t.Result), TaskScheduler.Default);
                } else {
                    flight.Done.TrySetResult(new ReadOutcome(ReadKind.Cancelled));
                }
            }
        }

        if (dropped != null) {
            host.Info($"a read of the sheet was dropped: {dropped}");
        } else if (outcome.Kind == ReadKind.Accepted) {
            host.Info($"sheet answered: {rows.Count} rows, {timing}");
            foreach (RemoteRow row in duplicates) {
                host.Warn($"in two places on the sheet, never exported: {row.Tab} / {row.Chapter} / {row.Cp} (band {row.Band})");
            }
        } else {
            string failed = $"a read of the sheet failed, keeping what is held: {outcome.Kind} {outcome.Error}";
            if (outcome.Kind == ReadKind.NoRows) {
                host.Warn(failed);
            } else {
                host.Info(failed);
            }
        }

        if (start != null) {
            Launch(start);
        }
    }

    private static ReadOutcome Parse(string body, string error, out List<RemoteRow> rows, out string timing) {
        rows = null;
        timing = null;
        if (error != null) {
            return new ReadOutcome(ReadKind.Unreachable, error);
        }

        if (!ExportProtocol.TryParseRows(body, out rows, out timing, out string parseError, out RowsFailure failure)) {
            ReadKind kind = failure switch {
                RowsFailure.OutOfDate => ReadKind.OutOfDate,
                RowsFailure.Refused => ReadKind.Refused,
                _ => ReadKind.NotTheScript,
            };
            return new ReadOutcome(kind, parseError);
        }

        if (!RemoteBests.HoldsAnyOf(rows, SheetRows.All.Select(SheetRows.TargetOf))) {
            return new ReadOutcome(ReadKind.NoRows, ExportProtocol.Localize("SRS_EXPORT_ERR_NO_ROWS"));
        }

        return new ReadOutcome(ReadKind.Accepted);
    }

    /// Before a POST is sent.
    public static WriteToken BeginWrite() {
        lock (gate) {
            writes++;
            posting++;
            return new WriteToken(host.Url());
        }
    }

    /// On every outcome of an export; answered pairs with the first updates
    /// sent and stops where a request did not answer or could not be read.
    /// Applied with the mod off too: the sheet was written either way.
    public static void EndWrite(WriteToken token, IReadOnlyList<ExportUpdate> sent, IReadOnlyList<ExportResult> answered) {
        lock (gate) {
            writes++;
            posting--;
            if (token.Url == host.Url()) {
                if (answered.Count != sent.Count) {
                    unansweredWrite = true;
                }

                List<(SheetRowRef Row, string Time, string Timing)> written =
                    Written(sent.Take(answered.Count).ToList(), answered);
                if (written.Count > 0) {
                    RemoteBests.ApplyWritten(written);
                    SaveCopyLocked(token.Url);
                }
            }
        }

        Refresh("an export answered");
    }

    /// The script answers its updates in order, one result each; an answer that
    /// does not pair up is trusted for nothing.
    internal static List<(SheetRowRef Row, string Time, string Timing)> Written(IReadOnlyList<ExportUpdate> sent,
        IReadOnlyList<ExportResult> results) {
        List<(SheetRowRef Row, string Time, string Timing)> written = [];
        if (results.Count != sent.Count) {
            return written;
        }

        for (int i = 0; i < sent.Count; i++) {
            string time = results[i].Status == "written" ? sent[i].Time.Trim() : null;
            string timing = results[i].Timing is "set" or "kept" ? sent[i].Timing : null;
            if (time != null || timing != null) {
                written.Add((new SheetRowRef(sent[i].Tab, sent[i].Chapter, sent[i].Cp), time, timing));
            }
        }

        return written;
    }

    /// After the URL was set to another or forgotten: nothing held is about it.
    public static void Forget() {
        lock (gate) {
            RemoteBests.Reset();
            unansweredWrite = false;
            next?.Done.TrySetResult(new ReadOutcome(ReadKind.Cancelled));
            next = null;
            try {
                if (File.Exists(host.CopyPath)) {
                    File.Delete(host.CopyPath);
                }
            } catch (Exception e) {
                // harmless: the fingerprint keeps it from loading as this sheet's
                host.Warn("could not delete the saved sheet times: " + e.GetType().Name);
            }
        }
    }

    /// Once, at load, before the launch read: the times of the last session.
    public static void LoadCopy() {
        lock (gate) {
            string url = host.Url();
            string path = host.CopyPath;
            try {
                if (!File.Exists(path)) {
                    return;
                }

                if (string.IsNullOrWhiteSpace(url)) {
                    File.Delete(path);
                    host.Info("saved sheet times deleted: no sheet URL is set");
                    return;
                }

                if (!SheetCopy.TryParse(File.ReadAllText(path), url, out List<RemoteRow> rows,
                        out DateTime savedAt, out string why)) {
                    host.Info("saved sheet times not used: " + why);
                    if (why == SheetCopy.AnotherSheet) {
                        File.Delete(path);
                    }

                    return;
                }

                if (RemoteBests.TryAcceptSaved(rows, savedAt)) {
                    host.Info($"saved sheet times loaded: {rows.Count} rows, saved {savedAt:u}");
                }
            } catch (Exception e) {
                host.Warn("could not read the saved sheet times: " + e.GetType().Name);
            }
        }
    }

    // url is the sheet the held rows came from, never a fresh read of the
    // current one: the URL can change between the publish and the save.
    // The snapshot is the one current inside the lock, never one handed in:
    // a save racing a newer publish would otherwise write the older one last
    private static void SaveCopyLocked(string url) {
        if (string.IsNullOrWhiteSpace(url) || !RemoteBests.IsResolved) {
            return;
        }

        try {
            string json = SheetCopy.Serialize(url, RemoteBests.SavedAtUtc, RemoteBests.Rows);
            if (!AtomicFile.TryWrite(host.CopyPath, json, out Exception error, out _)) {
                host.Warn("could not save the sheet's times: " + error.GetType().Name);
            }
        } catch (Exception e) {
            host.Warn("could not save the sheet's times: " + e.GetType().Name);
        }
    }
}
