using System;
using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet;

/// The rows above the timer: the segments closed with a time since the
/// attempt started, oldest first, each with its serial. A frame adds its most
/// specific segment (after the Heart Respawn piece closing under it, if any),
/// then its most specific chapter run. A load or a level from the loader
/// empties it, except a re-entry and a death that zeroes the clock with a
/// chapter row kept. A finished run's rows stay up until the next record
/// (keptSerial). The HUD's tier rows show the last and step back from it.
internal sealed class AttemptRows {
    private readonly List<(SegmentRecord Record, int Serial)> rows = [];

    // the last serial of a finished run's rows (a re-entry from the map that
    // carried nothing on or closed a chapter chain at the leave, a Return to
    // Map split closed without a pick): they go when the next run closes its
    // first
    private int keptSerial;

    public IReadOnlyList<(SegmentRecord Record, int Serial)> Rows => rows;

    /// The room the rows show in until the player leaves it: where the last
    /// one closed, or where a re-entry that kept them landed.
    public string LatestRoom { get; private set; }

    /// Counts every record added, so a reader tells a new one from the same one.
    public int LatestSerial { get; private set; }

    public void Clear() => rows.Clear();

    /// Records closed in room. berriesOf is the room map's, for Specificity.
    public void Add(List<SegmentRecord> records, string room, Func<SegmentRule, IReadOnlyCollection<string>> berriesOf) {
        LatestRoom = room;
        rows.RemoveAll(entry => entry.Serial <= keptSerial);
        // a chapter run closes with a segment: the segment first, so the rows
        // show the chapter run and step back to the segment
        List<SegmentRule> rules = records.ConvertAll(record => record.Rule);
        foreach (bool chapterRun in (bool[])[false, true]) {
            if (Specificity.MostSpecific(rules, chapterRun, berriesOf) is >= 0 and int shown) {
                // the piece run since a Save and Quit, closing under a wider row:
                // one step back from it
                List<SegmentRecord> pieces = records.FindAll(record => record.Rule.Setup == StartSetup.HeartRespawn);
                if (!chapterRun && rules[shown].Setup != StartSetup.HeartRespawn
                    && Specificity.MostSpecific(pieces.ConvertAll(record => record.Rule), false, berriesOf) is >= 0 and int piece) {
                    rows.Add((pieces[piece], ++LatestSerial));
                }

                rows.Add((records[shown], ++LatestSerial));
            }
        }
    }

    /// A re-entry carried on from a leave whose last record had leftSerial,
    /// landing in room. A run that carries on keeps every row it closed. A
    /// finished one leaves its rows up until the next row closes: what closed
    /// at the leave, or all of a chapter run in pieces that closed there,
    /// whatever twin of it still carries on.
    public void Reentered(bool carried, int leftSerial, bool saveAndQuit, string room) {
        bool chainClosed = rows.Exists(entry => entry.Serial > leftSerial && entry.Record.Rule.SpansLeaves);
        if (!carried || chainClosed) {
            if (!chainClosed) {
                rows.RemoveAll(entry => entry.Serial <= leftSerial);
            }

            keptSerial = LatestSerial;
        }

        if (!saveAndQuit) {
            // shown in this room now
            LatestRoom = room;
        }
    }

    /// The run is over with no level left (a Return to Map split closed
    /// without a pick): what closed keeps its rows until the next row closes.
    public void KeepUntilNextRow() => keptSerial = LatestSerial;
}
