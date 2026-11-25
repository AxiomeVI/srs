using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the adapter's decisions, fed as the game would feed them: the events
// between and inside frames, each behind its gate
public class FrameFeedTests {
    private sealed class Rooms : IRoomMap {
        public readonly Dictionary<string, (string Start, string End)> ByName = [];
        public readonly Dictionary<string, string> Entries = [];
        public string StartRoomOf(SegmentRule rule) => ByName.TryGetValue(rule.Name, out var r) ? r.Start : null;
        public string EndRoomOf(SegmentRule rule) => ByName.TryGetValue(rule.Name, out var r) ? r.End : null;
        public string EntryRoomOf(SegmentRule rule) => Entries.GetValueOrDefault(rule.Name);
        public IReadOnlyCollection<string> BerriesOf(SegmentRule rule) => [];
    }

    private sealed class Game : IFrameGame {
        public Rooms Map = new();
        public string Room { get; set; } = "1";
        public (int Id, int Mode) Area { get; set; } = (1, 0);
        public long Time { get; set; }
        public bool TasPlaying { get; set; }
        public bool NotesUnknown { get; set; }
        public readonly List<SegmentRecord> Recorded = [];
        public IRoomMap Rooms => Map;
        public EndState End() => new(1, []);
        public void Record(List<SegmentRecord> records) => Recorded.AddRange(records);

        // whether the point a frame asks about is on the rule's start spawn
        public bool OnStart = true;
        public (float, float)? LoadedSpawnAsked;
        public bool CheckpointSpawn;

        public Func<SegmentRule, bool> AtLoadedSpawn(float x, float y) {
            LoadedSpawnAsked = (x, y);
            return _ => OnStart;
        }

        public Func<SegmentRule, bool> AtRespawnPoint() => _ => OnStart;
        public Func<SegmentRule, bool> AtPlayer() => _ => OnStart;
        public bool AtCheckpointSpawnPastStartRoom() => CheckpointSpawn;
    }

    private readonly Game game = new();
    private readonly List<SegmentRule> rules = [];
    private readonly List<TraceEvent> traced = [];
    private readonly FrameFeed feed = new();

    private SegmentRule Add(string name, string start, string end, EndKind endKind = EndKind.NextStart,
        Collectibles endsOn = Collectibles.None, StartSetup setup = StartSetup.CurrentRoom, string entry = null) {
        SegmentRule rule = new("1a", name, name, StartKind.Room, setup, endKind, endsOn, Collectibles.None, null, 0, 0,
            rules.Count);
        rules.Add(rule);
        game.Map.ByName[name] = (start, end);
        if (entry != null) {
            game.Map.Entries[name] = entry;
        }

        return rule;
    }

    // a fed frame happened and nothing was loaded since: events are taken
    private FrameFeed Started() {
        feed.StartTracker(rules, traced.Add);
        feed.Tracker.Rooms = game.Map;
        feed.FedLastFrame = true;
        feed.Stamp = 7;
        return feed;
    }

    private const long Stamp = 7;

    // a row from the start of room 1 to a cassette, open from reading 0
    private void OpenTapeRow() {
        Add("Tape RTM", "1", null, EndKind.Collect, Collectibles.Cassette);
        Started();
        feed.Tracker.Restart("1a", "1", 0, true, false, _ => true);
    }

    private long[] RecordedTicks() => game.Recorded.Select(record => record.Ticks).ToArray();

    // ---------------------------------------------------------------- gates

    [Theory]
    [InlineData(false, true, Stamp)]
    [InlineData(true, false, Stamp)]
    [InlineData(true, true, Stamp + 1)]
    public void AnEventIsTakenOnlyWhenOnFedAndNotAcrossALoad(bool on, bool fed, long savedStamp) {
        OpenTapeRow();
        feed.FedLastFrame = fed;
        game.Time = 30;

        feed.Collected(Collectibles.Cassette, on, savedStamp, game);
        feed.LevelExit(true, on, savedStamp, game);
        feed.SplitStage("ReturnToMap", "Split", on, savedStamp, game);

        Assert.Empty(game.Recorded);
        Assert.Null(feed.Leave);
        Assert.Null(feed.Via);
    }

    // ---------------------------------------------------------------- collects

    // inside an update, chapter time already counts the frame: the frame's
    // start reading times it, as a room entry
    [Fact]
    public void ACollectInsideAnUpdateIsTimedFromTheFramesStart() {
        OpenTapeRow();
        feed.InUpdate = true;
        feed.FrameStart = 20;
        game.Time = 37;

        feed.Collected(Collectibles.Cassette, true, Stamp, game);

        Assert.Equal([20L], RecordedTicks());
        Assert.Equal("Cassette", feed.Via);
    }

