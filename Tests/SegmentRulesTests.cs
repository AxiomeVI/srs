using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// one rule per imported row, and nothing that two rules could both claim
public class SegmentRulesTests {
    private static SegmentRule Rule(string scope, string name) => TestRules.Find(scope, name);

    // the rule a label alone derives, on a row of no table
    private static SegmentRule Synthetic(string label) =>
        SegmentRules.Build([new SheetRow(StandardsTab.ASides, "3a CP", label, "3a", label, "Huge Mess")])[0];

    private static readonly HashSet<string> ExpectedCurrentRoomRows = [
        "Prologue/Granny",
        "1a/Start",
        "1a/IL", "1a/IL Tape RTM", "1a/IL Tape Clear", "1a/IL Heart Tape RTM",
        "2a/Start",
        "2a/Start Heart RC", "2a/Start Tape Clear", "2a/Start Tape RTM",
        "2a/Awake",
        "2a/IL", "2a/IL Tape Clear",
        "3a/Start",
        "3a/IL", "3a/IL Heart Clear", "3a/IL Tape RTM", "3a/IL Heart Tape Clear", "3a/IL Heart Tape RTM",
        "4a/Start", "4a/Start Tape Clear", "4a/Start Tape RTM",
        "4a/IL", "4a/IL Heart Clear", "4a/IL Tape Clear", "4a/IL Heart Tape Clear", "4a/IL Heart Tape RTM",
        "5a/Start",
        "5a/Unravelling",
        "5a/IL", "5a/IL Tape RTM", "5a/IL Heart Tape RTM",
        "1b/Start", "1b/IL", "2b/Start", "2b/IL", "3b/Start", "3b/IL", "4b/Start", "4b/IL",
        "5b/Start",
        "5b/Through the Mirror",
        "5b/IL",
        "6a/Start",
        "6a/IL", "6a/IL Tape Clear", "6a/IL Tape RTM", "6a/IL Heart Tape RTM",
        "6b/Start", "6b/IL",
        "7b/Start", "7b/IL", "8b/Start", "8b/IL",
        "7a/Start Gem",
        "2a/FC Start", "4a/FC Start", "7a/FC Start",
        "2a/FC IL", "3a/FC IL", "4a/FC IL", "7a/FC IL", "8a/FC IL", "Farewell/FC DTS IL", "Farewell/FC No DTS IL",
        "7a/Start",
        "7a/IL", "7a/IL Tape Clear", "7a/IL Tape RTM", "7a/IL Heart Tape RTM",
        "8a/Start",
        "8a/IL", "8a/IL Tape Clear",
        "1c/1c", "2c/2c", "3c/3c", "4c/4c", "5c/5c", "6c/6c", "7c/7c", "8c/8c",
        "Farewell/Start",
        "Farewell/Start DTS",
        "Farewell/DTS IL",
        "Farewell/No DTS IL",
        "1a/ARB Start to RTM", "1a/ARB Start to Heart", "1a/ARB Start", "1a/ARB IL",
        "2a/ARB Start", "2a/ARB Awake", "2a/ARB IL",
        "3a/ARB Start", "3a/ARB IL",
        "4a/ARB Start", "4a/ARB IL",
        "5a/ARB Start", "5a/ARB Unravelling", "5a/ARB IL",
        "7a/ARB Start", "7a/ARB IL",
        "8a/ARB IL",
        "1a/FC IL", "5a/FC IL", "6a/FC IL",
    ];

    // a row given ChapterEnd by mistake would stay open across its checkpoints
    [Fact]
    public void OnlyTheIlAndCSideRowsAreAWholeChapter() {
        Assert.Equal([
                "1a/IL", "1a/IL Tape Clear", "2a/IL", "2a/IL Tape Clear",
                "3a/IL", "3a/IL Heart Clear", "3a/IL Heart Tape Clear",
                "4a/IL", "4a/IL Heart Clear", "4a/IL Tape Clear", "4a/IL Heart Tape Clear",
                "5a/IL", "6a/IL", "6a/IL Tape Clear", "7a/IL", "7a/IL Tape Clear",
                "8a/IL", "8a/IL Tape Clear",
                "1b/IL", "2b/IL", "3b/IL", "4b/IL", "5b/IL", "6b/IL", "7b/IL", "8b/IL",
                "1c/1c", "2c/2c", "3c/3c", "4c/4c", "5c/5c", "6c/6c", "7c/7c", "8c/8c",
                "Farewell/DTS IL", "Farewell/No DTS IL",
                "1a/ARB IL", "2a/ARB IL", "3a/ARB IL", "4a/ARB IL", "5a/ARB IL", "7a/ARB IL", "8a/ARB IL",
                "1a/FC IL", "2a/FC IL", "3a/FC IL", "4a/FC IL", "5a/FC IL", "6a/FC IL", "7a/FC IL", "8a/FC IL",
                "Farewell/FC DTS IL", "Farewell/FC No DTS IL",
            ],
            SegmentRules.All.Where(r => r.End == EndKind.ChapterEnd).Select(r => $"{r.Scope}/{r.Name}"));
    }

