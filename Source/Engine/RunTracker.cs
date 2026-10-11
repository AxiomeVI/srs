using System;
using System.Collections.Generic;
using System.Linq;

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
    /// never opens on an entry (a chapter's Start, 7A's start, a map-spawn row).
    string EntryRoomOf(SegmentRule rule);

    /// The berries the rule requires, as EntityID keys. Null or empty
    /// when they cannot be resolved: the rule is then never met.
    IReadOnlyCollection<string> BerriesOf(SegmentRule rule);
}

/// What the run carries when a segment ends, for the requirement checks.
internal readonly record struct EndState(int Dashes, IReadOnlyCollection<string> FollowingBerries);

/// A closed segment whose requirements the run met, with its time.
internal readonly record struct SegmentRecord(SegmentRule Rule, long Ticks);

/// How the level was left.
internal enum LeaveKind {
    // the saved session can be resumed where it stopped
    SaveAndQuit,
    ReturnToMap,
    // Completed turned true: the level goes on to its ending, and nothing
    // open outlives it
    ChapterEnd,
}

/// Every segment that can be running, at once. RunWatcher feeds it the game's
/// events with chapter-time readings (`Session.Time` ticks: only a difference
/// of two is a time); each event returns what it closed.
internal sealed class RunTracker(IReadOnlyList<SegmentRule> rules, IRoomMap rooms) {
    private sealed class OpenSegment(SegmentRule rule, long start) {
        public readonly SegmentRule Rule = rule;
        // the reading its current piece started on: a re-entry moves it
        public long Start = start;
        // chapter time of the pieces before a Save and Quit
        public long Banked;
        public Collectibles Collected;
        public int Gems;
        public readonly HashSet<string> Berries = [];
        public bool Disqualified;
        // the dashes at the first checkpoint crossed: what a whole chapter's
        // dash rule reads, since by Farewell's end both routes carry the same
        public int? FirstDashes;
    }

    private readonly List<OpenSegment> open = [];
    // set aside by a Save and Quit, each with all it holds, until the level
    // is entered again or a new attempt forgets them
    private readonly List<OpenSegment> aside = [];
    // starts reached without control, waiting for it to come back in
    // pendingRoom. FromLoad: a savestate loaded without control, whose start
    // is tested where the player appears
    private readonly List<(SegmentRule Rule, bool FromLoad)> pending = [];
    private string pendingRoom;

    /// Swapped by RunWatcher when the area changes.
    public IRoomMap Rooms { get; set; } = rooms;

    /// For the tests.
    public IEnumerable<SegmentRule> Open => open.Select(segment => segment.Rule);

    /// The timeline changed under the open segments: nothing open or set
    /// aside is recorded.
    public void Drop() {
        open.Clear();
        aside.Clear();
        pending.Clear();
        pendingRoom = null;
    }

    /// Nothing open can be recorded any more, but it stays open, so that the
    /// segment after it still opens; starts waiting for control are forgotten.
    /// For a stretch the run did not play (6A's watched fall).
    public void Disqualify() {
        foreach (OpenSegment segment in open) {
            segment.Disqualified = true;
        }

        pending.Clear();
        pendingRoom = null;
    }

    /// A new attempt (a level from the loader, a room teleport, a first-room
    /// reset, a savestate loaded with control): nothing open is recorded.
    /// The Current Room and map-spawn segments starting in this room then open
    /// where atStart says the player is at their start; nothing else does.
    /// fromState: the savestate, the only one of the four a Heart Respawn
    /// segment opens on.
    public void Restart(string scope, string room, long reading, bool control, bool launching,
        Func<SegmentRule, bool> atStart, bool fromState = false) {
        Drop();
        OpenAt(scope, room, reading, control, launching, standalone: true,
            rule => rule.Setup != StartSetup.NextRoom && (fromState || rule.Setup != StartSetup.HeartRespawn)
                    && atStart(rule),
            fromLoad: false);
    }

