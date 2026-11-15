using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Celeste.Mod.SpeedrunSheet;

/// Rooms for rules, which only the game thread can resolve (AreaData, the
/// English dialog). RoomMap is the game's; the tests use a table.
internal interface IRoomMap {
    /// The room a run of the rule starts in; null when it cannot be resolved.
    string StartRoomOf(SegmentRule rule);

    /// The room a NextStart rule ends in; null when it has none and ends when
    /// chapter time stops. For a Restart rule, the room its segment ends in.
    string EndRoomOf(SegmentRule rule);

    /// The room the rule's start room must be entered from; null when the rule
    /// never opens on an entry (a chapter's Start, 7A's start, a map-spawn row
    /// that does not also walk in, a heart-respawn row).
    string EntryRoomOf(SegmentRule rule);

    /// The berries the rule requires, as EntityID keys. Null or empty
    /// when they cannot be resolved: the rule is then never met.
    IReadOnlyCollection<string> BerriesOf(SegmentRule rule);
}

/// What the run carries when a segment ends, for the requirement checks.
internal readonly record struct EndState(int Dashes, IReadOnlyCollection<string> FollowingBerries);

/// Whether a Save and Quit happened inside a row's run, for the sheet's
/// dropdown. Unknown: the run cannot say, so the export leaves the cell alone.
public enum SaveAndQuitNote { Unknown, Without, With }

/// Whether a Return to Map happened inside a row's run, ending it included,
/// for the sheet's dropdown. Unknown leaves the cell alone.
public enum ReturnToMapNote { Unknown, Without, With }

/// A closed segment whose requirements the run met, with its time.
internal readonly record struct SegmentRecord(SegmentRule Rule, long Ticks, SaveAndQuitNote Note,
    ReturnToMapNote ReturnToMap = ReturnToMapNote.Unknown);

/// How the level was left.
internal enum LeaveKind {
    // the saved session can be resumed where it stopped
    SaveAndQuit,
    ReturnToMap,
    // Completed turned true: the level goes on to its ending, and only a
    // segment that spans leaves outlives it
    ChapterEnd,
}

/// Every segment that can be running, at once. RunWatcher feeds it the game's
/// events with chapter-time readings (`Session.Time` ticks: only a difference
/// of two is a time); each event returns what it closed.
internal sealed class RunTracker(IReadOnlyList<SegmentRule> rules, IRoomMap rooms) {
    // what opened a segment, which the note of an after-wake-up row follows
    internal enum Opening { WalkIn, SaveAndQuit, Other }

    private sealed class OpenSegment(SegmentRule rule, long start, Opening opening) {
        public readonly SegmentRule Rule = rule;
        // the first opening stands: OpenIfClosed does not reopen
        public readonly Opening Opening = opening;
        // a Save and Quit re-entry carried it on: the Save and Quit was in the row
        public bool Resumed;
        // a split's Save and Quit stayed (SplitStayed): whether one happened is unknown
        public bool Unsure;
        // the reading its current piece started on: a re-entry moves it
        public long Start = start;
        // chapter time of the pieces a leave has set aside
        public long Banked;
        public Collectibles Collected;
        public int Gems;
        public readonly HashSet<string> Berries = [];
        public bool Disqualified;
        // a row that may return to the map reached its end short of something:
        // the next walk there drops it
        public bool Peeked;
        // a Return to Map happened in its run
        public bool ReturnedToMap;
        // one of its pieces reached the chapter's end. The segment's own: the
        // session of a level entered from the map knows nothing of it
        public bool Completed;
        // the rooms a segment that spans leaves has walked into
        public readonly HashSet<string> Entered = [];
        // what it lacked (Lacks) when its current piece started from the map;
        // null for a piece that started with the chapter
        public int? Lacked;
        // the dashes at the first checkpoint crossed: what a whole chapter's
        // dash rule reads, since by Farewell's end both routes carry the same
        public int? FirstDashes;
    }

