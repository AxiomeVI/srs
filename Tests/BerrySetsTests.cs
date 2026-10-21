using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// a berry set against 1A's red berries as the map files them
public class BerrySetsTests {
    private static readonly MapBerry[] ForsakenCity = [
        new("2", 11, 0), new("3", 9, 0), new("3b", 2, 0), new("5z", 10, 0), new("5", 21, 0), new("5a", 2, 0),
        new("7zb", 2, 1), new("6", 12, 1), new("s1", 9, 1), new("7z", 3, 1), new("8zb", 1, 1), new("7a", 12, 1),
        new("9z", 3, 1), new("8b", 1, 1), new("9", 14, 1),
        new("10zb", 1, 2), new("11", 9, 2), new("9b", 9, 2), new("9c", 2, 2), new("12z", 8, 2),
    ];

    private static int CheckpointOf(string name) => name switch {
        "Start" => 0,
        "Crossing" => 1,
        "Chasm" => 2,
        _ => -1,
    };

    private static List<string> Keys(BerrySet set) =>
        BerrySets.Resolve(set, ForsakenCity, CheckpointOf)?.Order().ToList();

    private static BerrySet Row(string scope, string name) => RowTraits.All[(scope, name)].Berries;

    private static List<string> Sorted(params string[] keys) => keys.Order().ToList();

    [Fact]
    public void ACheckpointsBerriesAreItsOwn() {
        Assert.Equal(Sorted("2:11", "3:9", "3b:2", "5z:10", "5:21", "5a:2"), Keys(new BerrySet("Start")));
    }

    [Fact]
    public void TheChapterIsEveryBerry() {
        Assert.Equal(20, BerrySets.Resolve(BerrySet.WholeChapter, ForsakenCity, CheckpointOf).Count);
    }

    // 1A's Start to Heart, as the table holds it
    [Fact]
    public void RoomsAddToACheckpoint() {
        Assert.Equal(Sorted("2:11", "3:9", "3b:2", "5z:10", "5:21", "5a:2", "6:12", "s1:9"),
            Keys(Row("1a", "ARB Start to Heart")));
    }

    // 1A's Crossing to Heart
    [Fact]
    public void RoomsAloneAreASet() {
        Assert.Equal(Sorted("6:12", "s1:9"), Keys(Row("1a", "ARB Crossing to Heart")));
    }

    // 1A's Crossing and Chasm
    [Fact]
    public void ExceptLeavesRoomsOut() {
        Assert.Equal(Sorted("6:12", "s1:9", "7z:3", "8zb:1", "7a:12", "9z:3", "8b:1", "9:14"),
            Keys(Row("1a", "ARB Crossing")));
        Assert.Equal(Sorted("9b:9", "9c:2", "12z:8"), Keys(Row("1a", "ARB Chasm")));
        // 4A's, which no map here holds
        Assert.Equal(("Cliff Face", "d-00b"),
            (Row("4a", "ARB Cliff Face (from RTM)").Checkpoint, Row("4a", "ARB Cliff Face (from RTM)").Except.Single()));
    }

    // a set read wrong must never pass for a set met
    [Fact]
    public void ASetTheMapDoesNotHoldIsNull() {
        Assert.Null(Keys(new BerrySet("Chasn")));
        Assert.Null(Keys(new BerrySet(Rooms: ["6", "s9"])));
        // 7zb is not a Chasm room: the exception removes nothing
        Assert.Null(Keys(new BerrySet("Chasm", Except: ["7zb"])));
        Assert.Null(Keys(new BerrySet()));
        Assert.Null(BerrySets.Resolve(BerrySet.WholeChapter, [], CheckpointOf));
    }

    // Farewell's moon berry is a set of its own, and no red set holds one
    [Fact]
    public void TheMoonBerryIsNoRedBerry() {
        MapBerry[] map = [new("a-01", 4, 0), new("j-19", 7, 0, Moon: true)];

        Assert.Equal(["j-19:7"], BerrySets.Resolve(BerrySet.MoonBerry, map, CheckpointOf));
        Assert.Equal(["a-01:4"], BerrySets.Resolve(BerrySet.WholeChapter, map, CheckpointOf));
        Assert.Equal(["a-01:4"], BerrySets.Resolve(new BerrySet("Start"), map, CheckpointOf));
        Assert.Null(BerrySets.Resolve(BerrySet.MoonBerry, ForsakenCity, CheckpointOf));
    }

    // 1A's rows that end on a Return to Map: the berries each takes
    [Fact]
    public void TheReturnToMapRowsOfForsakenCity() {
        Assert.Equal(Sorted("2:11", "3:9", "3b:2", "5z:10", "5:21", "5a:2", "7zb:2"), Keys(Row("1a", "ARB Start to RTM")));
        Assert.Equal(Sorted("6:12", "s1:9", "7z:3", "8zb:1", "7a:12", "9z:3", "8b:1", "9:14", "11:9", "10zb:1"),
            Keys(Row("1a", "ARB Crossing to RTM")));
        Assert.Equal(Sorted("7z:3", "8zb:1", "7a:12", "9z:3", "8b:1", "9:14", "11:9", "10zb:1"),
            Keys(Row("1a", "ARB Heart to RTM")));
        Assert.Equal(["7zb:2"], Keys(Row("1a", "ARB Crossing-2")));
        Assert.Equal(Sorted("11:9", "10zb:1"), Keys(Row("1a", "ARB Chasm-2")));
    }