    [Fact]
    public void ACollectBetweenTwoUpdatesIsTimedNow() {
        OpenTapeRow();
        feed.InUpdate = false;
        feed.FrameStart = 20;
        game.Time = 37;

        feed.Collected(Collectibles.Cassette, true, Stamp, game);

        Assert.Equal([37L], RecordedTicks());
    }

    [Fact]
    public void ARowClosedWhileATasPlaysIsTracedNotRecorded() {
        OpenTapeRow();
        game.TasPlaying = true;
        game.Time = 37;

        feed.Collected(Collectibles.Cassette, true, Stamp, game);

        Assert.Empty(game.Recorded);
        Assert.Empty(feed.Rows.Rows);
        Assert.Contains(traced, e => e.Kind == TraceKind.NotRecorded && e.Reason == TraceReason.TasPlayed);
    }

    [Fact]
    public void AnOlderSplitModLeavesEveryNoteUnknown() {
        OpenTapeRow();
        game.NotesUnknown = true;
        game.Time = 37;

        feed.Collected(Collectibles.Cassette, true, Stamp, game);

        SegmentRecord record = Assert.Single(game.Recorded);
        Assert.Equal(SaveAndQuitNote.Unknown, record.Note);
        Assert.Equal(ReturnToMapNote.Unknown, record.ReturnToMap);
    }

    [Fact]
    public void ARecordGoesToTheRowsInTheRoomItClosedIn() {
        OpenTapeRow();
        game.Room = "4";
        game.Time = 37;

        feed.Collected(Collectibles.Cassette, true, Stamp, game);

        Assert.Equal("Tape RTM", Assert.Single(feed.Rows.Rows).Record.Rule.Name);
        Assert.Equal("4", feed.Rows.LatestRoom);
    }

    // ---------------------------------------------------------------- level exit

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnExitIsALeaveReadOffTheLevel(bool saveAndQuit) {
        LeaveKind kind = saveAndQuit ? LeaveKind.SaveAndQuit : LeaveKind.ReturnToMap;
        Started();
        game.Room = "6";
        game.Area = (1, 0);
        game.Time = 500;

        feed.LevelExit(saveAndQuit, true, Stamp, game);

        Assert.Equal(new FrameFeed.PendingLeave(kind, (1, 0), "6", 500, 0, false), feed.Leave);
        Assert.Equal("LevelExit", feed.Via);
    }

    // the serial is read before the leave closes anything
    [Fact]
    public void ALeaveKeepsTheSerialOfTheLastRecordBeforeIt() {
        OpenTapeRow();
        game.Time = 37;
        feed.Collected(Collectibles.Cassette, true, Stamp, game);

        feed.LevelExit(true, true, Stamp, game);

        Assert.Equal(1, feed.Leave.Serial);
    }

    [Fact]
    public void NoExitIsFedAfterAnEnding() {
        Started();
        feed.AfterEnding = true;

        feed.LevelExit(true, true, Stamp, game);

        Assert.Null(feed.Leave);
    }

    [Fact]
    public void AReturnToMapWhileAChapterEndPendsIsThatEnd() {
        Started();
        feed.FeedLeave(game, LeaveKind.ChapterEnd, 400, game.End());
        FrameFeed.PendingLeave end = feed.Leave;

        feed.LevelExit(false, true, Stamp, game);

        Assert.Same(end, feed.Leave);
    }

    [Fact]
    public void ASaveAndQuitWhileAChapterEndPendsReplacesIt() {
        Started();
        feed.FeedLeave(game, LeaveKind.ChapterEnd, 400, game.End());

        feed.LevelExit(true, true, Stamp, game);

        Assert.Equal(LeaveKind.SaveAndQuit, feed.Leave.Kind);
    }

    // ---------------------------------------------------------------- split stages

    [Fact]
    public void ASaveAndQuitSplitThatStaysLeavesNothing() {
        Started();

        feed.SplitStage("SaveAndQuit", "Stayed", true, Stamp, game);

        Assert.Null(feed.Leave);
        Assert.Equal("SplitSaveAndQuitStayed", feed.Via);
    }

    [Fact]
    public void ASaveAndQuitSplitThatLoadsIsALeave() {
        Started();
        game.Time = 90;

        feed.SplitStage("SaveAndQuit", "Loading", true, Stamp, game);

        Assert.Equal(LeaveKind.SaveAndQuit, feed.Leave.Kind);
        Assert.False(feed.Leave.NoLoadYet);
        Assert.Equal(90, feed.Leave.Reading);
    }

    [Fact]
    public void AReturnToMapSplitIsALeaveWaitingForItsLoad() {
        Started();

        feed.SplitStage("ReturnToMap", "Split", true, Stamp, game);

        Assert.Equal(LeaveKind.ReturnToMap, feed.Leave.Kind);
        Assert.True(feed.Leave.NoLoadYet);

        feed.SplitStage("ReturnToMap", "Loading", true, Stamp, game);
        Assert.False(feed.Leave.NoLoadYet);
    }