    private readonly List<OpenSegment> open = [];
    // set aside by a leave (Left says which), each with all it holds, until
    // the level is entered again or a new attempt forgets them
    private readonly List<OpenSegment> aside = [];
    // starts reached without control, waiting for it to come back in
    // pendingRoom. FromLoad: a savestate loaded without control, whose start
    // is tested where the player appears
    private readonly List<(SegmentRule Rule, bool FromLoad, Opening Opening)> pending = [];
    private string pendingRoom;
    // the last leave was a Save and Quit while a start waited for control: the
    // run had reached it, so the re-entry carries the run on
    private bool waitedAtLeave;
    // backed out of their first room into the room it is entered from: no
    // walk-in opens them again until a new attempt
    private readonly HashSet<SegmentRule> backedOut = [];

    /// Swapped by RunWatcher when the area changes.
    public IRoomMap Rooms { get; set; } = rooms;

    /// For the tests.
    public IEnumerable<SegmentRule> Open => open.Select(segment => segment.Rule);

    /// Told every change to a row, inside the call that made it; null when
    /// nobody listens. Nothing is reported while a row merely stays open.
    public Action<TraceEvent> Trace { get; set; }

    private void Report(TraceKind kind, SegmentRule rule, TraceReason reason, long? reading,
        SegmentRecord? record = null, string room = null, string from = null, string entry = null) =>
        Trace?.Invoke(new TraceEvent(kind, rule, reason, reading, record, room, from, entry));

    private void ReportPending(TraceReason reason, long? reading) {
        foreach ((SegmentRule rule, _, _) in pending) {
            Report(TraceKind.StoppedWaiting, rule, reason, reading, room: pendingRoom);
        }
    }

    private static TraceReason ReasonOf(LeaveKind kind) => kind switch {
        LeaveKind.SaveAndQuit => TraceReason.SaveAndQuit,
        LeaveKind.ReturnToMap => TraceReason.ReturnToMap,
        _ => TraceReason.ChapterEnd,
    };

    /// The timeline changed under the open segments: nothing open or set
    /// aside is recorded.
    public void Drop() => Drop(TraceReason.TimelineChanged, null);

    private void Drop(TraceReason reason, long? reading) {
        if (Trace != null) {
            foreach (OpenSegment segment in open.Concat(aside)) {
                Report(TraceKind.Dropped, segment.Rule, reason, reading);
            }

            ReportPending(reason, reading);
        }

        open.Clear();
        aside.Clear();
        pending.Clear();
        pendingRoom = null;
        waitedAtLeave = false;
        backedOut.Clear();
    }

    /// Nothing open can be recorded any more, but it stays open, so that the
    /// segment after it still opens; starts waiting for control are forgotten.
    /// For a stretch the run did not play (6A's watched fall).
    public void Disqualify() {
        foreach (OpenSegment segment in open) {
            Disqualify(segment, TraceReason.Fall);
        }

        ReportPending(TraceReason.Fall, null);
        pending.Clear();
        pendingRoom = null;
    }

    /// A TAS played this frame: nothing open or set aside can be recorded any
    /// more, and it still chains, as Disqualify. A start waiting for control is
    /// kept: it times from the frame control returns, which marks it in turn
    /// if the TAS is still playing.
    public void TasPlayed() {
        foreach (OpenSegment segment in open.Concat(aside)) {
            Disqualify(segment, TraceReason.TasPlayed);
        }
    }

    // reported once, not on every frame a TAS plays
    private void Disqualify(OpenSegment segment, TraceReason reason) {
        if (!segment.Disqualified) {
            segment.Disqualified = true;
            Report(TraceKind.Disqualified, segment.Rule, reason, null);
        }
    }

