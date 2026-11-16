using System;
using System.Collections.Generic;
using Celeste.Mod.SpeedrunTool;
using Microsoft.Xna.Framework;
using Monocle;
using MonoMod.ModInterop;

namespace Celeste.Mod.SpeedrunSheet;

// Feeds RunTracker from the game: chapter time, how each room was reached, the
// player's control, the collects, the frame chapter time stops and Restart
// Chapter.
public static class RunWatcher {
    // a room walked into in one of these waits for control: a wake-up is the
    // "Wake Up" row's, and 6A's watched fall is no row's. Any other room
    // entered without control is a cutscene, which is chapter time
    private static readonly HashSet<int> ArrivalStates = [
        Player.StIntroWalk, Player.StIntroRespawn, Player.StIntroWakeUp, Player.StReflectionFall,
        Player.StTempleFall, Player.StIntroMoonJump, Player.StIntroThinkForABit,
    ];

    // the player has no control in these states: a start reached in one waits
    // for control in that room. ⚠️ StIntroJump must stay out: 7A's 500m to
    // 3000m are entered in it, from the launch, and open on the entry
    private static readonly HashSet<int> NoControlStates = [
        Player.StDummy, Player.StIntroWalk, Player.StIntroRespawn,
        Player.StIntroWakeUp, Player.StBirdDashTutorial, Player.StFrozen, Player.StReflectionFall,
        Player.StTempleFall, Player.StIntroMoonJump, Player.StIntroThinkForABit,
    ];

    // with Speed Run Tool's "freeze after load" Off, a player load counts the
    // frame its own wipe ends on, which Session.Time does not. A TAS load
    // starts no wipe, and restores the one its state was saved under
    private static readonly long WipeEndFrame = TimeSpan.FromMilliseconds(17).Ticks;

    // every never-saved piece of state, the tracker and the rows above the timer
    private static readonly FrameFeed feed = new();
    private static RunTracker tracker => feed.Tracker;

    // the trace, at Verbose: why nothing is fed (null while it is)
    private static string notFed;

    // game state, saved and restored with each savestate: a stamp written on
    // every fed frame (a load puts back an older one, which is how a load is
    // seen), the room the next walk-in comes from, and where the player last
    // appeared, exactly, with whether they have moved since
    private static class Saved {
        public static long Stamp;
        public static string From;
        public static Vector2 SpawnAt;
        public static bool Moved = true;
    }

    private static object saveLoadAction;
    private static object wipeAction;
    // the wipe the last load restored with the level, before a player load
    // starts its own; read and cleared on the next frame
    private static ScreenWipe restoredWipe;
    // never saved: the edges against the last fed frame
    private static string lastRoom;
    private static int lastState = -1;
    private static bool lastControl = true;
    private static bool lastStopped;
    private static bool lastCompleted;

    // the berries following the player, rebuilt on every fed frame and read
    // inside it only
    private static readonly List<string> following = [];

    // the rows above the timer, which TierComparison reads
    internal static IReadOnlyList<(SegmentRecord Record, int Serial)> Attempt => feed.Rows.Rows;
    internal static string LatestRoom => feed.Rows.LatestRoom;
    internal static int LatestSerial => feed.Rows.LatestSerial;

    // fields are filled at runtime by ModInterop()
#pragma warning disable CS0649
    [ModImportName("SpeedrunTool.SaveLoad")]
    private static class SaveLoadImports {
        public static Func<Type, string[], object> RegisterStaticTypes;

        // saveState, loadState, clearState, beforeSaveState, beforeLoadState, preCloneEntities
        public static Func<Action<Dictionary<Type, Dictionary<string, object>>, Level>,
            Action<Dictionary<Type, Dictionary<string, object>>, Level>, Action, Action<Level>, Action<Level>,
            Action, object> RegisterSaveLoadAction;

        public static Action<object> Unregister;
    }

    [ModImportName("SomeSplitButtons")]
    private static class SplitImports {
        // observer(action, stage, frame)
        public static Action<Action<string, string, ulong>> AddSplitObserver;
        public static Action<Action<string, string, ulong>> RemoveSplitObserver;
        public static Func<int> InteropVersion;
    }