    // every distinct route through 1A's berries: each takes every berry exactly once
    [Theory]
    [InlineData("Start|Crossing|Chasm|Crossing-2|Chasm-2")]
    [InlineData("Start|Crossing to RTM|Chasm|Crossing-2")]
    [InlineData("Start|Crossing to Heart|Heart to RTM|Chasm|Crossing-2")]
    [InlineData("Start to Heart|Heart to RTM|Chasm|Crossing-2")]
    [InlineData("Start to RTM|Crossing to Heart|Heart to RTM|Chasm")]
    [InlineData("Start to RTM|Crossing|Chasm|Chasm-2")]
    [InlineData("Start to RTM|Crossing to RTM|Chasm")]
    public void EveryRouteThroughForsakenCityTakesEachBerryOnce(string route) {
        List<string> taken = route.Split('|')
            .SelectMany(name => Keys(TestRules.Find("1a", "ARB " + name).Berries)).Order().ToList();

        Assert.Equal(ForsakenCity.Select(berry => berry.Key).Order().ToList(), taken);
    }

    // the chapter rows start in the first room, and nothing else of the
    // room map matters to them
    private sealed class ChapterRooms : IRoomMap {
        public string StartRoomOf(SegmentRule rule) => rule.Anchor == "Start" ? "1" : null;
        public string EndRoomOf(SegmentRule rule) => null;
        public string EntryRoomOf(SegmentRule rule) => null;
        public IReadOnlyCollection<string> BerriesOf(SegmentRule rule) =>
            rule.Berries == null ? null : BerrySets.Resolve(rule.Berries, ForsakenCity, CheckpointOf);
    }

    // the sheet's twelve routes through 1A, on the real rules. Each piece
    // takes its row's berries and its heart, and lasts 100 ticks. It walks
    // into Crossing's room when it starts at the chapter's start, and into
    // Chasm's when it plays Crossing to its end. A piece ends as its row
    // does: on a Return to Map, with the chapter (Chasm), on the Save and
    // Quit after the heart, or by walking on. The next one then starts on its
    // own checkpoint, its clock at zero after the map. The cassette is taken
    // on the way to room 11's berry. Both chapter rows end once, when the
    // last piece does
    [Theory]
    [InlineData("Start|Crossing|Chasm|Crossing-2|Chasm-2")]
    [InlineData("Start|Crossing|Chasm-2|Crossing-2|Chasm")]
    [InlineData("Start|Crossing to RTM|Chasm|Crossing-2")]
    [InlineData("Start|Crossing to RTM|Crossing-2|Chasm")]
    [InlineData("Start|Crossing to Heart|Heart to RTM|Chasm|Crossing-2")]
    [InlineData("Start|Crossing to Heart|Heart to RTM|Crossing-2|Chasm")]
    [InlineData("Start to Heart|Heart to RTM|Chasm|Crossing-2")]
    [InlineData("Start to Heart|Heart to RTM|Crossing-2|Chasm")]
    [InlineData("Start to RTM|Crossing to Heart|Heart to RTM|Chasm")]
    [InlineData("Start to RTM|Crossing|Chasm|Chasm-2")]
    [InlineData("Start to RTM|Crossing|Chasm-2|Chasm")]
    [InlineData("Start to RTM|Crossing to RTM|Chasm")]
    public void TheChapterRowsEndOnceWithTheLastPieceOfEveryRouteThroughForsakenCity(string route) {
        ChapterRooms rooms = new();
        RunTracker tracker = new(SegmentRules.All.Where(rule => rule.Scope == "1a").ToList(), rooms);
        EndState end = new(1, []);
        SegmentRule[] pieces = [.. route.Split('|').Select(name => TestRules.Find("1a", "ARB " + name))];
        List<(string Name, long Ticks, int Piece)> closed = [];

        tracker.Restart("1a", "1", 0, true, false, _ => true);
        long clock = 0;
        for (int i = 0; i < pieces.Length; i++) {
            SegmentRule piece = pieces[i];
            if (piece.Anchor == "Start") {
                tracker.RoomEntered("1a", "5", "6", clock, true, false, end);
            } else if (piece.Name is "ARB Crossing" or "ARB Crossing to RTM" or "ARB Heart to RTM") {
                tracker.RoomEntered("1a", "9", "9b", clock, true, false, end);
            }

            foreach (string berry in rooms.BerriesOf(piece)) {
                tracker.BerryCollected(berry);
                if (berry == "11:9") {
                    tracker.Collected(Collectibles.Cassette, clock, end);
                }
            }

            if ((piece.Requires & Collectibles.Heart) != 0) {
                tracker.Collected(Collectibles.Heart, clock, end);
            }

            clock += 100;
            LeaveKind? leave = piece.End == EndKind.ReturnToMap ? LeaveKind.ReturnToMap
                : piece.Name == "ARB Chasm" ? LeaveKind.ChapterEnd
                : piece.End == EndKind.Collect ? LeaveKind.SaveAndQuit
                : null;
            if (leave is not { } kind) {
                continue;
            }

            closed.AddRange(tracker.Left(kind, clock, end)
                .Where(record => record.Rule.SpansLeaves).Select(record => (record.Rule.Name, record.Ticks, i)));
            if (i + 1 < pieces.Length) {
                clock = kind == LeaveKind.SaveAndQuit ? clock : 0;
                tracker.Reentered(kind, "1a",
                    pieces[i + 1].Anchor switch { "Crossing" => "6", "Chasm" => "9b", _ => "s1" },
                    clock, true, false, _ => false);
            }
        }

        long whole = 100 * pieces.Length;
        Assert.Equal([("ARB IL", whole, pieces.Length - 1), ("FC IL", whole, pieces.Length - 1)], closed);
    }
}