    /// A split's Save and Quit stayed in the level: a Save and Quit may have
    /// been taken without the level reloading, so no open segment can say
    /// whether one happened in it, and neither can a start walked into and
    /// still waiting for control.
    public void SplitStayed() {
        foreach (OpenSegment segment in open) {
            if (!segment.Unsure) {
                segment.Unsure = true;
                Report(TraceKind.Unsure, segment.Rule, TraceReason.SplitStayed, null);
            }
        }

        for (int i = 0; i < pending.Count; i++) {
            if (pending[i].Opening == Opening.WalkIn) {
                pending[i] = pending[i] with { Opening = Opening.Other };
            }
        }
    }

    /// A new attempt (a level from the loader, a room teleport, a first-room
    /// reset, a savestate loaded with control): nothing open is recorded.
    /// The Current Room and map-spawn segments starting in this room then open
    /// where atStart says the player is at their start; nothing else does.
    /// fromState: the savestate, the only one of the four a Heart Respawn
    /// segment opens on. chapterLoad: a level whose load played the chapter's
    /// intro, the only one a Chapter Load segment opens on.
    /// What it opens counts as opened by neither a walk-in nor a Save and Quit.
    public void Restart(string scope, string room, long reading, bool control, bool launching,
        Func<SegmentRule, bool> atStart, bool fromState = false, bool chapterLoad = false) {
        Drop(TraceReason.NewAttempt, reading);
        OpenAt(scope, room, reading, control, launching, standalone: true,
            rule => rule.Setup != StartSetup.NextRoom && (fromState || rule.Setup != StartSetup.HeartRespawn)
                    && (chapterLoad || rule.Setup != StartSetup.ChapterLoad) && atStart(rule),
            fromLoad: false, Opening.Other, TraceReason.Restart);
    }

    /// A savestate loaded without control (mid-wake-up, mid-respawn, mid-intro):
    /// nothing open is recorded, and the Current Room, map-spawn and
    /// heart-respawn segments starting in this room wait for the player to
    /// appear, where ControlReturned tests them.
    public void RestartAtAppearance(string scope, string room) {
        Drop(TraceReason.NewAttempt, null);
        OpenAt(scope, room, 0, control: false, launching: false, standalone: true,
            rule => rule.Setup is not (StartSetup.NextRoom or StartSetup.ChapterLoad), fromLoad: true, Opening.Other,
            TraceReason.Appearance);
    }

    /// The player walked into this room from another. Closes first, then opens:
    /// the segment ending here is never the one starting here. A segment
    /// ending here closes with a time only on an entry from the entry room of
    /// the row that follows it on its own route; any other way in may be a
    /// shortcut, or another route's door, and drops it unrecorded. A Restart
    /// row ending here is dropped on any way in: a restart after it is not a
    /// run of the row.
    /// Walking from a row's start room back into its entry room drops it
    /// (backedOut).
    public List<SegmentRecord> RoomEntered(string scope, string from, string room, long reading, bool control,
        bool launching, EndState end) {
        bool entry = IsEntry(scope, from, room);
        foreach (OpenSegment segment in open) {
            segment.Entered.Add(room);

            if (entry) {
                segment.FirstDashes ??= end.Dashes;
            }
        }

        if (from != null) {
            foreach (SegmentRule rule in rules) {
                if (rule.Scope == scope && Rooms.StartRoomOf(rule) == from && Rooms.EntryRoomOf(rule) == room) {
                    backedOut.Add(rule);
                    if (open.RemoveAll(segment => segment.Rule == rule) > 0) {
                        Report(TraceKind.Dropped, rule, TraceReason.BackedOut, reading, room: room, from: from);
                    }
                }
            }
        }

        Predicate<OpenSegment> fromItsEntry = segment => from != null && SuccessorEntryOf(segment.Rule, scope, room) == from;
        Predicate<OpenSegment> atItsEnd = segment => segment.Rule.End == EndKind.NextStart && Rooms.EndRoomOf(segment.Rule) == room;
        List<SegmentRecord> records = CloseWhere(
            // one that may still return to the map goes on the first time, unless it is met here
            segment => atItsEnd(segment) && (!segment.Rule.MayReturnToMap || segment.Peeked
                                             || (fromItsEntry(segment) && Met(segment, reading, end) != null)),
            reading, end,
            refuses: segment => segment.Peeked ? (TraceReason.PassedEndBefore, null)
                : !fromItsEntry(segment) ? (TraceReason.NotFromNextEntry, SuccessorEntryOf(segment.Rule, scope, room))
                : (TraceReason.None, null),
            room, from);
        foreach (OpenSegment segment in open) {
            if (!segment.Peeked && atItsEnd(segment)) {
                segment.Peeked = true;
                Report(TraceKind.PassedEnd, segment.Rule, TraceReason.None, reading, room: room, from: from);
            }
        }

        CloseWhere(segment => segment.Rule.End == EndKind.Restart && Rooms.EndRoomOf(segment.Rule) == room,
            reading, end, refuses: _ => (TraceReason.EndRoomWalkedInto, null), room, from);
        ReportPending(TraceReason.LeftRoom, reading);
        pending.Clear();
        pendingRoom = null;
        if (entry) {
            OpenAt(scope, room, reading, control, launching, standalone: false,
                rule => rule.OpensOnWalkIn && Rooms.EntryRoomOf(rule) == from && !backedOut.Contains(rule),
                fromLoad: false, Opening.WalkIn, TraceReason.WalkIn, from);
        }

        if (Trace != null) {
            ReportNotOpened(scope, from, room, reading);
        }

        return records;
    }

