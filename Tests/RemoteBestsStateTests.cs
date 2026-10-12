using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

[Collection(RemoteBestsCollection.Name)]
public class RemoteBestsStateTests {
    private static RemoteRow Row(string cp, string time, string band = "checkpoint", string chapter = "1a") =>
        new() { Tab = "A Sides", Band = band, Chapter = chapter, Cp = cp, Time = time };

    private static readonly SheetRowRef Crossing = new("A Sides", "1a", "Crossing");

    [Fact]
    public void StartsWithNothingHeld() {
        RemoteBests.Reset();
        Assert.Equal(HeldSource.None, RemoteBests.Source);
        Assert.False(RemoteBests.IsResolved);
        Assert.Equal(TimeSpan.MaxValue, RemoteBests.Age);
    }

    [Fact]
    public void AFreshAnswerIsHeldAndIndexed() {
        RemoteBests.Reset();
        int before = RemoteBests.Accepts;
        RemoteBests.AcceptFresh([Row("Crossing", "21.948")]);

        Assert.Equal(HeldSource.Fresh, RemoteBests.Source);
        Assert.Equal(before + 1, RemoteBests.Accepts);
        Assert.True(RemoteBests.TryGet(Crossing, out RemoteRow row));
        Assert.Equal("21.948", row.Time);
        Assert.True(RemoteBests.Age < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ASavedCopyIsTakenOnlyWhenNothingIsHeld() {
        RemoteBests.Reset();
        DateTime saved = DateTime.UtcNow.AddHours(-2);
        Assert.True(RemoteBests.TryAcceptSaved([Row("Crossing", "22.000")], saved));
        Assert.Equal(HeldSource.Saved, RemoteBests.Source);
        Assert.True(RemoteBests.Age >= TimeSpan.FromHours(2));

        RemoteBests.AcceptFresh([Row("Crossing", "21.948")]);
        Assert.False(RemoteBests.TryAcceptSaved([Row("Crossing", "22.000")], saved));
        Assert.True(RemoteBests.TryGet(Crossing, out RemoteRow row));
        Assert.Equal("21.948", row.Time);
    }

    [Fact]
    public void AClockThatWentBackShowsTheCopyAsJustSaved() {
        DateTime now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.Zero, RemoteBests.AgeSince(now.AddMinutes(5), now));
        Assert.Equal(TimeSpan.FromMinutes(5), RemoteBests.AgeSince(now.AddMinutes(-5), now));
    }

    // a read failing keeps the rows a screen was built from
    [Fact]
    public void AFailureKeepsWhatIsHeld() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([Row("Crossing", "21.948")]);
        RemoteBests.Fail("timeout");

        Assert.Equal(HeldSource.Fresh, RemoteBests.Source);
        Assert.Equal("timeout", RemoteBests.Error);
        Assert.True(RemoteBests.TryGet(Crossing, out _));

        RemoteBests.AcceptFresh([Row("Crossing", "21.948")]);
        Assert.Null(RemoteBests.Error);
    }

    // the script does not detect a key in two bands: srs refuses it, in one band or two
    [Theory]
    [InlineData("checkpoint", "il")]
    [InlineData("checkpoint", "checkpoint")]
    public void ARepeatedKeyIsADuplicateAndNeverARow(string firstBand, string secondBand) {
        RemoteBests.Reset();
        List<RemoteRow> repeated = RemoteBests.AcceptFresh([
            Row("Crossing", "21.948", firstBand), Row("Crossing", "1:02.000", secondBand), Row("Chasm", "30.000"),
        ]);

        Assert.Equal(2, repeated.Count);
        Assert.True(RemoteBests.IsDuplicate(Crossing));
        Assert.False(RemoteBests.TryGet(Crossing, out _));
        Assert.True(RemoteBests.TryGet(new SheetRowRef("A Sides", "1a", "Chasm"), out _));
        // the copy keeps every row the sheet sent, so a reload finds the same duplicate
        Assert.Equal(3, RemoteBests.Rows.Count);
    }

    [Fact]
    public void AWrittenTimeBecomesTheCellAndKeepsTheSourceAndDate() {
        RemoteBests.Reset();
        DateTime saved = DateTime.UtcNow.AddHours(-2);
        RemoteBests.TryAcceptSaved([Row("Crossing", "22.000")], saved);
        int accepts = RemoteBests.Accepts;

        RemoteBests.ApplyWritten([(Crossing, "21.948", null)]);

        Assert.True(RemoteBests.TryGet(Crossing, out RemoteRow row));
        Assert.Equal("21.948", row.Time);
        Assert.Equal("checkpoint", row.Band);
        Assert.Equal(HeldSource.Saved, RemoteBests.Source);
        Assert.Equal(saved, RemoteBests.SavedAtUtc);
        // counts like an answer: a table built on the old cells must rebuild
        Assert.Equal(accepts + 1, RemoteBests.Accepts);
    }

    [Fact]
    public void AWriteNeverTouchesTheRowsAnEarlierSnapshotHanded() {
        RemoteBests.Reset();
        RemoteRow original = Row("Crossing", "22.000");
        RemoteBests.AcceptFresh([original]);
        RemoteBests.ApplyWritten([(Crossing, "21.948", null)]);
        Assert.Equal("22.000", original.Time);
    }