    // a chapter's end that pends would be replaced
    [Fact]
    public void AReturnToMapSplitOverAPendingLeaveIsNone() {
        Started();
        feed.FeedLeave(game, LeaveKind.ChapterEnd, 400, game.End());
        FrameFeed.PendingLeave end = feed.Leave;

        feed.SplitStage("ReturnToMap", "Split", true, Stamp, game);
        feed.SplitStage("ReturnToMap", "Stayed", true, Stamp, game);

        Assert.Same(end, feed.Leave);
    }

    // no list, or one closed without a pick: no level was left, and what
    // closed keeps its rows until the next row closes
    [Fact]
    public void AReturnToMapSplitThatStaysLeavesNoLevelAndKeepsTheRows() {
        OpenTapeRow();
        game.Time = 37;
        feed.Collected(Collectibles.Cassette, true, Stamp, game);
        feed.SplitStage("ReturnToMap", "Split", true, Stamp, game);

        feed.SplitStage("ReturnToMap", "Stayed", true, Stamp, game);

        Assert.Null(feed.Leave);
        Assert.Equal("SplitReturnToMapStayed", feed.Via);
        Assert.Single(feed.Rows.Rows);
        Assert.Empty(feed.Tracker.Open);

        feed.Tracker.Restart("1a", "1", 40, true, false, _ => true);
        game.Time = 60;
        feed.Collected(Collectibles.Cassette, true, Stamp, game);
        Assert.Equal(2, Assert.Single(feed.Rows.Rows).Serial);
    }

    [Fact]
    public void NoSplitIsFedAfterAnEnding() {
        Started();
        feed.AfterEnding = true;

        feed.SplitStage("ReturnToMap", "Split", true, Stamp, game);

        Assert.Null(feed.Leave);
    }

    // ---------------------------------------------------------------- the switch

    [Fact]
    public void ForgetClearsTheLeaveAndDropsWhatIsOpen() {
        OpenTapeRow();
        feed.LevelExit(true, true, Stamp, game);

        feed.Forget();

        Assert.Null(feed.Leave);
        Assert.Empty(feed.Tracker.Open);
        Assert.Equal("SwitchMoved", feed.Via);
    }

    // ---------------------------------------------------------------- room loads and restarts

    [Fact]
    public void ARespawnInsideReloadIsADeathAndOutsideATeleport() {
        Started();
        feed.Reloading = true;
        feed.LevelLoaded(false, true);
        Assert.Equal(RoomLoad.Respawn, feed.RoomLoad);

        feed.Reloading = false;
        feed.LevelLoaded(false, true);
        Assert.Equal(RoomLoad.Teleport, feed.RoomLoad);
    }

    [Fact]
    public void OnlyALevelFromTheLoaderSetsItsIntro() {
        Started();
        feed.LevelLoaded(true, true);
        Assert.True(feed.LoaderIntroRespawn);

        feed.LevelLoaded(false, false);
        Assert.True(feed.LoaderIntroRespawn);

        feed.LevelLoaded(true, false);
        Assert.False(feed.LoaderIntroRespawn);
        Assert.Equal(RoomLoad.Loader, feed.RoomLoad);
    }

    [Fact]
    public void RestartChapterKeepsTheOldSessionsTimeAndArea() {
        Started();
        feed.Restarted(null, 1234, (2, 0));

        Assert.True(feed.ChapterRestarted);
        Assert.Equal(1234, feed.RestartedTime);
        Assert.Equal((2, 0), feed.RestartedArea);
    }

    // ---------------------------------------------------------------- the frame's decisions

    private static PlayerSide Standing(bool control = true) => new(true, control, false, false, false, 10, 20);

    private static readonly SavedValues Unmoved = new(Stamp, "0", 10, 20, false);

    // a fed frame in room 1, nothing loaded, chapter time 100 then 117
    private static FrameReading Plain() => new(100, "1", Standing(), false, false, Unmoved, false,
        117, "1", "1a", Standing(), false, false, false, (1, 0), new EndState(1, []), Unmoved, false);

    // a savestate loaded: the stamp it brings back is older than the feed's
    private static FrameReading Loaded(bool control = true, bool moved = false) => Plain() with {
        PlayerBefore = Standing(control),
        SavedBefore = Unmoved with { Stamp = 3, Moved = moved },
        SavedAfter = Unmoved with { Stamp = 3, Moved = moved },
    };

    private FrameOutcome Feed(FrameReading reading) {
        feed.BeforeUpdate(reading.SavedBefore.Stamp, reading.Before);
        return feed.Decide(in reading, game);
    }

    // a Current Room row in room 1, and a row from room 1's map spawn
    private void StartRows() {
        Add("Start", "1", "2");
        Started();
        feed.LastRoom = "1";
    }

    private IEnumerable<TraceEvent> Opened() => traced.Where(e => e.Kind == TraceKind.Opened);