    // the rows starting in this room that a walk-in opens, but not this one
    private void ReportNotOpened(string scope, string from, string room, long reading) {
        foreach (SegmentRule rule in rules) {
            if (rule.Scope != scope || !rule.OpensOnWalkIn || Rooms.StartRoomOf(rule) != room
                || Rooms.EntryRoomOf(rule) is not { } entry) {
                continue;
            }

            if (backedOut.Contains(rule)) {
                Report(TraceKind.NotOpened, rule, TraceReason.BackedOut, reading, room: room, from: from, entry: entry);
            } else if (entry != from) {
                Report(TraceKind.NotOpened, rule, TraceReason.WrongEntry, reading, room: room, from: from, entry: entry);
            }
        }
    }

    // a segment starting in this room is entered from that one
    private bool IsEntry(string scope, string from, string room) {
        if (from == null) {
            return false;
        }

        foreach (SegmentRule rule in rules) {
            if (rule.Scope == scope && Rooms.StartRoomOf(rule) == room && Rooms.EntryRoomOf(rule) == from) {
                return true;
            }
        }

        return false;
    }

    // the room the row after `ending` is entered from, on its own route: the
    // first row starting in this room of the same family, berry or plain,
    // else the first of the other. A map-spawn or heart-respawn row follows
    // nothing, unless a walk-in opens it too
    private string SuccessorEntryOf(SegmentRule ending, string scope, string room) {
        string other = null;
        foreach (SegmentRule rule in rules) {
            if (rule.Scope != scope || !rule.OpensOnWalkIn
                || Rooms.StartRoomOf(rule) != room || Rooms.EntryRoomOf(rule) is not { } entry) {
                continue;
            }

            if (rule.RequiresBerries == ending.RequiresBerries) {
                return entry;
            }

            other ??= entry;
        }

        return other;
    }

    /// Control came back in this room: a start reached without it opens now.
    /// One a savestate load left waiting opens only where atAppearance says
    /// the player appeared at its start.
    public void ControlReturned(string room, long reading, Func<SegmentRule, bool> atAppearance) {
        foreach ((SegmentRule rule, bool fromLoad, Opening opening) in pending) {
            if (pendingRoom != room) {
                Report(TraceKind.StoppedWaiting, rule, TraceReason.ControlElsewhere, reading, room: room);
            } else if (!fromLoad || atAppearance(rule)) {
                OpenIfClosed(rule, reading, opening, TraceReason.ControlReturned, room);
            } else {
                Report(TraceKind.StoppedWaiting, rule, TraceReason.NotAtStart, reading, room: room);
            }
        }

        pending.Clear();
        pendingRoom = null;
    }