    [ModImportName("CelesteTAS")]
    private static class TasImports {
        public static Func<bool> IsTasActive;
    }
#pragma warning restore CS0649

    private static bool observing;
    private static bool interopsLogged;

    // a Debug build started with this marker file in place also records
    // rows a TAS played
#if DEBUG
    private static readonly bool testCountsTasRuns =
        System.IO.File.Exists(System.IO.Path.Combine(Everest.PathSettings, "srs", "test-auto-export-tas"));
#else
    private const bool testCountsTasRuns = false;
#endif

    private static bool TasPlaying() => !testCountsTasRuns && TasImports.IsTasActive?.Invoke() == true;

    public static void Load() {
        On.Celeste.Level.LoadLevel += LevelOnLoadLevel;
        On.Celeste.Level.Reload += LevelOnReload;
        On.Celeste.Session.Restart += SessionOnRestart;
        On.Celeste.SaveData.RegisterCassette += OnRegisterCassette;
        On.Celeste.SaveData.RegisterSummitGem += OnRegisterSummitGem;
        On.Celeste.HeartGem.RegisterAsCollected += OnRegisterHeart;
        // Hook: a berry banked, for the rows that require it
        On.Celeste.Strawberry.OnCollect += OnBerryCollect;
        // Hook: the game's Save and Quit and Return to Map, raised by
        // LevelExit's constructor while the level is still the scene
        Everest.Events.Level.OnExit += OnLevelExit;

        typeof(SaveLoadImports).ModInterop();
        saveLoadAction = SaveLoadImports.RegisterStaticTypes?.Invoke(typeof(Saved),
            [nameof(Saved.Stamp), nameof(Saved.From), nameof(Saved.SpawnAt), nameof(Saved.Moved)]);
        if (saveLoadAction == null) {
            Logger.Log(LogLevel.Warn, "srs", "Speed Run Tool's SaveLoad did not bind: savestate loads cannot be seen, so srs records nothing");
        }

        // saveState runs from Speed Run Tool's hotkey, before the level
        // updates, which stays frozen until the save's wipe ends: the next
        // fed frame reads the level as saved, as a load of it would.
        // loadState runs once the level is restored, before a player load's
        // wipe starts. clearState stays unwired: clearing a slot rewinds
        // nothing, and a TAS may clear the slots as it starts
        wipeAction = SaveLoadImports.RegisterSaveLoadAction?.Invoke(
            (_, _) => feed.StateSaved = true,
            (_, level) => restoredWipe = level.Wipe, null, null, null, null);

        typeof(SplitImports).ModInterop();
        typeof(TasImports).ModInterop();

        feed.StartTracker(SegmentRules.All, WriteTrace);
    }

    // no line is built unless the tag logs at Verbose (`setloglevel srs verbose`)
    private static bool Tracing => Logger.GetLogLevel("srs") <= LogLevel.Verbose;

    private static void WriteTrace(TraceEvent e) {
        if (Tracing) {
            // the notes Emit stores
            if (NotesUnknown && e.Record is { } record) {
                e = e with { Record = record with { Note = SaveAndQuitNote.Unknown, ReturnToMap = ReturnToMapNote.Unknown } };
            }

            Logger.Log(LogLevel.Verbose, "srs", TraceLine.Format(e, feed.Via));
        }
    }

    // once per edge, never per frame
    private static void FeedingIs(string reason) {
        if (reason != notFed) {
            if (Tracing) {
                Logger.Log(LogLevel.Verbose, "srs", $"trace Feeding reason={reason ?? "Resumed"}");
            }

            notFed = reason;
        }
    }

    public static void Unload() {
        On.Celeste.Level.LoadLevel -= LevelOnLoadLevel;
        On.Celeste.Level.Reload -= LevelOnReload;
        On.Celeste.Session.Restart -= SessionOnRestart;
        On.Celeste.SaveData.RegisterCassette -= OnRegisterCassette;
        On.Celeste.SaveData.RegisterSummitGem -= OnRegisterSummitGem;
        On.Celeste.HeartGem.RegisterAsCollected -= OnRegisterHeart;
        On.Celeste.Strawberry.OnCollect -= OnBerryCollect;
        Everest.Events.Level.OnExit -= OnLevelExit;

        if (saveLoadAction != null) {
            SaveLoadImports.Unregister?.Invoke(saveLoadAction);
            saveLoadAction = null;
        }

        if (wipeAction != null) {
            SaveLoadImports.Unregister?.Invoke(wipeAction);
            wipeAction = null;
        }

        SplitImports.RemoveSplitObserver?.Invoke(OnSplitStage);
        observing = false;

        restoredWipe = null;
        feed.Leave = null;
    }

