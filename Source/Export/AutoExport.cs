using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Celeste.Mod.SpeedrunSheet;

/// What AutoExport needs from the game, passed in so that it compiles into the
/// test project.
internal sealed class AutoHost {
    /// the master switch
    public Func<bool> Enabled { get; init; }
    /// SrsSettings.AutoExport
    public Func<bool> Setting { get; init; }
    public Func<string> Url { get; init; }
    public Func<bool> ScreenOpen { get; init; }
    public Action<string> Info { get; init; }
    /// Task.Run in the mod, inline in a test
    public Action<Action> Run { get; init; }
}

/// Sends what beats the sheet with no screen. ⚠️ Offer, TakeFailed and
/// TakeWritten are the only members the game thread calls during play: none
/// locks, logs or waits.
internal static class AutoExport {
    private sealed record Offered(RunBook.Run[] Runs, string Playing);

    private static volatile AutoHost host;
    // the session's bests and the side played, in one assignment
    private static volatile Offered offered;
    // set by Offer alone: a read is asked, and an unanswered request retried,
    // once per improved row
    private static int askPending;
    private static int retryPending;
    // 0 idle, 1 evaluating, 2 evaluating and asked again
    private static int state;
    private static int failed;
    // rows of sends that left no row behind. One with a row not written counts
    // in failed alone; one with a changed row, which goes again by itself, nowhere
    private static int written;
    // the two below belong to the evaluation, which never runs twice at once
    private static string lastCause;
    private static readonly HashSet<SheetRowRef> absentLogged = [];

    /// May be called again: Wake is never subscribed twice.
    public static void Install(AutoHost value) {
        host = value;
        SheetReader.Changed -= Wake;
        SheetReader.Changed += Wake;
    }

    /// Game thread, at a row's end. ⚠️ Nothing of the host but Run may be
    /// called from here: the settings, the URL and the screen are the worker's.
    public static void Offer(RunBook.Run[] runs, string playing) {
        offered = new Offered(runs, playing);
        Volatile.Write(ref askPending, 1);
        Volatile.Write(ref retryPending, 1);
        Schedule();
    }

    /// SheetReader.Changed, and the export screen closing.
    public static void Wake() {
        if (offered != null) {
            Schedule();
        }
    }

    /// The setting switched off.
    public static void Drop() => offered = null;

    /// Game thread, each frame: the rows not written since last asked, else 0.
    public static int TakeFailed() =>
        Volatile.Read(ref failed) == 0 ? 0 : Interlocked.Exchange(ref failed, 0);

    /// Game thread, each frame: the rows written since last asked, else 0.
    public static int TakeWritten() =>
        Volatile.Read(ref written) == 0 ? 0 : Interlocked.Exchange(ref written, 0);

    /// For the tests: nothing offered, nothing pending, idle.
    internal static void Reset() {
        offered = null;
        Volatile.Write(ref askPending, 0);
        Volatile.Write(ref retryPending, 0);
        Volatile.Write(ref state, 0);
        Volatile.Write(ref failed, 0);
        Volatile.Write(ref written, 0);
        lastCause = null;
        absentLogged.Clear();
    }

    // ⚠️ Never a plain write of 2: Loop may have just left, and a 2 written
    // over its 0 would stop every later trigger for the session.
    private static void Schedule() {
        AutoHost current = host;
        if (current == null) {
            return;
        }

        while (true) {
            int seen = Volatile.Read(ref state);
            if (seen == 0) {
                if (Interlocked.CompareExchange(ref state, 1, 0) == 0) {
                    try {
                        current.Run(Loop);
                    } catch (Exception) {
                        // a 1 left with no loop running would stop every later trigger
                        Volatile.Write(ref state, 0);
                    }

                    return;
                }
            } else if (Interlocked.CompareExchange(ref state, 2, seen) == seen) {
                return;
            }
        }
    }

    // a trigger during Evaluate sets 2, the exchange fails, and it runs again
    private static void Loop() {
        do {
            Volatile.Write(ref state, 1);
            AutoHost current = host;
            try {
                Evaluate(current);
            } catch (Exception e) {
                // the type alone: a message could carry the URL
                try {
                    current.Info("an automatic export could not be evaluated: " + e.GetType().Name);
                } catch (Exception) {
                    // a throw here would leave state at 1 for the session
                }
            }
        } while (Interlocked.CompareExchange(ref state, 0, 1) != 1);
    }

    private static void Evaluate(AutoHost current) {
        Offered now = offered;
        if (now == null || !current.Enabled() || !current.Setting() || string.IsNullOrWhiteSpace(current.Url())
            || current.ScreenOpen()) {
            return;
        }

        // read before the rows are built: TryBeginWrite refuses if it moved since
        int accepts = RemoteBests.Accepts;
        if (!RemoteBests.IsResolved || SheetReader.UnansweredWrite) {
            // once per improved row, never from Changed: that would spin while offline
            if (Interlocked.Exchange(ref askPending, 0) == 1) {
                SheetReader.Refresh("auto-export");
            }

            Cause(current, "waiting for a read of the sheet");
            return;
        }

        // the last read failed: what is held may be behind, and a row refused as
        // changed would wait on it. Asked, not waited for: the held copy still sends
        if (RemoteBests.Error != null && Interlocked.Exchange(ref askPending, 0) == 1) {
            SheetReader.Refresh("auto-export");
        }

        List<PendingUpdate> rows = ExportSource.Collect(now.Runs, now.Playing);
        LogAbsent(current, rows);
        bool retry = Interlocked.Exchange(ref retryPending, 0) == 1;
        List<ExportUpdate> sent = SendLedger.Shared.Pick(rows, retry);
        if (sent.Count == 0) {
            Cause(current, null);
            return;
        }

        string url = current.Url();
        WriteRefusal refusal = ExportSend.TryStart(sent, accepts, "automatic",
            () => current.Enabled() && current.Setting() && current.Url() == url,
            _ => { },
            outcome => {
                if (outcome.NotWritten > 0) {
                    Interlocked.Add(ref failed, outcome.NotWritten);
                } else if (outcome.Written > 0 && outcome.Results.All(r => r.Status != "changed")) {
                    Interlocked.Add(ref written, outcome.Written);
                }
            });
        if (refusal != WriteRefusal.None) {
            // put back: the pick was not sent, and the next evaluation may retry
            if (retry) {
                Volatile.Write(ref retryPending, 1);
            }

            // rows are held, so nothing else would ask: a redeployed script
            // is only seen by a read
            if (refusal == WriteRefusal.OutOfDate && Interlocked.Exchange(ref askPending, 0) == 1) {
                SheetReader.Refresh("auto-export");
            }

            Cause(current, "not sent: " + refusal);
            return;
        }

        // no wake is needed after a send: EndWrite raises Changed
        Cause(current, null);
    }

    // once per change of cause; the URL never enters a line
    private static void Cause(AutoHost current, string text) {
        if (text == lastCause) {
            return;
        }

        lastCause = text;
        if (text != null) {
            current.Info(text);
        }
    }

    // a row srs lists and the sheet lacks: the screen sends it, where the
    // player sees the refusal. Once per row, ever
    private static void LogAbsent(AutoHost current, List<PendingUpdate> rows) {
        foreach (PendingUpdate row in rows) {
            if (row.Selected && !row.OnSheet && absentLogged.Add(row.Row)) {
                current.Info("not on the sheet, never sent by itself: "
                    + ExportTable.RowLabel(row.Row.Tab, row.Row.Chapter, row.Row.Cp));
            }
        }
    }
}