    [Fact]
    public void ConcurrentWritersNeverUndoEachOther() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([Row("Crossing", "22.000"), Row("Chasm", "31.000")]);
        Parallel.For(0, 200, i => {
            if (i % 2 == 0) {
                RemoteBests.ApplyWritten([(Crossing, "21.948", null)]);
            } else {
                RemoteBests.Fail("timeout " + i);
            }
        });

        Assert.True(RemoteBests.TryGet(Crossing, out RemoteRow row));
        Assert.Equal("21.948", row.Time);
        Assert.StartsWith("timeout", RemoteBests.Error);
    }

    [Fact]
    public void AnAnswerHoldingNoTargetIsSeen() {
        Assert.False(RemoteBests.HoldsAnyOf([], [Crossing]));
        Assert.False(RemoteBests.HoldsAnyOf([Row("Nowhere", "1.000")], [Crossing]));
        Assert.True(RemoteBests.HoldsAnyOf([Row(" CROSSING ", "1.000")], [Crossing]));
    }

    [Fact]
    public void LookupIgnoresCaseAndSpacingButNotTheEmoji() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([
            new RemoteRow { Tab = "a sides", Chapter = "6A", Cp = "  HOLLOWS   \U0001F4FC ", Time = "8.704" },
        ]);

        Assert.True(RemoteBests.TryGet(new SheetRowRef("A Sides", "6a", "Hollows \U0001F4FC"), out _));
        Assert.False(RemoteBests.TryGet(new SheetRowRef("A Sides", "6a", "Hollows"), out _));
    }

    // the script returns the raw cell text, which may carry a U+FE0F the table's literal lacks
    [Fact]
    public void LookupStripsVariationSelectorFromRawSheetEcho() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([
            new RemoteRow { Tab = "A Sides", Chapter = "6a", Cp = "Hollows \U0001F4FC\uFE0F", Time = "8.704" },
        ]);

        Assert.True(RemoteBests.TryGet(new SheetRowRef("A Sides", "6a", "Hollows \U0001F4FC"), out _));
    }

    // the sheet has such pairs ("7a Start" / "7a Start \U0001F48E"): stripping emoji merges them
    [Fact]
    public void RowsDifferingOnlyByEmojiStayDistinct() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([
            new RemoteRow { Tab = "A Sides", Chapter = "7a", Cp = "7a Start", Time = "39.457" },
            new RemoteRow { Tab = "A Sides", Chapter = "7a", Cp = "7a Start \U0001F48E", Time = "12.345" },
        ]);

        Assert.True(RemoteBests.TryGet(new SheetRowRef("A Sides", "7a", "7a Start"), out RemoteRow plain));
        Assert.Equal("39.457", plain.Time);
        Assert.True(RemoteBests.TryGet(new SheetRowRef("A Sides", "7a", "7a Start \U0001F48E"), out RemoteRow gem));
        Assert.Equal("12.345", gem.Time);
        Assert.False(RemoteBests.IsDuplicate(new SheetRowRef("A Sides", "7a", "7a Start")));
    }

    [Fact]
    public void UnknownRowIsNotFound() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([]);
        Assert.False(RemoteBests.TryGet(new SheetRowRef("A Sides", "9z", "Nowhere"), out _));
    }

    private static RemoteRow Full() => new() {
        Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = "Crossing", Time = "22.000", Timing = "FileTime",
        TimingOptions = ["S&Q", "No S&Q", "FileTime"], TimingGroup = "1a", FileTimeBuffer = 5, RankOffset = 2,
    };

    [Fact]
    public void ATimeAloneKeepsTheTimingAndTheBuffer() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([Full()]);
        RemoteBests.ApplyWritten([(Crossing, "21.948", null)]);

        Assert.True(RemoteBests.TryGet(Crossing, out RemoteRow row));
        Assert.Equal(("21.948", "FileTime", 5.0), (row.Time, row.Timing, row.FileTimeBuffer));
    }

    [Fact]
    public void ATimingAloneKeepsTheTimeAndClearsTheBuffer() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([Full()]);
        RemoteBests.ApplyWritten([(Crossing, null, "S&Q")]);

        Assert.True(RemoteBests.TryGet(Crossing, out RemoteRow row));
        Assert.Equal(("22.000", "S&Q"), (row.Time, row.Timing));
        Assert.Null(row.FileTimeBuffer);
    }

    [Fact]
    public void BothSetBothAndEveryOtherFieldIsKept() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([Full()]);
        RemoteBests.ApplyWritten([(Crossing, "21.948", "No S&Q")]);

        Assert.True(RemoteBests.TryGet(Crossing, out RemoteRow row));
        Assert.Equal(("21.948", "No S&Q", "checkpoint", "1a", 2.0), (row.Time, row.Timing, row.Band, row.TimingGroup, row.RankOffset));
        Assert.Equal(["S&Q", "No S&Q", "FileTime"], row.TimingOptions);
        Assert.Null(row.FileTimeBuffer);
    }
}
