using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the tracker's trace on synthetic rules: what it says opened, closed and
// stopped, and why. The rows and their times are RunTrackerTests'
public class RunTrackerTraceTests {
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
    private readonly List<TraceEvent> events = [];

    private void Add(string name, string start, string end, Collectibles requires = Collectibles.None,
        EndKind endKind = EndKind.NextStart, StartSetup setup = StartSetup.NextRoom, string entry = null,
        int gems = 0, int? dashes = null, bool berries = false, bool mayReturnToMap = false) {
        rules.Add(new SegmentRule("1a", name, name, StartKind.Room, setup, endKind, Collectibles.None, requires,
            dashes, 0, 0, rules.Count) { Gems = gems, Berries = berries ? BerrySet.WholeChapter : null,
                MayReturnToMap = mayReturnToMap });
        rooms.ByName[name] = (start, end);
        if (entry != null) {
            rooms.Entries[name] = entry;
        }
    }

    private RunTracker Tracker() => new(rules, rooms) { Trace = events.Add };

    private static readonly EndState One = new(1, []);

    private List<(TraceKind, string, TraceReason)> Seen() =>
        events.Select(e => (e.Kind, e.Rule.Name, e.Reason)).ToList();

    private TraceEvent Only(TraceKind kind) => Assert.Single(events, e => e.Kind == kind);

    // the case a player meets most: Crossing's first room entered from a room
    // its row does not open from
    [Fact]
    public void AStartRoomEnteredFromElsewhereSaysWhereItsRowOpensFrom() {
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => false);
        t.RoomEntered("1a", "4", "6", 100, true, false, One);