    /// A savestate loaded without control (mid-wake-up, mid-respawn, mid-intro):
    /// nothing open is recorded, and the Current Room, map-spawn and
    /// heart-respawn segments starting in this room wait for the player to
    /// appear, where ControlReturned tests them.
    public void RestartAtAppearance(string scope, string room) {
        Drop();
        OpenAt(scope, room, 0, control: false, launching: false, standalone: true,
            rule => rule.Setup != StartSetup.NextRoom, fromLoad: true);
    }

    /// The player walked into this room from another. Closes first, then opens:
    /// the segment ending here is never the one starting here. A segment
    /// ending here closes with a time only on an entry from the entry room of
    /// the row that follows it on its own route; any other way in may be a
    /// shortcut, or another route's door, and drops it unrecorded. A Restart
    /// row ending here is dropped on any way in: a restart after it is not a
    /// run of the row.
    public List<SegmentRecord> RoomEntered(string scope, string from, string room, long reading, bool control,
        bool launching, EndState end) {
        bool entry = IsEntry(scope, from, room);
        if (entry) {
            foreach (OpenSegment segment in open) {
                segment.FirstDashes ??= end.Dashes;
            }
        }

        List<SegmentRecord> records = CloseWhere(
            segment => segment.Rule.End == EndKind.NextStart && Rooms.EndRoomOf(segment.Rule) == room,
            reading, end, records: segment => from != null && SuccessorEntryOf(segment.Rule, scope, room) == from);
        CloseWhere(segment => segment.Rule.End == EndKind.Restart && Rooms.EndRoomOf(segment.Rule) == room,
            reading, end, records: _ => false);
        pending.Clear();
        pendingRoom = null;
        if (entry) {
            OpenAt(scope, room, reading, control, launching, standalone: false,
                rule => rule.Setup is not (StartSetup.MapSpawn or StartSetup.HeartRespawn)
                        && Rooms.EntryRoomOf(rule) == from,
                fromLoad: false);
        }

        return records;
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
    // else the first of the other. A map-spawn or heart-respawn row follows nothing
    private string SuccessorEntryOf(SegmentRule ending, string scope, string room) {
        string other = null;
        foreach (SegmentRule rule in rules) {
            if (rule.Scope != scope || rule.Setup is StartSetup.MapSpawn or StartSetup.HeartRespawn
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
        if (pendingRoom == room) {
            foreach ((SegmentRule rule, bool fromLoad) in pending) {
                if (!fromLoad || atAppearance(rule)) {
                    OpenIfClosed(rule, reading);
                }
            }
        }

        pending.Clear();
        pendingRoom = null;
    }

    /// The launch ended in this room.
    public void LaunchEnded(string scope, string room, long reading) {
        foreach (SegmentRule rule in rules) {
            if (rule.Scope == scope && rule.Start == StartKind.AfterLaunch && Rooms.StartRoomOf(rule) == room) {
                OpenIfClosed(rule, reading);
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

    /// The level was left, on this reading. Starts waiting for control are
    /// forgotten. A chapter's end closes what chapter time stopping closes,
    /// a Return to Map the segments that end on one. A Save and Quit then
    /// sets every open segment aside with its time so far, for Reentered;
    /// any other leave drops what is still open, unrecorded. What is
    /// already set aside stays, with no time added.
    public List<SegmentRecord> Left(LeaveKind kind, long reading, EndState end) {
        pending.Clear();
        pendingRoom = null;
        List<SegmentRecord> records = kind switch {
            LeaveKind.ChapterEnd => ChapterTimeStopped(reading, end),
            // the game loses a berry still following with the level
            LeaveKind.ReturnToMap => CloseWhere(segment => segment.Rule.End == EndKind.ReturnToMap, reading,
                end with { FollowingBerries = [] }),
            _ => [],
        };
        if (kind == LeaveKind.SaveAndQuit) {
            foreach (OpenSegment segment in open) {
                segment.Banked += reading - segment.Start;
                aside.Add(segment);
            }
        }

        open.Clear();
        return records;
    }

    /// The level that was left is entered again; RunWatcher says which level
    /// from the loader is one, and which leave it answers. What was set aside
    /// carries on from this reading; nothing opened since the leave does.
    /// Then the Current Room and map-spawn segments starting in this room
    /// open where atStart says the player is at their start, as on a
    /// Restart, and after a Save and Quit the Heart Respawn ones too. One
    /// carried on is not opened again.
    public void Reentered(LeaveKind kind, string scope, string room, long reading, bool control, bool launching,
        Func<SegmentRule, bool> atStart) {
        open.Clear();
        pending.Clear();
        pendingRoom = null;
        foreach (OpenSegment segment in aside) {
            segment.Start = reading;
            open.Add(segment);
        }

        aside.Clear();
        OpenAt(scope, room, reading, control, launching, standalone: true,
            rule => rule.Setup != StartSetup.NextRoom
                    && (kind == LeaveKind.SaveAndQuit || rule.Setup != StartSetup.HeartRespawn) && atStart(rule),
            fromLoad: false);
    }

    /// Restart Chapter left the level, with the old session's last reading: the
    /// segments ending on it close, and everything else open is dropped
    /// unrecorded. The new level's Restart then opens what starts there.
    public List<SegmentRecord> ChapterRestarted(long reading, EndState end) {
        List<SegmentRecord> records = CloseWhere(segment => segment.Rule.End == EndKind.Restart, reading, end);
        Drop();
        return records;
    }

    private void OpenAt(string scope, string room, long reading, bool control, bool launching, bool standalone,
        Func<SegmentRule, bool> opens, bool fromLoad) {
        foreach (SegmentRule rule in rules) {
            if (rule.Scope != scope || Rooms.StartRoomOf(rule) != room || !opens(rule)) {
                continue;
            }

            if (rule.Start == StartKind.AfterLaunch) {
                // the launch's own end opens it (LaunchEnded); a load after the
                // landing opens it here
                if (standalone && control && !launching) {
                    OpenIfClosed(rule, reading);
                }

                continue;
            }

            if (control) {
                OpenIfClosed(rule, reading);
            } else {
                pending.Add((rule, fromLoad));
                pendingRoom = room;
            }
        }
    }

    // a segment already open stays open: a route looping through its start
    // room would lose the loop
    private void OpenIfClosed(SegmentRule rule, long reading) {
        foreach (OpenSegment segment in open) {
            if (segment.Rule == rule) {
                return;
            }
        }

        open.Add(new OpenSegment(rule, reading));
    }

    // records null: every segment that ends now may be recorded; else only
    // those it accepts, and the others are dropped
    private List<SegmentRecord> CloseWhere(Predicate<OpenSegment> ends, long reading, EndState end,
        Predicate<OpenSegment> records = null) {
        List<SegmentRecord> result = [];
        for (int i = 0; i < open.Count;) {
            OpenSegment segment = open[i];
            if (!ends(segment)) {
                i++;
                continue;
            }

            open.RemoveAt(i);
            if ((records == null || records(segment)) && !segment.Disqualified && Met(segment, reading, end) is { } ticks) {
                result.Add(new SegmentRecord(segment.Rule, ticks));
            }
        }

        result.Sort((a, b) => a.Rule.Order.CompareTo(b.Rule.Order));
        return result;
    }

    // the time, or null when the run did not meet the row's requirements
    private long? Met(OpenSegment segment, long reading, EndState end) {
        SegmentRule rule = segment.Rule;
        // the row's whole time: a piece of no length still records the others
        long elapsed = segment.Banked + reading - segment.Start;
        if (elapsed <= 0
            || (segment.Collected & rule.Requires) != rule.Requires
            || segment.Gems < rule.Gems
            || (rule.Dashes is { } dashes
                && (rule.End == EndKind.ChapterEnd ? segment.FirstDashes : end.Dashes) != dashes)
            || (rule.RequiresBerries && !HasEveryBerry(segment, end))) {
            return null;
        }

        return elapsed + rule.HeadTicks + rule.TailTicks;
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
