using System;
using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet;

/// The level being updated or left, as FrameFeed asks for it. Read when
/// asked, inside the call it was handed to.
internal interface IFrameGame {
    /// The rooms of the area, for the rules of its scope.
    IRoomMap Rooms { get; }

    string Room { get; }

    (int Id, int Mode) Area { get; }

    /// Chapter time now.
    long Time { get; }

    /// The dashes and the berries following the player now.
    EndState End();

    /// A TAS is playing: nothing it closes is recorded.
    bool TasPlaying { get; }

    /// A split of an older SomeSplitButtons reports nothing: no record can
    /// vouch for its note.
    bool NotesUnknown { get; }

    /// Keeps the session's bests, and offers them to auto-export.
    void Record(List<SegmentRecord> records);

    /// Whether a point is on a start spawn of the rule, in a room as the
    /// tracker asks. A savestate further into the room does not play it.
    /// Built only where a frame tests one: each is a new delegate.
    Func<SegmentRule, bool> AtLoadedSpawn(float x, float y);

    /// The session's respawn point, in its room: where a loader, a
    /// teleport or a death puts the player.
    Func<SegmentRule, bool> AtRespawnPoint();

    /// The player's position now.
    Func<SegmentRule, bool> AtPlayer();

    /// The respawn point is a checkpoint's own spawn, the chapter's first
    /// room excluded: what a checkpoint picked on the map lands on.
    bool AtCheckpointSpawnPastStartRoom();

    /// Speed Run Tool's "freeze after load" is Off.
    bool FreezeAfterLoadOff { get; }
}

/// What a frame's decisions left for the feed that follows them.
internal readonly record struct FrameOutcome(bool Feeds, bool Restarted, bool Watched, bool Teleported, RoomLoad Load,
    bool TasFrame, bool Ending);

/// Feeds RunTracker with what the adapter read: the frames and the events
/// between and inside them. Holds every never-saved piece of state; the
/// savestate-registered values stay with the adapter and cross as copies.
internal sealed class FrameFeed {
    private RunTracker tracker;
    private Action<TraceEvent> trace;

    /// A new tracker, on every module load. The rest of the state outlives it.
    public void StartTracker(IReadOnlyList<SegmentRule> rules, Action<TraceEvent> trace) {
        this.trace = trace;
        tracker = new RunTracker(rules, null) { Trace = trace };
    }

    public RunTracker Tracker => tracker;

    /// What the tracker is being fed, written on each of its trace lines.
    public string Via;

    public readonly AttemptRows Rows = new();

    // never saved: what a load is compared against. Stamp only grows, so a
    // saved stamp differs from it once a frame has been fed since the save
    public long Stamp;
    public bool FedLastFrame;

    // how the room was loaded since the last fed frame
    public RoomLoad RoomLoad;
    // the intro of the last LoadLevel from the loader was Respawn: the
    // session does not start the chapter
    public bool LoaderIntroRespawn;
    // inside Level.Reload, the game's only LoadLevel(Respawn)
    public bool Reloading;

    // a savestate made since the last fed frame. Never saved: a load must
    // not bring it back
    public bool StateSaved;

    // Restart Chapter, seen when the new session is made: the old session's
    // last chapter time, for the rows that end on it
    public bool ChapterRestarted;
    public long RestartedTime;
    public (int Id, int Mode) RestartedArea;

    // the level was left, and no level from the loader has been judged
    // against it yet. Reading is chapter time at the leave, Serial the last
    // record before the leave closed anything. NoLoadYet: a split button's
    // leave, until its mod says it loads a level for it. Never saved: a state
    // load is a new attempt, and clears it
    public sealed record PendingLeave(LeaveKind Kind, (int Id, int Mode) Area, string Room, long Reading, int Serial,
        bool NoLoadYet);

    public PendingLeave Leave;

    // the last level updated was 7A's credits, or the Epilogue they end in.
    // The game counts no time in either (Level.UpdateTime), so neither is an
    // attempt: the leave pending from the chapter's end is judged by the
    // level entered after them. Never saved: a state load clears the leave
    public bool AfterEnding;