    // an IL starts where its chapter's Start does: 7A's after the launch, with the same head
    [Fact]
    public void AnIlStartsLikeItsChaptersStart() {
        foreach (SegmentRule il in SegmentRules.All.Where(r => r.Name.Contains("IL"))) {
            SegmentRule start = SegmentRules.All.Single(r =>
                r.Scope == il.Scope && r.Name == "Start");

            Assert.Equal((start.Anchor, start.Start, start.Setup, start.HeadTicks, start.TailTicks),
                (il.Anchor, il.Start, il.Setup, il.HeadTicks, il.TailTicks));
        }
    }

    // every other row is NextRoom, MapSpawn (TheMapSpawnRows) or HeartRespawn: a new Start-anchored
    // row, or a new wake-up, shows up here as a diff
    [Fact]
    public void CurrentRoomRowsAreTheChapterStartsAndTheWakeUps() {
        HashSet<string> currentRoom = SegmentRules.All.Where(r => r.Setup == StartSetup.CurrentRoom)
            .Select(r => $"{r.Scope}/{r.Name}").ToHashSet();

        Assert.Equal(ExpectedCurrentRoomRows, currentRoom);
    }

    [Fact]
    public void OneRulePerImportedRow() {
        Assert.Equal(SheetRows.All.Length, SegmentRules.All.Count);
    }

    // two rules with the same start, setup, end, requirements, berries and exclusivity
    // could not be told apart by anything a run does
    [Fact]
    public void NoTwoRulesAreIndistinguishable() {
        var clashes = SegmentRules.All
            .GroupBy(r => (r.Scope, r.Anchor, r.Start, r.Setup, r.End, r.EndsOn, r.Requires, SetOf(r.Berries), r.Dashes))
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(" / ", group.Select(r => r.Name)))
            .ToList();