    /// The launch ended in this room.
    public void LaunchEnded(string scope, string room, long reading) {
        foreach (SegmentRule rule in rules) {
            if (rule.Scope == scope && rule.Start == StartKind.AfterLaunch && Rooms.StartRoomOf(rule) == room) {
                OpenIfClosed(rule, reading, Opening.WalkIn, TraceReason.LaunchEnded, room);
            }
        }
    }

    /// A collect, between updates: it counts for every segment already open,
    /// and closes those that end on it.
    public List<SegmentRecord> Collected(Collectibles kind, long reading, EndState end) {
        foreach (OpenSegment segment in open) {
            segment.Collected |= kind;
            if (kind == Collectibles.Gem) {
                segment.Gems++;
            }
        }

        return CloseWhere(
            segment => segment.Rule.End == EndKind.Collect
                       && (segment.Collected & segment.Rule.EndsOn) == segment.Rule.EndsOn,
            reading, end);
    }

    public void BerryCollected(string berry) {
        foreach (OpenSegment segment in open) {
            segment.Berries.Add(berry);
        }
    }

    /// TimerStopped or Completed went from false to true. `counted` includes
    /// the frame that stopped it, as the game's chapter time does: a chapter's
    /// last segment and its whole chapter end on the same reading.
    public List<SegmentRecord> ChapterTimeStopped(long counted, EndState end) =>
        CloseWhere(
            segment => segment.Rule.End == EndKind.ChapterEnd
                       || (segment.Rule.End == EndKind.NextStart && Rooms.EndRoomOf(segment.Rule) == null),
            counted, end);

    /// The level was left, on this reading. A Save and Quit sets every open
    /// segment aside, for Reentered. Any other leave sets aside the segments
    /// that span leaves, unless they end on it with all they require. A piece
    /// from the map that fetched nothing its row lacked (a try, at a Return to
    /// Map or at the end of a row already completed) is not set aside: the
    /// whole row goes, bank included. A Return to Map also sets aside the
    /// segments that span one. It then
    /// closes what ends there and drops the rest, unrecorded.
    public List<SegmentRecord> Left(LeaveKind kind, long reading, EndState end) {
        waitedAtLeave = kind == LeaveKind.SaveAndQuit && pending.Count > 0;
        ReportPending(TraceReason.LeftLevel, reading);
        pending.Clear();
        pendingRoom = null;
        // the attempt ends, unless a Save and Quit resumes it
        if (kind != LeaveKind.SaveAndQuit) {
            backedOut.Clear();
        }

        if (kind == LeaveKind.ReturnToMap) {
            // the game loses a berry still following with the level
            end = end with { FollowingBerries = [] };
        }

        for (int i = 0; i < open.Count;) {
            OpenSegment segment = open[i];
            bool completedBefore = segment.Completed;
            segment.Completed |= kind == LeaveKind.ChapterEnd;
            segment.ReturnedToMap |= kind == LeaveKind.ReturnToMap;
            if (kind == LeaveKind.SaveAndQuit
                || (kind == LeaveKind.ReturnToMap && segment.Rule.SpansReturnToMap)
                || (segment.Rule.SpansLeaves && !(segment.Completed && Met(segment, reading, end) != null))) {
                open.RemoveAt(i);
                bool atTheEndOfACompletedRow = kind == LeaveKind.ChapterEnd && completedBefore;
                bool aTry = segment.Rule.SpansLeaves && (kind == LeaveKind.ReturnToMap || atTheEndOfACompletedRow);
                if (!aTry || segment.Lacked != Lacks(segment)) {
                    segment.Banked += reading - segment.Start;
                    aside.Add(segment);
                    Report(TraceKind.SetAside, segment.Rule, ReasonOf(kind), reading);
                } else {
                    Report(TraceKind.Dropped, segment.Rule, TraceReason.TryFetchedNothing, reading);
                }
            } else {
                i++;
            }
        }

        List<SegmentRecord> records = kind switch {
            LeaveKind.ChapterEnd => ChapterTimeStopped(reading, end),
            LeaveKind.ReturnToMap => CloseWhere(
                segment => segment.Rule.End == EndKind.ReturnToMap || segment.Rule.SpansLeaves
                           || segment.Rule.MayReturnToMap, reading, end),
            _ => [],
        };
        if (Trace != null) {
            foreach (OpenSegment segment in open) {
                Report(TraceKind.Dropped, segment.Rule, TraceReason.LeftLevel, reading);
            }
        }

        open.Clear();
        return records;
    }

