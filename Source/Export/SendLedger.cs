using System.Collections.Generic;
using System.Linq;

namespace Celeste.Mod.SpeedrunSheet;

/// What every send this session posted and what came back. Auto-export reads
/// it so that a time written is never written back, a refusal is not repeated
/// until the request changes, and an unanswered request waits for a new PB.
/// Game-free. Every member takes the one lock: workers and POST continuations
/// both call it.
internal sealed class SendLedger {
    public static readonly SendLedger Shared = new();

    internal readonly record struct Request(SheetRowRef Row, string Time, string Expect,
        string Timing, string ExpectTiming, double? ExpectBuffer) {
        public static Request Of(ExportUpdate u) => new(new SheetRowRef(u.Tab, u.Chapter, u.Cp),
            u.Time, u.Expect, u.Timing, u.ExpectTiming, u.ExpectBuffer);
    }

    private readonly object gate = new();
    private string url;
    private readonly HashSet<(SheetRowRef Row, string Time)> written = [];
    private readonly HashSet<Request> refused = [];
    private readonly HashSet<Request> unanswered = [];

    /// One request of a send, as it is posted. Another URL empties the ledger.
    public void Posted(string url, IReadOnlyList<ExportUpdate> batch) {
        lock (gate) {
            if (this.url != url) {
                ClearSets();
                this.url = url;
            }

            foreach (ExportUpdate u in batch) {
                unanswered.Add(Request.Of(u));
            }
        }
    }

    /// The answers of a send, paired with the first updates sent. More answers
    /// than updates pair with nothing: the ledger keeps what it holds. Answers
    /// from another URL than the one held say nothing of this sheet.
    public void Answered(string url, IReadOnlyList<ExportUpdate> sent, IReadOnlyList<ExportResult> results) {
        if (results.Count > sent.Count) {
            return;
        }

        lock (gate) {
            if (this.url != url) {
                return;
            }

            for (int i = 0; i < results.Count; i++) {
                Request request = Request.Of(sent[i]);
                unanswered.Remove(request);
                string status = results[i].Status;
                if (status is "written" or "unchanged") {
                    written.Add((request.Row, request.Time));
                } else {
                    // "changed" too: the read that follows makes a new request,
                    // and this one would be refused again
                    refused.Add(request);
                }
            }
        }
    }

    /// The rows auto-export may send: ticked, writable, on the sheet, their time
    /// not written before; then, as updates, not refused before, and not
    /// unanswered unless retry.
    public List<ExportUpdate> Pick(IReadOnlyList<PendingUpdate> rows, bool retry) {
        lock (gate) {
            List<PendingUpdate> candidates = rows
                .Where(r => r.Selected && r.Writable && r.OnSheet && !written.Contains((r.Row, r.LocalText)))
                .ToList();
            return SheetTiming.Updates(candidates).Where(u => {
                Request request = Request.Of(u);
                return !refused.Contains(request) && (retry || !unanswered.Contains(request));
            }).ToList();
        }
    }

    public void Reset() {
        lock (gate) {
            ClearSets();
            url = null;
        }
    }

    private void ClearSets() {
        written.Clear();
        refused.Clear();
        unanswered.Clear();
    }
}