        TraceEvent e = Assert.Single(events);
        Assert.Equal((TraceKind.NotOpened, "Crossing", TraceReason.WrongEntry), (e.Kind, e.Rule.Name, e.Reason));
        Assert.Equal(("6", "4", "5", 100L), (e.Room, e.From, e.Entry, e.Reading));
    }

    [Fact]
    public void AWalkInOpensAndTheNextOneClosesWithItsTime() {
        Add("Crossing", "6", "9b", entry: "5");
        Add("Chasm", "9b", null, entry: "9");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => false);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.RoomEntered("1a", "9", "9b", 400, true, false, One);

        Assert.Equal([
            (TraceKind.Opened, "Crossing", TraceReason.WalkIn),
            (TraceKind.Closed, "Crossing", TraceReason.None),
            (TraceKind.Opened, "Chasm", TraceReason.WalkIn),
        ], Seen());
        Assert.Equal(300L, Only(TraceKind.Closed).Record?.Ticks);
    }

    [Fact]
    public void AnEndEnteredFromAnotherRouteSaysTheEntryItNeeded() {
        Add("Crossing", "6", "9b", entry: "5");
        Add("Chasm", "9b", null, entry: "9");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => false);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.RoomEntered("1a", "8", "9b", 400, true, false, One);

        TraceEvent e = Only(TraceKind.NotRecorded);
        Assert.Equal((TraceReason.NotFromNextEntry, "8", "9"), (e.Reason, e.From, e.Entry));
    }

    // the rule's types are internal: the theory takes an index
    private static readonly (Collectibles Requires, int Gems, int? Dashes, bool Berries, TraceReason Reason)[] Shortfalls = [
        (Collectibles.Heart, 0, null, false, TraceReason.MissingHeart),
        (Collectibles.Cassette, 0, null, false, TraceReason.MissingCassette),
        (Collectibles.Gem, 0, null, false, TraceReason.MissingGem),
        (Collectibles.None, 2, null, false, TraceReason.MissingGems),
        (Collectibles.None, 0, 2, false, TraceReason.WrongDashes),
        (Collectibles.None, 0, null, true, TraceReason.MissingBerries),
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ARowShortOfARequirementSaysWhich(int which) {
        (Collectibles requires, int gems, int? dashes, bool berries, TraceReason reason) = Shortfalls[which];
        Add("Start", "1", null, requires, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom, gems: gems,
            dashes: dashes, berries: berries);
        rooms.Berries["Start"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        Assert.Empty(t.ChapterTimeStopped(300, One));

        Assert.Equal(reason, Only(TraceKind.NotRecorded).Reason);
    }

    [Fact]
    public void ARowOfNoLengthSaysSo() {
        Add("Start", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "1", 100, true, false, _ => true);
        t.ChapterTimeStopped(100, One);

        Assert.Equal(TraceReason.NoTime, Only(TraceKind.NotRecorded).Reason);
    }

    // a TAS marks it on every frame it plays: said once
    [Fact]
    public void ADisqualifiedRowIsSaidOnceAndClosesUnrecorded() {
        Add("Start", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.TasPlayed();
        t.TasPlayed();
        t.ChapterTimeStopped(300, One);

        Assert.Equal(TraceReason.TasPlayed, Only(TraceKind.Disqualified).Reason);
        Assert.Equal(TraceReason.Disqualified, Only(TraceKind.NotRecorded).Reason);
    }

    [Fact]
    public void ANewAttemptDropsWhatWasOpen() {
        Add("Start", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.Restart("1a", "3", 200, true, false, _ => true);

        TraceEvent e = Only(TraceKind.Dropped);
        Assert.Equal((TraceReason.NewAttempt, 200L), (e.Reason, e.Reading));
    }

    [Fact]
    public void BackingOutSaysSoThenBarsTheNextWalkIn() {
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => false);
        t.RoomEntered("1a", "5", "6", 100, true, false, One);
        t.RoomEntered("1a", "6", "5", 150, true, false, One);
        t.RoomEntered("1a", "5", "6", 200, true, false, One);

        Assert.Equal([
            (TraceKind.Opened, "Crossing", TraceReason.WalkIn),
            (TraceKind.Dropped, "Crossing", TraceReason.BackedOut),
            (TraceKind.NotOpened, "Crossing", TraceReason.BackedOut),
        ], Seen());
    }

    [Fact]
    public void AStartWithoutControlWaitsThenOpensWhenItComesBack() {
        Add("Crossing", "6", "9b", entry: "5");
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => false);
        t.RoomEntered("1a", "5", "6", 100, false, false, One);
        t.ControlReturned("6", 130, _ => false);

        Assert.Equal([
            (TraceKind.Waiting, "Crossing", TraceReason.WalkIn),
            (TraceKind.Opened, "Crossing", TraceReason.ControlReturned),
        ], Seen());
        Assert.Equal(130L, Only(TraceKind.Opened).Reading);
    }

    [Fact]
    public void ALoadWithoutControlAwayFromTheStartOpensNothing() {
        Add("Start", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.RestartAtAppearance("1a", "1");
        t.ControlReturned("1", 40, _ => false);

        Assert.Equal([
            (TraceKind.Waiting, "Start", TraceReason.Appearance),
            (TraceKind.StoppedWaiting, "Start", TraceReason.NotAtStart),
        ], Seen());
    }

    [Fact]
    public void ASaveAndQuitSetsTheRowAsideAndTheReentryCarriesItOn() {
        Add("Start", "1", null, endKind: EndKind.ChapterEnd, setup: StartSetup.CurrentRoom);
        RunTracker t = Tracker();

        t.Restart("1a", "1", 0, true, false, _ => true);
        t.Left(LeaveKind.SaveAndQuit, 100, One);
        t.Reentered(LeaveKind.SaveAndQuit, "1a", "2", 100, true, false, _ => false);

        Assert.Equal([
            (TraceKind.Opened, "Start", TraceReason.Restart),
            (TraceKind.SetAside, "Start", TraceReason.SaveAndQuit),
            (TraceKind.CarriedOn, "Start", TraceReason.SaveAndQuit),
        ], Seen());
    }

    // 5A's berry Start: its end room passed short of a berry, then again
    [Fact]
    public void ARowThatPassedItsEndShortSaysSoWhenItEnds() {
        Add("ARB Start", "a-00", "b-00", setup: StartSetup.CurrentRoom, berries: true, mayReturnToMap: true);
        Add("Depths", "b-00", null, entry: "a-13");
        rooms.Berries["ARB Start"] = ["b1"];
        RunTracker t = Tracker();

        t.Restart("1a", "a-00", 0, true, false, _ => true);
        t.RoomEntered("1a", "a-13", "b-00", 100, true, false, One);
        t.RoomEntered("1a", "a-13", "b-00", 200, true, false, One);

        Assert.Equal(TraceReason.None, Only(TraceKind.PassedEnd).Reason);
        Assert.Equal(TraceReason.PassedEndBefore, Only(TraceKind.NotRecorded).Reason);
    }

    [Fact]
    public void ALineHoldsEveryValueRaw() {
        SegmentRule rule = new("1a", "Hollows Tape RTM", "Hollows", StartKind.Room, StartSetup.NextRoom,
            EndKind.NextStart, Collectibles.None, Collectibles.None, null, 0, 0, 0);
        SegmentRecord record = new(rule, 12345, SaveAndQuitNote.With, ReturnToMapNote.Without);

        Assert.Equal("trace Closed \"1a/Hollows Tape RTM\" reason=None at=67890 ticks=12345 note=With rtm=Without"
                     + " room=9b from=9 via=WalkIn",
            TraceLine.Format(new TraceEvent(TraceKind.Closed, rule, TraceReason.None, 67890, record, "9b", "9"),
                "WalkIn"));
        Assert.Equal("trace Dropped \"1a/Hollows Tape RTM\" reason=TimelineChanged at=-",
            TraceLine.Format(new TraceEvent(TraceKind.Dropped, rule, TraceReason.TimelineChanged, null), null));
    }
}
