using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

[Collection(RemoteBestsCollection.Name)]
public class ExportSourceTests {
    private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    private static RunBook.Run Run(string scope, string name, double seconds) =>
        new(scope, name, Ticks(seconds), SaveAndQuitNote.Without);

    // the sheet's order, whatever order the session ran them in, and without
    // the standards: the rows come from srs's own table
    [Fact]
    public void ListsEveryRunInTheSheetsOrder() {
        RemoteBests.Reset();
        List<PendingUpdate> updates = ExportSource.Collect([
            Run("2a", "Awake", 40), Run("1a", "Chasm", 30), Run("1a", "Start", 20),
        ]);

        Assert.Equal(["Start", "Chasm", "Awake"], updates.Select(u => u.Label));
        Assert.Equal(new SheetRowRef("A Sides", "1a", "1a Start"), updates[0].Row);
        // nothing held: every row is an improvement on an empty cell
        Assert.All(updates, u => Assert.True(u.Selected));
    }

    [Fact]
    public void ListsTheSideBeingPlayedFirst() {
        RemoteBests.Reset();
        RunBook.Run[] runs = [
            Run("1a", "Start", 20), Run("2a", "ARB Start", 70), Run("2a", "Awake", 40),
            Run("2b", "Start", 30), Run("3a", "Start", 50),
        ];

        Assert.Equal(["2a", "2arb", "1a", "3a", "2b"],
            ExportSource.Collect(runs, "2a").Select(u => ExportTable.GroupLabel(u.Row)));
        // a side with no run this session moves nothing
        Assert.Equal(["1a", "2a", "2arb", "3a", "2b"],
            ExportSource.Collect(runs, "7a").Select(u => ExportTable.GroupLabel(u.Row)));
    }

    // a side's berry rows follow that side's own, before the next chapter,
    // under a header of their own
    [Fact]
    public void ListsASidesBerryRowsUnderThatSide() {
        RemoteBests.Reset();
        List<PendingUpdate> updates = ExportSource.Collect([
            Run("2a", "ARB IL", 150), Run("2a", "Awake", 40), Run("1a", "ARB Start", 35),
            Run("2a", "ARB Start", 70), Run("1a", "IL", 100),
        ]);

        Assert.Equal(["IL", "Start", "Awake", "Start", "IL"], updates.Select(u => u.Label));
        Assert.Equal(["1a", "1arb", "2a", "2arb", "2arb"], updates.Select(u => ExportTable.GroupLabel(u.Row)));
        Assert.Equal(new SheetRowRef("ARB/Full Clear", "2a \U0001F353", "2a \U0001F353"), updates[4].Row);
    }

    [Fact]
    public void ComparesAgainstTheSheetAndCarriesItsBand() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([
            new RemoteRow { Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = "Crossing", Time = "21.948" },
        ]);
        PendingUpdate update = Assert.Single(ExportSource.Collect([Run("1a", "Crossing", 22.5)]));