    private void LeaveBy(LeaveKind kind, string room = "6", long reading = 100, bool noLoadYet = false,
        (int, int)? area = null) {
        game.Room = room;
        game.Area = area ?? (1, 0);
        feed.FeedLeave(game, kind, reading, game.End(), noLoadYet);
        game.Room = "1";
        game.Area = (1, 0);
    }

    // with control, unmoved on its start spawn: the room's Current Room row
    // opens from the reading before the update
    [Fact]
    public void ASavestateLoadedWithControlOnItsSpawnOpensTheRoomsRow() {
        StartRows();

        FrameOutcome outcome = Feed(Loaded());

        Assert.Equal("SavestateLoad", feed.Via);
        Assert.Equal(100, Assert.Single(Opened()).Reading);
        Assert.Equal((10f, 20f), game.LoadedSpawnAsked);
        Assert.True(outcome.Feeds);
    }

    [Fact]
    public void ASavestateLoadedAfterAMoveOpensNothing() {
        StartRows();

        Feed(Loaded(moved: true));

        Assert.Empty(Opened());
        Assert.Null(game.LoadedSpawnAsked);
    }

    [Fact]
    public void ASavestateLoadedWithoutControlWaitsForTheAppearance() {
        StartRows();

        Feed(Loaded(control: false));

        Assert.Empty(Opened());
        Assert.Equal("SavestateLoad", feed.Via);
    }

    // a load is a new attempt: the rows and the leave go
    [Fact]
    public void ASavestateLoadClearsTheRowsAndTheLeave() {
        OpenTapeRow();
        game.Time = 37;
        feed.Collected(Collectibles.Cassette, true, Stamp, game);
        LeaveBy(LeaveKind.SaveAndQuit);

        Feed(Loaded());

        Assert.Empty(feed.Rows.Rows);
        Assert.Null(feed.Leave);
    }

    // this frame's edges start from the level the load left
    [Fact]
    public void ASavestateLoadRebasesTheEdgesOnTheLevelItLeft() {
        StartRows();
        feed.LastRoom = "5";
        feed.LastControl = false;

        Feed(Loaded() with { RoomBefore = "1", StoppedBefore = true, CompletedBefore = true });

        Assert.Equal("1", feed.LastRoom);
        Assert.True(feed.LastControl);
        Assert.True(feed.LastStopped);
        Assert.True(feed.LastCompleted);
    }

    [Fact]
    public void ASavestateMadeIsANewAttemptAsIfLoadedAtOnce() {
        StartRows();
        feed.StateSaved = true;

        Feed(Plain());

        Assert.Equal("SavestateMade", feed.Via);
        Assert.Equal(100, Assert.Single(Opened()).Reading);
        Assert.False(feed.StateSaved);
    }

    [Fact]
    public void ASavestateMadeAndLoadedOnOneFrameIsALoad() {
        StartRows();
        feed.StateSaved = true;

        Feed(Loaded());

        Assert.Equal("SavestateLoad", feed.Via);
        Assert.Equal(100, Assert.Single(Opened()).Reading);
    }

    [Fact]
    public void ALoadInsideTheUpdateDropsEverythingAndWatchesNothing() {
        OpenTapeRow();
        game.Time = 37;
        feed.Collected(Collectibles.Cassette, true, Stamp, game);
        LeaveBy(LeaveKind.SaveAndQuit);

        FrameOutcome outcome = Feed(Plain() with { SavedAfter = Unmoved with { Stamp = 3 } });

        Assert.Equal("LoadInFrame", feed.Via);
        Assert.Empty(feed.Tracker.Open);
        Assert.Empty(feed.Rows.Rows);
        Assert.Null(feed.Leave);
        Assert.False(outcome.Watched);
        Assert.False(outcome.Feeds);
    }

    // ---------------------------------------------------------------- the loader

    [Fact]
    public void ALevelFromTheLoaderIsANewAttemptFromTheRespawnPoint() {
        StartRows();
        feed.LevelLoaded(true, false);

        FrameOutcome outcome = Feed(Plain());

        Assert.Equal("LevelLoad", feed.Via);
        Assert.Single(Opened());
        Assert.True(outcome.Restarted);
        Assert.False(outcome.Feeds);
    }

    // a Save and Quit and Continue resumes the saved session: same chapter,
    // same room, chapter time where the leave read it
    [Fact]
    public void AContinueAfterASaveAndQuitCarriesOnAndKeepsTheRows() {
        OpenTapeRow();
        game.Time = 37;
        feed.Collected(Collectibles.Cassette, true, Stamp, game);
        LeaveBy(LeaveKind.SaveAndQuit, room: "1", reading: 100);
        feed.LevelLoaded(true, true);

        Feed(Plain());

        Assert.Equal("Reentry", feed.Via);
        Assert.Single(feed.Rows.Rows);
        Assert.Null(feed.Leave);
    }

