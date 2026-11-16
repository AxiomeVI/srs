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
    }

    private readonly Game game = new();
    private readonly List<SegmentRule> rules = [];
    private readonly List<TraceEvent> traced = [];
    private readonly FrameFeed feed = new();

    private SegmentRule Add(string name, string start, string end, EndKind endKind = EndKind.NextStart,
        Collectibles endsOn = Collectibles.None, StartSetup setup = StartSetup.CurrentRoom) {
        SegmentRule rule = new("1a", name, name, StartKind.Room, setup, endKind, endsOn, Collectibles.None, null, 0, 0,
            rules.Count);
        rules.Add(rule);
        game.Map.ByName[name] = (start, end);
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
        feed.Restarted(1234, (2, 0));

        Assert.True(feed.ChapterRestarted);
        Assert.Equal(1234, feed.RestartedTime);
        Assert.Equal((2, 0), feed.RestartedArea);
    }
}