    // every room load passes here; the kinds are RoomLoad's. A cutscene's room
    // change (2A's dream, the mirrors, 6A's fall, Farewell's intro) is a
    // walk-in. Some of them run in OnEndOfFrame, after the update hook: the
    // next fed frame reads the kind
    private static void LevelOnLoadLevel(On.Celeste.Level.orig_LoadLevel orig, Level self,
        Player.IntroTypes playerIntro, bool isFromLoader) {
        orig(self, playerIntro, isFromLoader);
        feed.LevelLoaded(isFromLoader, playerIntro == Player.IntroTypes.Respawn);

        // here and not in Load: by a level every mod is loaded, whatever the
        // order. Below version 3 no stage says when a split lands
        if (isFromLoader && !interopsLogged) {
            interopsLogged = true;
            InteropReport.Log(TasImports.IsTasActive != null, SplitImports.InteropVersion?.Invoke());
        }

        if (isFromLoader && !observing && SplitImports.AddSplitObserver != null
            && SplitImports.InteropVersion?.Invoke() >= 3) {
            SplitImports.AddSplitObserver(OnSplitStage);
            observing = true;
        }
    }

    private static void LevelOnReload(On.Celeste.Level.orig_Reload orig, Level self) {
        feed.Reloading = true;
        try {
            orig(self);
        } finally {
            feed.Reloading = false;
        }
    }

    // Restart Chapter makes the new session with no room, with or without the
    // wipe; a golden berry restart names its room. Nothing updates the old
    // session afterwards, so its time is the run's last chapter time
    private static Session SessionOnRestart(On.Celeste.Session.orig_Restart orig, Session self, string intoLevel) {
        Session restarted = orig(self, intoLevel);
        if (intoLevel == null) {
            feed.Restarted(self.Time, (self.Area.ID, (int)self.Area.Mode));
        }

        return restarted;
    }

    /// Any assist or variant but Play as Badeline, which changes nothing of
    /// the run. The game resets them all on a file with neither mode on.
    private static bool Assisted() {
        if (SaveData.Instance == null) {
            return false;
        }

        // field by field: Equals on a struct boxes, on every frame
        Assists a = SaveData.Instance.Assists;
        return a.GameSpeed != 10 || a.Invincible || a.DashMode != Assists.DashModes.Normal || a.DashAssist
            || a.InfiniteStamina || a.MirrorMode || a.ThreeSixtyDashing || a.InvisibleMotion || a.NoGrabbing
            || a.LowFriction || a.SuperDashing || a.Hiccups;
    }