    [Theory]
    [InlineData("2", 100, 1, false)]
    [InlineData("1", 90, 1, false)]
    [InlineData("1", 100, 2, false)]
    [InlineData("1", 100, 1, true)]
    public void AnyOtherLoadAfterASaveAndQuitIsANewAttempt(string room, long reading, int area, bool noLoadYet) {
        OpenTapeRow();
        game.Time = 37;
        feed.Collected(Collectibles.Cassette, true, Stamp, game);
        LeaveBy(LeaveKind.SaveAndQuit, room, reading, noLoadYet, (area, 0));
        feed.LevelLoaded(true, true);

        Feed(Plain());

        Assert.Equal("LevelLoad", feed.Via);
        Assert.Empty(feed.Rows.Rows);
    }

    // after a Return to Map or a chapter's end, a checkpoint picked on the map
    // carries on from its own spawn; the chapter's start does not
    [Theory]
    [InlineData(true, "Reentry")]
    [InlineData(false, "LevelLoad")]
    public void ACheckpointPickedOnTheMapCarriesOnFromItsSpawn(bool onCheckpointSpawn, string via) {
        StartRows();
        LeaveBy(LeaveKind.ReturnToMap);
        game.CheckpointSpawn = onCheckpointSpawn;
        feed.LevelLoaded(true, true);

        Feed(Plain());

        Assert.Equal(via, feed.Via);
    }

    // only a load that plays the chapter's intro opens a Chapter Load row
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void AChapterLoadRowOpensOnlyOnALoaderIntro(bool respawnIntro, int opened) {
        Add("Start", "1", "2", setup: StartSetup.ChapterLoad);
        Started();
        feed.LevelLoaded(true, respawnIntro);

        Feed(Plain());

        Assert.Equal(opened, Opened().Count());
    }

    // the credits, and the Epilogue they end in: the leave pending from the
    // chapter's end waits for the level after them
    [Fact]
    public void ALevelFromTheLoaderInTheCreditsChangesNothing() {
        OpenTapeRow();
        game.Time = 37;
        feed.Collected(Collectibles.Cassette, true, Stamp, game);
        LeaveBy(LeaveKind.ChapterEnd);
        FrameFeed.PendingLeave leave = feed.Leave;
        feed.LevelLoaded(true, false);
        feed.Via = null;

        FrameOutcome outcome = Feed(Plain() with { InCredits = true });

        Assert.Same(leave, feed.Leave);
        Assert.Single(feed.Rows.Rows);
        Assert.Null(feed.Via);
        Assert.True(outcome.Ending);
        Assert.False(outcome.Restarted);
    }

    [Theory]
    [InlineData(true, 8, true)]
    [InlineData(false, 8, false)]
    [InlineData(true, 7, false)]
    public void TheEpilogueIsAnEndingOnlyRightAfterTheCredits(bool afterEnding, int area, bool ending) {
        StartRows();
        feed.AfterEnding = afterEnding;

        Assert.Equal(ending, Feed(Plain() with { Area = (area, 0) }).Ending);
    }

    // the rows ending on Restart Chapter close on the old session's time,
    // before the restart drops the rest
    [Fact]
    public void RestartChapterClosesItsRowsOnTheOldTimeBeforeTheRestart() {
        Add("Start RC", "1", null, EndKind.Restart);
        Started();
        feed.Tracker.Restart("1a", "1", 0, true, false, _ => true);
        feed.Restarted(null, 250, (1, 0));
        feed.LevelLoaded(true, false);

        Feed(Plain());

        Assert.Equal([250L], RecordedTicks());
        Assert.Equal("LevelLoad", feed.Via);
    }

    [Fact]
    public void RestartChapterInAnotherAreaClosesNothing() {
        Add("Start RC", "1", null, EndKind.Restart);
        Started();
        feed.Tracker.Restart("1a", "1", 0, true, false, _ => true);
        feed.Restarted(null, 250, (2, 0));
        feed.LevelLoaded(true, false);

        Feed(Plain());

        Assert.Empty(game.Recorded);
    }

    // ---------------------------------------------------------------- teleports, the switch, the clock

    [Fact]
    public void ARespawnOutsideReloadIsATeleport() {
        OpenTapeRow();
        game.Time = 37;
        feed.Collected(Collectibles.Cassette, true, Stamp, game);
        feed.LevelLoaded(false, true);

        FrameOutcome outcome = Feed(Plain());

        Assert.Equal("Teleport", feed.Via);
        Assert.Empty(feed.Rows.Rows);
        Assert.True(outcome.Restarted);
        Assert.True(outcome.Teleported);
    }

    // a room change no LoadLevel walked into
    [Fact]
    public void ARoomChangeWithNoWalkInIsATeleport() {
        StartRows();

        FrameOutcome outcome = Feed(Plain() with { Room = "4" });

        Assert.Equal("Teleport", feed.Via);
        Assert.True(outcome.Teleported);
    }

