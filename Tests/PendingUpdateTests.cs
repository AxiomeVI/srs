using System;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the remote side is passed as the cell the script read, never as a parsed
// time: telling an empty cell from an unreadable one is the whole point, and a
// caller that parses first has already lost the distinction
public class PendingUpdateTests {
    private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    // the row and its label never matter here
    private static readonly SheetRowRef Crossing = new("A Sides", "1a", "Crossing");

    private static RemoteRow Remote(string cell, string band = "checkpoint") =>
        cell == null ? null : new RemoteRow { Tab = "A Sides", Band = band, Chapter = "1a", Cp = "Crossing", Time = cell };

    private static PendingUpdate Make(double seconds, string cell) =>
        PendingUpdate.Create(Crossing, "Crossing", Ticks(seconds), Remote(cell), SaveAndQuitNote.Unknown, false, 0);

    [Fact]
    public void AFasterLocalTimeIsAnImprovementAndIsPreselected() {
        var update = Make(67.915, "69.412");

        Assert.True(update.Selected);
        Assert.Equal(TimeFormat.Delta(Ticks(67.915) - Ticks(69.412)), update.DeltaText);
    }

    [Fact]
    public void ASlowerLocalTimeIsShownUnselected() {
        var update = Make(52.479, "51.980");

        Assert.False(update.Selected);
        Assert.Equal(TimeFormat.Delta(Ticks(52.479) - Ticks(51.980)), update.DeltaText);
    }

    [Fact]
    public void AnEmptyCellCountsAsAnImprovement() {
        var update = Make(41.5, "");

        Assert.True(update.Selected);
        Assert.Equal("", update.RemoteText);
        Assert.Equal("", update.DeltaText);
    }

    [Fact]
    public void AnAbsentCellCountsAsAnImprovement() {
        var update = Make(41.5, null);

        Assert.True(update.Selected);
        Assert.Equal("", update.DeltaText);
        Assert.Equal("", update.RemoteCell);
    }

    // the sheet writes 0:00.000 into a cell that has never held a time: its own
    // placeholder for "no record yet", not a PB to beat
    [Fact]
    public void AZeroCellCountsAsAnImprovement() {
        var update = Make(41.5, "0:00.000");

        Assert.True(update.Selected);
        Assert.Null(SheetTiming.ReadCell("0:00.000").Ticks);
        Assert.Equal("", update.RemoteText);
        Assert.Equal("", update.DeltaText);
        // the raw cell is still what the write compares against
        Assert.Equal("0:00.000", update.RemoteCell);
    }

    [Fact]
    public void AnIdenticalTimeIsNotAnImprovement() {
        var update = Make(21.948, "21.948");

        Assert.False(update.Selected);
        Assert.Equal(TimeFormat.Delta(0), update.DeltaText);
    }

    // the sheet holds a time and this mod cannot read it. Treated as an empty
    // cell, the row would be ticked and overwritten, and a sheet whose Google
    // locale writes a decimal comma would do it on every row, every time
    [Theory]
    [InlineData("8,704")]   // a French locale's decimal comma
    [InlineData("n/a")]
    [InlineData("see below")]
    public void AnUnreadableCellIsNeverAnImprovement(string cell) {
        var update = Make(30.0, cell);

        Assert.False(update.Selected);
        Assert.Null(SheetTiming.ReadCell(cell).Ticks);
        // shown as it stands: only the player can tell a locale from a typo
        Assert.Equal(cell, update.RemoteText);
        Assert.Equal("?", update.DeltaText);
    }

    [Fact]
    public void AWhitespaceOnlyCellIsEmptyRatherThanUnreadable() {
        var update = Make(30.0, "   ");

        Assert.True(update.Selected);
        Assert.Equal("", update.DeltaText);
    }

    // "65:03.250" is the same hour written as the standards write theirs
    [Theory]
    [InlineData("1:05:03.250")]
    [InlineData("65:03.250")]
    public void ACellOfAnHourOrMoreIsCompared(string cell) {
        var update = Make(3900.0, cell);

        Assert.Equal(Ticks(3903.25), SheetTiming.ReadCell(cell).Ticks);
        Assert.True(update.Selected);
        Assert.Equal("1:05:00.000", update.LocalText);
    }

    // what the format looks like is Speed Run Tool's business and only the
    // stand-in's here: assert the route, never the string
    [Fact]
    public void LocalTextGoesThroughTheTimeFormat() {
        var update = Make(14.722, null);

        Assert.Equal(TimeFormat.FromTicks(Ticks(14.722)), update.LocalText);
    }

