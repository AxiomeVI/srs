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
    /// milliseconds on a clock that never goes back
    public Func<long> Now { get; init; } = () => Environment.TickCount64;
    public Func<TimeSpan, Task> Delay { get; init; } = Task.Delay;
}

/// Why a write was not taken; None when it was.
internal enum WriteRefusal { None, NothingHeld, WriteOut, Unanswered, OutOfDate, Moved }

/// The URL a POST was sent to, so its answer lands only on that sheet.
internal readonly record struct WriteToken(string Url);

/// The one reader of the player's sheet, and the only writer of what srs holds
/// of it (RemoteBests and the saved copy).
///
/// ⚠️ Every decision whether an answer counts, every write of the held state
/// and every handoff of the fetch happens under `gate`; the network and the
/// delay never do. A check made outside it can pass, then lose to a URL
/// change or a write landing before the publish.
internal static class SheetReader {
    private sealed class Flight {
        public string Url;
        public int Writes;
        public string Why;
        // its fetch started once the script behind an unanswered export could
        // no longer be running. Set where the fetch starts, never where it lands
        public bool AskedPastLimit;
        public readonly TaskCompletionSource<ReadOutcome> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// A POST srs gave up on. The script may run on, and a read made
    /// meanwhile shows its cache or a batch half written: no cell says it ended.
    private sealed class Settling {
        public string Url;
        // on host.Now(): the script cannot run past it. Moved to the moment
        // the delay ends, whose clock need not agree with the host's
        public long Limit;
    }

    // how long the script host lets one request run
    private static readonly TimeSpan ScriptLimit = TimeSpan.FromSeconds(360);

    /// From a request's timeout to the latest moment its script can still be running.
    internal static readonly TimeSpan SettleAfter = ScriptLimit - ExportBatches.WriteTimeout;

    private static readonly object gate = new();
    private static ReaderHost host;
    private static Flight inFlight;
    // one read asked for while another was out that cannot answer for it;
    // shared by every caller until it starts
    private static Flight next;
    // bumped when a POST is sent and when it answers: the script serves the
    // old cells until its write ends, so a read asked before a POST or during
    // it is dropped if it lands after. One asked and landed during it is taken
    // in, and EndWrite applies the written cells over it
    private static int writes;
    // POSTs sent and not yet answered: Export stays off while one is out, even
    // on a screen opened after it was sent
    private static int posting;
    private static bool unansweredWrite;
    // set with unansweredWrite when its request timed out, null whenever
    // unansweredWrite is false: only a read asked past its limit clears both
    private static Settling settling;
    // a read found a script of another protocol version, and none was taken in since
    private static bool scriptOutOfDate;
    // what that read said, for the settings; volatile: read each frame of a menu, without the gate
    private static volatile string outOfDateError;
    // the sheet the held rows came from. The URL is set before Forget runs:
    // in between, what is held describes another sheet
    private static string heldUrl;

    /// After an accepted read, a write's end and Forget: what a waiting sender
    /// waits on. Handlers schedule, never work inline: Forget runs on the game thread.
    public static event Action Changed;

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
            settling = null;
            scriptOutOfDate = false;
            outOfDateError = null;
            heldUrl = null;
            RemoteBests.Reset();
        }

        Changed = null;
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

    /// The script speaks another version: it would not write what srs sends
    /// as srs means it, whatever copy of its rows is still held.
    public static bool ScriptOutOfDate {
        get {
            lock (gate) {
                return scriptOutOfDate;
            }
        }
    }

    /// The out-of-date read's message, null once an answer was taken in.
    public static string OutOfDateError => outOfDateError;

    /// A POST went unanswered and no read since says what it wrote. After a
    /// timeout it stands until a read asked once the script cannot be running.
    public static bool UnansweredWrite {
        get {
            lock (gate) {
                return unansweredWrite;
            }
        }
    }

    /// The task of the read that answers for the sheet as it is now. A read in
    /// flight is joined only if it was asked for this URL since the latest
    /// write, and not before the limit of a script that may still be writing
    /// when that limit has passed; otherwise this waits for the next one, which
    /// starts when the current one lands.
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
                bool tooEarly = !inFlight.AskedPastLimit && PastLimit(current);
                if (inFlight.Url == current && inFlight.Writes == writes && !tooEarly) {
                    return inFlight.Done.Task;
                }