    [Fact]
    public void AWalkInIsNoTeleport() {
        StartRows();
        feed.LevelLoaded(false, false);

        FrameOutcome outcome = Feed(Plain() with { Room = "4" });

        Assert.False(outcome.Teleported);
        Assert.True(outcome.Feeds);
        Assert.Equal(RoomLoad.WalkIn, outcome.Load);
    }

    // the state's room is not a room change
    [Fact]
    public void ALoadIntoAnotherRoomIsNoTeleport() {
        StartRows();

        FrameOutcome outcome = Feed(Loaded() with { Room = "4" });

        Assert.False(outcome.Teleported);
        Assert.Equal("SavestateLoad", feed.Via);
    }

    [Fact]
    public void TheFirstFrameBackOnDropsWhatWasOpen() {
        OpenTapeRow();
        feed.FedLastFrame = false;

        FrameOutcome outcome = Feed(Plain());

        Assert.Equal("FeedingResumed", feed.Via);
        Assert.Empty(feed.Tracker.Open);
        Assert.False(outcome.Watched);
        Assert.False(outcome.Feeds);
    }

    // Level.Reload zeroes chapter time on a death in the first room with
    // nothing collected
    [Fact]
    public void ADeathThatZeroesTheClockIsAFirstRoomDeath() {
        StartRows();
        feed.Reloading = true;
        feed.LevelLoaded(false, true);

        FrameOutcome outcome = Feed(Plain() with { After = 0 });

        Assert.Equal("FirstRoomDeath", feed.Via);
        Assert.True(outcome.Restarted);
    }

    // a clock drop that is no first-room death restarts from the reading
    // after it, not before
    [Fact]
    public void AnyOtherClockDropRestartsFromTheReadingAfter() {
        StartRows();

        Feed(Plain() with { After = 5 });

        Assert.Equal("ClockReset", feed.Via);
        Assert.Equal(5, Assert.Single(Opened()).Reading);
    }

    [Fact]
    public void ANormalFrameFeedsOnlyWithAScope() {
        StartRows();

        Assert.True(Feed(Plain()).Feeds);
        Assert.False(Feed(Plain() with { Scope = null }).Feeds);
    }

    [Fact]
    public void AFrameATasPlayedIsMarked() {
        StartRows();

        Assert.True(Feed(Plain() with { TasAfter = true }).TasFrame);
        Assert.Equal("Tas", feed.Via);
        Assert.False(Feed(Plain()).TasFrame);
    }

    // a TAS that stopped inside the update still played the frame: the row
    // this frame closes is marked before it closes
    [Fact]
    public void ARowClosedOnTheFrameATasStoppedInIsNotRecorded() {
        Add("Start RC", "1", null, EndKind.Restart);
        Started();
        feed.Tracker.Restart("1a", "1", 0, true, false, _ => true);
        feed.Restarted(null, 250, (1, 0));
        feed.LevelLoaded(true, false);

        Assert.True(Feed(Plain() with { TasBefore = true }).TasFrame);

        Assert.Empty(game.Recorded);
        Assert.Contains(traced, e => e.Kind == TraceKind.Disqualified && e.Reason == TraceReason.TasPlayed);
    }

    [Fact]
    public void ACollectIsInsideTheUpdateOnlyFromItsStartToItsDecision() {
        StartRows();

        feed.BeforeUpdate(Stamp, 100);
        Assert.True(feed.InUpdate);
        feed.Decide(Plain(), game);
        Assert.False(feed.InUpdate);
    }

    // ---------------------------------------------------------------- the feed and the savestate fields

    private SavedValues Whole(FrameReading reading) {
        feed.BeforeUpdate(reading.SavedBefore.Stamp, reading.Before);
        return feed.Frame(in reading, game);
    }

    private static PlayerSide At(float x, float y, bool control = true, bool launching = false, bool fall = false,
        bool arriving = false) => new(true, control, launching, fall, arriving, x, y);

    // a row walked into from room 3, its entry room
    private void WalkInRow() {
        Add("Crossing", "4", "9", setup: StartSetup.NextRoom, entry: "3");
        Started();
        feed.LastRoom = "3";
        feed.LevelLoaded(false, false);
    }

    private static FrameReading WalkedInto(PlayerSide player) => Plain() with {
        RoomBefore = "3", Room = "4", Player = player,
        SavedBefore = Unmoved with { From = "3" }, SavedAfter = Unmoved with { From = "3" },
    };

    // a room entry is timed from the reading before the update, from the room
    // the next walk-in comes from
    [Fact]
    public void AWalkInIsTimedFromTheReadingBeforeTheUpdate() {
        WalkInRow();

        Whole(WalkedInto(At(10, 20)));

        TraceEvent opened = Assert.Single(Opened());
        Assert.Equal(100, opened.Reading);
        Assert.Equal("3", opened.From);
        Assert.Equal("WalkIn", feed.Via);
    }