    // the frame being fed: a collect inside it is timed from its start, as
    // a room entry is (Session.Time already counts the frame by then)
    public bool InUpdate;
    public long FrameStart;

    // the edges against the last fed frame
    public string LastRoom;
    public bool LastLaunching;
    public bool LastFall;
    public bool LastControl = true;
    public bool LastStopped;
    public bool LastCompleted;

    // read before the game's update: a load, or a savestate made since the
    // last fed frame
    private bool loaded;
    private bool saved;

    // with Speed Run Tool's "freeze after load" Off, a player load counts the
    // frame its own wipe ends on, which Session.Time does not. A TAS load
    // starts no wipe, and restores the one its state was saved under
    private static readonly long WipeEndFrame = TimeSpan.FromMilliseconds(17).Ticks;

    /// Before the game's update of a fed frame: before is chapter time, the
    /// reading a room entry is timed from.
    public void BeforeUpdate(long savedStamp, long before) {
        loaded = savedStamp != Stamp;
        if (loaded) {
            // a room loaded before the load belongs to the timeline it left
            RoomLoad = RoomLoad.None;
        }

        // making a savestate is a new attempt too, as if it were loaded at once
        saved = StateSaved && !loaded;
        StateSaved = false;
        FrameStart = before;
        InUpdate = true;
    }

    /// After the game's update of a fed frame: what it means, then the edges
    /// it feeds, closes before opens. Returns what RunWatcher.Saved holds from
    /// now on.
    public SavedValues Frame(in FrameReading r, IFrameGame game) {
        FrameOutcome o = Decide(in r, game);
        PlayerSide player = r.Player;
        // the credits set Completed again and change room: neither is the run's
        if (o.Feeds && !o.Ending) {
            FeedEdges(in r, game, o.Load);
        }

        // the player appears when control returns, when the intro jump ends
        // (1A's too), and when a restart lands with control; a load restores
        // the last appearance
        bool appeared = o.Restarted
            ? player.Control
            : o.Watched && ((!LastControl && player.Control) || (LastLaunching && !player.Launching));
        bool disturbed = o.Load is RoomLoad.Respawn or RoomLoad.Teleport or RoomLoad.Loader || o.Teleported
                         || !FedLastFrame;
        (float X, float Y)? at = player.Present ? (player.X, player.Y) : null;
        // a restart that has control already appears where the frame began, so
        // a direction held into its first frame is a move
        (float X, float Y)? appearedAt = o.Restarted && player.Control && r.PlayerBefore.Present
            ? (r.PlayerBefore.X, r.PlayerBefore.Y)
            : at;
        Stillness still = new Stillness(r.SavedAfter.SpawnX, r.SavedAfter.SpawnY, r.SavedAfter.Moved)
            .After(disturbed, appeared, appearedAt)
            .After(false, false, at);

        if (o.TasFrame) {
            Via = "Tas";
            tracker.TasPlayed();
        }

        // during 6A's watched fall the rooms passed through are not where the
        // run comes from: 00 is entered from start, as after either skip
        string from = o.Restarted || !o.Watched || !player.Fall ? r.Room : r.SavedAfter.From;

        LastRoom = r.Room;
        LastLaunching = player.Launching;
        LastFall = player.Fall;
        LastControl = player.Control;
        LastStopped = r.Stopped;
        LastCompleted = r.Completed;
        AfterEnding = o.Ending;
        ChapterRestarted = false;
        FedLastFrame = true;
        return new SavedValues(++Stamp, from, still.X, still.Y, still.Moved);
    }