        Assert.False(update.Selected);
        Assert.Equal("21.948", update.RemoteCell);
        Assert.Equal("checkpoint", update.Band);
    }

    [Fact]
    public void ADuplicateRowIsListedButNeverTicked() {
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([
            new RemoteRow { Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = "Crossing", Time = "25.000" },
            new RemoteRow { Tab = "A Sides", Band = "il", Chapter = "1a", Cp = "Crossing", Time = "25.000" },
        ]);
        PendingUpdate update = Assert.Single(ExportSource.Collect([Run("1a", "Crossing", 20)]));

        Assert.True(update.Duplicate);
        Assert.False(update.Selected);
    }

    [Fact]
    public void ARunTheTableDoesNotHoldIsLeftOut() {
        RemoteBests.Reset();
        Assert.Empty(ExportSource.Collect([Run("1a", "Nowhere", 20)]));
    }

    // one chapter cell holds a checkpoint row and the chapter row: two cells, two bands
    [Fact]
    public void ABerryChapterRowIsComparedInItsOwnBand() {
        const string cell = "2a \U0001F353";
        RemoteBests.Reset();
        RemoteBests.AcceptFresh([
            new RemoteRow { Tab = "ARB/Full Clear", Band = "checkpoint", Chapter = cell, Cp = "2a Start", Time = "1:05.000" },
            new RemoteRow { Tab = "ARB/Full Clear", Band = "il", Chapter = cell, Cp = cell, Time = "2:50.000" },
        ]);
        List<PendingUpdate> updates = ExportSource.Collect([
            Run("2a", "ARB IL", 165), Run("2a", "ARB Start", 64),
        ]);

        Assert.Equal(["Start", "IL"], updates.Select(u => u.Label));
        Assert.Equal(["checkpoint", "il"], updates.Select(u => u.Band));
        Assert.Equal(["1:05.000", "2:50.000"], updates.Select(u => u.RemoteCell));
        Assert.Equal(new SheetRowRef("ARB/Full Clear", cell, cell), updates[1].Row);
        Assert.All(updates, u => Assert.False(u.Duplicate));
    }

    // Full Clear rows follow the berry rows of their side under the sheet's own
    // chapter cell; 8A's, typed on the A-side tab, stays with the A-side rows
    [Fact]
    public void ListsFullClearRowsUnderTheirChapterCell() {
        RemoteBests.Reset();
        List<PendingUpdate> updates = ExportSource.Collect([
            Run("7a", "FC IL", 700), Run("7a", "FC Start", 60), Run("7a", "ARB Start", 50),
            Run("7a", "Start Gem", 40), Run("8a", "HotM Horizontal Tape", 65),
            Run("8a", "ARB IL", 300), Run("Farewell", "FC Moon Berry", 170),
            Run("Farewell", "Farewell", 160),
        ]);

        Assert.Equal(["Start Gem", "Start", "Start", "IL", "HotM Horizontal Tape", "IL", "Farewell", "Moon Berry"],
            updates.Select(u => u.Label));
        Assert.Equal(["7a", "7arb", "7afc", "7afc", "8a", "8arb", "Farewell", "Fw FC"],
            updates.Select(u => ExportTable.GroupLabel(u.Row)));
    }

    [Fact]
    public void PassesTheNoteTheDropdownAndTheRankOffset() {
        RemoteBests.Reset();
        Assert.True(SheetRows.TryFind("1a", "ARB IL", out SheetRow sheetRow));
        SheetRowRef target = SheetRows.TargetOf(sheetRow);
        RemoteBests.AcceptFresh([
            new RemoteRow { Tab = target.Tab, Band = "il", Chapter = target.Chapter, Cp = target.Cp, Time = "3:00.000",
                Timing = "IL", TimingOptions = ["IL", "FileTime"], RankOffset = 2.0 },
        ]);
        RunBook.Run run = new("1a", "ARB IL", Ticks(100), SaveAndQuitNote.Without);
        PendingUpdate update = Assert.Single(ExportSource.Collect([run]));

        Assert.Equal(("IL", "IL"), (update.Timing, update.ExpectTiming));
        Assert.Equal(TimeFormat.FromTicks(Ticks(102)), update.AdjustedText);

        // another scope's row never gets the offset
        Assert.True(SheetRows.TryFind("1a", "Crossing", out SheetRow other));
        SheetRowRef otherTarget = SheetRows.TargetOf(other);
        RemoteBests.AcceptFresh([
            new RemoteRow { Tab = otherTarget.Tab, Band = "checkpoint", Chapter = otherTarget.Chapter, Cp = otherTarget.Cp,
                Time = "30.000", RankOffset = 2.0 },
        ]);
        Assert.Equal("", Assert.Single(ExportSource.Collect([Run("1a", "Crossing", 20)])).AdjustedText);
    }
}
