using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

public class SheetTimingTests {
    private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    private static readonly string[] Sq = ["S&Q", "No S&Q", "FileTime"];

    [Theory]
    [InlineData(SaveAndQuitNote.With, "S&Q")]
    [InlineData(SaveAndQuitNote.Without, "No S&Q")]
    [InlineData(SaveAndQuitNote.Unknown, null)]
    public void BothSqOptionsFollowTheNote(SaveAndQuitNote note, string expected) =>
        Assert.Equal(expected, SheetTiming.Choice(Sq, note));

    [Theory]
    [InlineData(SaveAndQuitNote.With)]
    [InlineData(SaveAndQuitNote.Without)]
    [InlineData(SaveAndQuitNote.Unknown)]
    public void TheOtherLinesOfTheTableIgnoreTheNote(SaveAndQuitNote note) {
        Assert.Null(SheetTiming.Choice(["S&Q", "FileTime"], note));
        Assert.Equal("IL", SheetTiming.Choice(["IL", "FileTime"], note));
        Assert.Equal("SRT", SheetTiming.Choice(["SRT", "FileTime"], note));
        Assert.Null(SheetTiming.Choice([], note));
        Assert.Null(SheetTiming.Choice(null, note));
        Assert.Null(SheetTiming.Choice(["FileTime"], note));
        // exact strings: no case folding
        Assert.Null(SheetTiming.Choice(["s&q", "no s&q"], SaveAndQuitNote.With));
    }

    private static RemoteRow Cell(string time, string timing = null, double? buffer = null) =>
        new() { Tab = "A Sides", Chapter = "1a", Cp = "Crossing", Time = time, Timing = timing, FileTimeBuffer = buffer };

    [Fact]
    public void APlainCellIsItsOwnTicks() {
        Assert.Equal((false, (long?) Ticks(65)), SheetTiming.Comparable(Cell("1:05.000")));
        Assert.Equal((false, (long?) Ticks(65)), SheetTiming.Comparable(Cell("1:05.000", "IL", 5)));
    }

    [Fact]
    public void AFileTimeCellIsConvertedByTheBuffer() =>
        Assert.Equal((false, (long?) Ticks(60)), SheetTiming.Comparable(Cell("1:05.000", "FileTime", 5)));

    [Fact]
    public void TheBufferIsRoundedToTheMillisecond() =>
        Assert.Equal((false, (long?) (Ticks(65) - Ticks(5.123))), SheetTiming.Comparable(Cell("1:05.000", "FileTime", 5.1234)));

    [Theory]
    [InlineData(null)]
    [InlineData(-0.5)]
    [InlineData(65.0)]
    [InlineData(70.0)]
    public void AFileTimeCellWithoutAUsableBufferIsUnreadable(double? buffer) =>
        Assert.Equal((true, (long?) null), SheetTiming.Comparable(Cell("1:05.000", "FileTime", buffer)));

    [Fact]
    public void AnUnreadableCellIsUnreadable() =>
        Assert.Equal((true, (long?) null), SheetTiming.Comparable(Cell("8,704")));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0:00.000")]
    public void AnEmptyCellIsNoTimeWhateverItsTiming(string time) {
        Assert.Equal((false, (long?) null), SheetTiming.Comparable(Cell(time)));
        Assert.Equal((false, (long?) null), SheetTiming.Comparable(Cell(time, "FileTime", 5)));
    }

    [Fact]
    public void NoRowIsNoTime() => Assert.Equal((false, (long?) null), SheetTiming.Comparable(null));

    private static PendingUpdate Pending(string cp, double seconds, string cell, string timing, string[] options,
        SaveAndQuitNote note, string group = null, double? buffer = null) {
        RemoteRow remote = new() {
            Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = cp, Time = cell,
            Timing = timing, TimingOptions = options == null ? null : [.. options], TimingGroup = group,
            FileTimeBuffer = buffer,
        };
        return PendingUpdate.Create(new SheetRowRef("A Sides", "1a", cp), cp, Ticks(seconds), remote, note, false, 0);
    }

    [Fact]
    public void ARowWithoutADropdownSendsNoTimingFields() {
        PendingUpdate u = Pending("Crossing", 20, "21.948", null, null, SaveAndQuitNote.With);

        ExportUpdate update = Assert.Single(SheetTiming.Updates([u]));
        Assert.Equal(("A Sides", "checkpoint", "1a", "Crossing", TimeFormat.FromTicks(Ticks(20)), "21.948"),
            (update.Tab, update.Band, update.Chapter, update.Cp, update.Time, update.Expect));
        string json = ExportProtocol.SerializeRequest(new ExportRequest { Updates = [update] });
        Assert.DoesNotContain("iming", json);
        Assert.DoesNotContain("expectBuffer", json);
    }