        Assert.True(clashes.Count == 0, string.Join("\n", clashes));
    }

    [Fact]
    public void ACollectRuleEndsOnSomething() {
        Assert.All(SegmentRules.All.Where(r => r.End == EndKind.Collect),
            r => Assert.NotEqual(Collectibles.None, r.EndsOn));
    }

    // a berry row with no set would never be met, and a set on another row would never be read
    [Fact]
    public void OnlyTheBerryTabsRowsRequireBerries() {
        Assert.All(SegmentRules.All, r => {
            // 6A has no berry, and 8A's row is typed on the A-side tab under an A-side name
            bool berryTab = r.Name.StartsWith("ARB ") || (r.Name.StartsWith("FC ") && r.Scope != "6a");
            Assert.Equal(berryTab, r.RequiresBerries);
        });
        Assert.Equal(50, SegmentRules.All.Count(r => r.Name.StartsWith("ARB ")));
        Assert.Equal(25, SegmentRules.All.Count(r => r.Name.StartsWith("FC ") && r.RequiresBerries));
        Assert.Equal(16, SegmentRules.All.Count(r => r.RequiresBerries && r.ChapterRun));
    }

    [Fact]
    public void TheMapSpawnRows() {
        Assert.Equal(
            ["1a/ARB Crossing to Heart", "1a/ARB Crossing to RTM", "1a/ARB Crossing", "1a/ARB Chasm", "1a/ARB Crossing-2",
             "1a/ARB Chasm-2", "4a/ARB Cliff Face (from RTM)", "5a/ARB Depths", "7a/ARB 2500m-2",
             "1a/FC Crossing to RTM", "1a/FC Chasm-2", "5a/FC Depths", "6a/FC Hollows (from RTM)"],
            SegmentRules.All.Where(r => r.Setup == StartSetup.MapSpawn).Select(r => $"{r.Scope}/{r.Name}"));
    }

    // 7A's heart needs the gems, which the berry route does not take
    [Fact]
    public void TheBerryRowsThatNeedTheHeart() {
        Assert.Equal(
            ["1a/ARB Crossing to Heart", "1a/ARB Crossing to RTM", "1a/ARB Start to Heart", "1a/ARB Crossing",
             "3a/ARB Huge Mess", "4a/ARB Shrine", "1a/ARB IL", "3a/ARB IL", "4a/ARB IL"],
            SegmentRules.All.Where(r => r.Name.StartsWith("ARB ") && r.Requires == Collectibles.Heart)
                .Select(r => $"{r.Scope}/{r.Name}"));
        Assert.All(SegmentRules.All.Where(r => r.Name.StartsWith("ARB ")),
            r => Assert.True(r.Requires is Collectibles.None or Collectibles.Heart));
    }

    // the berry rows on the virtual split and on the override start and end
    // where their A-side rows do, since both hang on the anchor
    [Fact]
    public void ABerryRowKeepsItsAnchorsBoundaries() {
        Assert.Equal("Heart of the Mountain", Rule("8a", "ARB HotM Vertical").Anchor);
        Assert.Equal("Awake", Rule("2a", "ARB Awake").Anchor);
        Assert.Equal("2500 M", Rule("7a", "ARB 2500m-full").Anchor);
        Assert.Equal((Rule("7a", "Start").Start, Rule("7a", "Start").HeadTicks),
            (Rule("7a", "ARB Start").Start, Rule("7a", "ARB Start").HeadTicks));
    }

    // 3000m's three pieces chain as checkpoints do, the first from where
    // 3000m starts, and a berry piece asks for its own rooms' berries only
    [Fact]
    public void TheDraftRowsCutThe3000mCheckpoint() {
        Assert.All(new[] { "Downdraft", "Updraft", "Nodraft" }, name => {
            SegmentRule plain = Rule("7a", name);
            SegmentRule berry = Rule("7a", "ARB " + name);
            Assert.Equal((name, StartSetup.NextRoom, EndKind.NextStart, false), (plain.Anchor, plain.Setup, plain.End, plain.ChapterRun));
            Assert.Equal((plain.Anchor, plain.Setup, plain.End, Collectibles.None), (berry.Anchor, berry.Setup, berry.End, berry.Requires));
            Assert.Null(plain.Berries);
            // a virtual anchor is no checkpoint of the map: the set names rooms
            Assert.Null(berry.Berries.Checkpoint);
            Assert.Single(berry.Berries.Rooms);
        });
        Assert.Equal(SegmentAutoDetect.EntryRooms[("7a", "3000 M")], SegmentAutoDetect.EntryRooms[("7a", "Downdraft")]);
    }

    // the rooms of the three pieces, by value: RunTrackerTests builds its own copy
    [Theory]
    [InlineData("Downdraft", "g-00", "f-11", "Updraft")]
    [InlineData("Updraft", "g-01", "g-00b", "Nodraft")]
    [InlineData("Nodraft", "g-02", "g-01", null)]
    public void TheDraftRowsRoomsAreTheseOnes(string name, string start, string entry, string splitInto) {
        Assert.Equal(start, SegmentAutoDetect.StartRoomOverrides[("7a", name)]);
        Assert.Equal(entry, SegmentAutoDetect.EntryRooms[("7a", name)]);
        Assert.Equal(splitInto, SegmentAutoDetect.SplitCheckpoints.GetValueOrDefault(("7a", name)));
    }

    // a Full Clear label says nothing of what the row collects: the whole table, as the owner gave it
    [Theory]
    [InlineData("1a", "FC IL", (int)(Collectibles.Heart | Collectibles.Cassette), 0)]
    [InlineData("2a", "FC Start", (int)(Collectibles.Cassette), 0)]
    [InlineData("2a", "FC IL", (int)(Collectibles.Cassette), 0)]
    [InlineData("3a", "FC Elevator Shaft", (int)(Collectibles.Cassette), 0)]
    [InlineData("3a", "FC IL", (int)(Collectibles.Heart | Collectibles.Cassette), 0)]
    [InlineData("4a", "FC Start", (int)(Collectibles.Cassette), 0)]
    [InlineData("4a", "FC IL", (int)(Collectibles.Heart | Collectibles.Cassette), 0)]
    [InlineData("5a", "FC Depths", (int)(Collectibles.Heart | Collectibles.Cassette), 0)]
    [InlineData("5a", "FC IL", (int)(Collectibles.Heart | Collectibles.Cassette), 0)]
    [InlineData("6a", "FC Hollows (from RTM)", (int)(Collectibles.None), 0)]
    [InlineData("6a", "FC IL", (int)(Collectibles.Heart | Collectibles.Cassette), 0)]
    [InlineData("7a", "FC Start", (int)(Collectibles.Gem), 1)]
    [InlineData("7a", "FC 500m", (int)(Collectibles.Gem), 1)]
    [InlineData("7a", "FC 1000m", (int)(Collectibles.Gem), 1)]
    [InlineData("7a", "FC 1500m", (int)(Collectibles.Gem | Collectibles.Cassette), 1)]
    [InlineData("7a", "FC 2000m", (int)(Collectibles.Gem), 1)]
    [InlineData("7a", "FC 2500m", (int)(Collectibles.Gem), 1)]
    [InlineData("7a", "FC 3000m", (int)(Collectibles.Heart), 0)]
    [InlineData("7a", "FC Downdraft", (int)(Collectibles.Heart), 0)]
    [InlineData("7a", "FC IL", (int)(Collectibles.Gem | Collectibles.Cassette | Collectibles.Heart), 6)]
    [InlineData("8a", "HotM Horizontal Tape", (int)(Collectibles.Cassette), 0)]
    [InlineData("8a", "FC IL", (int)(Collectibles.Cassette), 0)]
    [InlineData("Farewell", "FC Moon Berry", (int)(Collectibles.None), 0)]
    [InlineData("Farewell", "FC DTS IL", (int)(Collectibles.None), 0)]
    [InlineData("Farewell", "FC No DTS IL", (int)(Collectibles.None), 0)]
    public void WhatAFullClearRowCollects(string scope, string name, int requires, int gems) {
        SegmentRule rule = Rule(scope, name);

        Assert.Equal(((Collectibles)requires, gems, Collectibles.None), (rule.Requires, rule.Gems, rule.EndsOn));
        Assert.Equal(name.EndsWith("IL") ? EndKind.ChapterEnd : EndKind.NextStart, rule.End);
    }

    [Fact]
    public void TheFullClearRowsOffTheDefaultBerrySet() {
        Assert.Null(Rule("6a", "FC Hollows (from RTM)").Berries);
        Assert.Null(Rule("8a", "HotM Horizontal Tape").Berries);
        Assert.Equal(["g-00b"], Rule("7a", "FC Downdraft").Berries.Rooms);
        Assert.Equal(["g-00b"], Rule("7a", "ARB Downdraft").Berries.Rooms);
        Assert.Equal(["g-01"], Rule("7a", "ARB Updraft").Berries.Rooms);
        Assert.Equal(["g-03"], Rule("7a", "ARB Nodraft").Berries.Rooms);
        Assert.All(new[] { "FC Moon Berry", "FC DTS IL", "FC No DTS IL" },
            name => Assert.Same(BerrySet.MoonBerry, Rule("Farewell", name).Berries));
        Assert.Equal("Start", Rule("2a", "FC Start").Berries.Checkpoint);
        Assert.Same(BerrySet.WholeChapter, Rule("7a", "FC IL").Berries);
    }

    // the Farewell tab's own ILs get their dash count from their label; these two cannot
    [Fact]
    public void TheFarewellFullClearRunsKeepTheDashRule() {
        Assert.Equal((2, 1, (int?)null),
            (Rule("Farewell", "FC DTS IL").Dashes.Value, Rule("Farewell", "FC No DTS IL").Dashes.Value,
             Rule("Farewell", "FC Moon Berry").Dashes));
    }

    // a Full Clear row must ask for its berry twin's very set, or it would not pass for more
    // specific than the row it is the full clear of
    [Fact]
    public void AFullClearRowAsksForItsBerryTwinsBerries() {
        foreach (SegmentRule fc in SegmentRules.All.Where(r => r.Name.StartsWith("FC ") && r.Scope != "Farewell")) {
            string twin = "ARB " + fc.Name[3..].Replace("2500m", "2500m-full");
            if (SegmentRules.All.SingleOrDefault(r => r.Scope == fc.Scope && r.Name == twin) is { } arb) {
                Assert.Equal((arb.Berries, arb.Anchor, arb.Setup, arb.End == EndKind.Collect ? fc.End : arb.End),
                    (fc.Berries, fc.Anchor, fc.Setup, fc.End));
            } else {
                Assert.Contains($"{fc.Scope}/{fc.Name}", new[] { "6a/FC Hollows (from RTM)", "6a/FC IL" });
            }
        }
    }

    // what the timer row shows when a 7A checkpoint closes, by what the run collected
    [Fact]
    public void TheMostSpecificRowMetIsShown() {
        SegmentRule plain = Rule("7a", "500m"), gem = Rule("7a", "500m Gem"), arb = Rule("7a", "ARB 500m"),
            fc = Rule("7a", "FC 500m");

        Assert.Same(fc, Shown(plain, gem, arb, fc));
        Assert.Same(gem, Shown(plain, gem));
        Assert.Same(arb, Shown(plain, arb));
        Assert.Same(Rule("7a", "FC IL"),
            ShownOf(true, [Rule("7a", "IL"), Rule("7a", "IL Tape Clear"), Rule("7a", "ARB IL"), Rule("7a", "FC IL")]));

        static SegmentRule Shown(params SegmentRule[] met) => ShownOf(false, met);
    }

    // without a map, a set is told by its description: the same one is the
    // same berries, and no other holds it
    private static SegmentRule ShownOf(bool chapterRun, SegmentRule[] met) =>
        met[Specificity.MostSpecific(met, chapterRun, rule => rule.Berries == null ? null : [SetOf(rule.Berries)])];

    private static string SetOf(BerrySet set) => set == null ? null
        : $"{set.Checkpoint}|{string.Join(",", (set.Rooms ?? []).Order())}|{string.Join(",", (set.Except ?? []).Order())}"
          + $"|{set.Chapter}|{set.Moon}";

    // a gem row is its plain row plus the gem its label marks
    [Fact]
    public void AGemRowAsksForItsCheckpointsGem() {
        Assert.All(new[] { "Start", "500m", "1000m", "2000m", "2500m" }, name => {
            SegmentRule plain = Rule("7a", name);
            SegmentRule gem = Rule("7a", name + " Gem");
            Assert.Equal((plain.Anchor, plain.Start, plain.Setup, plain.End, plain.HeadTicks),
                (gem.Anchor, gem.Start, gem.Setup, gem.End, gem.HeadTicks));
            Assert.Equal((Collectibles.Gem, 1), (gem.Requires, gem.Gems));
        });
        Assert.Equal((Collectibles.Gem | Collectibles.Cassette, 1, EndKind.NextStart),
            (Rule("7a", "1500m Gem Tape").Requires, Rule("7a", "1500m Gem Tape").Gems, Rule("7a", "1500m Gem Tape").End));
        Assert.All(SegmentRules.All, r => Assert.Equal(r.Gems > 0, (r.Requires & Collectibles.Gem) != 0));
    }

    // what the rows above the timer and the export screen print
    [Fact]
    public void NoTwoRowsShowTheSameName() {
        Assert.Equal(SegmentRules.All.Count,
            SegmentRules.All.Select(r => TierLine.NameOf(r.Scope, r.Name)).Distinct().Count());
    }

    // the sheet's spacing around a marker is irregular, and a row marking both
    // ends on the later of the two. Collectibles as an int: the enum is internal
    [Theory]
    [InlineData("Depths 📼 RTM", (int)Collectibles.Cassette)]
    [InlineData("📼RTM", (int)Collectibles.Cassette)]
    [InlineData("Shrine 💙 RTM", (int)Collectibles.Heart)]
    [InlineData("💙+📼 RTM", (int)(Collectibles.Heart | Collectibles.Cassette))]
    public void AnRtmLabelEndsAtWhatItMarks(string label, int endsOn) {
        SegmentRule rule = Synthetic(label);

        Assert.Equal((EndKind.Collect, (Collectibles)endsOn), (rule.End, rule.EndsOn));
    }

    [Theory]
    [InlineData("2a Start 💙 RC")]
    [InlineData("2a Start 💙 RC ")]
    public void AnRcLabelEndsAtTheRestartAndRequiresWhatItMarks(string label) {
        SegmentRule rule = Synthetic(label);

        Assert.Equal((EndKind.Restart, Collectibles.None, Collectibles.Heart), (rule.End, rule.EndsOn, rule.Requires));
    }

    // "Clear" on an A-side checkpoint row means collect and keep going, not the
    // chapter's end, and an RTM marking nothing a run collects has nothing to
    // end on
    [Theory]
    [InlineData("Granny")]
    [InlineData("Start DTS")]
    [InlineData("Crossing 💙")]
    [InlineData("Shrine 💙 Clear")]
    [InlineData("Hollows 📼Clear")]
    [InlineData("Plain RTM")]
    public void EveryOtherLabelEndsWithItsSegment(string label) {
        Assert.Equal(EndKind.NextStart, Synthetic(label).End);
    }

    // the markers are what a run must collect, wherever they sit in the label
    [Theory]
    [InlineData("Crossing 💙", (int)Collectibles.Heart)]
    [InlineData("Hollows 📼Clear", (int)Collectibles.Cassette)]
    [InlineData("7a Start 💎", (int)Collectibles.Gem)]
    [InlineData("Crossing", (int)Collectibles.None)]
    public void ALabelRequiresWhatItMarks(string label, int requires) {
        Assert.Equal((Collectibles)requires, Synthetic(label).Requires);
    }

    // "X DTS" keeps both dashes and the "X" beside it lost one; a Farewell row
    // without a twin, and any other tab's, has no dash rule
    [Fact]
    public void ADtsTwinSplitsItsRowOnTheDashes() {
        List<SegmentRule> rules = SegmentRules.Build([
            new SheetRow(StandardsTab.Farewell, "Farewell", "Singular", "Farewell", "Singular", "Singular"),
            new SheetRow(StandardsTab.Farewell, "Farewell", "Singular DTS", "Farewell", "Singular DTS", "Singular"),
            new SheetRow(StandardsTab.Farewell, "Farewell", "Stubbornness", "Farewell", "Stubbornness", "Stubbornness"),
            new SheetRow(StandardsTab.ASides, "1a CP", "Crossing DTS", "1a", "Crossing DTS", "Crossing"),
        ]);

        Assert.Equal(new int?[] { 1, 2, null, null }, rules.Select(r => r.Dashes));
    }

    // a marker read wrong mistimes its row without a sound. "to RTM" is no marker: those rows end
    // on a Return to Map (TheRowsThatEndOnAReturnToMap)
    [Fact]
    public void OnlyTheRcRtmAndToHeartRowsEndEarly() {
        Assert.Equal(
            SheetRows.All.Where(r => (r.Label.EndsWith("RTM") && !r.Label.EndsWith("to RTM")) || r.Label.EndsWith("RC")
                                     || r.Label.EndsWith("to Heart"))
                .Select(r => $"{r.Scope}/{r.Name}"),
            SegmentRules.All.Where(r => r.End is EndKind.Collect or EndKind.Restart).Select(r => $"{r.Scope}/{r.Name}"));
    }

    // what the rows above the timer split on: an IL "RTM" is a chapter run
    // too, and no checkpoint row is
    [Fact]
    public void TheIlAndCSideRowsAreChapterRuns() {
        Assert.All(SegmentRules.All, r => Assert.Equal(r.Name.Contains("IL") || r.Scope.EndsWith('c'), r.ChapterRun));
    }

    // the B-sides tab has no IL block: a chapter's "Clear" is the whole chapter
    // there, and only there
    [Fact]
    public void ABSidesClearIsTheWholeChapter() {
        SheetRow clear = new(StandardsTab.BSides, "1b", "1b Clear", "1b", "IL", "Start");

        Assert.Equal((EndKind.ChapterEnd, true), (SegmentRules.Build([clear])[0].End, SegmentRules.Build([clear])[0].ChapterRun));
        Assert.Equal((EndKind.NextStart, false), (Synthetic("1b Clear").End, Synthetic("1b Clear").ChapterRun));
    }

    [Fact]
    public void FarewellsDtsTwinsAreExclusive() {
        Assert.Equal(2, Rule("Farewell", "Start DTS").Dashes);
        Assert.Equal(1, Rule("Farewell", "Start").Dashes);
        Assert.Equal(2, Rule("Farewell", "Determination DTS").Dashes);
        Assert.Equal(2, Rule("Farewell", "DTS IL").Dashes);
        Assert.Equal(1, Rule("Farewell", "No DTS IL").Dashes);
        Assert.Equal(1, Rule("Farewell", "Determination").Dashes);
        // the skip is over by Stubbornness: no twin, no dash requirement
        Assert.Null(Rule("Farewell", "Stubbornness").Dashes);
        Assert.Null(Rule("1a", "Start").Dashes);
    }

    [Fact]
    public void HeadsAndTheTailAreTheSheetsConstants() {
        Assert.Equal(TimeSpan.FromMilliseconds(5508).Ticks, Rule("7a", "Start").HeadTicks);
        Assert.Equal(StartKind.AfterLaunch, Rule("7a", "Start").Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1037).Ticks, Rule("Prologue", "Granny").HeadTicks);
        Assert.Equal(TimeSpan.FromMilliseconds(544).Ticks, Rule("Prologue", "Granny").TailTicks);
        Assert.Equal(Rule("7a", "Start").HeadTicks, Rule("7b", "Start").HeadTicks);
        Assert.Equal(StartKind.AfterLaunch, Rule("7b", "Start").Start);
        Assert.Equal(0, Rule("7a", "500m").HeadTicks);
        Assert.Equal(StartKind.Room, Rule("7a", "500m").Start);
        Assert.Equal(StartKind.Room, Rule("2a", "Awake").Start);
    }

    [Fact]
    public void RulesKeepSheetOrder() {
        Assert.Equal(Enumerable.Range(0, SegmentRules.All.Count), SegmentRules.All.Select(r => r.Order));
    }

    [Fact]
    public void ABerryTabRowRequiresItsCheckpointsBerries() {
        SegmentRule rule = Rule("2a", "ARB Intervention");

        Assert.True(rule.RequiresBerries);
        Assert.Equal("Intervention", rule.Berries.Checkpoint);
        Assert.Null(rule.Berries.Rooms);
        Assert.Null(rule.Berries.Except);
        Assert.False(rule.Berries.Chapter);
        Assert.Equal((StartSetup.NextRoom, EndKind.NextStart, Collectibles.None, false),
            (rule.Setup, rule.End, rule.Requires, rule.ChapterRun));
        Assert.Null(rule.EntryRoom);
    }

    // the second block names a row by its chapter cell
    [Fact]
    public void ABerryTabChapterRowIsAChapterRunOverEveryBerry() {
        SegmentRule rule = Rule("2a", "ARB IL");

        Assert.True(rule.ChapterRun);
        Assert.Equal((StartSetup.CurrentRoom, EndKind.ChapterEnd, Collectibles.None),
            (rule.Setup, rule.End, rule.Requires));
        Assert.True(rule.Berries.Chapter);
    }

    // the chapter rows whose route returns to the map. RunTracker.Left ends
    // them like any whole chapter, so each must be one: timed from the
    // chapter's start to its end, with no dash rule
    [Fact]
    public void TheChapterRowsThatSpanAReturnToMap() {
        Assert.Equal(
            ["1a/ARB IL", "4a/ARB IL", "5a/ARB IL", "7a/ARB IL", "1a/FC IL", "4a/FC IL", "5a/FC IL", "6a/FC IL"],
            SegmentRules.All.Where(r => r.SpansLeaves).Select(r => $"{r.Scope}/{r.Name}"));
        Assert.All(SegmentRules.All.Where(r => r.SpansLeaves), r =>
            Assert.Equal((true, EndKind.ChapterEnd, "Start", (int?)null), (r.ChapterRun, r.End, r.Anchor, r.Dashes)));
    }

    // no emoji in the label says so
    [Theory]
    [InlineData("ARB Start to Heart")]
    [InlineData("ARB Crossing to Heart")]
    public void TheToHeartRowsEndOnTheHeart(string name) {
        SegmentRule rule = Rule("1a", name);

        Assert.Equal((EndKind.Collect, Collectibles.Heart, Collectibles.Heart), (rule.End, rule.EndsOn, rule.Requires));
        Assert.False(rule.ChapterRun);
    }

    // the eleven rows that end on a Return to Map, and nothing else does
    [Fact]
    public void TheRowsThatEndOnAReturnToMap() {
        Assert.Equal(
            ["1a/ARB Start to RTM", "1a/ARB Crossing to RTM", "1a/ARB Heart to RTM", "1a/ARB Crossing-2", "1a/ARB Chasm-2",
             "4a/ARB Old Trail (to RTM)", "5a/ARB Start", "7a/ARB 2500m-2",
             "1a/FC Heart to RTM", "1a/FC Crossing to RTM", "1a/FC Chasm-2"],
            SegmentRules.All.Where(r => r.End == EndKind.ReturnToMap).Select(r => $"{r.Scope}/{r.Name}"));
        Assert.All(SegmentRules.All.Where(r => r.End == EndKind.ReturnToMap),
            r => Assert.Equal((Collectibles.None, false), (r.EndsOn, r.ChapterRun)));
    }

    // the two rows that start on the next room entered, which no list of start
    // setups pins. StartSetup as an int: the enum is internal
    [Theory]
    [InlineData("4a", "ARB Old Trail (to RTM)", "Old Trail", (int)StartSetup.NextRoom)]
    [InlineData("7a", "ARB 2500m-1", "2500 M", (int)StartSetup.NextRoom)]
    public void WhereARowOfAReturnToMapRouteStarts(string scope, string name, string anchor, int setup) {
        SegmentRule rule = Rule(scope, name);

        Assert.Equal((anchor, (StartSetup)setup, StartKind.Room), (rule.Anchor, rule.Setup, rule.Start));
    }

    // what the three rows 1A's full clear route adds collect
    [Theory]
    [InlineData("FC Heart to RTM", (int)Collectibles.Cassette)]
    [InlineData("FC Crossing to RTM", (int)(Collectibles.Heart | Collectibles.Cassette))]
    [InlineData("FC Chasm-2", (int)Collectibles.Cassette)]
    public void WhatAForsakenCityFullClearRowCollects(string name, int requires) {
        SegmentRule rule = Rule("1a", name);

        Assert.Equal(((Collectibles)requires, 0, Collectibles.None, EndKind.ReturnToMap),
            (rule.Requires, rule.Gems, rule.EndsOn, rule.End));
    }

    // the sets the map cannot check here: 4A's, 5A's and 7A's
    [Fact]
    public void TheBerrySetsOfTheOtherReturnToMapRoutes() {
        BerrySet oldTrail = Rule("4a", "ARB Old Trail (to RTM)").Berries;
        Assert.Equal(("Old Trail", "d-00b", null), (oldTrail.Checkpoint, oldTrail.Rooms.Single(), oldTrail.Except));
        Assert.Equal(new BerrySet("Start"), Rule("5a", "ARB Start").Berries);
        BerrySet first = Rule("7a", "ARB 2500m-1").Berries;
        Assert.Equal(("2500 M", null, "f-01"), (first.Checkpoint, first.Rooms, first.Except.Single()));
        BerrySet second = Rule("7a", "ARB 2500m-2").Berries;
        Assert.Equal((null, "f-01", null), (second.Checkpoint, second.Rooms.Single(), second.Except));
    }

    // 2500m as the map files it (checkpointID 5), and 3000m (6). A run with every berry of 2500m
    // records the full row and the one that skips f-01, and shows the full one; 7A's last piece
    // still steps behind 3000m
    [Fact]
    public void ARowIsOutdoneByTheRowWhoseBerriesHoldItsOwn() {
        MapBerry[] summit = [
            new("f-00", 590, 5), new("f-01", 639, 5), new("f-07", 711, 5), new("f-08b", 856, 5), new("f-08c", 759, 5),
            new("f-11", 1068, 5), new("f-11", 1229, 5), new("f-11", 1238, 5),
            new("g-00b", 37, 6), new("g-00b", 114, 6), new("g-00b", 127, 6), new("g-01", 66, 6), new("g-01", 279, 6),
            new("g-01", 342, 6), new("g-03", 1504, 6),
        ];
        IReadOnlyCollection<string> BerriesOf(SegmentRule rule) => rule.Berries == null ? null
            : BerrySets.Resolve(rule.Berries, summit, name => name == "2500 M" ? 5 : name == "3000 M" ? 6 : -1);

        SegmentRule plain = Rule("7a", "2500m"), full = Rule("7a", "ARB 2500m-full"), first = Rule("7a", "ARB 2500m-1"),
            fc = Rule("7a", "FC 2500m");
        Assert.Equal(7, BerriesOf(first).Count);
        Assert.Same(full, Shown(plain, full, first));
        Assert.Same(full, Shown(first, full, plain));
        Assert.Same(first, Shown(plain, first));
        Assert.Same(fc, Shown(plain, full, first, fc));
        Assert.Same(Rule("7a", "FC 3000m"),
            Shown(Rule("7a", "3000m"), Rule("7a", "Nodraft"), Rule("7a", "ARB 3000m"), Rule("7a", "ARB Nodraft"), Rule("7a", "FC 3000m")));
        Assert.Same(Rule("7a", "ARB 3000m"),
            Shown(Rule("7a", "3000m"), Rule("7a", "Nodraft"), Rule("7a", "ARB Nodraft"), Rule("7a", "ARB 3000m")));

        SegmentRule Shown(params SegmentRule[] met) => met[Specificity.MostSpecific(met, chapterRun: false, BerriesOf)];
    }
}