    /// What a fed frame means, first match wins. Feeds nothing but the
    /// restarts and the rows a Restart Chapter closes.
    internal FrameOutcome Decide(in FrameReading r, IFrameGame game) {
        InUpdate = false;
        // a row a TAS played one frame of is never recorded, whoever ends it:
        // a TAS may hand control back mid-row (QuicksaveMod replays one from
        // the room's start). Marked before this frame's closes, which Emit
        // lets through once the TAS has stopped, and again last, so a row
        // opened this frame is marked too
        bool tasFrame = r.TasBefore || r.TasAfter;
        if (tasFrame) {
            Via = "Tas";
            tracker.TasPlayed();
        }

        PlayerSide player = r.Player;
        // the Epilogue counts as an ending only while the last level updated
        // was one: AfterEnding is rewritten by a level update only, so one
        // entered again after the credits stays transparent
        bool ending = r.InCredits || (AfterEnding && r.Area.Id == 8);
        tracker.Rooms = game.Rooms;
        RoomLoad load = RoomLoad;
        RoomLoad = RoomLoad.None;
        // a room change no LoadLevel walked into is a teleport too. Not on a
        // load: the state's room is not a room change
        bool teleported = r.SavedAfter.Stamp == Stamp
                          && (load == RoomLoad.Teleport
                              || (FedLastFrame && r.Room != LastRoom && load < RoomLoad.WalkIn));

        bool feeds = false;
        // a new attempt landed this frame, so the player appears now if they
        // have control
        bool restarted = false;
        // this frame's control and state edges are the player's own: not on a
        // load inside orig, nor on the frame srs is switched back on
        bool watched = true;
        if (!loaded && r.SavedAfter.Stamp != Stamp) {
            // a load inside orig: this frame's events would mix two timelines
            Via = "LoadInFrame";
            tracker.Drop();
            Rows.Clear();
            Leave = null;
            watched = false;
        } else if (loaded || saved) {
            // every load is a new attempt. With control, the room's Current Room
            // segment opens if the player has not moved since appearing on its
            // start spawn; without, it waits for the appearance that follows.
            // A save starts its wipe a frame later (a TAS's starts none, but
            // a row a TAS plays is never recorded)
            bool wipeEnd = (saved || r.LoadWiping) && game.FreezeAfterLoadOff;
            Via = loaded ? "SavestateLoad" : "SavestateMade";
            if (r.PlayerBefore.Control) {
                tracker.Restart(r.Scope, r.RoomBefore, r.Before - (wipeEnd ? WipeEndFrame : 0), true,
                    r.PlayerBefore.Launching,
                    r.SavedBefore.Moved ? _ => false : game.AtLoadedSpawn(r.SavedBefore.SpawnX, r.SavedBefore.SpawnY),
                    fromState: true);
            } else {
                tracker.RestartAtAppearance(r.Scope, r.RoomBefore);
            }

            Rows.Clear();
            Leave = null;
            // this frame's events start from the level the load left
            LastRoom = r.RoomBefore;
            LastLaunching = r.PlayerBefore.Launching;
            LastFall = r.PlayerBefore.Fall;
            LastControl = r.PlayerBefore.Control;
            LastStopped = r.StoppedBefore;
            LastCompleted = r.CompletedBefore;
            feeds = r.Scope != null;
        } else if (load == RoomLoad.Loader && ending) {
            // the leave and what is set aside wait for the level entered after
            // it. The rows shown do not: TierComparison dismisses them in the
            // credits (not the room they closed in), and the serial that
            // would bring them back has not moved
        } else if (load == RoomLoad.Loader) {
            // the spawn tested is the one the player appears at (an intro
            // starts off screen). A checkpoint entered from chapter select
            // counts without its wake-up
            Func<SegmentRule, bool> atStart = game.AtRespawnPoint();
            bool chapterLoad = !LoaderIntroRespawn;
            PendingLeave left = Leave;
            Leave = null;
            bool reentry = left != null && IsReentry(left, in r, game);
            if (!reentry) {
                Rows.Clear();
            }

            if (ChapterRestarted && RestartedArea == r.Area) {
                // the rows ending on Restart Chapter close first, on the old
                // session's time; the restart then drops the rest unrecorded
                Via = "RestartChapter";
                Emit(game, tracker.ChapterRestarted(RestartedTime, r.End));
            }

            // a re-entry carries on from the reading before orig: chapter
            // time counted before control comes back is the row's
            if (reentry) {
                // A Save and Quit and Continue always carries on: every row stays
                Via = "Reentry";
                bool carried = tracker.Reentered(left.Kind, r.Scope, r.Room, r.Before, player.Control, player.Launching,
                    atStart, chapterLoad);
                Rows.Reentered(carried, left.Serial, left.Kind == LeaveKind.SaveAndQuit, r.Room);
            } else {
                Via = "LevelLoad";
                tracker.Restart(r.Scope, r.Room, r.Before, player.Control, player.Launching, atStart,
                    chapterLoad: chapterLoad);
            }

            restarted = true;
        } else if (teleported) {
            // Speed Run Tool's room teleport skips rooms without lowering chapter
            // time: a new attempt from the spawn it put the player on
            Via = "Teleport";
            tracker.Restart(r.Scope, r.Room, r.Before, player.Control, player.Launching, game.AtRespawnPoint());
            Rows.Clear();
            Leave = null;
            restarted = true;
        } else if (!FedLastFrame) {
            // switched back on: whatever was open missed events
            Via = "FeedingResumed";
            tracker.Drop();
            Rows.Clear();
            watched = false;
        } else if (r.After < r.Before) {
            // Level.Reload zeroes chapter time on a death in the first room
            // with nothing collected: a new attempt from the spawn, which
            // without control waits for ControlReturned. Only that death
            // lets a chapter row from the map keep the time read before it
            Func<SegmentRule, bool> spawn = game.AtRespawnPoint();
            bool firstRoomDeath = load == RoomLoad.Respawn && r.Room == LastRoom;
            Via = firstRoomDeath ? "FirstRoomDeath" : "ClockReset";
            // a chapter row kept through it keeps the rows of its pieces
            bool kept = firstRoomDeath
                        && tracker.ClockZeroed(r.Scope, r.Room, r.Before, r.After, player.Control, player.Launching, spawn);
            if (!firstRoomDeath) {
                tracker.Restart(r.Scope, r.Room, r.After, player.Control, player.Launching, spawn);
            }

            if (!kept) {
                Rows.Clear();
            }

            Leave = null;
            restarted = true;
        } else {
            feeds = r.Scope != null;
        }

        return new FrameOutcome(feeds, restarted, watched, teleported, load, tasFrame, ending);
    }