    /// The level that was left is entered again; RunWatcher says which level
    /// from the loader is one, and which leave it answers. What was set aside
    /// carries on from this reading, from the map only into a room it has
    /// walked into: a checkpoint picked further on would skip what lies
    /// between. One that spans a Return to Map carries on only from its own
    /// checkpoint. Nothing opened since the leave carries on.
    /// Then the Current Room and map-spawn segments starting in this room
    /// open where atStart says the player is at their start, as on a
    /// Restart, and after a Save and Quit the Heart Respawn ones too; the
    /// Chapter Load ones when the load played the chapter's intro. One
    /// carried on is not opened again. True when the run goes on: after a
    /// Save and Quit, anything that can still record carried on, or a start
    /// waited for control at the leave; from the map, a segment that already
    /// holds something it requires (one that holds nothing is a twin the run
    /// never played for).
    public bool Reentered(LeaveKind kind, string scope, string room, long reading, bool control, bool launching,
        Func<SegmentRule, bool> atStart, bool chapterLoad = false) {
        open.Clear();
        pending.Clear();
        pendingRoom = null;
        bool carried = waitedAtLeave;
        foreach (OpenSegment segment in aside) {
            if (kind != LeaveKind.SaveAndQuit) {
                bool carriesOn = segment.Rule.SpansReturnToMap
                    ? Rooms.StartRoomOf(segment.Rule) == room
                    : segment.Entered.Contains(room);
                if (!carriesOn) {
                    Report(TraceKind.NotCarriedOn, segment.Rule,
                        segment.Rule.SpansReturnToMap ? TraceReason.NotItsCheckpoint : TraceReason.RoomNotWalked,
                        reading, room: room);
                    continue;
                }

                segment.Lacked = Lacks(segment);
            }

            segment.Start = reading;
            segment.Resumed |= kind == LeaveKind.SaveAndQuit;
            open.Add(segment);
            Report(TraceKind.CarriedOn, segment.Rule, ReasonOf(kind), reading, room: room);
            carried |= kind == LeaveKind.SaveAndQuit ? !segment.Disqualified : segment.Lacked < Needs(segment.Rule);
        }

        aside.Clear();
        OpenAt(scope, room, reading, control, launching, standalone: true,
            rule => rule.Setup != StartSetup.NextRoom
                    && (kind == LeaveKind.SaveAndQuit || rule.Setup != StartSetup.HeartRespawn)
                    && (chapterLoad || rule.Setup != StartSetup.ChapterLoad) && atStart(rule),
            fromLoad: false, kind == LeaveKind.SaveAndQuit ? Opening.SaveAndQuit : Opening.Other, TraceReason.Reentry);
        return carried;
    }