    /// SrsModule's Level.Update hook, first in the frame. Hotkeys.Poll follows
    /// each call to orig at once: the rows read its Pressed after this returns.
    internal static void Update(On.Celeste.Level.orig_Update orig, Level self) {
        // switched off, without Speed Run Tool's SaveLoad, or under an assist
        // or a variant: nothing is fed. Moved is set on every frame off, so a
        // savestate made or loaded meanwhile opens nothing
        if (!SrsModule.Settings.Enabled || saveLoadAction == null || Assisted()) {
            FeedingIs(!SrsModule.Settings.Enabled ? "SwitchedOff" : saveLoadAction == null ? "NoSaveLoad" : "Assisted");
            // cleared after orig too: a LoadLevel or a restart inside it would set them
            feed.FedLastFrame = false;
            restoredWipe = null;
            feed.StateSaved = false;
            orig(self);
            Hotkeys.Poll(self);
            feed.RoomLoad = RoomLoad.None;
            feed.ChapterRestarted = false;
            feed.Leave = null;
            feed.AfterEnding = false;
            Saved.Moved = true;
            if (self.Tracker.GetEntity<Player>()?.StateMachine.State != Player.StReflectionFall) {
                Saved.From = self.Session.Level;
            }

            return;
        }

        // before orig: the reading a room entry is timed from, and the level
        // and Saved as a load left them, before this frame can move the player
        bool loaded = Saved.Stamp != feed.Stamp;
        if (loaded) {
            // a room loaded before the load belongs to the timeline it left
            feed.RoomLoad = RoomLoad.None;
        }

        // making a savestate is a new attempt too, as if it were loaded at once
        bool saved = feed.StateSaved && !loaded;
        feed.StateSaved = false;

        long before = self.Session.Time;
        Player playerBefore = self.Tracker.GetEntity<Player>();
        LevelData dataBefore = self.Session.LevelData;
        string roomBefore = self.Session.Level;
        int stateBefore = playerBefore?.StateMachine.State ?? -1;
        Vector2? exactBefore = playerBefore?.ExactPosition;
        bool controlBefore = playerBefore != null && !self.InCutscene && !NoControlStates.Contains(stateBefore);
        bool stoppedBefore = self.TimerStopped || self.Completed;
        bool completedBefore = self.Completed;
        // a player load's own wipe
        bool loadWiping = self.Wipe != null && self.Wipe != restoredWipe;
        restoredWipe = null;
        string from = Saved.From;
        bool movedAtLoad = Saved.Moved;
        Vector2 spawnAtLoad = Saved.SpawnAt;

        FeedingIs(null);
        bool tasBefore = TasPlaying();
        feed.FrameStart = before;
        feed.InUpdate = true;
        orig(self);
        Hotkeys.Poll(self);
        feed.InUpdate = false;

        // a row a TAS played one frame of is never recorded, whoever ends it:
        // a TAS may hand control back mid-row (QuicksaveMod replays one from
        // the room's start). Marked before this frame's closes, which Emit
        // lets through once the TAS has stopped, and again last, so a row
        // opened this frame is marked too
        bool tasFrame = tasBefore || TasPlaying();
        if (tasFrame) {
            feed.Via = "Tas";
            tracker.TasPlayed();
        }

        Session session = self.Session;
        long after = session.Time;
        string scope = SegmentAutoDetect.ScopeOf(session);
        string room = session.Level;
        Player player = self.Tracker.GetEntity<Player>();
        int state = player?.StateMachine.State ?? -1;
        bool control = player != null && !self.InCutscene && !NoControlStates.Contains(state);
        bool launching = state == Player.StIntroJump;
        bool stopped = self.TimerStopped || self.Completed;
        bool completed = self.Completed;
        // the Epilogue counts as an ending only while the last level updated
        // was one: afterEnding is rewritten by a level update only, so one
        // entered again after the credits stays transparent
        bool ending = self.InCredits || (feed.AfterEnding && session.Area.ID == 8);
        tracker.Rooms = RoomMap.For(session);
        EndState end = EndOf(session, player);
        RoomLoad load = feed.RoomLoad;
        feed.RoomLoad = RoomLoad.None;
        // a room change no LoadLevel walked into is a teleport too. Not on a
        // load: the state's room is not a room change
        bool teleported = Saved.Stamp == feed.Stamp
                          && (load == RoomLoad.Teleport
                              || (feed.FedLastFrame && room != lastRoom && load < RoomLoad.WalkIn));

        bool feeds = false;
        // a new attempt landed this frame, so the player appears now if they
        // have control
        bool restarted = false;
        // this frame's control and state edges are the player's own: not on a
        // load inside orig, nor on the frame srs is switched back on
        bool watched = true;
        if (!loaded && Saved.Stamp != feed.Stamp) {
            // a load inside orig: this frame's events would mix two timelines
            feed.Via = "LoadInFrame";
            tracker.Drop();
            feed.Rows.Clear();
            feed.Leave = null;
            watched = false;
        } else if (loaded || saved) {
            // every load is a new attempt. With control, the room's Current Room
            // segment opens if the player has not moved since appearing on its
            // start spawn; without, it waits for the appearance that follows.
            // A save starts its wipe a frame later (a TAS's starts none, but
            // a row a TAS plays is never recorded)
            bool wipeEnd = (saved || loadWiping)
                           && SpeedrunToolSettings.Instance?.FreezeAfterLoadStateType == FreezeAfterLoadStateType.Off;
            feed.Via = loaded ? "SavestateLoad" : "SavestateMade";
            if (controlBefore) {
                tracker.Restart(scope, roomBefore, before - (wipeEnd ? WipeEndFrame : 0), true,
                    stateBefore == Player.StIntroJump,
                    movedAtLoad ? _ => false : AtStartSpawn(session, dataBefore, spawnAtLoad), fromState: true);
            } else {
                tracker.RestartAtAppearance(scope, roomBefore);
            }

            feed.Rows.Clear();
            feed.Leave = null;
            // this frame's events start from the level the load left
            lastRoom = roomBefore;
            lastState = stateBefore;
            lastControl = controlBefore;
            lastStopped = stoppedBefore;
            lastCompleted = completedBefore;
            feeds = scope != null;
        } else if (load == RoomLoad.Loader && ending) {
            // the leave and what is set aside wait for the level entered after
            // it. The rows shown do not: TierComparison dismisses them in the
            // credits (not the room they closed in), and the serial that
            // would bring them back has not moved
        } else if (load == RoomLoad.Loader) {
            // the spawn tested is the one the player appears at (an intro
            // starts off screen). A checkpoint entered from chapter select
            // counts without its wake-up
            Func<SegmentRule, bool> atStart = AtStartSpawn(session, session.LevelData, session.RespawnPoint);
            bool chapterLoad = !feed.LoaderIntroRespawn;
            FrameFeed.PendingLeave left = feed.Leave;
            feed.Leave = null;
            bool reentry = left != null && IsReentry(left, session, before);
            if (!reentry) {
                feed.Rows.Clear();
            }

            if (feed.ChapterRestarted && feed.RestartedArea == (session.Area.ID, (int)session.Area.Mode)) {
                // the rows ending on Restart Chapter close first, on the old
                // session's time; the restart then drops the rest unrecorded
                feed.Via = "RestartChapter";
                feed.Emit(game.At(self), tracker.ChapterRestarted(feed.RestartedTime, end));
            }

            // a re-entry carries on from the reading before orig: chapter
            // time counted before control comes back is the row's
            if (reentry) {
                // A Save and Quit and Continue always carries on: every row stays
                feed.Via = "Reentry";
                bool carried = tracker.Reentered(left.Kind, scope, room, before, control, launching, atStart,
                    chapterLoad);
                feed.Rows.Reentered(carried, left.Serial, left.Kind == LeaveKind.SaveAndQuit, room);
            } else {
                feed.Via = "LevelLoad";
                tracker.Restart(scope, room, before, control, launching, atStart, chapterLoad: chapterLoad);
            }

            restarted = true;
        } else if (teleported) {
            // Speed Run Tool's room teleport skips rooms without lowering chapter
            // time: a new attempt from the spawn it put the player on
            feed.Via = "Teleport";
            tracker.Restart(scope, room, before, control, launching,
                AtStartSpawn(session, session.LevelData, session.RespawnPoint));
            feed.Rows.Clear();
            feed.Leave = null;
            restarted = true;
        } else if (!feed.FedLastFrame) {
            // switched back on: whatever was open missed events
            feed.Via = "FeedingResumed";
            tracker.Drop();
            feed.Rows.Clear();
            watched = false;
        } else if (after < before) {
            // Level.Reload zeroes chapter time on a death in the first room
            // with nothing collected: a new attempt from the spawn, which
            // without control waits for ControlReturned. Only that death
            // lets a chapter row from the map keep the time read before it
            Func<SegmentRule, bool> spawn = AtStartSpawn(session, session.LevelData, session.RespawnPoint);
            bool firstRoomDeath = load == RoomLoad.Respawn && room == lastRoom;
            feed.Via = firstRoomDeath ? "FirstRoomDeath" : "ClockReset";
            // a chapter row kept through it keeps the rows of its pieces
            bool kept = firstRoomDeath && tracker.ClockZeroed(scope, room, before, after, control, launching, spawn);
            if (!firstRoomDeath) {
                tracker.Restart(scope, room, after, control, launching, spawn);
            }

            if (!kept) {
                feed.Rows.Clear();
            }

            feed.Leave = null;
            restarted = true;
        } else {
            feeds = scope != null;
        }

        // the credits set Completed again and change room: neither is the run's
        if (feeds && !ending) {
            if (state == Player.StReflectionFall && lastState != Player.StReflectionFall) {
                // 6A's watched fall is not a run of 6a Start: disqualified, not
                // dropped, so it still closes in Lake's start room and Lake
                // opens there
                feed.Via = "ReflectionFall";
                tracker.Disqualify();
            }

            // closes before opens: the stop edge, then the room entry (which
            // closes before it opens), then the starts waiting. The chapter's
            // end takes the reading after orig, as chapter time does; Speed
            // Run Tool's display stops a frame short there.
            //
            // Completed turning true is the chapter's end, and a leave: fed
            // once, here, so the exit that follows adds nothing. TimerStopped
            // alone closes and is no leave: every LoadLevel resets it
            if (completed && !lastCompleted) {
                feed.Via = "ChapterEnd";
                feed.FeedLeave(game.At(self), LeaveKind.ChapterEnd, after, end);
            } else if (stopped && !lastStopped) {
                feed.Via = "TimerStopped";
                feed.Emit(game.At(self), tracker.ChapterTimeStopped(after, end));
            }

            if (room != lastRoom && load == RoomLoad.WalkIn) {
                bool opensNow = control || (player != null && !ArrivalStates.Contains(state));
                feed.Via = "WalkIn";
                feed.Emit(game.At(self), tracker.RoomEntered(scope, from, room, before, opensNow, launching, end));
            }

            // the state changes during this frame's update, and a savestate
            // saved at the end of this frame times from the next frame: the
            // start reading is the one after it
            if (lastState == Player.StIntroJump && !launching) {
                feed.Via = "LaunchEnded";
                tracker.LaunchEnded(scope, room, after);
            }

            if (!lastControl && control) {
                feed.Via = "ControlReturned";
                tracker.ControlReturned(room, after, AtStartSpawn(session, session.LevelData, player?.ExactPosition));
            }
        }

        // the player appears when control returns, when the intro jump ends
        // (1A's too), and when a restart lands with control; a load restores
        // the last appearance
        bool appeared = restarted
            ? control
            : watched && ((!lastControl && control) || (lastState == Player.StIntroJump && !launching));
        bool disturbed = load is RoomLoad.Respawn or RoomLoad.Teleport or RoomLoad.Loader || teleported || !feed.FedLastFrame;
        (float X, float Y)? at = player == null ? null : (player.ExactPosition.X, player.ExactPosition.Y);
        // a restart that has control already appears where the frame began, so
        // a direction held into its first frame is a move
        (float X, float Y)? appearedAt = restarted && control && exactBefore is { } start ? (start.X, start.Y) : at;
        Stillness still = new Stillness(Saved.SpawnAt.X, Saved.SpawnAt.Y, Saved.Moved)
            .After(disturbed, appeared, appearedAt)
            .After(false, false, at);
        Saved.SpawnAt = new Vector2(still.X, still.Y);
        Saved.Moved = still.Moved;

        if (tasFrame) {
            feed.Via = "Tas";
            tracker.TasPlayed();
        }

        Saved.Stamp = ++feed.Stamp;
        // during 6A's watched fall the rooms passed through are not where the
        // run comes from: 00 is entered from start, as after either skip
        if (restarted || !watched || state != Player.StReflectionFall) {
            Saved.From = room;
        }

        lastRoom = room;
        lastState = state;
        lastControl = control;
        lastStopped = stopped;
        lastCompleted = completed;
        feed.AfterEnding = ending;
        feed.ChapterRestarted = false;
        feed.FedLastFrame = true;
    }

