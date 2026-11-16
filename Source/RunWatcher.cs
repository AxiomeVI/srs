using System;
using System.Collections.Generic;
using Celeste.Mod.SpeedrunTool;
using Microsoft.Xna.Framework;
using Monocle;
using MonoMod.ModInterop;

namespace Celeste.Mod.SpeedrunSheet;

// Reads the game for FrameFeed, which decides: chapter time, how each room was
// reached, the player's control, the collects, the frame chapter time stops
// and Restart Chapter. Keeps the hooks, the savestate fields and the spawn
// tests that need map data.
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

    // every never-saved piece of state, the tracker and the rows above the timer
    private static readonly FrameFeed feed = new();

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

    private static void FeedingIs(string reason) {
        if (feed.FeedingIs(reason) && Tracing) {
            Logger.Log(LogLevel.Verbose, "srs", $"trace Feeding reason={reason ?? "Resumed"}");
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

    // nothing updates the old session afterwards, so its time is the run's
    // last chapter time
    private static Session SessionOnRestart(On.Celeste.Session.orig_Restart orig, Session self, string intoLevel) {
        Session restarted = orig(self, intoLevel);
        feed.Restarted(intoLevel, self.Time, (self.Area.ID, (int)self.Area.Mode));
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
        // or a variant: nothing is fed
        if (!SrsModule.Settings.Enabled || saveLoadAction == null || Assisted()) {
            FeedingIs(!SrsModule.Settings.Enabled ? "SwitchedOff" : saveLoadAction == null ? "NoSaveLoad" : "Assisted");
            feed.OffBeforeUpdate();
            restoredWipe = null;
            orig(self);
            Hotkeys.Poll(self);
            Write(feed.OffAfterUpdate(SavedNow(), self.Session.Level, SideOf(self, self.Tracker.GetEntity<Player>()).Fall));
            return;
        }

        // before orig: the reading a room entry is timed from, and the level
        // and Saved as a load left them, before this frame can move the player
        long before = self.Session.Time;
        feed.BeforeUpdate(Saved.Stamp, before);
        game.Begin(self);
        string roomBefore = self.Session.Level;
        PlayerSide sideBefore = SideOf(self, self.Tracker.GetEntity<Player>());
        bool stoppedBefore = self.TimerStopped || self.Completed;
        bool completedBefore = self.Completed;
        bool loadWiping = self.Wipe != null && self.Wipe != restoredWipe;
        restoredWipe = null;
        SavedValues savedBefore = SavedNow();

        FeedingIs(null);
        bool tasBefore = TasPlaying();
        orig(self);
        Hotkeys.Poll(self);

        Session session = self.Session;
        Player player = self.Tracker.GetEntity<Player>();
        FrameReading reading = new(before, roomBefore, sideBefore, stoppedBefore, completedBefore, loadWiping,
            savedBefore, tasBefore,
            session.Time, session.Level, SegmentAutoDetect.ScopeOf(session), SideOf(self, player),
            self.TimerStopped || self.Completed, self.Completed, self.InCredits,
            (session.Area.ID, (int)session.Area.Mode), EndOf(session, player), SavedNow(), TasPlaying());
        Write(feed.Frame(in reading, game.At(self)));
        game.Forget();
    }

    private static void Write(SavedValues saved) {
        Saved.Stamp = saved.Stamp;
        Saved.From = saved.From;
        Saved.SpawnAt = new Vector2(saved.SpawnX, saved.SpawnY);
        Saved.Moved = saved.Moved;
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
        game.Forget();
    }

    // SomeSplitButtons' split stages, raised from its Level.Update hook or its
    // list's update, so after this frame was fed or inside orig; chapter time
    // stands still from Split to its outcome
    private static void OnSplitStage(string action, string stage, ulong _) {
        if (Engine.Scene is Level level) {
            feed.SplitStage(action, stage, Switched, Saved.Stamp, game.At(level));
            game.Forget();
        }
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
            game.Forget();
        }
    }

    // srs switched on, with Speed Run Tool's SaveLoad bound
    private static bool Switched => SrsModule.Settings.Enabled && saveLoadAction != null;

    // a split of an older SomeSplitButtons reports nothing: no record can
    // vouch for its note, so none claims one
    private static bool NotesUnknown => SplitImports.InteropVersion != null && !observing;

    // the player reduced to what the decisions test
    private static PlayerSide SideOf(Level level, Player player) {
        if (player == null) {
            return default;
        }

        int state = player.StateMachine.State;
        return new PlayerSide(true, !level.InCutscene && !NoControlStates.Contains(state), state == Player.StIntroJump,
            state == Player.StReflectionFall, ArrivalStates.Contains(state), player.ExactPosition.X,
            player.ExactPosition.Y);
    }

    private static SavedValues SavedNow() => new(Saved.Stamp, Saved.From, Saved.SpawnAt.X, Saved.SpawnAt.Y, Saved.Moved);

    // the level being updated or left, one object for all of them
    private static readonly LevelGame game = new();

    private sealed class LevelGame : IFrameGame {
        private Level level;
        // the room's data before the update, where a savestate left the player
        private LevelData dataBefore;

        public void Begin(Level at) {
            level = at;
            dataBefore = at.Session.LevelData;
        }

        public LevelGame At(Level at) {
            level = at;
            return this;
        }

        // no level is held from one frame to the next: every use names its own
        public void Forget() => level = null;

        public Func<SegmentRule, bool> AtLoadedSpawn(float x, float y) =>
            AtStartSpawn(level.Session, dataBefore, new Vector2(x, y));

        public Func<SegmentRule, bool> AtRespawnPoint() =>
            AtStartSpawn(level.Session, level.Session.LevelData, level.Session.RespawnPoint);

        public Func<SegmentRule, bool> AtPlayer() =>
            AtStartSpawn(level.Session, level.Session.LevelData, level.Tracker.GetEntity<Player>()?.ExactPosition);

        public bool AtCheckpointSpawnPastStartRoom() {
            Session session = level.Session;
            return session.Level != session.MapData?.StartLevel()?.Name
                   && session.RespawnPoint is { } at && session.LevelData is { } data
                   && CheckpointSpawn(data) is { } spawn && Near(at, spawn);
        }

        public bool FreezeAfterLoadOff =>
            SpeedrunToolSettings.Instance?.FreezeAfterLoadStateType == FreezeAfterLoadStateType.Off;

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