    // closes before opens: the stop edge, then the room entry (which closes
    // before it opens), then the starts waiting
    private void FeedEdges(in FrameReading r, IFrameGame game, RoomLoad load) {
        PlayerSide player = r.Player;
        if (player.Fall && !LastFall) {
            // 6A's watched fall is not a run of 6a Start: disqualified, not
            // dropped, so it still closes in Lake's start room and Lake
            // opens there
            Via = "ReflectionFall";
            tracker.Disqualify();
        }

        // The chapter's end takes the reading after orig, as chapter time
        // does. Before 3.29.5, SpeedrunTool stops a frame short where the
        // area completes inside the frame's update: the Prologue, 5A,
        // Farewell's final boost, and a heart before 3.29.4.
        //
        // Completed turning true is the chapter's end, and a leave: fed
        // once, here, so the exit that follows adds nothing. TimerStopped
        // alone closes and is no leave: every LoadLevel resets it
        if (r.Completed && !LastCompleted) {
            Via = "ChapterEnd";
            FeedLeave(game, LeaveKind.ChapterEnd, r.After, r.End);
        } else if (r.Stopped && !LastStopped) {
            Via = "TimerStopped";
            Emit(game, tracker.ChapterTimeStopped(r.After, r.End));
        }

        // a room entry is timed from the reading before the update
        if (r.Room != LastRoom && load == RoomLoad.WalkIn) {
            bool opensNow = player.Control || (player.Present && !player.Arriving);
            Via = "WalkIn";
            Emit(game, tracker.RoomEntered(r.Scope, r.SavedBefore.From, r.Room, r.Before, opensNow, player.Launching,
                r.End));
        }

        // the state changes during this frame's update, and a savestate
        // saved at the end of this frame times from the next frame: the
        // start reading is the one after it
        if (LastLaunching && !player.Launching) {
            Via = "LaunchEnded";
            tracker.LaunchEnded(r.Scope, r.Room, r.After);
        }

        if (!LastControl && player.Control) {
            Via = "ControlReturned";
            tracker.ControlReturned(r.Room, r.After, game.AtPlayer());
        }
    }