    // a savestate further into the room does not play it: the point must be on
    // a start spawn, or at its SpawnOffsets offset. A "Start" row's are the
    // room's default spawn (the game's own, Level.DefaultSpawnPoint) and the
    // one beside each checkpoint. The session is read when the tracker asks,
    // inside the call that was handed the test
    private static Func<SegmentRule, bool> AtStartSpawn(Session session, LevelData data, Vector2? point) =>
        rule => point is { } at && AtStartSpawn(session, data, at, rule);

    private static bool AtStartSpawn(Session session, LevelData data, Vector2 point, SegmentRule rule) {
        if (data == null || data.Spawns.Count == 0) {
            return false;
        }

        (int x, int y) = SegmentAutoDetect.SpawnOffsets.GetValueOrDefault((rule.Scope, rule.Anchor));
        Vector2 offset = new(x, y);
        bool On(Vector2 spawn) => Near(point, spawn) || Near(point, spawn + offset);

        if (rule.Setup == StartSetup.MapSpawn) {
            return CheckpointSpawn(data) is { } spawn && On(spawn);
        }

        // the spawn a Save and Quit taken after the heart resumes on: the one
        // the room's respawn trigger sets on the way to it, never the room's
        // entrance. The heart is the session's, and which restart this is,
        // the tracker knows
        if (rule.Setup == StartSetup.HeartRespawn) {
            return session.HeartGem && RespawnTriggerSpawn(data) is { } respawn && On(respawn);
        }

        if (rule.Anchor != "Start") {
            return SegmentAutoDetect.WakeUpSpawns.TryGetValue((rule.Scope, rule.Anchor), out (int X, int Y) wakeUp)
                   && On(new Vector2(wakeUp.X, wakeUp.Y));
        }

        if (On(data.Spawns.ClosestTo(new Vector2(data.Bounds.Left, data.Bounds.Bottom)))) {
            return true;
        }

        foreach (EntityData entity in data.Entities) {
            if (entity.Name == "checkpoint" && On(data.Spawns.ClosestTo(data.Position + entity.Position))) {
                return true;
            }
        }

        return false;
    }