    /// A death zeroed chapter time (Level.Reload, in a level's first room
    /// with nothing collected): a new attempt from the spawn, as Restart,
    /// for everything but a segment that spans leaves, or a Return to Map, in
    /// a piece started from the map. It keeps the time read before the reset,
    /// `last`, and carries on from the new reading. True when one was kept.
    public bool ClockZeroed(string scope, string room, long last, long reading, bool control, bool launching,
        Func<SegmentRule, bool> atStart) {
        List<OpenSegment> kept = open.FindAll(segment =>
            (segment.Rule.SpansLeaves || segment.Rule.SpansReturnToMap) && segment.Lacked != null);
        foreach (OpenSegment segment in kept) {
            segment.Banked += last - segment.Start;
            segment.Start = reading;
            Report(TraceKind.Kept, segment.Rule, TraceReason.FirstRoomDeath, reading, room: room);
        }

        // out of the restart's drop, which would report them dropped
        open.RemoveAll(kept.Contains);
        Restart(scope, room, reading, control, launching, atStart);
        open.InsertRange(0, kept);
        return kept.Count > 0;
    }

    /// Restart Chapter left the level, with the old session's last reading: the
    /// segments ending on it close, and everything else open is dropped
    /// unrecorded. The new level's Restart then opens what starts there.
    public List<SegmentRecord> ChapterRestarted(long reading, EndState end) {
        List<SegmentRecord> records = CloseWhere(segment => segment.Rule.End == EndKind.Restart, reading, end);
        Drop(TraceReason.RestartChapter, reading);
        return records;
    }

    private void OpenAt(string scope, string room, long reading, bool control, bool launching, bool standalone,
        Func<SegmentRule, bool> opens, bool fromLoad, Opening opening, TraceReason how, string from = null) {
        foreach (SegmentRule rule in rules) {
            if (rule.Scope != scope || Rooms.StartRoomOf(rule) != room || !opens(rule)) {
                continue;
            }

            if (rule.Start == StartKind.AfterLaunch) {
                // the launch's own end opens it (LaunchEnded); a load after the
                // landing opens it here
                if (standalone && control && !launching) {
                    OpenIfClosed(rule, reading, opening, how, room, from);
                }

                continue;
            }

            if (control) {
                OpenIfClosed(rule, reading, opening, how, room, from);
            } else {
                pending.Add((rule, fromLoad, opening));
                pendingRoom = room;
                Report(TraceKind.Waiting, rule, how, fromLoad ? null : reading, room: room, from: from);
            }
        }
    }

    // a segment already open stays open: a route looping through its start
    // room would lose the loop
    private void OpenIfClosed(SegmentRule rule, long reading, Opening opening, TraceReason how, string room,
        string from = null) {
        foreach (OpenSegment segment in open) {
            if (segment.Rule == rule) {
                return;
            }
        }

        open.Add(new OpenSegment(rule, reading, opening));
        Report(TraceKind.Opened, rule, how, reading, room: room, from: from);
    }

    // refuses null: every segment that ends now may be recorded; else only
    // those it gives no reason against, and the others are dropped. Room and
    // from are for the trace
    private List<SegmentRecord> CloseWhere(Predicate<OpenSegment> ends, long reading, EndState end,
        Func<OpenSegment, (TraceReason Reason, string Entry)> refuses = null, string room = null, string from = null) {
        List<SegmentRecord> result = [];
        for (int i = 0; i < open.Count;) {
            OpenSegment segment = open[i];
            if (!ends(segment)) {
                i++;
                continue;
            }

            open.RemoveAt(i);
            (TraceReason reason, string entry) = refuses?.Invoke(segment) ?? (TraceReason.None, null);
            long ticks = 0;
            if (reason == TraceReason.None) {
                reason = segment.Disqualified ? TraceReason.Disqualified : Shortfall(segment, reading, end, out ticks);
            }

            if (reason == TraceReason.None) {
                SegmentRecord record = new(segment.Rule, ticks, NoteOf(segment),
                    segment.ReturnedToMap ? ReturnToMapNote.With : ReturnToMapNote.Without);
                result.Add(record);
                Report(TraceKind.Closed, segment.Rule, reason, reading, record, room, from);
            } else {
                Report(TraceKind.NotRecorded, segment.Rule, reason, reading, room: room, from: from, entry: entry);
            }
        }

        result.Sort((a, b) => a.Rule.Order.CompareTo(b.Rule.Order));
        return result;
    }