                next ??= new Flight();
                next.Why = why;
                return next.Done.Task;
            }

            start = inFlight = new Flight { Url = current, Writes = writes, Why = why, AskedPastLimit = PastLimit(current) };
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
                    heldUrl = flight.Url;
                    // the rows are taken in either way: only the flag waits
                    if (settling == null || settling.Url != flight.Url || flight.AskedPastLimit) {
                        unansweredWrite = false;
                        settling = null;
                    }

                    scriptOutOfDate = false;
                    outOfDateError = null;
                    SaveCopyLocked(flight.Url);
                } else if (dropped == null) {
                    // only an answer taken in clears it: an unreachable sheet
                    // says nothing of the script
                    if (outcome.Kind == ReadKind.OutOfDate) {
                        scriptOutOfDate = true;
                        outOfDateError = outcome.Error;
                    }
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
                    start.AskedPastLimit = PastLimit(current);
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

        if (dropped == null && outcome.Kind == ReadKind.Accepted) {
            RaiseChanged();
        }
    }

    // under the gate: a read of url asked now is asked after the script ended
    private static bool PastLimit(string url) =>
        settling != null && settling.Url == url && host.Now() >= settling.Limit;

    // outside the gate, like the delay itself
    private static void SettleLater(Settling state) {
        host.Info($"an export timed out: its rows are held back for up to {(int) SettleAfter.TotalSeconds} s");
        host.Delay(SettleAfter).ContinueWith(_ => Settled(state), TaskScheduler.Default);
    }

    private static void Settled(Settling state) {
        lock (gate) {
            // cleared, forgotten or replaced since: nothing to ask. The URL is
            // set before Forget runs
            if (settling != state || state.Url != host.Url()) {
                return;
            }

            state.Limit = host.Now();
        }

        Refresh("an unanswered export settled");
    }

    // outside the gate: a handler is not ours to hold it for
    private static void RaiseChanged() {
        Action handlers = Changed;
        if (handlers == null) {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>()) {
            try {
                handler();
            } catch (Exception e) {
                host.Warn("a handler of Changed threw: " + e.GetType().Name);
            }
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

    /// The check and the take in one step. accepts is RemoteBests.Accepts as read
    /// before the updates were built: a send is never built on replaced cells.
    public static WriteRefusal TryBeginWrite(int accepts, out WriteToken token) {
        token = default;
        lock (gate) {
            string current = host.Url();
            WriteRefusal refusal = !RemoteBests.IsResolved || heldUrl != current ? WriteRefusal.NothingHeld
                : posting > 0 ? WriteRefusal.WriteOut
                : unansweredWrite ? WriteRefusal.Unanswered
                : scriptOutOfDate ? WriteRefusal.OutOfDate
                : RemoteBests.Accepts != accepts ? WriteRefusal.Moved
                : WriteRefusal.None;
            if (refusal == WriteRefusal.None) {
                writes++;
                posting++;
                token = new WriteToken(current);
            }

            return refusal;
        }
    }

    /// On every outcome of an export; answered pairs with the first updates
    /// sent and stops where a request did not answer or could not be read.
    /// Applied with the mod off too: the sheet was written either way.
    /// timedOut: the request left unanswered was given up on, so its script
    /// may still be writing.
    public static void EndWrite(WriteToken token, IReadOnlyList<ExportUpdate> sent, IReadOnlyList<ExportResult> answered,
        bool timedOut = false) {
        Settling opened = null;
        lock (gate) {
            writes++;
            posting--;
            if (token.Url == host.Url()) {
                if (answered.Count != sent.Count) {
                    unansweredWrite = true;
                    settling = opened = !timedOut ? null : new Settling {
                        Url = token.Url,
                        Limit = host.Now() + (long) SettleAfter.TotalMilliseconds,
                    };
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
        if (opened != null) {
            SettleLater(opened);
        }

        RaiseChanged();
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
            // what was sent to the previous sheet says nothing of this one
            SendLedger.Shared.Reset();
            heldUrl = null;
            unansweredWrite = false;
            settling = null;
            scriptOutOfDate = false;
            outOfDateError = null;
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

        RaiseChanged();
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
                    heldUrl = url;
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