    // a wake-up or 6A's fall waits for control; any other room entered
    // without it is a cutscene, which is chapter time
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void AnArrivalWithoutControlWaitsAndACutsceneDoesNot(bool arriving, int opened) {
        WalkInRow();

        Whole(WalkedInto(At(10, 20, control: false, arriving: arriving)));

        Assert.Equal(opened, Opened().Count());
    }

    [Fact]
    public void AWalkInWithNoPlayerWaits() {
        WalkInRow();

        Whole(WalkedInto(default));

        Assert.Empty(Opened());
    }

    // a load is no walk-in
    [Fact]
    public void ARoomChangeOnAnotherLoadIsNoWalkIn() {
        WalkInRow();
        feed.RoomLoad = RoomLoad.None;

        Whole(WalkedInto(At(10, 20)) with { Room = "4" });

        Assert.Empty(Opened());
        Assert.Equal("Teleport", feed.Via);
    }

    [Fact]
    public void TheEndOfTheLaunchIsTimedFromTheReadingAfter() {
        StartRows();
        feed.LastLaunching = true;

        Whole(Plain());

        Assert.Equal("LaunchEnded", feed.Via);
    }

    [Fact]
    public void ControlComingBackIsAnAppearance() {
        StartRows();
        feed.LastControl = false;

        SavedValues saved = Whole(Plain() with {
            Player = At(30, 40), SavedAfter = Unmoved with { SpawnX = 1, SpawnY = 2, Moved = true },
        });

        Assert.Equal("ControlReturned", feed.Via);
        Assert.Equal((30f, 40f, false), (saved.SpawnX, saved.SpawnY, saved.Moved));
    }

    // a restart that lands with control appears where the frame began: a
    // direction held into its first frame is a move
    [Fact]
    public void ARestartWithControlAppearsWhereTheFrameBegan() {
        StartRows();
        feed.LevelLoaded(true, false);

        SavedValues saved = Whole(Plain() with { PlayerBefore = At(5, 6), Player = At(7, 6) });

        Assert.Equal((5f, 6f, true), (saved.SpawnX, saved.SpawnY, saved.Moved));
    }

    // a teleport, a death or a loader: whatever was last appeared at is gone
    [Fact]
    public void ATeleportDisturbsTheLastAppearance() {
        StartRows();
        feed.LevelLoaded(false, true);

        SavedValues saved = Whole(Plain() with { PlayerBefore = At(10, 20, control: false), Player = At(10, 20, control: false) });

        Assert.True(saved.Moved);
    }

    // a death that keeps the clock is a normal frame, and still no load
    // may open from where the player last appeared
    [Fact]
    public void ADeathDisturbsTheLastAppearance() {
        StartRows();
        feed.Reloading = true;
        feed.LevelLoaded(false, true);

        SavedValues saved = Whole(Plain() with { PlayerBefore = At(10, 20, control: false), Player = At(10, 20, control: false) });

        Assert.True(saved.Moved);
    }

    [Fact]
    public void StandingStillKeepsTheLastAppearance() {
        StartRows();

        SavedValues saved = Whole(Plain());

        Assert.Equal((10f, 20f, false), (saved.SpawnX, saved.SpawnY, saved.Moved));
    }

    [Fact]
    public void MovingMarksTheLastAppearance() {
        StartRows();

        Assert.True(Whole(Plain() with { Player = At(10.28f, 20) }).Moved);
    }

    // 6A's watched fall is not a run: disqualified once
    [Fact]
    public void TheWatchedFallDisqualifiesOnce() {
        StartRows();
        feed.Tracker.Restart("1a", "1", 0, true, false, _ => true);

        SavedValues saved = Whole(Plain() with { Player = At(10, 20, control: false, fall: true) });
        Assert.Equal("ReflectionFall", feed.Via);

        feed.Via = null;
        Whole(Plain() with {
            PlayerBefore = At(10, 20, control: false, fall: true), Player = At(10, 20, control: false, fall: true),
            SavedBefore = saved, SavedAfter = saved,
        });
        Assert.Null(feed.Via);
    }

    // during the fall the rooms passed through are not where the run comes from
    [Theory]
    [InlineData(true, "0")]
    [InlineData(false, "1")]
    public void TheFallKeepsTheRoomTheNextWalkInComesFrom(bool fall, string from) {
        StartRows();

        SavedValues saved = Whole(Plain() with { Player = At(10, 20, fall: fall) });

        Assert.Equal(from, saved.From);
    }

    [Fact]
    public void ARestartInTheFallStillMovesTheRoomOn() {
        StartRows();
        feed.LevelLoaded(true, false);

        Assert.Equal("1", Whole(Plain() with { Player = At(10, 20, fall: true) }).From);
    }

