using System;
using Xunit;
using Run = Celeste.Mod.SpeedrunSheet.RunBook.Run;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the session's bests, one per row: what the export writes
public class RunBookTests {
    private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    private static SegmentRecord Record(string scope, string name, double seconds,
        SaveAndQuitNote note = SaveAndQuitNote.Without) =>
        new(TestRules.Find(scope, name), Ticks(seconds), note);

    [Fact]
    public void KeepsTheBestOfEachRow() {
        RunBook book = new();
        Assert.Single(book.Offer([Record("6a", "Hollows", 45)]));
        Assert.Empty(book.Offer([Record("6a", "Hollows", 47)]));
        Assert.Single(book.Offer([Record("6a", "Hollows", 44)]));

        Assert.Equal(Ticks(44), Assert.Single(book.All).Ticks);
    }

    // equal is no improvement: the export would offer the same time again
    [Fact]
    public void AnEqualTimeDoesNotReplaceTheBest() {
        RunBook book = new();
        book.Offer([Record("6a", "Hollows", 45)]);

        Assert.Empty(book.Offer([Record("6a", "Hollows", 45)]));
    }

    // a cassette run never stands for the plain segment, and neither replaces the other
    [Fact]
    public void EveryRowKeepsItsOwnBest() {
        RunBook book = new();
        book.Offer([Record("6a", "Hollows Tape RTM", 20)]);
        book.Offer([Record("6a", "Hollows", 45)]);

        Assert.Equal(2, book.All.Count);
    }

    // a session's bests outlive the chapter: an export from 2a carries 1a's too
    [Fact]
    public void KeepsTheRowsOfEveryChapter() {
        RunBook book = new();
        book.Offer([Record("1a", "Start", 30.5)]);
        book.Offer([Record("2a", "Start", 61)]);

        Assert.Equal(2, book.All.Count);
    }

    [Fact]
    public void ASlowerRunWithAnotherNoteChangesNothing() {
        RunBook book = new();
        book.Offer([Record("6a", "Hollows", 45)]);

        Assert.Empty(book.Offer([Record("6a", "Hollows", 47, SaveAndQuitNote.With)]));
        Assert.Equal(SaveAndQuitNote.Without, Assert.Single(book.All).Note);
    }

    [Fact]
    public void AFasterRunReplacesTimeAndNote() {
        RunBook book = new();
        book.Offer([Record("6a", "Hollows", 45)]);

        Assert.Single(book.Offer([Record("6a", "Hollows", 44, SaveAndQuitNote.With)]));
        Run only = Assert.Single(book.All);
        Assert.Equal((Ticks(44), SaveAndQuitNote.With), (only.Ticks, only.Note));
    }
}
