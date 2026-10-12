using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the engine on synthetic rules, event by event. A segment ending in a room
// closes with a time only on an entry from the entry room of the segment
// starting there, so most tests give the segment after the one they time
public class RunTrackerTests {
    private sealed class Rooms : IRoomMap {
        public readonly Dictionary<string, (string Start, string End)> ByName = [];
        public readonly Dictionary<string, string[]> Berries = [];
        public readonly Dictionary<string, string> Entries = [];
        public string StartRoomOf(SegmentRule rule) => ByName.TryGetValue(rule.Name, out var r) ? r.Start : null;
        public string EndRoomOf(SegmentRule rule) => ByName.TryGetValue(rule.Name, out var r) ? r.End : null;
        public string EntryRoomOf(SegmentRule rule) => Entries.GetValueOrDefault(rule.Name);
        public IReadOnlyCollection<string> BerriesOf(SegmentRule rule) => Berries.GetValueOrDefault(rule.Name, []);
    }

    private readonly Rooms rooms = new();
    private readonly List<SegmentRule> rules = [];

    private SegmentRule Add(string name, string start, string end, Collectibles requires = Collectibles.None,
        EndKind endKind = EndKind.NextStart, Collectibles endsOn = Collectibles.None, int? dashes = null,
        StartKind startKind = StartKind.Room, StartSetup setup = StartSetup.NextRoom, long head = 0, long tail = 0,
        bool berries = false, string scope = "1a", string entry = null, int gems = 0, bool spans = false) {
        SegmentRule rule = new(scope, name, name, startKind, setup, endKind, endsOn, requires, dashes,
            head, tail, rules.Count) { Berries = berries ? BerrySet.WholeChapter : null, Gems = gems, SpansLeaves = spans };
        rules.Add(rule);
        rooms.ByName[name] = (start, end);
        if (entry != null) {
            rooms.Entries[name] = entry;
        }

        return rule;
    }

    private RunTracker Tracker() => new(rules, rooms);

    private static readonly EndState One = new(1, []);

    private static List<(string, long)> Of(List<SegmentRecord> records) =>
        records.Select(r => (r.Rule.Name, r.Ticks)).ToList();

    [Fact]
    public void ABacktrackIntoAStartRoomOpensNothing() {
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        // a state saved in room 7, inside Crossing, then back into its first room
        t.Restart("1a", "7", 0, true, false, _ => true);
        Assert.Empty(t.Open);
        Assert.Empty(t.RoomEntered("1a", "7", "6", 30, true, false, One));

        Assert.Empty(t.Open);
    }

