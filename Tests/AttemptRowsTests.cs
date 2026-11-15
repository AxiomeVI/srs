using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the rows above the timer: what a frame's records add, and what a re-entry
// or a split keeps of them
public class AttemptRowsTests {
    private static int order;

    private static SegmentRule Rule(string name, Collectibles requires = Collectibles.None, bool chapterRun = false,
        StartSetup setup = StartSetup.NextRoom, bool spansLeaves = false) =>
        new("1a", name, "Start", StartKind.Room, setup, EndKind.NextStart, Collectibles.None, requires, null, 0, 0,
            order++) { ChapterRun = chapterRun, SpansLeaves = spansLeaves };

    private static IReadOnlyCollection<string> NoBerries(SegmentRule rule) => null;

    private static SegmentRecord Record(SegmentRule rule) => new(rule, 10, SaveAndQuitNote.Without);

    private static void Add(AttemptRows rows, string room, params SegmentRule[] closed) =>
        rows.Add(closed.Select(Record).ToList(), room, NoBerries);

    private static string[] Names(AttemptRows rows) => rows.Rows.Select(entry => entry.Record.Rule.Name).ToArray();

    [Fact]
    public void AFrameAddsItsSegmentThenItsChapterRun() {
        AttemptRows rows = new();
        Add(rows, "9", Rule("IL", chapterRun: true), Rule("Chasm"));

        Assert.Equal(["Chasm", "IL"], Names(rows));
        Assert.Equal([1, 2], rows.Rows.Select(entry => entry.Serial));
        Assert.Equal(2, rows.LatestSerial);
        Assert.Equal("9", rows.LatestRoom);
    }

    // of several segments, only the most specific is shown
    [Fact]
    public void OnlyTheMostSpecificSegmentIsShown() {
        AttemptRows rows = new();
        Add(rows, "9", Rule("Chasm"), Rule("Chasm Heart", Collectibles.Heart));

        Assert.Equal(["Chasm Heart"], Names(rows));
    }

    // the piece run since a Save and Quit, closing under a wider row, is one
    // step back from it
    [Fact]
    public void AHeartRespawnPieceClosingUnderAWiderRowGoesFirst() {
        AttemptRows rows = new();
        Add(rows, "9", Rule("Crossing", Collectibles.Heart), Rule("Heart to RTM", setup: StartSetup.HeartRespawn));

        Assert.Equal(["Heart to RTM", "Crossing"], Names(rows));
    }

    [Fact]
    public void AHeartRespawnPieceShownAloneIsNotAddedTwice() {
        AttemptRows rows = new();
        Add(rows, "9", Rule("Heart to RTM", setup: StartSetup.HeartRespawn));

        Assert.Equal(["Heart to RTM"], Names(rows));
    }

    [Fact]
    public void NoRecordAddsNothing() {
        AttemptRows rows = new();
        Add(rows, "3");

        Assert.Empty(rows.Rows);
        Assert.Equal(0, rows.LatestSerial);
        Assert.Equal("3", rows.LatestRoom);
    }

    [Fact]
    public void ClearEmptiesTheRowsButKeepsTheSerial() {
        AttemptRows rows = new();
        Add(rows, "3", Rule("Start"));
        rows.Clear();

        Assert.Empty(rows.Rows);
        Assert.Equal(1, rows.LatestSerial);
    }

    // a Save and Quit and Continue carries on: every row stays, shown in the
    // room the rows closed in
    [Fact]
    public void ASaveAndQuitReentryThatCarriedOnKeepsEverything() {
        AttemptRows rows = new();
        Add(rows, "3", Rule("Start"));
        rows.Reentered(carried: true, leftSerial: 1, saveAndQuit: true, room: "6");
        Add(rows, "6", Rule("Crossing"));

        Assert.Equal(["Start", "Crossing"], Names(rows));
    }

    [Fact]
    public void ASaveAndQuitReentryKeepsTheRoomTheRowsShowIn() {
        AttemptRows rows = new();
        Add(rows, "3", Rule("Start"));
        rows.Reentered(carried: true, leftSerial: 1, saveAndQuit: true, room: "6");

        Assert.Equal("3", rows.LatestRoom);
    }

    [Fact]
    public void AReentryFromTheMapShowsTheRowsWhereItLanded() {
        AttemptRows rows = new();
        Add(rows, "3", Rule("Start"));
        rows.Reentered(carried: true, leftSerial: 1, saveAndQuit: false, room: "6");

        Assert.Equal("6", rows.LatestRoom);
    }

    // a finished run: what closed before the leave goes at once, what closed
    // at it stays up until the next row closes
    [Fact]
    public void AReentryThatCarriedNothingKeepsOnlyTheLeavesRowsUntilTheNextRecord() {
        AttemptRows rows = new();
        Add(rows, "3", Rule("Start"));
        Add(rows, "9", Rule("Chasm"));
        rows.Reentered(carried: false, leftSerial: 1, saveAndQuit: false, room: "6");

        Assert.Equal(["Chasm"], Names(rows));

        Add(rows, "6", Rule("Crossing"));
        Assert.Equal(["Crossing"], Names(rows));
    }

    // a chapter run in pieces closed at the leave: all of it stays up, even
    // what closed before the leave
    [Fact]
    public void AChainClosedAtTheLeaveKeepsAllItsRowsUntilTheNextRecord() {
        AttemptRows rows = new();
        Add(rows, "3", Rule("Start"));
        Add(rows, "9", Rule("ARB IL", chapterRun: true, spansLeaves: true));
        rows.Reentered(carried: true, leftSerial: 1, saveAndQuit: false, room: "6");

        Assert.Equal(["Start", "ARB IL"], Names(rows));

        Add(rows, "6", Rule("Crossing"));
        Assert.Equal(["Crossing"], Names(rows));
    }

    [Fact]
    public void AChainClosedBeforeTheLeaveIsNotOneClosedAtIt() {
        AttemptRows rows = new();
        Add(rows, "9", Rule("ARB IL", chapterRun: true, spansLeaves: true));
        rows.Reentered(carried: true, leftSerial: 1, saveAndQuit: false, room: "6");
        Add(rows, "6", Rule("Crossing"));

        Assert.Equal(["ARB IL", "Crossing"], Names(rows));
    }

    // a Return to Map split closed without a pick
    [Fact]
    public void KeptRowsStayUntilTheNextRecordReplacesThem() {
        AttemptRows rows = new();
        Add(rows, "3", Rule("Start"));
        rows.KeepUntilNextRow();

        Assert.Equal(["Start"], Names(rows));

        Add(rows, "4", Rule("Crossing"));
        Assert.Equal(["Crossing"], Names(rows));
    }
}
