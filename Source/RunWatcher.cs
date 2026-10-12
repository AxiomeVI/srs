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

    // how the room was loaded since the last fed frame, read from LoadLevel;
    // when several loads land in one gap, the strongest wins
    private enum RoomLoad {
        None,
        // Respawn from Level.Reload: a death
        Respawn,
        // any intro but Respawn: a transition, or a room change the game makes
        // in a cutscene
        WalkIn,
        // any other Respawn: Speed Run Tool's teleport, into another room or to
        // a summit flag of this one
        Teleport,
        // a level from the loader: entering a chapter, a restart, console load,
        // the debug map
        Loader,
    }

    private static RunTracker tracker;

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

    // never saved: what a load is compared against. stamp only grows, so a
    // saved stamp differs from it once a frame has been fed since the save
    private static long stamp;
    private static string lastRoom;
    private static int lastState = -1;
    private static bool lastControl = true;
    private static bool lastStopped;
    private static bool lastCompleted;
    private static bool fedLastFrame;
    private static RoomLoad roomLoad;
    // inside Level.Reload, the game's only LoadLevel(Respawn)
    private static bool reloading;

    // Restart Chapter, seen when the new session is made: the old session's
    // last chapter time, for the rows that end on it
    private static bool chapterRestarted;
    private static long restartedTime;
    private static AreaKey restartedArea;

    // the level was left, and no level from the loader has been judged
    // against it yet. Reading is chapter time at the leave, Serial the last
    // record before the leave closed anything. NoLoadYet: a split button's
    // leave, until its mod says it loads a level for it. Never saved: a state
    // load is a new attempt, and clears it
    private sealed record PendingLeave(LeaveKind Kind, AreaKey Area, string Room, long Reading, int Serial,
        bool NoLoadYet);
    private static PendingLeave leave;

    // the last serial of a finished run's rows (a re-entry that carried
    // nothing on or closed a chapter chain at the leave, a Return to Map split
    // closed without a pick): they go when the next run closes its first
    private static int keptSerial;

    // the last level updated was 7A's credits, or the Epilogue they end in.
    // The game counts no time in either (Level.UpdateTime), so neither is an
    // attempt: the leave pending from the chapter's end is judged by the
    // level entered after them. Never saved: a state load clears the leave
    private static bool afterEnding;

    // the frame being fed: a collect inside it is timed from its start, as
    // a room entry is (Session.Time already counts the frame by then)
    private static bool inUpdate;
    private static long frameStart;

    // the berries following the player, rebuilt on every fed frame and read
    // inside it only
    private static readonly List<string> following = [];

    /// The segments closed with a time since the attempt started, oldest
    /// first, each with its serial. A frame adds its most specific segment
    /// (after the Heart Respawn piece closing under it, if any), then its most
    /// specific chapter run. A load or a level from the loader empties it,
    /// except a re-entry and a death that zeroes the clock with a chapter row
    /// kept. A finished run's rows stay up until the next record (keptSerial).
    /// 7A's credits and the Epilogue leave it untouched. The HUD's tier rows
    /// show the last and step back from it.
    internal static IReadOnlyList<(SegmentRecord Record, int Serial)> Attempt => attempt;
    private static readonly List<(SegmentRecord Record, int Serial)> attempt = [];

    /// The room the rows show in until the player leaves it: where the last
    /// one closed, or where a re-entry that kept them landed.
    internal static string LatestRoom { get; private set; }

    /// Counts every record added, so a reader tells a new one from the same one.
    internal static int LatestSerial { get; private set; }

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
#pragma warning restore CS0649

    private static bool observing;

    public static void Load() {
        // hook order: see SrsModule.Load
        On.Celeste.Level.Update += LevelOnUpdate;
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

        // loadState runs once the level is restored, before a player load's
        // wipe starts. clearState stays unwired: clearing a slot rewinds
        // nothing, and a TAS may clear the slots as it starts
        wipeAction = SaveLoadImports.RegisterSaveLoadAction?.Invoke(null,
            (_, level) => restoredWipe = level.Wipe, null, null, null, null);

        typeof(SplitImports).ModInterop();

        tracker = new RunTracker(SegmentRules.All, null);
    }

    public static void Unload() {
        On.Celeste.Level.Update -= LevelOnUpdate;
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
        leave = null;
    }

    // every room load passes here; the kinds are RoomLoad's. A cutscene's room
    // change (2A's dream, the mirrors, 6A's fall, Farewell's intro) is a
    // walk-in. Some of them run in OnEndOfFrame, after the update hook: the
    // next fed frame reads the kind
    private static void LevelOnLoadLevel(On.Celeste.Level.orig_LoadLevel orig, Level self,
        Player.IntroTypes playerIntro, bool isFromLoader) {
        orig(self, playerIntro, isFromLoader);
        RoomLoad kind = isFromLoader ? RoomLoad.Loader
            : playerIntro != Player.IntroTypes.Respawn ? RoomLoad.WalkIn
            : reloading ? RoomLoad.Respawn
            : RoomLoad.Teleport;
        if (kind > roomLoad) {
            roomLoad = kind;
        }

        // here and not in Load: by a level every mod is loaded, whatever the
        // order. Below version 3 no stage says when a split lands
        if (isFromLoader && !observing && SplitImports.AddSplitObserver != null
            && SplitImports.InteropVersion?.Invoke() >= 3) {
            SplitImports.AddSplitObserver(OnSplitStage);
            observing = true;
        }
    }

    private static void LevelOnReload(On.Celeste.Level.orig_Reload orig, Level self) {
        reloading = true;
        try {
            orig(self);
        } finally {
            reloading = false;
        }
    }

    // Restart Chapter makes the new session with no room, with or without the
    // wipe; a golden berry restart names its room. Nothing updates the old
    // session afterwards, so its time is the run's last chapter time
    private static Session SessionOnRestart(On.Celeste.Session.orig_Restart orig, Session self, string intoLevel) {
        Session restarted = orig(self, intoLevel);
        if (intoLevel == null) {
            chapterRestarted = true;
            restartedTime = self.Time;
            restartedArea = self.Area;
        }

        return restarted;
    }

    private static void LevelOnUpdate(On.Celeste.Level.orig_Update orig, Level self) {
        // switched off, or without Speed Run Tool's SaveLoad: nothing is fed.
        // Moved is set on every frame off, so a savestate made or loaded
        // meanwhile opens nothing
        if (!SrsModule.Settings.Enabled || saveLoadAction == null) {
            // cleared after orig too: a LoadLevel or a restart inside it would set them
            fedLastFrame = false;
            restoredWipe = null;
            orig(self);
            roomLoad = RoomLoad.None;
            chapterRestarted = false;
            leave = null;
            afterEnding = false;
            Saved.Moved = true;
            if (self.Tracker.GetEntity<Player>()?.StateMachine.State != Player.StReflectionFall) {
                Saved.From = self.Session.Level;
            }

            return;
        }

        // before orig: the reading a room entry is timed from, and the level
        // and Saved as a load left them, before this frame can move the player
        bool loaded = Saved.Stamp != stamp;
        if (loaded) {
            // a room loaded before the load belongs to the timeline it left
            roomLoad = RoomLoad.None;
        }

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

        frameStart = before;
        inUpdate = true;
        orig(self);
        inUpdate = false;

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
        bool ending = self.InCredits || (afterEnding && session.Area.ID == 8);
        tracker.Rooms = RoomMap.For(session);
        EndState end = EndOf(session, player);
        RoomLoad load = roomLoad;
        roomLoad = RoomLoad.None;
        // a room change no LoadLevel walked into is a teleport too. Not on a
        // load: the state's room is not a room change
        bool teleported = Saved.Stamp == stamp
                          && (load == RoomLoad.Teleport
                              || (fedLastFrame && room != lastRoom && load < RoomLoad.WalkIn));

        bool feed = false;
        // a new attempt landed this frame, so the player appears now if they
        // have control
        bool restarted = false;
        // this frame's control and state edges are the player's own: not on a
        // load inside orig, nor on the frame srs is switched back on
        bool watched = true;
        if (!loaded && Saved.Stamp != stamp) {
            // a load inside orig: this frame's events would mix two timelines
            tracker.Drop();
            attempt.Clear();
            leave = null;
            watched = false;
        } else if (loaded) {
            // every load is a new attempt. With control, the room's Current Room
            // segment opens if the player has not moved since appearing on its
            // start spawn; without, it waits for the appearance that follows
            bool wipeEnd = loadWiping
                           && SpeedrunToolSettings.Instance?.FreezeAfterLoadStateType == FreezeAfterLoadStateType.Off;
            if (controlBefore) {
                tracker.Restart(scope, roomBefore, before - (wipeEnd ? WipeEndFrame : 0), true,
                    stateBefore == Player.StIntroJump,
                    movedAtLoad ? _ => false : AtStartSpawn(session, dataBefore, spawnAtLoad), fromState: true);
            } else {
                tracker.RestartAtAppearance(scope, roomBefore);
            }

            attempt.Clear();
            leave = null;
            // this frame's events start from the level the load left
            lastRoom = roomBefore;
            lastState = stateBefore;
            lastControl = controlBefore;
            lastStopped = stoppedBefore;
            lastCompleted = completedBefore;
            feed = scope != null;
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
            PendingLeave left = leave;
            leave = null;
            bool reentry = left != null && IsReentry(left, session, before);
            if (!reentry) {
                attempt.Clear();
            }

            // == and not Equals: AreaKey.Equals(object) always returns false
            if (chapterRestarted && restartedArea == session.Area) {
                // the rows ending on Restart Chapter close first, on the old
                // session's time; the restart then drops the rest unrecorded
                Emit(session, tracker.ChapterRestarted(restartedTime, end));
            }

            // a re-entry carries on from the reading before orig: chapter
            // time counted before control comes back is the row's
            if (reentry) {
                // a run that carries on keeps every row it closed. A finished
                // one leaves its rows up until the next row closes: what
                // closed at the leave, or all of a chapter run in pieces
                // that closed there, whatever twin of it still carries on.
                // A Save and Quit closes nothing
                bool carried = tracker.Reentered(left.Kind, scope, room, before, control, launching, atStart);
                bool chainClosed = attempt.Exists(entry => entry.Serial > left.Serial && entry.Record.Rule.SpansLeaves);
                if (!carried || chainClosed) {
                    if (!chainClosed) {
                        attempt.RemoveAll(entry => entry.Serial <= left.Serial);
                    }

                    keptSerial = LatestSerial;
                }

                if (left.Kind != LeaveKind.SaveAndQuit) {
                    // shown in this room now
                    LatestRoom = room;
                }
            } else {
                tracker.Restart(scope, room, before, control, launching, atStart);
            }

            restarted = true;
        } else if (teleported) {
            // Speed Run Tool's room teleport skips rooms without lowering chapter
            // time: a new attempt from the spawn it put the player on
            tracker.Restart(scope, room, before, control, launching,
                AtStartSpawn(session, session.LevelData, session.RespawnPoint));
            attempt.Clear();
            leave = null;
            restarted = true;
        } else if (!fedLastFrame) {
            // switched back on: whatever was open missed events
            tracker.Drop();
            attempt.Clear();
            watched = false;
        } else if (after < before) {
            // Level.Reload zeroes chapter time on a death in the first room
            // with nothing collected: a new attempt from the spawn, which
            // without control waits for ControlReturned. Only that death
            // lets a chapter row from the map keep the time read before it
            Func<SegmentRule, bool> spawn = AtStartSpawn(session, session.LevelData, session.RespawnPoint);
            bool firstRoomDeath = load == RoomLoad.Respawn && room == lastRoom;
            // a chapter row kept through it keeps the rows of its pieces
            bool kept = firstRoomDeath && tracker.ClockZeroed(scope, room, before, after, control, launching, spawn);
            if (!firstRoomDeath) {
                tracker.Restart(scope, room, after, control, launching, spawn);
            }

            if (!kept) {
                attempt.Clear();
            }

            leave = null;
            restarted = true;
        } else {
            feed = scope != null;
        }

        // the credits set Completed again and change room: neither is the run's
        if (feed && !ending) {
            if (state == Player.StReflectionFall && lastState != Player.StReflectionFall) {
                // 6A's watched fall is not a run of 6a Start: disqualified, not
                // dropped, so it still closes in Lake's start room and Lake
                // opens there
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
                FeedLeave(session, LeaveKind.ChapterEnd, after, end);
            } else if (stopped && !lastStopped) {
                Emit(session, tracker.ChapterTimeStopped(after, end));
            }

            if (room != lastRoom && load == RoomLoad.WalkIn) {
                bool opensNow = control || (player != null && !ArrivalStates.Contains(state));
                Emit(session, tracker.RoomEntered(scope, from, room, before, opensNow, launching, end));
            }

            // the state changes during this frame's update, and a savestate
            // saved at the end of this frame times from the next frame: the
            // start reading is the one after it
            if (lastState == Player.StIntroJump && !launching) {
                tracker.LaunchEnded(scope, room, after);
            }

            if (!lastControl && control) {
                tracker.ControlReturned(room, after, AtStartSpawn(session, session.LevelData, player?.ExactPosition));
            }
        }

        // the player appears when control returns, when the intro jump ends
        // (1A's too), and when a restart lands with control; a load restores
        // the last appearance
        bool appeared = restarted
            ? control
            : watched && ((!lastControl && control) || (lastState == Player.StIntroJump && !launching));
        bool disturbed = load is RoomLoad.Respawn or RoomLoad.Teleport or RoomLoad.Loader || teleported || !fedLastFrame;
        (float X, float Y)? at = player == null ? null : (player.ExactPosition.X, player.ExactPosition.Y);
        // a restart that has control already appears where the frame began, so
        // a direction held into its first frame is a move
        (float X, float Y)? appearedAt = restarted && control && exactBefore is { } start ? (start.X, start.Y) : at;
        Stillness still = new Stillness(Saved.SpawnAt.X, Saved.SpawnAt.Y, Saved.Moved)
            .After(disturbed, appeared, appearedAt)
            .After(false, false, at);
        Saved.SpawnAt = new Vector2(still.X, still.Y);
        Saved.Moved = still.Moved;

        Saved.Stamp = ++stamp;
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
        afterEnding = ending;
        chapterRestarted = false;
        fedLastFrame = true;
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
    // when Completed turned true; an exit from the Epilogue 7A's credits end
    // in would replace the leave still pending from 7A's end
    private static void OnLevelExit(Level level, LevelExit exit, LevelExit.Mode mode, Session session, HiresSnow snow) {
        if (level == null || mode is not (LevelExit.Mode.SaveAndQuit or LevelExit.Mode.GiveUp) || afterEnding
            || !SrsModule.Settings.Enabled || saveLoadAction == null || !fedLastFrame || Saved.Stamp != stamp) {
            return;
        }

        // a Return to Map taken in the ending is the chapter's end that
        // already pends: a second leave would cost the run its rows at the pick
        if (mode == LevelExit.Mode.GiveUp && leave is { Kind: LeaveKind.ChapterEnd }) {
            return;
        }

        FeedLeave(level, mode == LevelExit.Mode.SaveAndQuit ? LeaveKind.SaveAndQuit : LeaveKind.ReturnToMap);
    }

    // SomeSplitButtons' split buttons: Split when one lands, then Loading (the
    // mod loads a level for it, on this frame) or Stayed (it loads nothing).
    // Raised from its Level.Update hook or its list's update, so after this
    // frame was fed or inside orig; chapter time stands still from Split to
    // its outcome. Gated like a collect.
    // A Save and Quit split is a leave only once it loads: one that stays
    // closes nothing, but leaves what is open unsure of its note.
    // A Return to Map split is one where it lands, list or
    // not, unless a chapter's end pends, which it would replace
    private static void OnSplitStage(string action, string stage, ulong _) {
        if (Engine.Scene is not Level level || afterEnding
            || !SrsModule.Settings.Enabled || saveLoadAction == null || !fedLastFrame || Saved.Stamp != stamp) {
            return;
        }

        if (action == "SaveAndQuit" && stage == "Stayed") {
            tracker.SplitStayed();
        } else if (action == "SaveAndQuit" && stage == "Loading") {
            FeedLeave(level, LeaveKind.SaveAndQuit);
        } else if (action == "ReturnToMap" && stage == "Split" && leave == null) {
            FeedLeave(level, LeaveKind.ReturnToMap, noLoadYet: true);
        } else if (action == "ReturnToMap" && leave is { NoLoadYet: true }) {
            if (stage == "Loading") {
                leave = leave with { NoLoadYet = false };
            } else if (stage == "Stayed") {
                // no list, or one closed without a pick: no level was left,
                // and what closed keeps its record, up until the next row closes
                keptSerial = LatestSerial;
                leave = null;
                tracker.Drop();
            }
        }
    }

    // a leave read off the level as it stands
    private static void FeedLeave(Level level, LeaveKind kind, bool noLoadYet = false) {
        tracker.Rooms = RoomMap.For(level.Session);
        FeedLeave(level.Session, kind, level.Session.Time, EndOf(level.Session, level.Tracker.GetEntity<Player>()),
            noLoadYet);
    }

    // the serial is read before the leave closes anything: a re-entry keeps
    // the records after it
    private static void FeedLeave(Session session, LeaveKind kind, long reading, EndState end, bool noLoadYet = false) {
        int serial = LatestSerial;
        Emit(session, tracker.Left(kind, reading, end));
        leave = new PendingLeave(kind, session.Area, session.Level, reading, serial, noLoadYet);
    }

    // whether this level from the loader carries on from the leave: the same
    // chapter and side, and after a Save and Quit the room that was left with
    // chapter time where the leave read it (the saved session resumed;
    // another file or a console load reads otherwise). After a Return to Map
    // or a chapter's end, a checkpoint's own spawn, the chapter's first room
    // excluded: entering at the chapter's start is a new attempt for everything.
    // A split whose mod announced no load was not left by this level
    private static bool IsReentry(PendingLeave left, Session session, long reading) {
        // != and not Equals: AreaKey.Equals(object) always returns false
        if (left.Area != session.Area || left.NoLoadYet) {
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
    internal static void Forget() {
        leave = null;
        tracker.Drop();
    }

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
        if (!SrsModule.Settings.Enabled || saveLoadAction == null || !fedLastFrame || Saved.Stamp != stamp) {
            return;
        }

        tracker.BerryCollected(self.ID.Key);
    }

    private static void OnRegisterHeart(On.Celeste.HeartGem.orig_RegisterAsCollected orig, HeartGem self, Level level, string poemId) {
        orig(self, level, poemId);
        OnCollect(Collectibles.Heart);
    }

    // collects land inside an update (entity updates) or between two; inside
    // one, the frame's start reading, as for a room entry
    private static void OnCollect(Collectibles kind) {
        if (!SrsModule.Settings.Enabled || saveLoadAction == null || !fedLastFrame || Saved.Stamp != stamp
            || Engine.Scene is not Level level) {
            return;
        }

        tracker.Rooms = RoomMap.For(level.Session);
        long reading = inUpdate ? frameStart : level.Session.Time;
        Emit(level.Session, tracker.Collected(kind, reading, EndOf(level.Session, level.Tracker.GetEntity<Player>())));
    }

    private static void Emit(Session session, List<SegmentRecord> records) {
        if (records.Count == 0) {
            return;
        }

        // a split of an older SomeSplitButtons reports nothing: no record can
        // vouch for its note, so none claims one
        if (SplitImports.InteropVersion != null && !observing) {
            for (int i = 0; i < records.Count; i++) {
                records[i] = records[i] with { Note = SaveAndQuitNote.Unknown };
            }
        }

        SessionBests.Record(records, session);
        LatestRoom = session.Level;
        attempt.RemoveAll(entry => entry.Serial <= keptSerial);
        // a chapter run closes with a segment: the segment first, so the rows
        // show the chapter run and step back to the segment
        List<SegmentRule> rules = records.ConvertAll(record => record.Rule);
        foreach (bool chapterRun in (bool[])[false, true]) {
            if (Specificity.MostSpecific(rules, chapterRun, tracker.Rooms.BerriesOf) is >= 0 and int shown) {
                // the piece run since a Save and Quit, closing under a wider row:
                // one step back from it
                List<SegmentRecord> pieces = records.FindAll(record => record.Rule.Setup == StartSetup.HeartRespawn);
                if (!chapterRun && rules[shown].Setup != StartSetup.HeartRespawn
                    && Specificity.MostSpecific(pieces.ConvertAll(record => record.Rule), false, tracker.Rooms.BerriesOf) is >= 0 and int piece) {
                    attempt.Add((pieces[piece], ++LatestSerial));
                }

                attempt.Add((records[shown], ++LatestSerial));
            }
        }
    }
}