    // where the game puts the player when the room's checkpoint is picked on
    // the map: the spawn closest to the checkpoint's node, or to the checkpoint.
    // The game keeps the last checkpoint of a room
    private static Vector2? CheckpointSpawn(LevelData data) {
        Vector2? spawn = null;
        foreach (EntityData entity in data.Entities) {
            if (entity.Name == "checkpoint") {
                Vector2 at = entity.Nodes is { Length: > 0 } nodes ? nodes[0] : entity.Position;
                spawn = data.Spawns.ClosestTo(data.Position + at);
            }
        }

        return spawn;
    }

    // the spawn the room's respawn trigger makes the session's respawn point,
    // as ChangeRespawnTrigger picks it: the closest to its node, or to its
    // centre. A trigger's position and node are the room's own, a spawn is
    // the world's. One trigger is expected: of several, the last is taken,
    // which the game does not promise
    private static Vector2? RespawnTriggerSpawn(LevelData data) {
        Vector2? spawn = null;
        foreach (EntityData trigger in data.Triggers) {
            if (trigger.Name == "changeRespawnTrigger") {
                Vector2 at = trigger.Nodes is { Length: > 0 } nodes
                    ? nodes[0]
                    : trigger.Position + new Vector2(trigger.Width, trigger.Height) / 2f;
                spawn = data.Spawns.ClosestTo(data.Position + at);
            }
        }

        return spawn;
    }

