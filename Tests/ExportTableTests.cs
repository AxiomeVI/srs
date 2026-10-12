using System;
using System.Collections.Generic;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

public class ExportTableTests {
    // the first state that applies wins. The status as a
    // name: ScreenStatus is internal, and a public test method cannot take it
    [Theory]
    [InlineData(HeldSource.None, true, null, false, false, "Loading")]
    [InlineData(HeldSource.None, false, "offline", false, false, "LoadFailed")]
    [InlineData(HeldSource.None, false, null, false, true, "Loading")]
    [InlineData(HeldSource.None, false, "offline", false, true, "LoadFailed")]
    [InlineData(HeldSource.Saved, false, null, false, true, "Writing")]
    [InlineData(HeldSource.Fresh, false, null, false, true, "Writing")]
    [InlineData(HeldSource.Fresh, false, "offline", true, true, "Writing")]
    [InlineData(HeldSource.Saved, true, null, true, false, "WritingChecking")]
    [InlineData(HeldSource.Fresh, false, "offline", true, false, "WritingFailed")]
    [InlineData(HeldSource.Saved, true, null, false, false, "SavedChecking")]
    [InlineData(HeldSource.Saved, true, "offline", false, false, "SavedChecking")]
    [InlineData(HeldSource.Saved, false, "offline", false, false, "SavedFailed")]
    [InlineData(HeldSource.Fresh, false, "offline", false, false, "FreshFailed")]
    [InlineData(HeldSource.Fresh, true, null, false, false, "None")]
    [InlineData(HeldSource.Fresh, false, null, false, false, "None")]
    public void StatusPrecedence(HeldSource source, bool reading, string error, bool unanswered, bool writing,
        string expected) {
        Assert.Equal(Enum.Parse<ScreenStatus>(expected), ExportTable.StatusOf(source, reading, error, unanswered, writing));
    }

    [Theory]
    [InlineData(0, 0, "SRS_AGE_MINUTES")]
    [InlineData(59, 59, "SRS_AGE_MINUTES")]
    [InlineData(60, 1, "SRS_AGE_HOURS")]
    [InlineData(47 * 60, 47, "SRS_AGE_HOURS")]
    [InlineData(48 * 60, 2, "SRS_AGE_DAYS")]
    public void AgeReadsInTheUnitThatSuitsIt(int minutes, int value, string unit) {
        Assert.Equal((value, unit), ExportTable.AgeOf(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void ARebuildKeepsWhatThePlayerPressedAndRetakesTheRest() {
        SheetRowRef pressedRow = new("A Sides", "1a", "1a Start");
        List<PendingUpdate> rebuilt = [
            PendingUpdate.Create(pressedRow, "Start", TimeSpan.FromSeconds(20).Ticks, null, SaveAndQuitNote.Unknown, false, 0),
            PendingUpdate.Create(new("A Sides", "1a", "Crossing"), "Crossing", TimeSpan.FromSeconds(20).Ticks, null, SaveAndQuitNote.Unknown, false, 0),
            PendingUpdate.Create(new("A Sides", "1a", "Chasm"), "Chasm", TimeSpan.FromSeconds(20).Ticks, null, SaveAndQuitNote.Unknown, true, 0),
        ];

        ExportTable.KeepPressed(rebuilt, new Dictionary<SheetRowRef, bool> {
            [pressedRow] = false,
            [new("A Sides", "1a", "Chasm")] = true,
        });

        Assert.False(rebuilt[0].Selected);
        Assert.True(rebuilt[1].Selected);
        // a duplicate stays unticked even if the player ticked it before
        Assert.False(rebuilt[2].Selected);
    }

    [Fact]
    public void ARowWithNoSwitchIsNeverReticked() {
        RemoteRow remote = new() {
            Tab = "A Sides", Chapter = "1a", Cp = "Crossing", Time = "1:10.000", Timing = "FileTime",
            TimingOptions = ["FileTime"], FileTimeBuffer = 5,
        };
        SheetRowRef row = new("A Sides", "1a", "Crossing");
        List<PendingUpdate> rebuilt = [
            PendingUpdate.Create(row, "Crossing", TimeSpan.FromSeconds(10).Ticks, remote, SaveAndQuitNote.Unknown, false, 0),
        ];

        ExportTable.KeepPressed(rebuilt, new Dictionary<SheetRowRef, bool> { [row] = true });

        Assert.False(rebuilt[0].Selected);
    }

    [Theory]
    [InlineData("A Sides", "1a", "1a Start", "1a Start")]
    [InlineData("A Sides", "1a", "Crossing", "1a Crossing")]
    [InlineData("Farewell", "", "Singular", "Farewell Singular")]
    // an IL variant's label is unreadable without its emoji: the srs name
    [InlineData("A Sides", "4a", "\U0001F499+\U0001F4FC Clear", "4a IL Heart Tape Clear")]
    [InlineData("A Sides", "4a", "Clear", "4a IL")]
    [InlineData("ARB/Full Clear", "1a \U0001F353", "Start", "1a ARB Start")]
    [InlineData("ARB/Full Clear", "2a \U0001F353", "2a \U0001F353", "2a ARB IL")]
    // not a row srs writes: the sheet's label
    [InlineData("A Sides", "1a", "Elsewhere", "1a Elsewhere")]
    public void RowLabelsCarryTheirChapterOnce(string tab, string chapter, string cp, string label) {
        Assert.Equal(label, ExportTable.RowLabel(tab, chapter, cp));
    }

    [Fact]
    public void TheSummaryCollapsesWrittenAndUnchangedRows() {
        List<string> lines = ExportTable.SummaryLines([
            new ExportResult { Tab = "A Sides", Chapter = "1a", Cp = "1a Start", Status = "written" },
            new ExportResult { Tab = "A Sides", Chapter = "1a", Cp = "Crossing", Status = "written" },
            new ExportResult { Tab = "A Sides", Chapter = "1a", Cp = "Chasm", Status = "unchanged", Reason = "already" },
            new ExportResult { Tab = "A Sides", Chapter = "2a", Cp = "Awake", Status = "changed", Reason = "now 40.1" },
            new ExportResult { Tab = "A Sides", Chapter = "2a", Cp = "Intervention", Status = "teapot", Reason = "" },
        ]);

        Assert.Equal([
            "SRS_EXPORT_SUMMARY_WRITTEN 1a Start, 1a Crossing",
            "SRS_EXPORT_SUMMARY_UNCHANGED 1a Chasm",
            "2a Awake: SRS_EXPORT_STATUS_CHANGED (now 40.1)",
            "2a Intervention: teapot",
        ], lines);
    }

    [Fact]
    public void AnEmptyAnswerStillSaysTheExportIsDone() {
        Assert.Equal(["SRS_EXPORT_DONE"], ExportTable.SummaryLines([]));
    }
}