    /// A frame srs does not feed (switched off, no SaveLoad, an assist),
    /// before the game's update.
    public void OffBeforeUpdate() {
        FedLastFrame = false;
        StateSaved = false;
    }

    /// The same frame after the update: a LoadLevel or a restart inside it
    /// would have set these. Moved is set on every frame off, so a savestate
    /// made or loaded meanwhile opens nothing; during 6A's watched fall the
    /// room is not where a walk-in comes from.
    public SavedValues OffAfterUpdate(SavedValues saved, string room, bool fall) {
        RoomLoad = RoomLoad.None;
        ChapterRestarted = false;
        Leave = null;
        AfterEnding = false;
        return saved with { Moved = true, From = fall ? saved.From : room };
    }

    // whether this level from the loader carries on from the leave: the same
    // chapter and side, and after a Save and Quit the room that was left with
    // chapter time where the leave read it (the saved session resumed;
    // another file or a console load reads otherwise). After a Return to Map
    // or a chapter's end, a checkpoint's own spawn, the chapter's first room
    // excluded: entering at the chapter's start is a new attempt for everything.
    // A split whose mod announced no load was not left by this level
    private static bool IsReentry(PendingLeave left, in FrameReading r, IFrameGame game) {
        if (left.Area != r.Area || left.NoLoadYet) {
            return false;
        }

        if (left.Kind == LeaveKind.SaveAndQuit) {
            return r.Room == left.Room && r.Before == left.Reading;
        }

        return game.AtCheckpointSpawnPastStartRoom();
    }

    // why nothing is fed (null while it is), for the trace
    private string notFed;

    /// Whether why nothing is fed changed: the trace says so once per edge,
    /// never per frame. Null: fed.
    public bool FeedingIs(string reason) {
        if (reason == notFed) {
            return false;
        }

        notFed = reason;
        return true;
    }

    /// An event is fed only with srs on and Speed Run Tool's SaveLoad bound,
    /// once a frame was fed, and not between a load and its first frame.
    private bool Accepts(bool on, long savedStamp) => on && FedLastFrame && savedStamp == Stamp;

    /// Every room load. The kinds are RoomLoad's; several in one gap, the
    /// strongest wins.
    public void LevelLoaded(bool fromLoader, bool respawnIntro) {
        if (fromLoader) {
            LoaderIntroRespawn = respawnIntro;
        }

        RoomLoad = RoomLoads.Add(RoomLoad, RoomLoads.Of(fromLoader, respawnIntro, Reloading));
    }

    /// A new session made from the old one, into a room or none. Restart
    /// Chapter makes it with no room, with or without the wipe; a golden
    /// berry restart names its room and is none. Time is the old one's last.
    public void Restarted(string intoRoom, long time, (int Id, int Mode) area) {
        if (intoRoom != null) {
            return;
        }

        ChapterRestarted = true;
        RestartedTime = time;
        RestartedArea = area;
    }

    /// The game's Save and Quit or Return to Map, raised inside the level's
    /// last update. A Return to Map taken in the ending is the chapter's end
    /// that already pends: a second leave would cost the run its rows at the
    /// pick. An exit from the Epilogue 7A's credits end in would replace the
    /// leave still pending from 7A's end
    public void LevelExit(bool saveAndQuit, bool on, long savedStamp, IFrameGame game) {
        if (AfterEnding || !Accepts(on, savedStamp)) {
            return;
        }

        if (!saveAndQuit && Leave is { Kind: LeaveKind.ChapterEnd }) {
            return;
        }

        Via = "LevelExit";
        FeedLeave(game, saveAndQuit ? LeaveKind.SaveAndQuit : LeaveKind.ReturnToMap);
    }