    private static bool Near(Vector2 a, Vector2 b) => Math.Abs(a.X - b.X) <= 1f && Math.Abs(a.Y - b.Y) <= 1f;

    // raised inside orig on the level's last update, chapter time already
    // holding that frame's tick; gated like a collect. An exit built with no
    // level as the scene (the debug keys) is none of srs's; Restart and the
    // golden restart are seen at Session.Restart; a chapter's end was fed
    // when Completed turned true
    private static void OnLevelExit(Level level, LevelExit exit, LevelExit.Mode mode, Session session, HiresSnow snow) {
        if (level == null || mode is not (LevelExit.Mode.SaveAndQuit or LevelExit.Mode.GiveUp)) {
            return;
        }

        feed.LevelExit(mode == LevelExit.Mode.SaveAndQuit, Switched, Saved.Stamp, game.At(level));
    }

    // SomeSplitButtons' split stages, raised from its Level.Update hook or its
    // list's update, so after this frame was fed or inside orig; chapter time
    // stands still from Split to its outcome
    private static void OnSplitStage(string action, string stage, ulong _) {
        if (Engine.Scene is Level level) {
            feed.SplitStage(action, stage, Switched, Saved.Stamp, game.At(level));
        }
    }

    // whether this level from the loader carries on from the leave: the same
    // chapter and side, and after a Save and Quit the room that was left with
    // chapter time where the leave read it (the saved session resumed;
    // another file or a console load reads otherwise). After a Return to Map
    // or a chapter's end, a checkpoint's own spawn, the chapter's first room
    // excluded: entering at the chapter's start is a new attempt for everything.
    // A split whose mod announced no load was not left by this level
    private static bool IsReentry(FrameFeed.PendingLeave left, Session session, long reading) {
        if (left.Area != (session.Area.ID, (int)session.Area.Mode) || left.NoLoadYet) {
            return false;
        }

        if (left.Kind == LeaveKind.SaveAndQuit) {
            return session.Level == left.Room && reading == left.Reading;
        }

        return session.Level != session.MapData?.StartLevel()?.Name
               && session.RespawnPoint is { } at && session.LevelData is { } data
               && CheckpointSpawn(data) is { } spawn && Near(at, spawn);
    }