    // the cell is kept as the sheet displayed it, beside the parsed value. The
    // write compares against this one: the sheet writes some times short, and
    // "1:36.9" reformatted is "1:36.900", which would refuse the row
    [Fact]
    public void KeepsTheSheetCellAsItWasRead() {
        var update = Make(90.0, "1:36.9");

        Assert.Equal("1:36.9", update.RemoteCell);
    }

    // the sheet and its script compare at the millisecond: a run a fraction
    // over the cell srs just wrote is the same time, not a regression
    [Fact]
    public void ATimeWrittenAtTheMillisecondIsNotBehind() {
        long ticks = TimeSpan.FromSeconds(21.948).Ticks + 4000;   // 21.9484 s
        var update = PendingUpdate.Create(Crossing, "Crossing", ticks, Remote("21.948"), SaveAndQuitNote.Unknown, false, 0);

        Assert.False(update.Selected);
        Assert.Equal(TimeFormat.Delta(0), update.DeltaText);
        Assert.Null(update.Ahead);
    }

    [Fact]
    public void AheadSaysWhichWayTheDeltaGoes() {
        Assert.True(Make(21.0, "21.948").Ahead);
        Assert.False(Make(22.0, "21.948").Ahead);
        Assert.Null(Make(22.0, "").Ahead);
        Assert.Null(Make(22.0, "8,704").Ahead);
    }

    [Fact]
    public void ADuplicateIsNeverTickedAndSaysWhy() {
        var update = PendingUpdate.Create(Crossing, "Crossing", Ticks(21.0), Remote(""),
            SaveAndQuitNote.With, true, 0);

        Assert.True(update.Duplicate);
        Assert.False(update.Selected);
        Assert.Equal("SRS_EXPORT_DUPLICATE", update.DeltaText);
        Assert.Equal("", update.RemoteText);
        Assert.Null(update.Ahead);
        Assert.False(update.Writable);
    }

    [Fact]
    public void KeepsTheBandItWasReadFrom() {
        Assert.Equal("checkpoint", Make(21.0, "21.948").Band);
    }

    // the Timing cells (wire version 3)

    private static RemoteRow Dropdown(string cell, string timing, string[] options, double? buffer = null,
        string group = null, double? rankOffset = null) {
        RemoteRow row = Remote(cell);
        row.Timing = timing;
        row.TimingOptions = [.. options];
        row.TimingGroup = group;
        row.FileTimeBuffer = buffer;
        row.RankOffset = rankOffset;
        return row;
    }

    private static PendingUpdate With(double seconds, RemoteRow remote, SaveAndQuitNote note = SaveAndQuitNote.Unknown,
        long? rankOffsetTicks = 0) =>
        PendingUpdate.Create(Crossing, "Crossing", Ticks(seconds), remote, note, false, rankOffsetTicks);

    private static readonly string[] SqOptions = ["S&Q", "No S&Q", "FileTime"];

    [Fact]
    public void AFileTimeRowIsJudgedOnItsConvertedTime() {
        // 70 s entered as FileTime with a 5 s buffer is 65 s on the measured basis
        RemoteRow remote = Dropdown("1:10.000", "FileTime", SqOptions, buffer: 5);

        Assert.True(With(64.0, remote, SaveAndQuitNote.With).Selected);
        // better than the raw 70 s, not than the converted 65 s
        PendingUpdate slower = With(67.0, remote, SaveAndQuitNote.With);
        Assert.False(slower.Selected);
        Assert.Equal(TimeFormat.Delta(Ticks(67.0) - Ticks(65.0)), slower.DeltaText);
        Assert.False(slower.Ahead);
        // the cell is still shown and kept as it stands
        Assert.Equal("1:10.000", slower.RemoteCell);
        Assert.Equal(TimeFormat.FromTicks(Ticks(70.0)), slower.RemoteText);
    }

    [Fact]
    public void AFileTimeRowWithoutAUsableBufferIsNeverTicked() {
        PendingUpdate update = With(10.0, Dropdown("1:10.000", "FileTime", SqOptions), SaveAndQuitNote.With);

        Assert.False(update.Selected);
        Assert.Equal("?", update.DeltaText);
        Assert.Null(update.Ahead);
        Assert.Equal("?", update.AdjustedText);
    }

    [Fact]
    public void AnEmptyFileTimeRowIsAnImprovement() {
        PendingUpdate update = With(10.0, Dropdown("", "FileTime", SqOptions, buffer: 5), SaveAndQuitNote.With);

        Assert.True(update.Selected);
        Assert.Equal("", update.AdjustedText);
        // nothing was judged through the buffer
        Assert.Null(update.ExpectBuffer);
    }

