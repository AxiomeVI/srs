using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

public class SendLedgerTests {
    private const string Url = "https://example.test/a";
    private static readonly SheetRowRef Crossing = new("A Sides", "1a", "Crossing");

    private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    private static RemoteRow Remote(string cell, string timing = null, List<string> options = null, double? buffer = null) =>
        new() { Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = "Crossing", Time = cell,
            Timing = timing, TimingOptions = options, FileTimeBuffer = buffer };

    private static PendingUpdate Row(double seconds, RemoteRow remote, SheetRowRef? at = null) =>
        PendingUpdate.Create(at ?? Crossing, "Crossing", Ticks(seconds), remote, SaveAndQuitNote.Unknown, false, 0);

    private static PendingUpdate Row(double seconds, string cell) => Row(seconds, Remote(cell));

    private static ExportResult Result(string status) => new() { Status = status };

    private static List<ExportUpdate> Send(SendLedger ledger, PendingUpdate row) {
        List<ExportUpdate> sent = ledger.Pick([row], false);
        // every caller sends one row: with none, what follows would pass on nothing
        Assert.Single(sent);
        ledger.Posted(Url, sent);
        return sent;
    }

    [Fact]
    public void PicksATickedRow() {
        var picked = new SendLedger().Pick([Row(60, "70.000")], false);

        ExportUpdate update = Assert.Single(picked);
        Assert.Equal(TimeFormat.FromTicks(Ticks(60)), update.Time);
    }

    [Fact]
    public void LeavesOutAnUntickedAnUnwritableAndARowTheSheetLacks() {
        PendingUpdate unticked = Row(80, "70.000");
        PendingUpdate absent = Row(60, (RemoteRow) null);
        PendingUpdate duplicate = PendingUpdate.Create(Crossing, "Crossing", Ticks(60), Remote("70.000"),
            SaveAndQuitNote.Unknown, true, 0);
        PendingUpdate fileTimeNoSwitch = Row(60, Remote("70.000", "FileTime", ["FileTime"], 0.5));
        unticked.Selected = false;
        duplicate.Selected = true;
        fileTimeNoSwitch.Selected = true;

        Assert.False(unticked.Selected);
        Assert.True(absent.Selected);
        Assert.False(fileTimeNoSwitch.Writable);
        Assert.False(absent.OnSheet);
        Assert.Empty(new SendLedger().Pick([unticked, absent, duplicate, fileTimeNoSwitch], true));
    }

    [Fact]
    public void ATimeOnceWrittenIsNotPickedAgainWhateverTheCell() {
        var ledger = new SendLedger();
        var sent = Send(ledger, Row(60, "70.000"));
        ledger.Answered(Url, sent, [Result("written")]);

        Assert.Empty(ledger.Pick([Row(60, "65.000")], true));
        Assert.Empty(ledger.Pick([Row(60, "")], true));
    }

    [Fact]
    public void UnchangedCountsAsWritten() {
        var ledger = new SendLedger();
        var sent = Send(ledger, Row(60, "70.000"));
        ledger.Answered(Url, sent, [Result("unchanged")]);

        Assert.Empty(ledger.Pick([Row(60, "70.000")], true));
    }

    [Fact]
    public void ARefusedRequestIsNotPickedAgain() {
        var ledger = new SendLedger();
        var sent = Send(ledger, Row(60, "70.000"));
        ledger.Answered(Url, sent, [Result("refused")]);

        Assert.Empty(ledger.Pick([Row(60, "70.000")], true));
    }

    [Fact]
    public void ARefusedRowIsPickedAgainOnceItsCellMoved() {
        var ledger = new SendLedger();
        var sent = Send(ledger, Row(60, "70.000"));
        ledger.Answered(Url, sent, [Result("refused")]);

        Assert.Single(ledger.Pick([Row(60, "69.000")], false));
    }

