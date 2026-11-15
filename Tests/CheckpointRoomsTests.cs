using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the room lookups over synthetic checkpoint lists
public class CheckpointRoomsTests {
    private static SegmentRule Rule(string scope, string anchor, string entry = null,
        StartSetup setup = StartSetup.NextRoom, bool alsoWalkIn = false) =>
        new(scope, anchor, anchor, StartKind.Room, setup, EndKind.NextStart, Collectibles.None, Collectibles.None,
            null, 0, 0, 0) { EntryRoom = entry, AlsoWalkIn = alsoWalkIn };

    private static CheckpointRooms Chapter(string scope, params (string Name, string Room)[] checkpoints) =>
        new(scope, "first", checkpoints.Select(c => new CheckpointRoom(c.Name, c.Room)).ToArray());

    private static readonly CheckpointRooms Plain =
        Chapter("1a", ("Alpha", "a-00"), ("Beta", "b-00"), ("Gamma", "c-00"));

    [Fact]
    public void ACheckpointStartsInItsOwnRoomAndStartInTheFirstRoom() {
        Assert.Equal("b-00", Plain.StartRoomOf(Rule("1a", "Beta")));
        Assert.Equal("first", Plain.StartRoomOf(Rule("1a", "Start")));
    }

    [Fact]
    public void AnUnknownAnchorHasNoStartRoom() {
        Assert.Null(Plain.StartRoomOf(Rule("1a", "Nowhere")));
    }

    [Fact]
    public void ARuleOfAnotherScopeHasNoRooms() {
        SegmentRule other = Rule("2a", "Beta");
        Assert.Null(Plain.StartRoomOf(other));
        Assert.Null(Plain.EndRoomOf(other, out bool missing));
        Assert.False(missing);
        Assert.Null(Plain.EntryRoomOf(other));
    }

    [Fact]
    public void StartEndsAtTheFirstCheckpoint() {
        Assert.Equal("a-00", Plain.EndRoomOf(Rule("1a", "Start"), out _));
    }

    [Fact]
    public void ACheckpointEndsWhereTheNextOneStarts() {
        Assert.Equal("b-00", Plain.EndRoomOf(Rule("1a", "Alpha"), out bool missing));
        Assert.False(missing);
        Assert.Equal("c-00", Plain.EndRoomOf(Rule("1a", "Beta"), out _));
    }

    [Fact]
    public void TheLastCheckpointEndsWithTheChapter() {
        Assert.Null(Plain.EndRoomOf(Rule("1a", "Gamma"), out bool missing));
        Assert.False(missing);
    }

    [Fact]
    public void AnUnknownAnchorEndsNowhere() {
        Assert.Null(Plain.EndRoomOf(Rule("1a", "Nowhere"), out bool missing));
        Assert.False(missing);
    }

    [Fact]
    public void AMapWithNoCheckpointEndsNothing() {
        CheckpointRooms none = Chapter("1a");
        Assert.Null(none.EndRoomOf(Rule("1a", "Start"), out _));
        Assert.Equal("first", none.StartRoomOf(Rule("1a", "Start")));
    }

    [Fact]
    public void ANextCheckpointWithoutARoomIsReported() {
        CheckpointRooms broken = Chapter("1a", ("Alpha", "a-00"), ("Beta", null));
        Assert.Null(broken.EndRoomOf(Rule("1a", "Alpha"), out bool missing));
        Assert.True(missing);
    }

    [Fact]
    public void AnOverrideMovesTheStartAndTheEndOfTheSegmentBefore() {
        // 2A: Awake starts in end_0, not in the checkpoint's own room
        CheckpointRooms chapter = Chapter("2a", ("Intervention", "3x"), ("Awake", "end_3"));
        Assert.Equal("end_0", chapter.StartRoomOf(Rule("2a", "Awake")));
        Assert.Equal("end_0", chapter.EndRoomOf(Rule("2a", "Intervention"), out _));
    }

    [Fact]
    public void AnOverrideOnStartMovesTheFirstRoomButNotItsEnd() {
        CheckpointRooms chapter = Chapter("7a", ("500 M", "a-04"));
        Assert.Equal("a-00", chapter.StartRoomOf(Rule("7a", "Start")));
        Assert.Equal("a-04", chapter.EndRoomOf(Rule("7a", "Start"), out _));
    }

    [Fact]
    public void ASplitCheckpointEndsWhereItsNextPieceStarts() {
        CheckpointRooms chapter = Chapter("7a", ("3000 M", "f-00"), ("Summit", "h-00"));
        Assert.Equal("g-01", chapter.EndRoomOf(Rule("7a", "Downdraft"), out _));
        Assert.Equal("g-02", chapter.EndRoomOf(Rule("7a", "Updraft"), out _));
    }

    [Fact]
    public void TheLastPieceOfAChainHasNoEndRoomOfItsOwn() {
        // Nodraft is no split key: it is no checkpoint of the map either, so
        // nothing resolves its end
        CheckpointRooms chapter = Chapter("7a", ("3000 M", "f-00"), ("Summit", "h-00"));
        Assert.Null(chapter.EndRoomOf(Rule("7a", "Nodraft"), out _));
    }

    [Fact]
    public void ARowEnteredFromItsOwnRoomKeepsItBeforeItsAnchors() {
        CheckpointRooms chapter = Chapter("2a", ("Intervention", "3x"), ("Awake", "end_3"));
        Assert.Equal("13", chapter.EntryRoomOf(Rule("2a", "Awake")));
        Assert.Equal("own", chapter.EntryRoomOf(Rule("2a", "Awake", entry: "own")));
    }

    [Fact]
    public void ARowWithNoEntryRoomAnywhereHasNone() {
        Assert.Null(Plain.EntryRoomOf(Rule("1a", "Beta")));
    }

    [Fact]
    public void AMapSpawnRowIsEnteredOnlyWhenAWalkInOpensItToo() {
        CheckpointRooms chapter = Chapter("1a", ("Crossing", "x"));
        Assert.Null(chapter.EntryRoomOf(Rule("1a", "Crossing", setup: StartSetup.MapSpawn)));
        Assert.Equal("5", chapter.EntryRoomOf(Rule("1a", "Crossing", setup: StartSetup.MapSpawn, alsoWalkIn: true)));
        Assert.Null(chapter.EntryRoomOf(Rule("1a", "Crossing", setup: StartSetup.HeartRespawn)));
    }

    [Fact]
    public void CheckpointIdsCountFromTheStart() {
        Assert.Equal(0, Plain.CheckpointId("Start"));
        Assert.Equal(1, Plain.CheckpointId("Alpha"));
        Assert.Equal(3, Plain.CheckpointId("Gamma"));
        Assert.Equal(-1, Plain.CheckpointId("Nowhere"));
    }

    // the tables key checkpoints by name: every split piece's start room and
    // every override must resolve through the lookups of its own scope
    [Fact]
    public void EverySplitCheckpointEndsInTheStartRoomOfItsNextPiece() {
        foreach (((string scope, string name), string next) in SegmentAutoDetect.SplitCheckpoints) {
            CheckpointRooms chapter = Chapter(scope, (name, "own"), (next, "ownNext"));
            string expected = chapter.StartRoomOf(Rule(scope, next));
            Assert.Equal(expected, chapter.EndRoomOf(Rule(scope, name), out _));
        }
    }
}