    // first match wins. A row that starts after a wake-up was either walked
    // into or entered by a Save and Quit re-entry; any other way in says nothing
    private static SaveAndQuitNote NoteOf(OpenSegment segment) {
        if (segment.Rule.NoSaveAndQuitNote) {
            return SaveAndQuitNote.Unknown;
        }

        if (segment.Resumed) {
            return SaveAndQuitNote.With;
        }

        if (segment.Rule.AfterWakeUp && segment.Opening == Opening.SaveAndQuit) {
            return SaveAndQuitNote.With;
        }

        if (segment.Unsure) {
            return SaveAndQuitNote.Unknown;
        }

        if (segment.Rule.AfterWakeUp) {
            return segment.Opening == Opening.WalkIn ? SaveAndQuitNote.Without : SaveAndQuitNote.Unknown;
        }

        return SaveAndQuitNote.Without;
    }

    // the time, or null when the run did not meet the row's requirements
    private long? Met(OpenSegment segment, long reading, EndState end) =>
        Shortfall(segment, reading, end, out long ticks) == TraceReason.None ? ticks : null;

    // the first requirement the run did not meet, or None with its time
    private TraceReason Shortfall(OpenSegment segment, long reading, EndState end, out long ticks) {
        SegmentRule rule = segment.Rule;
        // the row's whole time: a piece of no length still records the others
        long elapsed = segment.Banked + reading - segment.Start;
        ticks = elapsed + rule.HeadTicks + rule.TailTicks;
        Collectibles missing = rule.Requires & ~segment.Collected;
        return elapsed <= 0 ? TraceReason.NoTime
            : (missing & Collectibles.Heart) != 0 ? TraceReason.MissingHeart
            : (missing & Collectibles.Cassette) != 0 ? TraceReason.MissingCassette
            : missing != 0 ? TraceReason.MissingGem
            : segment.Gems < rule.Gems ? TraceReason.MissingGems
            : rule.Dashes is { } dashes && (rule.End == EndKind.ChapterEnd ? segment.FirstDashes : end.Dashes) != dashes
                ? TraceReason.WrongDashes
            : rule.RequiresBerries && !HasEveryBerry(segment, end) ? TraceReason.MissingBerries
            : TraceReason.None;
    }

    // how many things the rule requires in all: what Lacks counts down from
    private int Needs(SegmentRule rule) =>
        BitOperations.PopCount((uint)rule.Requires) + rule.Gems + (Rooms.BerriesOf(rule)?.Count ?? 0);

    // how many of the things the rule requires the segment does not hold: a
    // berry not banked (one still following is not), a heart or a cassette,
    // each gem short
    private int Lacks(OpenSegment segment) {
        SegmentRule rule = segment.Rule;
        int lacks = BitOperations.PopCount((uint)(rule.Requires & ~segment.Collected))
                    + Math.Max(0, rule.Gems - segment.Gems);
        if (Rooms.BerriesOf(rule) is { } berries) {
            lacks += berries.Count(berry => !segment.Berries.Contains(berry));
        }

        return lacks;
    }

    // a berry still following at the end counts. No berries resolved is not
    // "none required": the row is not met
    private bool HasEveryBerry(OpenSegment segment, EndState end) {
        if (Rooms.BerriesOf(segment.Rule) is not { Count: > 0 } berries) {
            return false;
        }

        foreach (string berry in berries) {
            if (!segment.Berries.Contains(berry) && !end.FollowingBerries.Contains(berry)) {
                return false;
            }
        }

        return true;
    }
}