    // Completed turning true is the chapter's end, and a leave; TimerStopped
    // alone closes and is no leave
    [Fact]
    public void TheChaptersEndIsALeave() {
        StartRows();

        Whole(Plain() with { Completed = true, Stopped = true });

        Assert.Equal("ChapterEnd", feed.Via);
        Assert.Equal(LeaveKind.ChapterEnd, feed.Leave.Kind);
        Assert.Equal(117, feed.Leave.Reading);
    }

    [Fact]
    public void ChapterTimeStoppingAloneIsNoLeave() {
        StartRows();

        Whole(Plain() with { Stopped = true });

        Assert.Equal("TimerStopped", feed.Via);
        Assert.Null(feed.Leave);
    }

    [Fact]
    public void AnEdgeIsFedOnce() {
        StartRows();
        SavedValues saved = Whole(Plain() with { Stopped = true });
        feed.Via = null;

        Whole(Plain() with { Stopped = true, StoppedBefore = true, SavedBefore = saved, SavedAfter = saved });

        Assert.Null(feed.Via);
    }

    // the credits set Completed again and change room: neither is the run's
    [Fact]
    public void NothingIsFedInTheCredits() {
        StartRows();

        Whole(Plain() with { Completed = true, InCredits = true });

        Assert.Null(feed.Leave);
        Assert.True(feed.AfterEnding);
    }

    [Fact]
    public void AMapNoRowCoversFeedsNothing() {
        StartRows();

        Whole(Plain() with { Completed = true, Scope = null });

        Assert.Null(feed.Leave);
    }

    [Fact]
    public void EveryFedFrameWritesANewStamp() {
        StartRows();

        Assert.Equal(Stamp + 1, Whole(Plain()).Stamp);
        Assert.Equal(Stamp + 1, feed.Stamp);
        Assert.True(feed.FedLastFrame);
    }

    // SpeedrunTool's room teleport updates the level from its hotkey: two
    // frames in a row, the first a teleport, the second a normal frame
    [Fact]
    public void TheTeleportsSecondUpdateIsANormalFrame() {
        StartRows();
        feed.LevelLoaded(false, true);

        SavedValues first = Whole(Plain() with { Room = "4" });
        Assert.Equal("Teleport", feed.Via);
        feed.Via = null;

        FrameReading second = Plain() with {
            Before = 117, RoomBefore = "4", Room = "4", SavedBefore = first, SavedAfter = first,
        };
        SavedValues after = Whole(second);

        Assert.Null(feed.Via);
        Assert.Equal(Stamp + 2, after.Stamp);
    }

    // a row a TAS plays one frame of is marked before this frame's closes,
    // and again last, so a row it opened this frame is marked too
    [Fact]
    public void ARowOpenedOnAFrameATasPlayedIsMarked() {
        WalkInRow();

        Whole(WalkedInto(At(10, 20)) with { TasAfter = true });

        Assert.Contains(traced, e => e.Kind == TraceKind.Disqualified && e.Reason == TraceReason.TasPlayed);
        Assert.Equal("Tas", feed.Via);
    }

    // ---------------------------------------------------------------- off

    [Fact]
    public void AFrameOffForgetsWhatAFedFrameWouldRead() {
        StartRows();
        feed.StateSaved = true;
        feed.OffBeforeUpdate();
        feed.LevelLoaded(true, false);
        feed.Restarted(null, 5, (1, 0));
        LeaveBy(LeaveKind.SaveAndQuit);
        feed.AfterEnding = true;

        SavedValues saved = feed.OffAfterUpdate(Unmoved, "5", fall: false);

        Assert.False(feed.FedLastFrame);
        Assert.False(feed.StateSaved);
        Assert.Equal(RoomLoad.None, feed.RoomLoad);
        Assert.False(feed.ChapterRestarted);
        Assert.Null(feed.Leave);
        Assert.False(feed.AfterEnding);
        Assert.Equal(Unmoved with { Moved = true, From = "5" }, saved);
    }

    [Fact]
    public void AFrameOffInTheFallKeepsTheRoomTheWalkInComesFrom() {
        Started();

        Assert.Equal("0", feed.OffAfterUpdate(Unmoved, "5", fall: true).From);
    }

    // ---------------------------------------------------------------- the trace's edges

    [Fact]
    public void AGoldenBerryRestartNamesItsRoomAndIsNoRestartChapter() {
        Started();
        feed.Restarted("3", 1234, (1, 0));

        Assert.False(feed.ChapterRestarted);
    }

    [Fact]
    public void WhyNothingIsFedIsToldOncePerEdge() {
        Assert.True(feed.FeedingIs("SwitchedOff"));
        Assert.False(feed.FeedingIs("SwitchedOff"));
        Assert.True(feed.FeedingIs(null));
        Assert.False(feed.FeedingIs(null));
        Assert.True(feed.FeedingIs("Assisted"));
    }
}