    [Fact]
    public void AGroupWhoseRowsAgreeKeepsBothValues() {
        List<ExportUpdate> sent = SheetTiming.Updates([
            Pending("Crossing", 20, "21.948", "No S&Q", Sq, SaveAndQuitNote.With, "1a"),
            Pending("Chasm", 30, "31.000", "No S&Q", Sq, SaveAndQuitNote.With, "1a"),
        ]);

        Assert.Equal(["S&Q", "S&Q"], sent.Select(u => u.Timing));
        Assert.Equal(["No S&Q", "No S&Q"], sent.Select(u => u.ExpectTiming));
    }

    [Fact]
    public void ALaterRowOfAGroupThatChoseOtherwiseLosesItsTimingButKeepsItsTime() {
        List<ExportUpdate> sent = SheetTiming.Updates([
            Pending("Crossing", 20, "21.948", "No S&Q", Sq, SaveAndQuitNote.With, "1a"),
            Pending("Chasm", 30, "31.000", "No S&Q", Sq, SaveAndQuitNote.Without, "1a"),
        ]);

        Assert.Equal("S&Q", sent[0].Timing);
        Assert.Null(sent[1].Timing);
        Assert.Null(sent[1].ExpectTiming);
        Assert.Equal(TimeFormat.FromTicks(Ticks(30)), sent[1].Time);
        Assert.Equal("31.000", sent[1].Expect);
    }

    [Fact]
    public void RowsOfOtherGroupsOrNoGroupAreIndependent() {
        List<ExportUpdate> sent = SheetTiming.Updates([
            Pending("Crossing", 20, "21.948", "No S&Q", Sq, SaveAndQuitNote.With, "1a"),
            Pending("Chasm", 30, "31.000", "S&Q", Sq, SaveAndQuitNote.Without, "other"),
            Pending("Start", 10, "11.000", "S&Q", Sq, SaveAndQuitNote.Without),
            Pending("Heart", 12, "13.000", "S&Q", Sq, SaveAndQuitNote.With),
        ]);

        Assert.Equal(["S&Q", "No S&Q", "No S&Q", "S&Q"], sent.Select(u => u.Timing));
    }

    [Fact]
    public void BufferedRowsGoFirstAndTheRestKeepTheirOrder() {
        List<ExportUpdate> sent = SheetTiming.Updates([
            Pending("A", 10, "11.000", null, null, SaveAndQuitNote.Without),
            Pending("B", 10, "1:05.000", "FileTime", Sq, SaveAndQuitNote.With, buffer: 5),
            Pending("C", 10, "11.000", null, null, SaveAndQuitNote.Without),
            Pending("D", 10, "1:05.000", "FileTime", Sq, SaveAndQuitNote.With, buffer: 4),
        ]);

        Assert.Equal(["B", "D", "A", "C"], sent.Select(u => u.Cp));
    }

    [Fact]
    public void AFileTimeRowWithNoSwitchIsLeftOut() {
        List<ExportUpdate> sent = SheetTiming.Updates([
            Pending("Crossing", 10, "1:05.000", "FileTime", Sq, SaveAndQuitNote.Unknown, buffer: 5),
            Pending("Chasm", 10, "1:05.000", "FileTime", null, SaveAndQuitNote.With, buffer: 5),
            Pending("Start", 10, "11.000", null, null, SaveAndQuitNote.Unknown),
        ]);

        Assert.Equal("Start", Assert.Single(sent).Cp);
        Assert.Empty(SheetTiming.Updates([]));
    }

    [Fact]
    public void AFileTimeRowIsNeverClearedByItsGroup() {
        List<ExportUpdate> sent = SheetTiming.Updates([
            Pending("Crossing", 20, "21.948", "No S&Q", Sq, SaveAndQuitNote.With, "1a"),
            Pending("Chasm", 30, "1:05.000", "FileTime", Sq, SaveAndQuitNote.Without, "1a", buffer: 5),
            Pending("Heart", 30, "", "FileTime", Sq, SaveAndQuitNote.Without, "1a"),
        ]);

        ExportUpdate buffered = sent.Single(u => u.Cp == "Chasm");
        Assert.Equal(("No S&Q", "FileTime", (double?) 5.0), (buffered.Timing, buffered.ExpectTiming, buffered.ExpectBuffer));
        // an empty FileTime cell was not judged through the buffer
        Assert.Equal("No S&Q", sent.Single(u => u.Cp == "Heart").Timing);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    [InlineData(90000.0)]
    public void AnOffsetNoSheetGivesIsUnusable(double seconds) {
        Assert.Null(SheetTiming.SecondsToTicks(seconds));
        Assert.Equal(Ticks(5.123), SheetTiming.SecondsToTicks(5.1234));
        // half a millisecond rounds up, as the sheet shows it
        Assert.Equal(Ticks(0.063), SheetTiming.SecondsToTicks(0.0625));
    }
}
