using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod.SpeedrunSheet;

/// What ExportSend needs from the game, passed in so that it compiles into the
/// test project.
internal sealed class SendHost {
    /// url, json. timedOut: the request was given up on, not refused
    public Func<string, string, Task<(string body, string error, bool timedOut)>> Post { get; init; }
    public Action<string> Info { get; init; }
    public Action<string> Warn { get; init; }
}

/// Results pairs with the first of Sent. Stopped: the mod, the setting or the
/// URL changed between two requests.
internal readonly record struct SendOutcome(IReadOnlyList<ExportUpdate> Sent,
    IReadOnlyList<ExportResult> Results, string Error, bool Stopped) {
    /// The rows a player would call not written. A changed row goes again by itself.
    public int NotWritten => Stopped ? 0
        : Sent.Count - Results.Count(r => r.Status is "written" or "unchanged" or "changed");

    /// The rows whose cell now holds the sent time, and did not before.
    public int Written => Stopped ? 0 : Results.Count(r => r.Status == "written");
}

/// The one sender of updates to the sheet, for the export screen and for
/// auto-export. Game-free.
internal static class ExportSend {
    private static SendHost host;

    public static void Install(SendHost value) => host = value;

    /// Takes the write and starts the requests, in one step. None: the
    /// requests are out and done will be called once, off the game thread.
    ///
    /// ⚠️ Nothing may sit between the take and the requests: a throw there
    /// would leave the write taken for good. Finish alone ends it.
    public static WriteRefusal TryStart(IReadOnlyList<ExportUpdate> sent, int accepts, string label,
        Func<bool> goOn, Action<int> progress, Action<SendOutcome> done) {
        // before the take: with no host, Finish could not end the write
        SendHost sender = host ?? throw new InvalidOperationException("ExportSend.Install was never called");
        WriteRefusal refusal = SheetReader.TryBeginWrite(accepts, out WriteToken token);
        if (refusal != WriteRefusal.None) {
            return refusal;
        }

        try {
            sender.Info($"exporting {sent.Count} row(s) ({label})");
        } catch (Exception) {
            // a log line is not worth a write left taken
        }

        // what EndWrite is told was sent: a send stopped before a request left
        // nothing unanswered
        int posted = 0;
        // a send stops at its first failed request, so this is about the one
        // left unanswered
        int timedOut = 0;
        _ = ExportBatches.Send(sent,
                json => sender.Post(token.Url, json).ContinueWith(task => {
                    (string body, string error, bool gaveUp) = task.Result;
                    if (gaveUp) {
                        Volatile.Write(ref timedOut, 1);
                    }

                    return (body, error);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default),
                goOn, progress,
                batch => {
                    SendLedger.Shared.Posted(token.Url, batch);
                    Interlocked.Add(ref posted, batch.Count);
                })
            .ContinueWith(task => Finish(sender, token, sent, Volatile.Read(ref posted),
                    Volatile.Read(ref timedOut) == 1, task, done),
                TaskScheduler.Default);
        return WriteRefusal.None;
    }

    // runs on a worker; only done may reach a screen, and through its own queue
    private static void Finish(SendHost sender, WriteToken token, IReadOnlyList<ExportUpdate> sent, int posted,
        bool timedOut, Task<(List<ExportResult> Results, int? Ms, string Error, bool Stopped)> task, Action<SendOutcome> done) {
        SendOutcome outcome = new(sent, [], null, false);
        // nothing above this continuation observes a throw: done would never
        // be called. A throw from a log line keeps the answers already read
        try {
            (List<ExportResult> results, int? ms, string error, bool stopped) = task.Result;
            outcome = new SendOutcome(sent, results, error, stopped);
            for (int i = 0; i < results.Count; i++) {
                ExportResult r = results[i];
                // the script answers its updates in order: what was sent for the row, so the log says it
                ExportUpdate u = i < sent.Count ? sent[i] : null;
                sender.Info($"  {ExportTable.RowLabel(r.Tab, r.Chapter, r.Cp)}: {r.Status}"
                    + (string.IsNullOrEmpty(r.Band) ? "" : $" [{r.Band}]")
                    + (u == null ? "" : $" {u.Time.Trim()}" + (u.Timing == null ? "" : $", timing {u.Timing}"))
                    + (string.IsNullOrEmpty(r.Reason) ? "" : $" ({r.Reason})"));
            }

            // the measurement the batch size is checked against
            sender.Info($"the script took {ms?.ToString() ?? "?"} ms for {results.Count} of {sent.Count} update(s)");
            if (error != null) {
                sender.Warn("export failed: " + error);
            }
        } catch (Exception e) {
            sender.Warn("an export answer could not be shown: " + e);
            outcome = outcome with {
                Error = outcome.Error ?? $"{ExportProtocol.Localize("SRS_EXPORT_ERR_UNREADABLE")} {e.GetType().Name}",
            };
        }

        try {
            SendLedger.Shared.Answered(token.Url, sent, outcome.Results);
        } catch (Exception e) {
            sender.Warn("an export's answers could not be recorded in the ledger: " + e);
        }

        // on every outcome, failed or orphaned too: the sheet may have been
        // written whatever is shown, and EndWrite asks it again
        try {
            SheetReader.EndWrite(token, sent.Take(posted).ToList(), outcome.Results, timedOut);
        } catch (Exception e) {
            sender.Warn("an export's outcome could not be recorded: " + e);
        }

        try {
            done(outcome);
        } catch (Exception e) {
            sender.Warn("an export's outcome could not be handled: " + e);
        }
    }
}
