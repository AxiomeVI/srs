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
}

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

    /// Restart Chapter made the new session: time is the old one's last.
    public void Restarted(long time, (int Id, int Mode) area) {
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