    [Fact]
    public void AdjustedTextShowsTheConvertedTimeAndTheValueSrsWrites() {
        RemoteRow remote = Dropdown("1:10.000", "FileTime", SqOptions, buffer: 5);

        Assert.Equal(TimeFormat.FromTicks(Ticks(65.0)) + " S&Q", With(64.0, remote, SaveAndQuitNote.With).AdjustedText);
        Assert.Equal(TimeFormat.FromTicks(Ticks(65.0)) + " No S&Q",
            With(64.0, remote, SaveAndQuitNote.Without).AdjustedText);
        RemoteRow il = Dropdown("1:10.000", "FileTime", ["IL", "FileTime"], buffer: 5);
        Assert.Equal(TimeFormat.FromTicks(Ticks(65.0)) + " IL", With(64.0, il).AdjustedText);
        RemoteRow srt = Dropdown("1:10.000", "FileTime", ["SRT", "FileTime"], buffer: 5);
        Assert.Equal(TimeFormat.FromTicks(Ticks(65.0)) + " SRT", With(64.0, srt).AdjustedText);
    }

    [Fact]
    public void AdjustedTextAddsTheRankOffsetForAStandardInFileTime() {
        RemoteRow remote = Dropdown("1:10.000", "IL", ["IL", "FileTime"], rankOffset: 2.5);

        Assert.Equal(TimeFormat.FromTicks(Ticks(62.5)), With(60.0, remote, rankOffsetTicks: Ticks(2.5)).AdjustedText);
        Assert.Equal("", With(60.0, remote).AdjustedText);
        Assert.Equal("", With(60.0, remote, rankOffsetTicks: null).AdjustedText);
    }

    [Fact]
    public void TheFileTimeCaseWinsOverTheRankOffset() {
        RemoteRow remote = Dropdown("1:10.000", "FileTime", ["IL", "FileTime"], buffer: 5, rankOffset: 2.5);

        Assert.Equal(TimeFormat.FromTicks(Ticks(65.0)) + " IL", With(60.0, remote, rankOffsetTicks: Ticks(2.5)).AdjustedText);
    }

    [Fact]
    public void AnSqRowSetsItsDropdownFromTheNote() {
        RemoteRow remote = Dropdown("1:10.000", "No S&Q", SqOptions, group: "1a");

        PendingUpdate with = With(60.0, remote, SaveAndQuitNote.With);
        Assert.Equal(("S&Q", "No S&Q", (double?) null, "1a"), (with.Timing, with.ExpectTiming, with.ExpectBuffer, with.Group));
        PendingUpdate unknown = With(60.0, remote);
        Assert.Null(unknown.Timing);
        Assert.Null(unknown.ExpectTiming);
    }

    [Fact]
    public void AnIlRowSetsItsValueWhateverTheNote() {
        RemoteRow remote = Dropdown("1:10.000", "FileTime", ["IL", "FileTime"], buffer: 5);

        PendingUpdate update = With(60.0, remote);
        Assert.Equal("IL", update.Timing);
        Assert.Equal("FileTime", update.ExpectTiming);
        Assert.Equal(5.0, update.ExpectBuffer);
        Assert.True(update.SheetIsFileTime);
    }

    // a FileTime cell srs has no value to switch cannot be written: it looks
    // like a duplicate, ticked by nothing
    [Fact]
    public void AFileTimeRowWithNoSwitchIsNotWritable() {
        PendingUpdate update = With(10.0, Dropdown("1:10.000", "FileTime", ["FileTime"], buffer: 5));

        Assert.False(update.Writable);
        Assert.False(update.Selected);
        Assert.Equal("?", update.DeltaText);
        Assert.True(With(10.0, Dropdown("1:10.000", "FileTime", ["IL", "FileTime"], buffer: 5)).Writable);
    }

    [Fact]
    public void ARowWithoutADropdownSetsNothing() {
        PendingUpdate update = With(60.0, Remote("1:10.000"), SaveAndQuitNote.With);

        Assert.Null(update.Timing);
        Assert.Null(update.ExpectTiming);
        Assert.Null(update.ExpectBuffer);
        Assert.Null(update.Group);
        Assert.False(update.SheetIsFileTime);
    }

    [Fact]
    public void ADuplicateSetsNothing() {
        RemoteRow remote = Dropdown("1:10.000", "FileTime", SqOptions, buffer: 5, group: "1a");
        PendingUpdate update = PendingUpdate.Create(Crossing, "Crossing", Ticks(60.0), remote,
            SaveAndQuitNote.With, true, 0);

        Assert.Null(update.Timing);
        Assert.Null(update.ExpectTiming);
        Assert.Null(update.ExpectBuffer);
    }
}