    [Fact]
    public void ReturningToTheFirstRoomDoesNotReopenStart() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "1", "5", 20, true, false, One);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.RoomEntered("1a", "6", "5", 120, true, false, One);
        t.RoomEntered("1a", "5", "1", 140, true, false, One);

        Assert.Equal(["Crossing"], t.Open.Select(r => r.Name));
    }

    [Fact]
    public void AChainRecordsEverySegment() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        Add("Chasm", "9b", null, entry: "9");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        Assert.Equal([("Start", 100L)], Of(t.RoomEntered("1a", "5", "6", 100, true, false, One)));
        Assert.Equal([("Crossing", 150L)], Of(t.RoomEntered("1a", "9", "9b", 250, true, false, One)));
        Assert.Equal([("Chasm", 150L)], Of(t.ChapterTimeStopped(400, One)));
    }

    // so a chapter's segments add up to its whole chapter
    [Fact]
    public void AChaptersLastSegmentAndItsWholeChapterEndOnTheSameReading() {
        Add("Chasm", "9b", null, setup: StartSetup.CurrentRoom);
        Add("IL", "9b", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "9b", 0, true, false, _ => true);
        Assert.Equal([("Chasm", 400L), ("IL", 400L)], Of(t.ChapterTimeStopped(400, One)));
    }

    // the "RTM" variant closes at its collect, the others with the chapter
    [Fact]
    public void AnIlStaysOpenAcrossCheckpoints() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("IL Tape RTM", "1", null, requires: Collectibles.Cassette, endKind: EndKind.Collect,
            endsOn: Collectibles.Cassette, setup: StartSetup.CurrentRoom);
        Add("IL Tape Clear", "1", null, requires: Collectibles.Cassette, endKind: EndKind.ChapterEnd,
            setup: StartSetup.CurrentRoom);
        Add("IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        Assert.Equal([("Start", 100L)], Of(t.RoomEntered("1a", "5", "6", 100, true, false, One)));
        Assert.Equal([("IL Tape RTM", 250L)], Of(t.Collected(Collectibles.Cassette, 250, One)));
        Assert.Equal([("IL Tape Clear", 400L), ("IL", 400L)], Of(t.ChapterTimeStopped(400, One)));
    }

    [Fact]
    public void AHeartRunCountsForBothRows() {
        Add("Huge Mess", "a", "b", setup: StartSetup.CurrentRoom);
        Add("Huge Mess Heart", "a", "b", requires: Collectibles.Heart, setup: StartSetup.CurrentRoom);
        Add("Elevator Shaft", "b", null, entry: "a9");
        RunTracker t = Tracker();

        t.Restart("1a", "a", 0, true, false, _ => true);
        Assert.Empty(t.Collected(Collectibles.Heart, 50, One));

        Assert.Equal([("Huge Mess", 100L), ("Huge Mess Heart", 100L)],
            Of(t.RoomEntered("1a", "a9", "b", 100, true, false, One)));
    }

    [Fact]
    public void WithoutTheHeartOnlyThePlainRowIsRecorded() {
        Add("Huge Mess", "a", "b", setup: StartSetup.CurrentRoom);
        Add("Huge Mess Heart", "a", "b", requires: Collectibles.Heart, setup: StartSetup.CurrentRoom);
        Add("Elevator Shaft", "b", null, entry: "a9");
        RunTracker t = Tracker();

        t.Restart("1a", "a", 0, true, false, _ => true);

        Assert.Equal([("Huge Mess", 100L)], Of(t.RoomEntered("1a", "a9", "b", 100, true, false, One)));
    }

    [Fact]
    public void ACollectBeforeTheOpeningDoesNotCount() {
        Add("Start", "1", "a", setup: StartSetup.CurrentRoom);
        Add("Huge Mess Heart", "a", "b", requires: Collectibles.Heart, entry: "1z");
        Add("Elevator Shaft", "b", null, entry: "a9");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.Collected(Collectibles.Heart, 20, One);
        t.RoomEntered("1a", "1z", "a", 50, true, false, One);

        Assert.Empty(t.RoomEntered("1a", "a9", "b", 100, true, false, One));
    }

    [Fact]
    public void ACollectAndStopRowClosesOnTheCollect() {
        Add("Hollows", "04", "b-00", setup: StartSetup.CurrentRoom);
        Add("Hollows Tape", "04", null, requires: Collectibles.Cassette, endKind: EndKind.Collect, endsOn: Collectibles.Cassette,
            setup: StartSetup.CurrentRoom);
        Add("Reflection", "b-00", null, entry: "20");
        RunTracker t = Tracker();

        t.Restart("1a", "04", 0, true, false, _ => true);
        Assert.Equal([("Hollows Tape", 40L)], Of(t.Collected(Collectibles.Cassette, 40, One)));
        Assert.Equal([("Hollows", 90L)], Of(t.RoomEntered("1a", "20", "b-00", 90, true, false, One)));
    }

    [Fact]
    public void TwoCollectsCloseOnTheLater() {
        Collectibles both = Collectibles.Heart | Collectibles.Cassette;
        Add("Depths Both", "b-00", null, requires: both, endKind: EndKind.Collect, endsOn: both,
            setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "b-00", 0, true, false, _ => true);
        Assert.Empty(t.Collected(Collectibles.Heart, 30, One));
        Assert.Equal([("Depths Both", 60L)], Of(t.Collected(Collectibles.Cassette, 60, One)));
    }

    [Theory]
    [InlineData(2, "Start DTS")]
    [InlineData(1, "Start")]
    public void DtsTwinsAreExclusive(int dashes, string recorded) {
        Add("Start", "intro", "a-00", dashes: 1, setup: StartSetup.CurrentRoom);
        Add("Start DTS", "intro", "a-00", dashes: 2, setup: StartSetup.CurrentRoom);
        Add("Singular", "a-00", null, entry: "intro-03-space");
        RunTracker t = Tracker();

        t.Restart("1a", "intro", 0, true, false, _ => true);

        Assert.Equal([(recorded, 100L)],
            Of(t.RoomEntered("1a", "intro-03-space", "a-00", 100, true, false, new EndState(dashes, []))));
    }

    // by the end of Farewell both routes carry the same dashes: the IL is
    // decided at the first checkpoint crossed
    [Theory]
    [InlineData(2, "DTS IL")]
    [InlineData(1, "No DTS IL")]
    public void AWholeChapterReadsItsDashesAtTheFirstCheckpoint(int dashes, string recorded) {
        Add("DTS IL", "intro", null, dashes: 2, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        Add("No DTS IL", "intro", null, dashes: 1, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        Add("Singular", "a-00", "c-00", entry: "intro-03-space");
        Add("Power Source", "c-00", null, entry: "b-07");
        RunTracker t = Tracker();

        t.Restart("1a", "intro", 0, true, false, _ => true);
        // a room that starts no checkpoint is not read: the dash is lost after it
        t.RoomEntered("1a", "intro", "intro-01", 50, true, false, new EndState(3 - dashes, []));
        t.RoomEntered("1a", "intro-03-space", "a-00", 100, true, false, new EndState(dashes, []));
        t.RoomEntered("1a", "b-07", "c-00", 200, true, false, new EndState(3 - dashes, []));

        Assert.Equal([(recorded, 400L), ("Power Source", 200L)],
            Of(t.ChapterTimeStopped(400, new EndState(2, []))));
    }

    // without a checkpoint crossed, nothing says which IL it was
    [Fact]
    public void AWholeChapterWithADashRuleNeedsACheckpoint() {
        Add("DTS IL", "intro", null, dashes: 2, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "intro", 0, true, false, _ => true);

        Assert.Empty(t.ChapterTimeStopped(400, new EndState(2, [])));
    }

    [Fact]
    public void DropForgetsEverything() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.Drop();

        Assert.Empty(t.Open);
        Assert.Empty(t.RoomEntered("1a", "5", "6", 100, true, false, One));
    }

    [Fact]
    public void NeverRecordsANonPositiveTime() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 100, true, false, _ => true);

        Assert.Empty(t.RoomEntered("1a", "5", "6", 100, true, false, One));
    }

    [Fact]
    public void RecordsAnHourOrMore() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();
        long hour = TimeSpan.FromHours(1).Ticks;

        t.Restart("1a", "1", 0, true, false, _ => true);
        Assert.Equal([("Start", hour)], Of(t.RoomEntered("1a", "5", "6", hour, true, false, One)));
    }

    [Fact]
    public void ReEnteringTheStartRoomKeepsTheFirstOpening() {
        Add("Huge Mess", "08-a", "09-d", entry: "07-a");
        Add("Elevator Shaft", "09-d", null, entry: "09-b");
        RunTracker t = Tracker();

        t.RoomEntered("1a", "07-a", "08-a", 0, true, false, One);
        // a route that steps back into the entry room and in again
        t.RoomEntered("1a", "08-a", "07-a", 30, true, false, One);
        t.RoomEntered("1a", "07-a", "08-a", 50, true, false, One);

        Assert.Equal([("Huge Mess", 100L)], Of(t.RoomEntered("1a", "09-b", "09-d", 100, true, false, One)));
    }

    [Fact]
    public void AStartReachedWithoutControlOpensWhenControlReturns() {
        Add("Intervention", "3", "end_0", entry: "3x");
        Add("Awake", "end_0", null, setup: StartSetup.CurrentRoom, entry: "13");
        RunTracker t = Tracker();

        t.RoomEntered("1a", "3x", "3", 0, true, false, One);
        // the flip into end_0 happens in the wake-up: the close counts, the opening waits
        Assert.Equal([("Intervention", 100L)], Of(t.RoomEntered("1a", "13", "end_0", 100, false, false, One)));
        Assert.Empty(t.Open);
        // a start reached by an entry is not tested where the player appears
        t.ControlReturned("end_0", 160, _ => false);

        Assert.Equal([("Awake", 240L)], Of(t.ChapterTimeStopped(400, One)));
    }

    // Awake reached by its wake-up, without control: it waits for control in end_0
    private RunTracker AwakeWaitingForControl() {
        Add("Before", "3", "end_0", setup: StartSetup.CurrentRoom);
        Add("Awake", "end_0", null, setup: StartSetup.CurrentRoom, entry: "13");
        RunTracker t = Tracker();
        t.Restart("1a", "3", 0, true, false, _ => true);
        t.RoomEntered("1a", "13", "end_0", 100, false, false, One);
        return t;
    }

    [Fact]
    public void ControlReturningInThePendingRoomOpensTheStart() {
        RunTracker t = AwakeWaitingForControl();
        t.ControlReturned("end_0", 130, _ => true);

        Assert.Equal(["Awake"], t.Open.Select(r => r.Name));
    }

    [Fact]
    public void ControlReturningInAnotherRoomOpensNothing() {
        RunTracker t = AwakeWaitingForControl();
        t.ControlReturned("end_1", 130, _ => true);

        Assert.Empty(t.Open);
    }

    [Fact]
    public void ALaunchRowOpensOnlyWhenTheLaunchEnds() {
        Add("7a Start", "a-00", "b-00", startKind: StartKind.AfterLaunch, head: 1000,
            setup: StartSetup.CurrentRoom);
        Add("500m", "b-00", null, entry: "a-06");
        RunTracker t = Tracker();

        t.Restart("1a", "a-00-intro", 0, true, false, _ => true);
        t.RoomEntered("1a", "a-00-intro", "a-00", 10, true, true, One);
        Assert.Empty(t.Open);
        t.LaunchEnded("1a", "a-00", 40);

        Assert.Equal([("7a Start", 60L + 1000)], Of(t.RoomEntered("1a", "a-06", "b-00", 100, true, false, One)));
    }

    [Fact]
    public void ALaunchRowOpensOnALoadAfterTheLanding() {
        Add("7a Start", "a-00", "b-00", startKind: StartKind.AfterLaunch, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "a-00", 0, true, true, _ => true);
        Assert.Empty(t.Open);
        t.Drop();
        t.Restart("1a", "a-00", 0, true, false, _ => true);

        Assert.Equal(["7a Start"], t.Open.Select(r => r.Name));
    }

    [Fact]
    public void HeadAndTailAreAdded() {
        Add("Granny", "0", null, head: 1037, tail: 544, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "0", 0, true, false, _ => true);

        Assert.Equal([("Granny", 1000L + 1037 + 544)], Of(t.ChapterTimeStopped(1000, One)));
    }

    [Fact]
    public void AFollowingBerryCounts() {
        Add("ARB Crossing", "6", "9b", berries: true, setup: StartSetup.CurrentRoom);
        Add("Chasm", "9b", null, entry: "9");
        rooms.Berries["ARB Crossing"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "6", 0, true, false, _ => true);
        t.BerryCollected("b1");

        Assert.Empty(t.RoomEntered("1a", "9", "9b", 100, true, false, One));
        t.Drop();
        t.Restart("1a", "6", 0, true, false, _ => true);
        t.BerryCollected("b1");
        Assert.Equal([("ARB Crossing", 100L)],
            Of(t.RoomEntered("1a", "9", "9b", 100, true, false, new EndState(1, ["b2"]))));
    }

    [Fact]
    public void ClosesComeBeforeOpens() {
        Add("A", "1", "2", setup: StartSetup.CurrentRoom);
        Add("B", "2", "3", entry: "1");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);

        Assert.Equal([("A", 100L)], Of(t.RoomEntered("1a", "1", "2", 100, true, false, One)));
        Assert.Equal(["B"], t.Open.Select(r => r.Name));
    }

    [Fact]
    public void OnlyTheScopesRulesOpen() {
        SegmentRule other = new("6b", "Elsewhere", "Start", StartKind.Room, StartSetup.CurrentRoom, EndKind.NextStart,
            Collectibles.None, Collectibles.None, null, 0, 0, 0);
        rules.Add(other);
        rooms.ByName["Elsewhere"] = ("1", "6");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);

        Assert.Empty(t.Open);
    }

    [Fact]
    public void ASegmentEndingWhereItStartsClosesThenReopens() {
        Add("Loop", "2", "2", setup: StartSetup.CurrentRoom, entry: "x");
        RunTracker t = Tracker();

        t.Restart("1a", "2", 0, true, false, _ => true);
        t.RoomEntered("1a", "2", "x", 30, true, false, One);

        Assert.Equal([("Loop", 100L)], Of(t.RoomEntered("1a", "x", "2", 100, true, false, One)));
        Assert.Equal(["Loop"], t.Open.Select(r => r.Name));
        t.RoomEntered("1a", "2", "x", 120, true, false, One);
        Assert.Equal([("Loop", 50L)], Of(t.RoomEntered("1a", "x", "2", 150, true, false, One)));
    }

    [Fact]
    public void ChapterTimeStoppedLeavesASegmentWhoseEndRoomWasNotReached() {
        Add("A", "1", "6", setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);

        Assert.Empty(t.ChapterTimeStopped(400, One));
        Assert.Equal(["A"], t.Open.Select(r => r.Name));
    }

    [Fact]
    public void DropForgetsAPendingStart() {
        RunTracker t = AwakeWaitingForControl();
        t.Drop();
        t.ControlReturned("end_0", 160, _ => true);

        Assert.Empty(t.Open);
    }

    [Fact]
    public void ARestartForgetsAPendingStartFromAnotherRoom() {
        Add("Before", "w", "y", setup: StartSetup.CurrentRoom);
        Add("FromY", "y", null, entry: "w");
        Add("FromX", "x", null, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        // FromY is pending in y when a restart lands in x
        t.Restart("1a", "w", 0, true, false, _ => true);
        t.RoomEntered("1a", "w", "y", 100, false, false, One);
        t.Restart("1a", "x", 0, false, false, _ => true);
        t.ControlReturned("x", 60, _ => true);

        Assert.Equal(["FromX"], t.Open.Select(r => r.Name));
    }

    // 6A's watched fall: Madeline falls from start through 04, 02b, 02 and 01
    // into 00 in the fall's own state, so every room of it is entered from start
    [Fact]
    public void ADisqualifiedSegmentRecordsNothingButStillChains() {
        Add("6a Start", "start", "00", setup: StartSetup.CurrentRoom);
        Add("Lake", "00", "04", entry: "start");
        Add("Hollows", "04", "b-00", entry: "02b");
        RunTracker t = Tracker();

        t.Restart("1a", "start", 0, true, false, _ => true);
        t.Disqualify();
        // the fall passes Hollows' room: nothing closes, nothing opens
        Assert.Empty(t.RoomEntered("1a", "start", "04", 50, false, false, One));
        Assert.Equal(["6a Start"], t.Open.Select(r => r.Name));
        Assert.Empty(t.RoomEntered("1a", "start", "02b", 60, false, false, One));
        Assert.Empty(t.RoomEntered("1a", "start", "02", 70, false, false, One));
        Assert.Empty(t.RoomEntered("1a", "start", "01", 75, false, false, One));
        // 6a Start closes unrecorded, and Lake waits for control
        Assert.Empty(t.RoomEntered("1a", "start", "00", 80, false, false, One));
        Assert.Empty(t.Open);
        t.ControlReturned("00", 100, _ => false);

        Assert.Equal([("Lake", 200L)], Of(t.RoomEntered("1a", "02b", "04", 300, true, false, One)));
    }

    [Fact]
    public void ADisqualifiedSegmentRecordsNothingOnACollectOrAStop() {
        Add("Hollows Tape", "04", null, requires: Collectibles.Cassette, endKind: EndKind.Collect,
            endsOn: Collectibles.Cassette, setup: StartSetup.CurrentRoom);
        Add("Final", "04", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "04", 0, true, false, _ => true);
        t.Disqualify();
        Assert.Empty(t.Collected(Collectibles.Cassette, 40, One));
        Assert.Equal(["Final"], t.Open.Select(r => r.Name));
        Assert.Empty(t.ChapterTimeStopped(90, One));
        Assert.Empty(t.Open);
    }

    [Fact]
    public void DisqualifyForgetsPendingStarts() {
        RunTracker t = AwakeWaitingForControl();
        t.Disqualify();
        t.ControlReturned("end_0", 160, _ => true);

        Assert.Empty(t.Open);
    }

    [Fact]
    public void AMidSegmentSavestateRecordsTheNextSegment() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        Add("Chasm", "9b", null, entry: "9");
        RunTracker t = Tracker();

        // a state saved in room 7, mid-Crossing
        t.Restart("1a", "7", 1000, true, false, _ => true);
        Assert.Empty(t.Open);

        Assert.Empty(t.RoomEntered("1a", "9", "9b", 1500, true, false, One));
        Assert.Equal([("Chasm", 500L)], Of(t.ChapterTimeStopped(2000, One)));
    }

    [Fact]
    public void ALoadAwayFromTheSpawnOpensNothing() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => false);
        Assert.Empty(t.Open);
    }

    [Fact]
    public void ALoadIntoANextRoomStartOpensNothing() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "6", 0, true, false, _ => true);
        Assert.Empty(t.Open);
    }

    [Fact]
    public void ALoadDropsWhatWasOpen() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        Add("Chasm", "9b", null, entry: "9");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.Restart("1a", "7", 50, true, false, _ => true);

        Assert.Empty(t.Open);
        Assert.Empty(t.RoomEntered("1a", "9", "9b", 300, true, false, One));
    }

    [Fact]
    public void ARestartWithoutControlWaitsForIt() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, false, false, _ => true);
        Assert.Empty(t.Open);
        t.ControlReturned("1", 40, _ => false);

        Assert.Equal([("Start", 60L)], Of(t.RoomEntered("1a", "5", "6", 100, true, false, One)));
    }

    // a state saved mid-respawn or mid-wake-up: where the player stood at the
    // save says nothing, where they appear does
    [Fact]
    public void ALoadWithoutControlTestsWhereThePlayerAppears() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");

        RunTracker t = Tracker();
        t.RestartAtAppearance("1a", "1");
        Assert.Empty(t.Open);
        t.ControlReturned("1", 40, _ => true);
        Assert.Equal([("Start", 60L)], Of(t.RoomEntered("1a", "5", "6", 100, true, false, One)));

        RunTracker refused = Tracker();
        refused.RestartAtAppearance("1a", "1");
        refused.ControlReturned("1", 40, _ => false);
        Assert.Empty(refused.Open);
    }

    [Fact]
    public void ALoadWithoutControlInANextRoomStartWaitsForNothing() {
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.RestartAtAppearance("1a", "6");
        t.ControlReturned("6", 40, _ => true);

        Assert.Empty(t.Open);
    }

    // the debug map into 2A's 13, then the phone: Awake opens from its entry
    // room however the run reached it
    [Fact]
    public void AWakeUpOpensOnceControlReturns() {
        Add("Intervention", "3", "end_0", entry: "3x", scope: "2a");
        Add("Awake", "end_0", null, setup: StartSetup.CurrentRoom, scope: "2a", entry: "13");
        RunTracker t = Tracker();

        t.Restart("2a", "13", 0, true, false, _ => true);
        // the wake-up changes rooms without control
        Assert.Empty(t.RoomEntered("2a", "13", "end_0", 100, false, false, One));
        Assert.Empty(t.Open);
        t.ControlReturned("end_0", 300, _ => false);

        Assert.Equal([("Awake", 200L)], Of(t.ChapterTimeStopped(500, One)));
    }

    // the debug map into 5A's b-16, then the mirror: void, and the wake-up in c-00
    [Fact]
    public void AnEntryFromTheEntryRoomOpensHoweverTheRunGotThere() {
        Add("Unravelling", "c-00", "d-00", setup: StartSetup.CurrentRoom, scope: "5a", entry: "void");
        Add("Search", "d-00", "e-00", scope: "5a", entry: "c-13");
        RunTracker t = Tracker();

        t.Restart("5a", "b-16", 0, true, false, _ => true);
        t.RoomEntered("5a", "b-16", "void", 50, false, false, One);
        t.RoomEntered("5a", "void", "c-00", 100, false, false, One);
        t.ControlReturned("c-00", 150, _ => false);

        Assert.Equal([("Unravelling", 250L)], Of(t.RoomEntered("5a", "c-13", "d-00", 400, true, false, One)));
    }

    // Speed Run Tool's PageDown into the room before Crossing's, then walking in
    [Fact]
    public void ATeleportIntoTheEntryRoomThenAnEntryRecords() {
        Add("Crossing", "6", "9b", entry: "5");
        Add("Chasm", "9b", null, entry: "9");
        RunTracker t = Tracker();

        t.Restart("1a", "5", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);

        Assert.Equal([("Crossing", 200L)], Of(t.RoomEntered("1a", "9", "9b", 300, true, false, One)));
    }

    // 4A's d-00 is reached from c-08, and from c-10, the berry room beside it
    [Fact]
    public void AnEntryFromAnotherRoomOfTheSegmentBeforeDropsItAndOpensNothing() {
        Add("Old Trail", "c-00", "d-00", scope: "4a", entry: "b-08");
        Add("Cliff Face", "d-00", null, scope: "4a", entry: "c-08");

        RunTracker control = Tracker();
        control.RoomEntered("4a", "b-08", "c-00", 0, true, false, One);
        Assert.Equal([("Old Trail", 500L)], Of(control.RoomEntered("4a", "c-08", "d-00", 500, true, false, One)));
        Assert.Equal(["Cliff Face"], control.Open.Select(r => r.Name));

        RunTracker t = Tracker();
        t.RoomEntered("4a", "b-08", "c-00", 0, true, false, One);
        Assert.Empty(t.RoomEntered("4a", "c-10", "d-00", 500, true, false, One));
        Assert.Empty(t.Open);
    }

    [Fact]
    public void PassingBackThroughTheLastCheckpointsRoomStillChains() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        Add("Chasm", "9b", null, entry: "9");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        // a route that loops back through Start's room inside Crossing
        t.RoomEntered("1a", "6x", "1", 150, true, false, One);
        Assert.Equal([("Crossing", 300L)], Of(t.RoomEntered("1a", "9", "9b", 400, true, false, One)));
        Assert.Equal(["Chasm"], t.Open.Select(r => r.Name));
    }

    // 2A's heart and Restart Chapter: the RC row is timed to the moment the old
    // level is left, with the old session's chapter time
    [Fact]
    public void TheRcRowClosesAtTheRestartAndTheRestIsDropped() {
        Add("Start", "start", "3", setup: StartSetup.CurrentRoom, scope: "2a");
        Add("Start Heart RC", "start", "3", requires: Collectibles.Heart, endKind: EndKind.Restart,
            setup: StartSetup.CurrentRoom, scope: "2a");
        Add("Intervention", "3", "end_0", scope: "2a", entry: "3x");
        RunTracker t = Tracker();

        t.Restart("2a", "start", 0, true, false, _ => true);
        Assert.Empty(t.Collected(Collectibles.Heart, 1322, One));
        Assert.Equal([("Start Heart RC", 1380L)], Of(t.ChapterRestarted(1380, One)));
        Assert.Empty(t.Open);

        // the new level opens the chapter's start again, and plain Start ranks
        t.Restart("2a", "start", 0, true, false, _ => true);
        Assert.Equal([("Start", 270L)], Of(t.RoomEntered("2a", "3x", "3", 270, true, false, One)));
    }

    [Fact]
    public void ARestartWithoutTheCollectibleRecordsNothing() {
        Add("Start Heart RC", "start", "3", requires: Collectibles.Heart, endKind: EndKind.Restart,
            setup: StartSetup.CurrentRoom, scope: "2a");
        RunTracker t = Tracker();

        t.Restart("2a", "start", 0, true, false, _ => true);

        Assert.Empty(t.ChapterRestarted(1380, One));
        Assert.Empty(t.Open);
    }

    // the heart, then on into Intervention, then Restart Chapter much later:
    // that run is not one of the row
    [Fact]
    public void TheRcRowDoesNotOutliveItsSegment() {
        Add("Start", "start", "3", setup: StartSetup.CurrentRoom, scope: "2a");
        Add("Start Heart RC", "start", "3", requires: Collectibles.Heart, endKind: EndKind.Restart,
            setup: StartSetup.CurrentRoom, scope: "2a");
        Add("Intervention", "3", "end_0", scope: "2a", entry: "3x");

        // control case: walking between the segment's own rooms keeps it open
        RunTracker control = Tracker();
        control.Restart("2a", "start", 0, true, false, _ => true);
        control.Collected(Collectibles.Heart, 100, One);
        Assert.Empty(control.RoomEntered("2a", "start", "s1", 150, true, false, One));
        Assert.Equal([("Start Heart RC", 200L)], Of(control.ChapterRestarted(200, One)));

        // walking out of its segment drops it unrecorded
        RunTracker t = Tracker();
        t.Restart("2a", "start", 0, true, false, _ => true);
        t.Collected(Collectibles.Heart, 100, One);
        Assert.Equal([("Start", 270L)], Of(t.RoomEntered("2a", "3x", "3", 270, true, false, One)));
        Assert.Equal(["Intervention"], t.Open.Select(r => r.Name));

        Assert.Empty(t.ChapterRestarted(9000, One));
    }

    // the tracker itself refuses: the double would let the row in from room 5
    [Fact]
    public void AMapSpawnRowNeverOpensOnAWalkIn() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        Add("ARB Crossing", "6", "9b", berries: true, setup: StartSetup.MapSpawn, entry: "5");
        rooms.Berries["ARB Crossing"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        Assert.Equal([("Start", 100L)], Of(t.RoomEntered("1a", "5", "6", 100, true, false, One)));

        Assert.Equal(["Crossing"], t.Open.Select(r => r.Name));
    }

    // a map-spawn row does not open on a walk-in, even where an entry room is named for it
    [Fact]
    public void AMapSpawnRowDoesNotOpenOnAWalkInEvenWithAnEntryRoom() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("ARB Crossing", "6", "9b", berries: true, setup: StartSetup.MapSpawn, entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);

        Assert.Empty(t.RoomEntered("1a", "5", "6", 100, true, false, One));
        Assert.Empty(t.Open);
    }

    [Fact]
    public void AMapSpawnRowOpensOnARestartAtItsSpawn() {
        Add("Crossing", "6", "9b", entry: "5");
        Add("ARB Crossing", "6", "9b", berries: true, setup: StartSetup.MapSpawn);
        Add("Chasm", "9b", null, entry: "9");
        rooms.Berries["ARB Crossing"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "6", 0, true, false, _ => false);
        Assert.Empty(t.Open);

        t.Restart("1a", "6", 10, true, false, _ => true);
        Assert.Equal(["ARB Crossing"], t.Open.Select(r => r.Name));
        t.BerryCollected("b1");
        Assert.Equal([("ARB Crossing", 90L)], Of(t.RoomEntered("1a", "9", "9b", 100, true, false, One)));
    }

    [Fact]
    public void AMapSpawnRowLoadedWithoutControlWaitsForTheAppearance() {
        Add("ARB Chasm", "9b", null, berries: true, setup: StartSetup.MapSpawn);
        RunTracker t = Tracker();

        t.RestartAtAppearance("1a", "9b");
        Assert.Empty(t.Open);
        t.ControlReturned("9b", 20, _ => true);

        Assert.Equal(["ARB Chasm"], t.Open.Select(r => r.Name));
    }

    // no berries resolved is not "no berries required"
    [Fact]
    public void ABerryRowWhoseBerriesAreUnknownIsNeverRecorded() {
        Add("ARB Start", "1", "6", berries: true, setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);

        Assert.Empty(t.RoomEntered("1a", "5", "6", 100, true, false, One));
    }

    [Fact]
    public void ARunMissingOneBerryRecordsThePlainRowOnly() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("ARB Start", "1", "6", berries: true, setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        rooms.Berries["ARB Start"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.BerryCollected("b1");
        Assert.Equal([("Start", 100L)], Of(t.RoomEntered("1a", "5", "6", 100, true, false, One)));

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.BerryCollected("b1");
        t.BerryCollected("b2");
        Assert.Equal([("Start", 100L), ("ARB Start", 100L)], Of(t.RoomEntered("1a", "5", "6", 100, true, false, One)));
    }

    // a berry banked before the row opened is not the run's
    [Fact]
    public void ABerryCollectedBeforeTheRowOpenedDoesNotCount() {
        Add("ARB Start", "1", "6", berries: true, setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        rooms.Berries["ARB Start"] = ["b1"];
        RunTracker t = Tracker();

        t.BerryCollected("b1");
        t.Restart("1a", "1", 0, true, false, _ => true);

        Assert.Empty(t.RoomEntered("1a", "5", "6", 100, true, false, One));
    }

    [Fact]
    public void ARowEndingOnTheHeartCountsTheBerriesStillFollowing() {
        Add("ARB Start to Heart", "1", null, requires: Collectibles.Heart, endKind: EndKind.Collect,
            endsOn: Collectibles.Heart, berries: true, setup: StartSetup.CurrentRoom);
        rooms.Berries["ARB Start to Heart"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.BerryCollected("b1");
        Assert.Equal([("ARB Start to Heart", 250L)],
            Of(t.Collected(Collectibles.Heart, 250, new EndState(1, ["b2"]))));

        // without the second berry the heart still closes the row, unrecorded
        t.Restart("1a", "1", 0, true, false, _ => true);
        t.BerryCollected("b1");
        Assert.Empty(t.Collected(Collectibles.Heart, 250, One));
        Assert.Empty(t.Open);
    }

    // a berry still following when chapter time stops counts
    [Fact]
    public void AFollowingBerryCountsWhenChapterTimeStops() {
        Add("ARB Chasm", "9b", null, berries: true, setup: StartSetup.MapSpawn);
        Add("ARB IL", "9b", null, endKind: EndKind.ChapterEnd, berries: true, setup: StartSetup.CurrentRoom);
        rooms.Berries["ARB Chasm"] = ["b1"];
        rooms.Berries["ARB IL"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "9b", 0, true, false, _ => true);
        t.BerryCollected("b1");

        Assert.Equal([("ARB Chasm", 400L), ("ARB IL", 400L)],
            Of(t.ChapterTimeStopped(400, new EndState(1, ["b2"]))));
    }

    // the heart is required and ends nothing
    [Fact]
    public void ABerryRowWithAHeartNeedsBoth() {
        Add("ARB Huge Mess", "a", "b", requires: Collectibles.Heart, berries: true, setup: StartSetup.CurrentRoom);
        Add("Elevator Shaft", "b", null, entry: "a9");
        rooms.Berries["ARB Huge Mess"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "a", 0, true, false, _ => true);
        t.BerryCollected("b1");
        Assert.Empty(t.RoomEntered("1a", "a9", "b", 100, true, false, One));

        t.Restart("1a", "a", 0, true, false, _ => true);
        t.BerryCollected("b1");
        Assert.Empty(t.Collected(Collectibles.Heart, 50, One));
        Assert.Equal([("ARB Huge Mess", 100L)], Of(t.RoomEntered("1a", "a9", "b", 100, true, false, One)));
    }

    // 4A's d-00 has two ways in. Each route keeps to itself: a segment ends
    // with a time only on the entry of the row that follows it on its route
    [Fact]
    public void EachRouteThroughCliffFaceKeepsToItself() {
        Add("Old Trail", "c-00", "d-00", scope: "4a", entry: "b-08");
        Add("Cliff Face", "d-00", null, scope: "4a", entry: "c-08");
        Add("ARB Old Trail", "c-00", "d-00", scope: "4a", entry: "b-08", berries: true);
        Add("ARB Cliff Face", "d-00", null, scope: "4a", entry: "c-10", berries: true);
        rooms.Berries["ARB Old Trail"] = ["b1"];
        rooms.Berries["ARB Cliff Face"] = ["b2"];

        RunTracker berryRoute = Tracker();
        berryRoute.RoomEntered("4a", "b-08", "c-00", 0, true, false, One);
        berryRoute.BerryCollected("b1");
        Assert.Equal([("ARB Old Trail", 500L)], Of(berryRoute.RoomEntered("4a", "c-10", "d-00", 500, true, false, One)));
        Assert.Equal(["ARB Cliff Face"], berryRoute.Open.Select(r => r.Name));

        // with every berry, and still not the berry row's way in
        RunTracker anyRoute = Tracker();
        anyRoute.RoomEntered("4a", "b-08", "c-00", 0, true, false, One);
        anyRoute.BerryCollected("b1");
        Assert.Equal([("Old Trail", 500L)], Of(anyRoute.RoomEntered("4a", "c-08", "d-00", 500, true, false, One)));
        Assert.Equal(["Cliff Face"], anyRoute.Open.Select(r => r.Name));
    }

    // 8A's berry HotM Vertical ends where only the plain HotM Horizontal
    // starts, and 1A's berry Start where the berry rows are map-spawn only
    [Fact]
    public void ABerryRowEndsOnThePlainEntryWhereNoBerryRowFollows() {
        Add("ARB HotM Vertical", "d-00", "d-08", berries: true, setup: StartSetup.CurrentRoom);
        Add("HotM Horizontal", "d-08", null, entry: "d-07");
        Add("ARB Start", "1", "6", berries: true, setup: StartSetup.CurrentRoom);
        Add("ARB Crossing", "6", "9b", berries: true, setup: StartSetup.MapSpawn, entry: "5");
        Add("Crossing", "6", "9b", entry: "5");
        rooms.Berries["ARB HotM Vertical"] = ["b1"];
        rooms.Berries["ARB Start"] = ["b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "d-00", 0, true, false, _ => true);
        t.BerryCollected("b1");
        Assert.Equal([("ARB HotM Vertical", 100L)], Of(t.RoomEntered("1a", "d-07", "d-08", 100, true, false, One)));

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.BerryCollected("b2");
        Assert.Equal([("ARB Start", 100L)], Of(t.RoomEntered("1a", "5", "6", 100, true, false, One)));
        Assert.Equal(["Crossing"], t.Open.Select(r => r.Name));
    }

    // 7A's 3000m is timed whole and in three pieces at once: a piece closes on
    // the walk into the next, the last one with the chapter
    [Fact]
    public void ACheckpointCutInPiecesIsAlsoTimedWhole() {
        Add("3000m", "g-00", null, entry: "f-11");
        Add("Downdraft", "g-00", "g-01", entry: "f-11");
        Add("Updraft", "g-01", "g-02", entry: "g-00b");
        Add("Nodraft", "g-02", null, entry: "g-01");
        Add("ARB Downdraft", "g-00", "g-01", berries: true, entry: "f-11");
        Add("ARB Updraft", "g-01", "g-02", berries: true, entry: "g-00b");
        rooms.Berries["ARB Downdraft"] = ["b1"];
        rooms.Berries["ARB Updraft"] = ["b2"];
        RunTracker t = Tracker();

        t.RoomEntered("1a", "f-11", "g-00", 0, true, false, One);
        Assert.Empty(t.RoomEntered("1a", "g-00", "g-00b", 40, true, false, One));
        t.BerryCollected("b1");
        Assert.Equal([("Downdraft", 100L), ("ARB Downdraft", 100L)],
            Of(t.RoomEntered("1a", "g-00b", "g-01", 100, true, false, One)));
        // the second piece's berry is missed: its plain row only
        Assert.Equal([("Updraft", 150L)], Of(t.RoomEntered("1a", "g-01", "g-02", 250, true, false, One)));
        Assert.Equal([("3000m", 400L), ("Nodraft", 150L)], Of(t.ChapterTimeStopped(400, One)));
    }

    // a chapter run asks for the chapter's six gems, a checkpoint row for its own:
    // the six are counted, since 7A's heart gate also opens on gems the file holds
    [Fact]
    public void GemsAreCounted() {
        Add("FC IL", "a-00", null, Collectibles.Gem, EndKind.ChapterEnd, setup: StartSetup.CurrentRoom, gems: 6);
        Add("Start Gem", "a-00", "b-00", Collectibles.Gem, setup: StartSetup.CurrentRoom, gems: 1);
        Add("500m", "b-00", "c-00", entry: "a-06");
        RunTracker t = Tracker();

        t.Restart("1a", "a-00", 0, true, false, _ => true);
        t.Collected(Collectibles.Gem, 50, One);
        Assert.Equal([("Start Gem", 100L)], Of(t.RoomEntered("1a", "a-06", "b-00", 100, true, false, One)));
        for (int gem = 1; gem < 5; gem++) {
            t.Collected(Collectibles.Gem, 100 + gem, One);
        }

        // five of six
        Assert.Empty(t.ChapterTimeStopped(600, One));

        t.Restart("1a", "a-00", 0, true, false, _ => true);
        for (int gem = 0; gem < 6; gem++) {
            t.Collected(Collectibles.Gem, 10 + gem, One);
        }

        Assert.Equal([("FC IL", 600L)], Of(t.ChapterTimeStopped(600, One)));
    }

    // a Return to Map ends no imported row: everything open is dropped, as
    // the next level's Restart dropped it before
    [Fact]
    public void AReturnToMapDropsEverythingOpenUnrecorded() {
        Add("Chasm", "9b", null, setup: StartSetup.CurrentRoom);
        Add("IL", "9b", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "9b", 0, true, false, _ => true);

        Assert.Empty(t.Left(LeaveKind.ReturnToMap, 300, One));
        Assert.Empty(t.Open);
        Assert.Empty(t.ChapterTimeStopped(400, One));
    }

    // a berry still following counts there, as when chapter time stops
    [Fact]
    public void AChapterEndClosesWhatChapterTimeStoppingCloses() {
        Add("Chasm", "9b", null, setup: StartSetup.CurrentRoom);
        Add("IL", "9b", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        Add("ARB Chasm", "9b", null, berries: true, setup: StartSetup.MapSpawn);
        rooms.Berries["ARB Chasm"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "9b", 0, true, false, _ => true);
        t.BerryCollected("b1");

        Assert.Equal([("Chasm", 400L), ("IL", 400L), ("ARB Chasm", 400L)],
            Of(t.Left(LeaveKind.ChapterEnd, 400, new EndState(1, ["b2"]))));
        Assert.Empty(t.Open);
    }

    // nothing outlives a chapter's end: a row that does not end there is dropped
    [Fact]
    public void AChapterEndDropsWhatDoesNotEndThere() {
        Add("A", "1", "6", setup: StartSetup.CurrentRoom);
        Add("IL Tape RTM", "1", null, requires: Collectibles.Cassette, endKind: EndKind.Collect,
            endsOn: Collectibles.Cassette, setup: StartSetup.CurrentRoom);
        Add("B", "6", null, entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);

        Assert.Empty(t.Left(LeaveKind.ChapterEnd, 400, One));
        Assert.Empty(t.Open);
        Assert.Empty(t.Collected(Collectibles.Cassette, 400, One));
        Assert.Empty(t.RoomEntered("1a", "5", "6", 400, true, false, One));
    }

    // a Save and Quit carries the clock. The tracker only takes differences,
    // so a clock restarted at zero gives the same time
    [Theory]
    [InlineData(300)]
    [InlineData(0)]
    public void ARowSetAsideByASaveAndQuitCarriesOnAtTheReentry(long resumedAt) {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        Assert.Empty(t.Left(LeaveKind.SaveAndQuit, 300, One));
        Assert.Empty(t.Open);

        t.Reentered(LeaveKind.SaveAndQuit, "1a", "3", resumedAt, true, false, _ => false);
        Assert.Equal(["Start"], t.Open.Select(r => r.Name));
        Assert.Equal([("Start", 400L)], Of(t.RoomEntered("1a", "5", "6", resumedAt + 100, true, false, One)));
        Assert.Equal(["Crossing"], t.Open.Select(r => r.Name));
    }

    // "nothing at 0 or less" is about the row's whole time: a last piece of no
    // length still records the pieces before it
    [Fact]
    public void ARowEndingOnTheReentryReadingRecordsItsEarlierPieces() {
        Add("Hollows Tape", "04", null, requires: Collectibles.Cassette, endKind: EndKind.Collect,
            endsOn: Collectibles.Cassette, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "04", 0, true, false, _ => true);
        t.Left(LeaveKind.SaveAndQuit, 300, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "04", 300, true, false, _ => false);

        Assert.Equal([("Hollows Tape", 300L)], Of(t.Collected(Collectibles.Cassette, 300, One)));
    }

    // what the run collected before the Save and Quit is still the row's
    [Fact]
    public void ARowSetAsideKeepsWhatItCollected() {
        Add("FC Start", "a-00", "b-00", Collectibles.Heart | Collectibles.Gem, berries: true,
            setup: StartSetup.CurrentRoom, gems: 1);
        Add("500m", "b-00", null, entry: "a-06");
        rooms.Berries["FC Start"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "a-00", 0, true, false, _ => true);
        t.Collected(Collectibles.Heart, 50, One);
        t.Collected(Collectibles.Gem, 60, One);
        t.BerryCollected("b1");
        t.Left(LeaveKind.SaveAndQuit, 100, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "a-03", 100, true, false, _ => false);
        t.BerryCollected("b2");

        Assert.Equal([("FC Start", 250L)], Of(t.RoomEntered("1a", "a-06", "b-00", 250, true, false, One)));
    }

    [Fact]
    public void AWholeChapterKeepsItsFirstCheckpointsDashesAcrossASaveAndQuit() {
        Add("DTS IL", "intro", null, dashes: 2, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        Add("Singular", "a-00", "c-00", entry: "intro-03-space");
        RunTracker t = Tracker();

        t.Restart("1a", "intro", 0, true, false, _ => true);
        t.RoomEntered("1a", "intro-03-space", "a-00", 100, true, false, new EndState(2, []));
        t.Left(LeaveKind.SaveAndQuit, 150, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "a-00", 150, true, false, _ => false);

        Assert.Equal([("DTS IL", 400L)], Of(t.Left(LeaveKind.ChapterEnd, 400, new EndState(1, []))));
    }

    [Fact]
    public void ADisqualifiedRowStaysDisqualifiedAcrossASaveAndQuit() {
        Add("Final", "04", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "04", 0, true, false, _ => true);
        t.Disqualify();
        t.Left(LeaveKind.SaveAndQuit, 50, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "04", 50, true, false, _ => false);

        Assert.Equal(["Final"], t.Open.Select(r => r.Name));
        Assert.Empty(t.Left(LeaveKind.ChapterEnd, 90, One));
    }

    // the re-entry lands on the spawn the row started from: it carries on, and
    // is not started over
    [Fact]
    public void ARowCarriedOnIsNotOpenedAgainOnItsOwnSpawn() {
        Add("ARB Crossing", "6", "9b", berries: true, setup: StartSetup.MapSpawn);
        Add("Chasm", "9b", null, entry: "9");
        rooms.Berries["ARB Crossing"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "6", 10, true, false, _ => true);
        t.BerryCollected("b1");
        t.Left(LeaveKind.SaveAndQuit, 50, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "6", 50, false, false, _ => true);
        t.ControlReturned("6", 50, _ => true);

        Assert.Equal(["ARB Crossing"], t.Open.Select(r => r.Name));
        Assert.Equal([("ARB Crossing", 90L)], Of(t.RoomEntered("1a", "9", "9b", 100, true, false, One)));
    }

    // the rows starting on the re-entry's spawn open as on a restart, beside
    // those carried on; a Next Room row does not
    [Fact]
    public void AReentryOpensTheRowsStartingOnItsSpawn() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");
        Add("ARB Crossing", "6", "9b", berries: true, setup: StartSetup.MapSpawn);
        Add("IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.Left(LeaveKind.SaveAndQuit, 150, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "6", 150, true, false, _ => true);

        Assert.Equal(["IL", "Crossing", "ARB Crossing"], t.Open.Select(r => r.Name));
    }

    // 2A's route from Pink up: the Save and Quit lands during the wake-up,
    // before Awake has opened
    [Fact]
    public void AWakeUpRowWaitingAtASaveAndQuitOpensAgainOnTheReentry() {
        RunTracker t = AwakeWaitingForControl();

        t.Left(LeaveKind.SaveAndQuit, 130, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "end_0", 130, false, false, _ => true);
        Assert.Empty(t.Open);
        t.ControlReturned("end_0", 190, _ => false);

        Assert.Equal([("Awake", 210L)], Of(t.ChapterTimeStopped(400, One)));
    }

    [Fact]
    public void ASaveAndQuitForgetsAStartWaitingForControl() {
        RunTracker t = AwakeWaitingForControl();

        t.Left(LeaveKind.SaveAndQuit, 130, One);
        t.ControlReturned("end_0", 160, _ => true);

        Assert.Empty(t.Open);
    }

    // a pause during the respawn: the level is left again before anything ran
    [Fact]
    public void ASecondLeaveBeforeControlAddsNoTime() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        Add("Crossing", "6", "9b", entry: "5");

        RunTracker t = Tracker();
        t.Restart("1a", "1", 0, true, false, _ => true);
        t.Left(LeaveKind.SaveAndQuit, 300, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "3", 300, false, false, _ => false);
        t.Left(LeaveKind.SaveAndQuit, 300, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "3", 300, true, false, _ => false);
        Assert.Equal(["Start"], t.Open.Select(r => r.Name));
        Assert.Equal([("Start", 400L)], Of(t.RoomEntered("1a", "5", "6", 400, true, false, One)));

        // and with no re-entry between the two
        RunTracker twice = Tracker();
        twice.Restart("1a", "1", 0, true, false, _ => true);
        twice.Left(LeaveKind.SaveAndQuit, 300, One);
        twice.Left(LeaveKind.SaveAndQuit, 300, One);
        twice.Reentered(LeaveKind.SaveAndQuit, "1a", "3", 300, true, false, _ => false);
        Assert.Equal([("Start", 400L)], Of(twice.RoomEntered("1a", "5", "6", 400, true, false, One)));
    }

    // a state load, a teleport, another level, Restart Chapter: each is a new
    // attempt, and none brings back what a Save and Quit set aside
    [Theory]
    [InlineData("drop")]
    [InlineData("restart")]
    [InlineData("load without control")]
    [InlineData("restart chapter")]
    public void ANewAttemptForgetsWhatWasSetAside(string how) {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.Left(LeaveKind.SaveAndQuit, 300, One);
        switch (how) {
            case "drop":
                t.Drop();
                break;
            case "restart":
                t.Restart("1a", "3", 300, true, false, _ => true);
                break;
            case "load without control":
                t.RestartAtAppearance("1a", "3");
                break;
            default:
                t.ChapterRestarted(300, One);
                break;
        }

        t.Reentered(LeaveKind.SaveAndQuit, "1a", "3", 300, true, false, _ => false);
        Assert.Empty(t.Open);
    }

    // the leave is fed inside the frame's update: what the frame feeds after
    // it finds nothing to close
    [Fact]
    public void TheFramesOwnFeedAfterALeaveClosesNothing() {
        Add("IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.Left(LeaveKind.SaveAndQuit, 300, One);
        Assert.Empty(t.ChapterTimeStopped(300, One));
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "3", 300, true, false, _ => false);

        Assert.Equal([("IL", 500L)], Of(t.ChapterTimeStopped(500, One)));
    }

    // a room an ending loads can open its row after the leave: the level
    // entered next does not inherit it
    // a Return to Map split that loaded nothing: the level goes on
    [Fact]
    public void AfterAReturnToMapThatLeftNothingARowWalkedIntoIsTimed() {
        Add("Start", "1", "6", setup: StartSetup.CurrentRoom, spans: true);
        Add("Crossing", "6", "9b", entry: "5");
        Add("Chasm", "9b", null, entry: "9");
        RunTracker t = Tracker();
        t.Restart("1a", "1", 0, true, false, _ => true);
        Assert.Empty(t.Left(LeaveKind.ReturnToMap, 100, One));
        t.Drop();

        Assert.Empty(t.RoomEntered("1a", "5", "6", 150, true, false, One));
        Assert.Equal([("Crossing", 250L)], Of(t.RoomEntered("1a", "9", "9b", 400, true, false, One)));
    }

    [Fact]
    public void AReturnToMapThatLeftNothingCarriesNothingToALaterReentry() {
        SegmentRule start = Add("Start", "1", "6", setup: StartSetup.CurrentRoom, spans: true);
        RunTracker t = Tracker();
        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "1", "6", 50, true, false, One);
        t.Left(LeaveKind.ReturnToMap, 100, One);
        t.Drop();

        t.Reentered(LeaveKind.ReturnToMap, "1a", "6", 100, true, false, _ => false);

        Assert.DoesNotContain(start, t.Open);
    }

    [Fact]
    public void ARowOpenedAfterALeaveDoesNotSurviveTheReentry() {
        Add("A", "1", null, setup: StartSetup.CurrentRoom);
        Add("B", "6", null, entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.Left(LeaveKind.ChapterEnd, 400, One);
        t.RoomEntered("1a", "5", "6", 400, true, false, One);
        Assert.Equal(["B"], t.Open.Select(r => r.Name));

        t.Reentered(LeaveKind.ChapterEnd, "1a", "9b", 0, true, false, _ => true);
        Assert.Empty(t.Open);
    }

    // a row that ends on a Return to Map closes there, on the leave's reading
    [Fact]
    public void AReturnToMapRowClosesAtThatLeave() {
        Add("ARB Crossing-2", "6", null, endKind: EndKind.ReturnToMap, setup: StartSetup.MapSpawn, berries: true);
        rooms.Berries["ARB Crossing-2"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "6", 10, true, false, _ => true);
        t.BerryCollected("b1");

        Assert.Equal([("ARB Crossing-2", 290L)], Of(t.Left(LeaveKind.ReturnToMap, 300, One)));
        Assert.Empty(t.Open);
    }

    // 5A's berry Start walks into Depths' room and back before it leaves
    [Fact]
    public void AReturnToMapRowDoesNotEndOnARoomEntry() {
        Add("Start", "a-00", "b-00", setup: StartSetup.CurrentRoom);
        Add("ARB Start", "a-00", "b-00", endKind: EndKind.ReturnToMap, setup: StartSetup.CurrentRoom, berries: true);
        Add("Depths", "b-00", null, entry: "a-13");
        rooms.Berries["ARB Start"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "a-00", 0, true, false, _ => true);
        t.BerryCollected("b1");
        Assert.Equal([("Start", 100L)], Of(t.RoomEntered("1a", "a-13", "b-00", 100, true, false, One)));
        Assert.Equal(["ARB Start", "Depths"], t.Open.Select(r => r.Name));
        t.RoomEntered("1a", "b-00", "a-13", 120, true, false, One);
        t.BerryCollected("b2");

        Assert.Equal([("ARB Start", 300L)], Of(t.Left(LeaveKind.ReturnToMap, 300, One)));
    }

    // nor on chapter time stopping: a chapter's end drops it like any row
    // that does not end there
    [Fact]
    public void AReturnToMapRowIsDroppedByAChaptersEnd() {
        Add("ARB Chasm-2", "9b", null, endKind: EndKind.ReturnToMap, setup: StartSetup.MapSpawn, berries: true);
        rooms.Berries["ARB Chasm-2"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "9b", 0, true, false, _ => true);
        t.BerryCollected("b1");
        Assert.Empty(t.ChapterTimeStopped(200, One));
        Assert.Equal(["ARB Chasm-2"], t.Open.Select(r => r.Name));

        Assert.Empty(t.Left(LeaveKind.ChapterEnd, 200, One));
        Assert.Empty(t.Open);
        Assert.Empty(t.Left(LeaveKind.ReturnToMap, 300, One));
    }

    // 1A's Crossing to RTM with the Save and Quit after the heart
    [Fact]
    public void AReturnToMapRowCarriesAcrossASaveAndQuit() {
        Add("ARB Crossing to RTM", "6", null, requires: Collectibles.Heart, endKind: EndKind.ReturnToMap,
            setup: StartSetup.MapSpawn, berries: true);
        rooms.Berries["ARB Crossing to RTM"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "6", 0, true, false, _ => true);
        t.BerryCollected("b1");
        t.Collected(Collectibles.Heart, 80, One);
        Assert.Empty(t.Left(LeaveKind.SaveAndQuit, 100, One));
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "s1", 100, true, false, _ => false);
        t.BerryCollected("b2");

        Assert.Equal([("ARB Crossing to RTM", 250L)], Of(t.Left(LeaveKind.ReturnToMap, 250, One)));
    }

    // the game loses a berry still following with the level. Anywhere else
    // it counts (AFollowingBerryCounts)
    [Fact]
    public void ABerryStillFollowingAtAReturnToMapDoesNotCount() {
        Add("ARB Crossing-2", "6", null, endKind: EndKind.ReturnToMap, setup: StartSetup.MapSpawn, berries: true);
        rooms.Berries["ARB Crossing-2"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "6", 0, true, false, _ => true);

        Assert.Empty(t.Left(LeaveKind.ReturnToMap, 300, new EndState(1, ["b1"])));
        Assert.Empty(t.Open);
    }

    [Theory]
    [InlineData("berry")]
    [InlineData("heart")]
    public void AReturnToMapRowMissingWhatItRequiresRecordsNothing(string missing) {
        Add("ARB Crossing to RTM", "6", null, requires: Collectibles.Heart, endKind: EndKind.ReturnToMap,
            setup: StartSetup.MapSpawn, berries: true);
        rooms.Berries["ARB Crossing to RTM"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "6", 0, true, false, _ => true);
        t.BerryCollected("b1");
        if (missing != "berry") {
            t.BerryCollected("b2");
        }

        if (missing != "heart") {
            t.Collected(Collectibles.Heart, 80, One);
        }

        Assert.Empty(t.Left(LeaveKind.ReturnToMap, 300, One));
        Assert.Empty(t.Open);
    }

    // what a row requires is a floor: a run that also takes the far berry
    // records the row that asks for it too
    [Fact]
    public void ARunThatTakesMoreRecordsEveryReturnToMapRowItMeets() {
        Add("ARB Crossing to RTM", "6", null, requires: Collectibles.Heart, endKind: EndKind.ReturnToMap,
            setup: StartSetup.MapSpawn, berries: true);
        Add("ARB Crossing-2", "6", null, endKind: EndKind.ReturnToMap, setup: StartSetup.MapSpawn, berries: true);
        rooms.Berries["ARB Crossing to RTM"] = ["b1", "b2"];
        rooms.Berries["ARB Crossing-2"] = ["b3"];
        RunTracker t = Tracker();

        t.Restart("1a", "6", 0, true, false, _ => true);
        t.BerryCollected("b1");
        t.BerryCollected("b2");
        t.BerryCollected("b3");
        t.Collected(Collectibles.Heart, 80, One);

        Assert.Equal([("ARB Crossing to RTM", 300L), ("ARB Crossing-2", 300L)],
            Of(t.Left(LeaveKind.ReturnToMap, 300, One)));
    }

    // 1A's Heart to RTM: the respawn of the Save and Quit that follows the
    // heart starts it. Whether the heart is taken is RunWatcher's to say,
    // through atStart
    [Fact]
    public void AHeartRespawnRowOpensOnASaveAndQuitReentry() {
        Add("ARB Crossing to Heart", "6", null, requires: Collectibles.Heart, endKind: EndKind.Collect,
            endsOn: Collectibles.Heart, setup: StartSetup.MapSpawn);
        Add("ARB Heart to RTM", "s1", null, endKind: EndKind.ReturnToMap, setup: StartSetup.HeartRespawn,
            berries: true);
        rooms.Berries["ARB Heart to RTM"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "6", 0, true, false, _ => true);
        Assert.Equal([("ARB Crossing to Heart", 80L)], Of(t.Collected(Collectibles.Heart, 80, One)));
        t.Left(LeaveKind.SaveAndQuit, 100, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "s1", 100, false, false, _ => true);
        Assert.Empty(t.Open);
        t.ControlReturned("s1", 100, _ => false);
        Assert.Equal(["ARB Heart to RTM"], t.Open.Select(r => r.Name));
        t.BerryCollected("b1");

        Assert.Equal([("ARB Heart to RTM", 200L)], Of(t.Left(LeaveKind.ReturnToMap, 300, One)));
    }

    // a level from the loader, a teleport, a first-room reset; a checkpoint
    // picked on the map; walking in, even where the double names an entry room
    [Theory]
    [InlineData("restart")]
    [InlineData("return to map")]
    [InlineData("chapter end")]
    [InlineData("walk in")]
    public void AHeartRespawnRowOpensOnNothingElse(string how) {
        Add("Before", "s0", "s1", setup: StartSetup.CurrentRoom);
        Add("ARB Heart to RTM", "s1", null, endKind: EndKind.ReturnToMap, setup: StartSetup.HeartRespawn,
            entry: "s0");
        RunTracker t = Tracker();

        switch (how) {
            case "restart":
                t.Restart("1a", "s1", 0, true, false, _ => true);
                break;
            case "return to map":
                t.Reentered(LeaveKind.ReturnToMap, "1a", "s1", 0, true, false, _ => true);
                break;
            case "chapter end":
                t.Reentered(LeaveKind.ChapterEnd, "1a", "s1", 0, true, false, _ => true);
                break;
            default:
                t.Restart("1a", "s0", 0, true, false, _ => true);
                t.RoomEntered("1a", "s0", "s1", 50, true, false, One);
                break;
        }

        Assert.DoesNotContain("ARB Heart to RTM", t.Open.Select(r => r.Name));
    }

    // the room a heart-respawn row starts in is not an entry its predecessor's
    // time is recorded on, even where the double names an entry room
    [Fact]
    public void AHeartRespawnRowIsNotTheSuccessorOfTheRowEndingInItsRoom() {
        Add("Before", "s0", "s1", setup: StartSetup.CurrentRoom);
        Add("ARB Heart to RTM", "s1", null, endKind: EndKind.ReturnToMap, setup: StartSetup.HeartRespawn,
            entry: "s0");
        RunTracker t = Tracker();

        t.Restart("1a", "s0", 0, true, false, _ => true);

        Assert.Empty(Of(t.RoomEntered("1a", "s0", "s1", 50, true, false, One)));
    }

    // a savestate made on that respawn, loaded with control or before it
    [Fact]
    public void AHeartRespawnRowOpensFromAStateOnItsSpawn() {
        Add("ARB Heart to RTM", "s1", null, endKind: EndKind.ReturnToMap, setup: StartSetup.HeartRespawn);
        RunTracker t = Tracker();

        t.Restart("1a", "s1", 0, true, false, _ => true, fromState: true);
        Assert.Equal(["ARB Heart to RTM"], t.Open.Select(r => r.Name));

        RunTracker waiting = Tracker();
        waiting.RestartAtAppearance("1a", "s1");
        waiting.ControlReturned("s1", 20, _ => true);
        Assert.Equal(["ARB Heart to RTM"], waiting.Open.Select(r => r.Name));
    }

    // a second Save and Quit on the same respawn carries the row on with its
    // time: it is not started over
    [Fact]
    public void AHeartRespawnRowCarriedOnIsNotOpenedAgain() {
        Add("ARB Heart to RTM", "s1", null, endKind: EndKind.ReturnToMap, setup: StartSetup.HeartRespawn);
        RunTracker t = Tracker();

        t.Left(LeaveKind.SaveAndQuit, 100, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "s1", 100, true, false, _ => true);
        t.Left(LeaveKind.SaveAndQuit, 150, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "s1", 150, true, false, _ => true);

        Assert.Equal(["ARB Heart to RTM"], t.Open.Select(r => r.Name));
        Assert.Equal([("ARB Heart to RTM", 150L)], Of(t.Left(LeaveKind.ReturnToMap, 250, One)));
    }

    // 6A's full clear: the heart and the cassette, a Return to Map, then the
    // rest of the chapter from Hollows. The leave does not end the row though
    // it holds all it requires: no piece has reached the chapter's end. The
    // level entered from the map starts its clock at zero
    [Fact]
    public void ASpanningRowIsSetAsideByAReturnToMapAndEndsWithTheChapter() {
        Add("FC IL", "start", null, Collectibles.Heart | Collectibles.Cassette, endKind: EndKind.ChapterEnd,
            setup: StartSetup.CurrentRoom, spans: true);
        RunTracker t = Tracker();

        t.Restart("1a", "start", 0, true, false, _ => true);
        t.RoomEntered("1a", "03", "04", 150, true, false, One);
        t.Collected(Collectibles.Cassette, 200, One);
        t.Collected(Collectibles.Heart, 250, One);
        Assert.Empty(t.Left(LeaveKind.ReturnToMap, 300, One));
        Assert.Empty(t.Open);

        t.Reentered(LeaveKind.ReturnToMap, "1a", "04", 0, true, false, _ => false);
        Assert.Equal(["FC IL"], t.Open.Select(r => r.Name));
        Assert.Equal([("FC IL", 800L)], Of(t.Left(LeaveKind.ChapterEnd, 500, One)));
    }

    // 1A's berry run through Chasm first: the chapter's end leaves two rooms'
    // berries to fetch, each from a checkpoint picked on the map. The row
    // ends on the Return to Map that finds everything collected, and the
    // plain chapter row beside it ends with the chapter as before
    [Fact]
    public void ASpanningRowLackingABerryOutlivesTheChaptersEndAndEndsOnAReturnToMap() {
        Add("IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        Add("ARB IL", "1", null, Collectibles.Heart, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom,
            berries: true, spans: true);
        rooms.Berries["ARB IL"] = ["b1", "b2", "b3"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.BerryCollected("b1");
        t.Collected(Collectibles.Heart, 150, One);
        t.RoomEntered("1a", "9", "9b", 250, true, false, One);
        Assert.Equal([("IL", 400L)], Of(t.Left(LeaveKind.ChapterEnd, 400, One)));

        t.Reentered(LeaveKind.ChapterEnd, "1a", "6", 0, true, false, _ => false);
        t.BerryCollected("b2");
        Assert.Empty(t.Left(LeaveKind.ReturnToMap, 60, One));

        t.Reentered(LeaveKind.ReturnToMap, "1a", "9b", 0, true, false, _ => false);
        t.BerryCollected("b3");
        Assert.Equal([("ARB IL", 590L)], Of(t.Left(LeaveKind.ReturnToMap, 130, One)));
        Assert.Empty(t.Open);
    }

    // the game loses a berry still following at a Return to Map, so the row
    // does not end there; it ends on the leave after the berry is banked.
    // The piece banked another berry, or the row would be gone with it
    [Fact]
    public void ABerryFollowingAtAReturnToMapDoesNotEndASpanningRow() {
        Add("ARB IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom, berries: true,
            spans: true);
        rooms.Berries["ARB IL"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.Left(LeaveKind.ChapterEnd, 400, One);
        t.Reentered(LeaveKind.ChapterEnd, "1a", "6", 0, true, false, _ => false);
        t.BerryCollected("b1");
        Assert.Empty(t.Left(LeaveKind.ReturnToMap, 60, new EndState(1, ["b2"])));

        t.Reentered(LeaveKind.ReturnToMap, "1a", "6", 0, true, false, _ => false);
        t.BerryCollected("b2");
        Assert.Equal([("ARB IL", 490L)], Of(t.Left(LeaveKind.ReturnToMap, 30, One)));
    }

    // 1A's heart room: the berry there can still be following when the Save
    // and Quit is taken, and the game loses it. The chapter's end then finds
    // the row one berry short, and it ends once the berry is fetched
    [Fact]
    public void ABerryFollowingAtASaveAndQuitIsNotCountedForASpanningRow() {
        Add("ARB IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom, berries: true,
            spans: true);
        rooms.Berries["ARB IL"] = ["s1:9"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 50, true, false, One);
        t.Left(LeaveKind.SaveAndQuit, 100, new EndState(1, ["s1:9"]));
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "s1", 100, true, false, _ => false);
        Assert.Empty(t.Left(LeaveKind.ChapterEnd, 400, One));

        t.Reentered(LeaveKind.ChapterEnd, "1a", "6", 0, true, false, _ => false);
        t.BerryCollected("s1:9");
        Assert.Equal([("ARB IL", 450L)], Of(t.Left(LeaveKind.ReturnToMap, 50, One)));
    }

    // only a row that spans leaves outlives a Return to Map, or a chapter's
    // end it does not finish on: the plain chapter row beside it, one heart
    // short like it, is dropped there. A Save and Quit taken in the ending
    // resumes all that is set aside, whatever the room
    [Theory]
    [InlineData("return to map")]
    [InlineData("chapter end")]
    [InlineData("save and quit in the ending")]
    public void OnlyARowThatSpansLeavesOutlivesAReturnToMapOrAChaptersEnd(string how) {
        Add("ARB IL", "1", null, Collectibles.Heart, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        Add("FC IL", "1", null, Collectibles.Heart, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom,
            spans: true);
        RunTracker t = Tracker();
        LeaveKind kind = how == "return to map" ? LeaveKind.ReturnToMap : LeaveKind.ChapterEnd;
        long resumed = 0;

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        Assert.Empty(t.Left(kind, 300, One));
        if (how == "save and quit in the ending") {
            resumed = 300;
            Assert.Empty(t.Left(LeaveKind.SaveAndQuit, resumed, One));
            t.Reentered(LeaveKind.SaveAndQuit, "1a", "6", resumed, true, false, _ => false);
        } else {
            t.Reentered(kind, "1a", "6", resumed, true, false, _ => false);
        }

        Assert.Equal(["FC IL"], t.Open.Select(r => r.Name));
        t.Collected(Collectibles.Heart, resumed + 50, One);
        Assert.Equal([("FC IL", 500L)], Of(t.Left(LeaveKind.ChapterEnd, resumed + 200, One)));
    }

    // a Return to Map taken during the ending: the row the chapter's end set
    // aside is not open, and gains no time
    [Fact]
    public void ALeaveDuringTheEndingAddsNoTimeToASpanningRow() {
        Add("ARB IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom, berries: true,
            spans: true);
        rooms.Berries["ARB IL"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.Left(LeaveKind.ChapterEnd, 400, One);
        Assert.Empty(t.Left(LeaveKind.ReturnToMap, 430, One));

        t.Reentered(LeaveKind.ReturnToMap, "1a", "6", 0, true, false, _ => false);
        t.BerryCollected("b1");
        Assert.Equal([("ARB IL", 450L)], Of(t.Left(LeaveKind.ReturnToMap, 50, One)));
    }

    // what a try from the map must fetch to count: something the row still
    // lacked, banked. A berry still following is lost with the level, and
    // the heart of an earlier piece comes back as a ghost. A piece to the
    // chapter's end is a try as well, once the row has completed
    [Theory]
    [InlineData("a berry in tow", false)]
    [InlineData("the heart again", false)]
    [InlineData("the cassette", true)]
    [InlineData("a gem", true)]
    [InlineData("nothing, to the chapter's end", false)]
    public void ATryFromTheMapCountsOnlyForWhatTheRowStillLacked(string what, bool kept) {
        Add("FC IL", "1", null, Collectibles.Heart | Collectibles.Cassette, endKind: EndKind.ChapterEnd,
            setup: StartSetup.CurrentRoom, berries: true, gems: 1, spans: true);
        rooms.Berries["FC IL"] = ["b1"];
        RunTracker t = Tracker();
        LeaveKind leave = what == "nothing, to the chapter's end" ? LeaveKind.ChapterEnd : LeaveKind.ReturnToMap;

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.Collected(Collectibles.Heart, 150, One);
        t.Left(LeaveKind.ChapterEnd, 400, One);
        t.Reentered(LeaveKind.ChapterEnd, "1a", "6", 0, true, false, _ => false);
        Assert.Equal(["FC IL"], t.Open.Select(r => r.Name));
        if (what is "the heart again" or "the cassette" or "a gem") {
            t.Collected(what == "the cassette" ? Collectibles.Cassette
                : what == "a gem" ? Collectibles.Gem : Collectibles.Heart, 20, One);
        }

        Assert.Empty(t.Left(leave, 60, what == "a berry in tow" ? new EndState(1, ["b1"]) : One));
        t.Reentered(leave, "1a", "6", 0, true, false, _ => false);
        Assert.Equal(kept ? ["FC IL"] : [], t.Open.Select(r => r.Name));
    }

    // a chapter row already completed, then a checkpoint tried five times
    // from the map to the chapter's end with nothing the row lacked: no try
    // adds up, the row is dropped at the first. A piece that fetches what the
    // row lacked is kept, at a chapter's end as at a Return to Map
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TriesFromTheMapToTheChaptersEndDoNotAddUpIntoACompletedRow(bool fetches) {
        Add("ARB IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom, berries: true,
            spans: true);
        rooms.Berries["ARB IL"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.RoomEntered("1a", "9", "9b", 250, true, false, One);
        Assert.Empty(t.Left(LeaveKind.ChapterEnd, 1000, One));
        t.Reentered(LeaveKind.ChapterEnd, "1a", "9b", 0, true, false, _ => false);
        if (fetches) {
            t.BerryCollected("b1");
            Assert.Equal([("ARB IL", 1200L)], Of(t.Left(LeaveKind.ChapterEnd, 200, One)));
            return;
        }

        Assert.Empty(t.Left(LeaveKind.ChapterEnd, 200, One));
        t.Reentered(LeaveKind.ChapterEnd, "1a", "9b", 0, true, false, _ => false);
        Assert.Empty(t.Open);
    }

    // a Save and Quit inside a piece from the map does not start another:
    // what the piece banked before it still counts at its Return to Map
    [Fact]
    public void ASaveAndQuitInsideAPieceFromTheMapKeepsWhatItFetched() {
        Add("ARB IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom, berries: true,
            spans: true);
        rooms.Berries["ARB IL"] = ["b1", "b2"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.Left(LeaveKind.ChapterEnd, 400, One);
        t.Reentered(LeaveKind.ChapterEnd, "1a", "6", 0, true, false, _ => false);
        t.BerryCollected("b1");
        t.Left(LeaveKind.SaveAndQuit, 30, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "6", 30, true, false, _ => false);
        Assert.Empty(t.Left(LeaveKind.ReturnToMap, 60, One));

        t.Reentered(LeaveKind.ReturnToMap, "1a", "6", 0, true, false, _ => false);
        t.BerryCollected("b2");
        Assert.Equal([("ARB IL", 510L)], Of(t.Left(LeaveKind.ReturnToMap, 50, One)));
    }

    // a checkpoint the run has not reached, picked on the map: the row would
    // skip what lies between, so it does not carry on there
    [Fact]
    public void ACheckpointPickedAheadOfTheRunDropsASpanningRow() {
        Add("FC IL", "start", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom, spans: true);
        RunTracker t = Tracker();

        t.Restart("1a", "start", 0, true, false, _ => true);
        t.RoomEntered("1a", "03", "04", 150, true, false, One);
        t.Left(LeaveKind.ReturnToMap, 300, One);
        t.Reentered(LeaveKind.ReturnToMap, "1a", "b-00", 0, true, false, _ => false);

        Assert.Empty(t.Open);
        Assert.Empty(t.Left(LeaveKind.ChapterEnd, 500, One));
    }

    // a death in the checkpoint's room right after the map: the game zeroes
    // chapter time, and the chapter row keeps the 70 ticks played before it.
    // The row that starts on that spawn starts again
    [Fact]
    public void ADeathThatZeroesTheClockKeepsASpanningRowAndItsTime() {
        Add("ARB IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom, berries: true,
            spans: true);
        Add("ARB Crossing-2", "6", null, endKind: EndKind.ReturnToMap, setup: StartSetup.MapSpawn);
        rooms.Berries["ARB IL"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.Left(LeaveKind.ChapterEnd, 400, One);
        t.Reentered(LeaveKind.ChapterEnd, "1a", "6", 0, true, false, _ => true);
        t.ClockZeroed("1a", "6", 70, 10, true, false, _ => true);
        Assert.Equal(["ARB IL", "ARB Crossing-2"], t.Open.Select(r => r.Name));

        t.BerryCollected("b1");
        Assert.Equal([("ARB IL", 520L), ("ARB Crossing-2", 50L)], Of(t.Left(LeaveKind.ReturnToMap, 60, One)));
    }

    // the same death in the chapter's first room is the game's own restart:
    // the row starts again with it, and keeps nothing
    [Fact]
    public void ADeathThatZeroesTheClockAtTheChaptersStartIsANewAttempt() {
        Add("ARB IL", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom, spans: true);
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.ClockZeroed("1a", "1", 80, 0, true, false, _ => true);

        Assert.Equal([("ARB IL", 300L)], Of(t.Left(LeaveKind.ChapterEnd, 300, One)));
    }
}