    [Fact]
    public void ARefusedRowIsPickedAgainOnceItsTimingCellMoved() {
        var ledger = new SendLedger();
        List<string> options = ["IL", "SRT"];
        var sent = Send(ledger, Row(60, Remote("70.000", "SRT", options)));
        ledger.Answered(Url, sent, [Result("refused")]);

        Assert.Empty(ledger.Pick([Row(60, Remote("70.000", "SRT", options))], false));
        Assert.Single(ledger.Pick([Row(60, Remote("70.000", "IL", options))], false));
    }

    [Fact]
    public void ARefusedRowIsPickedAgainOnceItsBufferMoved() {
        var ledger = new SendLedger();
        List<string> options = ["FileTime", "IL"];
        var sent = Send(ledger, Row(60, Remote("70.000", "FileTime", options, 0.5)));
        Assert.Single(sent);
        ledger.Answered(Url, sent, [Result("refused")]);

        Assert.Empty(ledger.Pick([Row(60, Remote("70.000", "FileTime", options, 0.5))], false));
        Assert.Single(ledger.Pick([Row(60, Remote("70.000", "FileTime", options, 1.0))], false));
    }

    [Fact]
    public void AChangedAnswerIsNotRepeatedOnTheSameCell() {
        var ledger = new SendLedger();
        var sent = Send(ledger, Row(60, "70.000"));
        ledger.Answered(Url, sent, [Result("changed")]);

        Assert.Empty(ledger.Pick([Row(60, "70.000")], true));
        Assert.Single(ledger.Pick([Row(60, "68.000")], true));
    }

    [Fact]
    public void AnUnansweredRequestWaitsForRetry() {
        var ledger = new SendLedger();
        Send(ledger, Row(60, "70.000"));

        Assert.Empty(ledger.Pick([Row(60, "70.000")], false));
        Assert.Single(ledger.Pick([Row(60, "70.000")], true));
    }

    [Fact]
    public void APartialAnswerLeavesTheRestUnanswered() {
        var ledger = new SendLedger();
        SheetRowRef other = new("A Sides", "1a", "Hallway");
        PendingUpdate first = Row(60, "70.000");
        PendingUpdate second = Row(50, Remote("70.000"), other);
        List<ExportUpdate> sent = ledger.Pick([first, second], false);
        Assert.Equal(2, sent.Count);
        ledger.Posted(Url, sent);
        ledger.Answered(Url, sent, [Result("written")]);

        List<ExportUpdate> again = ledger.Pick([first, second], false);
        Assert.Empty(again);
        again = ledger.Pick([first, second], true);
        ExportUpdate retried = Assert.Single(again);
        Assert.Equal(other.Cp, retried.Cp);
    }

    [Fact]
    public void MoreAnswersThanUpdatesChangeNothing() {
        var ledger = new SendLedger();
        var sent = Send(ledger, Row(60, "70.000"));
        ledger.Answered(Url, sent, [Result("written"), Result("written")]);

        Assert.Empty(ledger.Pick([Row(60, "70.000")], false));
        Assert.Single(ledger.Pick([Row(60, "70.000")], true));
    }

    [Fact]
    public void AnotherUrlEmptiesTheLedger() {
        var ledger = new SendLedger();
        var sent = Send(ledger, Row(60, "70.000"));
        ledger.Answered(Url, sent, [Result("written")]);

        ledger.Posted("https://example.test/b", []);

        Assert.Single(ledger.Pick([Row(60, "70.000")], false));
    }

    [Fact]
    public void AnAnswerFromAnotherUrlMarksNothing() {
        var ledger = new SendLedger();
        var sent = Send(ledger, Row(60, "70.000"));
        ledger.Posted("https://example.test/b", []);

        ledger.Answered(Url, sent, [Result("written")]);

        Assert.Single(ledger.Pick([Row(60, "70.000")], false));
    }

    [Fact]
    public void AFasterTimeIsANewRequest() {
        var ledger = new SendLedger();
        var sent = Send(ledger, Row(60, "70.000"));
        ledger.Answered(Url, sent, [Result("written")]);

        ExportUpdate update = Assert.Single(ledger.Pick([Row(58, "60.000")], false));
        Assert.Equal(TimeFormat.FromTicks(Ticks(58)), update.Time);
    }
}