    /// The master switch moved: a level left before it is not carried on from.
    internal static void Forget() => feed.Forget();

    // a berry still following when a row ends counts for it
    private static EndState EndOf(Session session, Player player) {
        following.Clear();
        if (player != null) {
            foreach (Follower follower in player.Leader.Followers) {
                if (follower.Entity is Strawberry berry) {
                    following.Add(berry.ID.Key);
                }
            }
        }

        return new EndState(session.Inventory.Dashes, following);
    }

    private static void OnRegisterCassette(On.Celeste.SaveData.orig_RegisterCassette orig, SaveData self, AreaKey area) {
        orig(self, area);
        OnCollect(Collectibles.Cassette);
    }

    // called on every smash, a gem the file already holds included
    private static void OnRegisterSummitGem(On.Celeste.SaveData.orig_RegisterSummitGem orig, SaveData self, int id) {
        orig(self, id);
        OnCollect(Collectibles.Gem);
    }

    // a banked berry stays banked through a death; one lost while following
    // was never fed. Goldens pass here too, and no row asks for them
    private static void OnBerryCollect(On.Celeste.Strawberry.orig_OnCollect orig, Strawberry self) {
        orig(self);
        feed.BerryCollected(self.ID.Key, Switched, Saved.Stamp);
    }

    private static void OnRegisterHeart(On.Celeste.HeartGem.orig_RegisterAsCollected orig, HeartGem self, Level level, string poemId) {
        orig(self, level, poemId);
        OnCollect(Collectibles.Heart);
    }

    // collects land inside an update (entity updates) or between two
    private static void OnCollect(Collectibles kind) {
        if (Engine.Scene is Level level) {
            feed.Collected(kind, Switched, Saved.Stamp, game.At(level));
        }
    }

    // srs switched on, with Speed Run Tool's SaveLoad bound
    private static bool Switched => SrsModule.Settings.Enabled && saveLoadAction != null;

    // a split of an older SomeSplitButtons reports nothing: no record can
    // vouch for its note, so none claims one
    private static bool NotesUnknown => SplitImports.InteropVersion != null && !observing;

    // the level being updated or left, one object for all of them
    private static readonly LevelGame game = new();

    private sealed class LevelGame : IFrameGame {
        private Level level;

        public LevelGame At(Level at) {
            level = at;
            return this;
        }

        public IRoomMap Rooms => RoomMap.For(level.Session);
        public string Room => level.Session.Level;
        public (int Id, int Mode) Area => (level.Session.Area.ID, (int)level.Session.Area.Mode);
        public long Time => level.Session.Time;
        public EndState End() => EndOf(level.Session, level.Tracker.GetEntity<Player>());
        public bool TasPlaying => RunWatcher.TasPlaying();
        public bool NotesUnknown => RunWatcher.NotesUnknown;

        // the copy is all auto-export takes from the game thread
        public void Record(List<SegmentRecord> records) {
            if (SessionBests.Record(records, level.Session) && SrsModule.Settings.AutoExport) {
                AutoExport.Offer([.. SessionBests.All], SegmentAutoDetect.ScopeOf(level.Session));
            }
        }
    }
}