    /// SomeSplitButtons' split buttons: Split when one lands, then Loading
    /// (the mod loads a level for it, on this frame) or Stayed (it loads
    /// nothing). A Save and Quit split is a leave only once it loads: one
    /// that stays closes nothing, but leaves what is open unsure of its note.
    /// A Return to Map split is one where it lands, list or not, unless a
    /// chapter's end pends, which it would replace
    public void SplitStage(string action, string stage, bool on, long savedStamp, IFrameGame game) {
        if (AfterEnding || !Accepts(on, savedStamp)) {
            return;
        }

        if (action == "SaveAndQuit" && stage == "Stayed") {
            Via = "SplitSaveAndQuitStayed";
            tracker.SplitStayed();
        } else if (action == "SaveAndQuit" && stage == "Loading") {
            Via = "SplitSaveAndQuitLoading";
            FeedLeave(game, LeaveKind.SaveAndQuit);
        } else if (action == "ReturnToMap" && stage == "Split" && Leave == null) {
            Via = "SplitReturnToMap";
            FeedLeave(game, LeaveKind.ReturnToMap, noLoadYet: true);
        } else if (action == "ReturnToMap" && Leave is { NoLoadYet: true }) {
            if (stage == "Loading") {
                Leave = Leave with { NoLoadYet = false };
            } else if (stage == "Stayed") {
                // no list, or one closed without a pick: no level was left,
                // and what closed keeps its record, up until the next row closes
                Rows.KeepUntilNextRow();
                Leave = null;
                Via = "SplitReturnToMapStayed";
                tracker.Drop();
            }
        }
    }

    /// A cassette, a gem or a heart. Inside an update, timed from the
    /// frame's start reading, as a room entry is; between two, now.
    public void Collected(Collectibles kind, bool on, long savedStamp, IFrameGame game) {
        if (!Accepts(on, savedStamp)) {
            return;
        }

        tracker.Rooms = game.Rooms;
        Via = kind switch {
            Collectibles.Heart => "Heart",
            Collectibles.Cassette => "Cassette",
            _ => "Gem",
        };
        long reading = InUpdate ? FrameStart : game.Time;
        Emit(game, tracker.Collected(kind, reading, game.End()));
    }

    /// A berry banked: it stays banked through a death.
    public void BerryCollected(string berry, bool on, long savedStamp) {
        if (Accepts(on, savedStamp)) {
            tracker.BerryCollected(berry);
        }
    }

    /// The master switch moved: a level left before it is not carried on from.
    public void Forget() {
        Via = "SwitchMoved";
        Leave = null;
        tracker.Drop();
    }

    // a leave read off the level as it stands
    private void FeedLeave(IFrameGame game, LeaveKind kind, bool noLoadYet = false) {
        tracker.Rooms = game.Rooms;
        FeedLeave(game, kind, game.Time, game.End(), noLoadYet);
    }

    // the serial is read before the leave closes anything: a re-entry keeps
    // the records after it
    public void FeedLeave(IFrameGame game, LeaveKind kind, long reading, EndState end, bool noLoadYet = false) {
        int serial = Rows.LatestSerial;
        Emit(game, tracker.Left(kind, reading, end));
        Leave = new PendingLeave(kind, game.Area, game.Room, reading, serial, noLoadYet);
    }

    /// Records closed: kept, offered, and added to the rows. A row closing
    /// while a TAS plays, a collect or a split between two frames included,
    /// is never recorded.
    public void Emit(IFrameGame game, List<SegmentRecord> records) {
        if (records.Count == 0) {
            return;
        }

        if (game.TasPlaying) {
            foreach (SegmentRecord record in records) {
                trace?.Invoke(new TraceEvent(TraceKind.NotRecorded, record.Rule, TraceReason.TasPlayed, null, record));
            }

            return;
        }

        if (game.NotesUnknown) {
            for (int i = 0; i < records.Count; i++) {
                records[i] = records[i] with { Note = SaveAndQuitNote.Unknown, ReturnToMap = ReturnToMapNote.Unknown };
            }
        }

        game.Record(records);
        Rows.Add(records, game.Room, tracker.Rooms.BerriesOf);
    }
}
