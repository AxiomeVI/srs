using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// The row table and the room tables (start-room overrides, split checkpoints,
// untimed heads and tails) address checkpoints by name, and nothing else forces
// them to agree with each other or with the sheet: a rename on either side
// degrades silently.
public class SheetConsistencyTests {
    // every raw (tab, chapter, checkpoint) row present in the exported tabs,
    // each tab parsed under the implicit chapter the importer gives it. The
    // tab is in the key: tabs share labels, and a row removed from one must
    // not hide behind another's
    private static readonly HashSet<(StandardsTab, string, string)> RawRows = [
        .. StandardsTabs.All.SelectMany(tab => BlocksOf(tab.Tab)
            .SelectMany(block => block.Segments)
            .Select(segment => (tab.Tab, segment.Chapter, segment.Name)))
    ];

    // a table key no row is anchored on never applies
    private static readonly HashSet<(string, string)> Anchors =
        [.. SheetRows.All.Select(row => (row.Scope, row.Anchor))];

    [Fact]
    public void EveryStartSetupKeyIsAnImportedAnchor() {
        Assert.All(SegmentAutoDetect.WakeUpSpawns.Keys, key => Assert.Contains(key, Anchors));
        Assert.All(SegmentAutoDetect.SpawnOffsets.Keys, key => Assert.Contains(key, Anchors));
        Assert.All(SegmentAutoDetect.HeartRespawns, key => Assert.Contains(key, Anchors));
    }

    // a row left without an entry room would never open, and the segment before
    // it would never close with a time. A chapter's Start and 7A's start have
    // none: they open on a restart or at the end of the launch, never on an
    // entry
    [Fact]
    public void EveryNextRoomAndWakeUpRowHasAnEntryRoom() {
        List<string> wrong = SegmentRules.All
            .Where(rule => rule.Setup != StartSetup.MapSpawn)
            .Where(rule => SegmentAutoDetect.EntryRooms.ContainsKey((rule.Scope, rule.Anchor))
                           != (rule.Setup == StartSetup.NextRoom
                               || SegmentAutoDetect.WakeUpSpawns.ContainsKey((rule.Scope, rule.Anchor))))
            .Select(rule => $"{rule.Scope}/{rule.Name}")
            .ToList();
        Assert.Empty(wrong);

        Assert.All(SegmentAutoDetect.EntryRooms.Keys, key => Assert.Contains(key, Anchors));
        Assert.DoesNotContain(SegmentAutoDetect.EntryRooms.Keys, key => key.GameName == "Start");
    }

    // of the two rooms Cliff Face's and Rescue's first rooms can be entered
    // from, only the imported routes' one counts; and the rooms a cutscene
    // brings the player into are entered from the room it starts in
    [Theory]
    [InlineData("4a", "Cliff Face", "c-08")]
    [InlineData("5a", "Rescue", "d-20")]
    [InlineData("2a", "Awake", "13")]
    [InlineData("5a", "Unravelling", "void")]
    [InlineData("5b", "Through the Mirror", "b-09")]
    [InlineData("6a", "Lake", "start")]
    public void TheEntryRoomsThatAreAChoice(string scope, string anchor, string room) {
        Assert.Equal(room, SegmentAutoDetect.EntryRooms[(scope, anchor)]);
    }

    // a Current Room start that is not a chapter's "Start" is a WakeUpSpawns
    // key, and opens on a restart only from that spawn
    [Fact]
    public void EveryWakeUpStartHasASpawn() {
        List<(string, string)> wakeUps = SegmentRules.All
            .Where(rule => rule.Setup == StartSetup.CurrentRoom && rule.Anchor != "Start")
            .Select(rule => (rule.Scope, rule.Anchor))
            .Distinct().Order().ToList();
        List<(string, string)> spawns = SegmentAutoDetect.WakeUpSpawns.Keys.Select(key => (key.Scope, key.GameName)).Order().ToList();
        Assert.Equal(wakeUps, spawns);
    }

    // the allowlist still matches the sheet. This is the test that catches a
    // rename on the sheet's side: refresh Tests/Fixtures/*.csv, and any row the
    // mod expects that no longer exists shows up here by name
    [Fact]
    public void EveryImportedRowStillExistsInTheSheet() {
        List<(StandardsTab, string, string)> missing = SheetRows.All
            .Select(row => (row.Tab, row.SheetChapter, row.Label))
            .Where(key => !RawRows.Contains(key))
            .ToList();

        Assert.Empty(missing);
    }

    // the same check, run by the mod itself on every download, where no
    // one has to refresh a fixture for it to fire
    [Fact]
    public void TheFixturesMissNoImportedRow() {
        Assert.Empty(Fixtures.Parsed.MissingRows);
    }

    // a 5A row the sheet spells otherwise than srs imports it
    [Fact]
    public void ARowTheSheetRenamedIsReportedByItsImportKey() {
        string renamed = Fixtures.ASides.Replace(",Unravelling,", ",Unraveling,");

        SheetData data = SheetData.Parse(new Dictionary<StandardsTab, string>(Fixtures.ByTab) {
            [StandardsTab.ASides] = renamed,
        });

        Assert.Equal([("5a CP", "Unravelling")], data.MissingRows);
    }

    // an override keyed on a checkpoint no row anchors would never fire, and the
    // previous segment would keep ending at the checkpoint's own room
    [Fact]
    public void EveryStartRoomOverrideTargetsAnAnchoredCheckpoint() {
        Assert.All(SegmentAutoDetect.StartRoomOverrides.Keys, key => Assert.Contains(key, Anchors));
    }

    [Fact]
    public void EverySplitCheckpointHasBothHalvesAnchored() {
        foreach (KeyValuePair<(string Scope, string GameName), string> entry in SegmentAutoDetect.SplitCheckpoints) {
            Assert.Contains(entry.Key, Anchors);
            Assert.Contains((entry.Key.Scope, entry.Value), Anchors);
            Assert.Contains((entry.Key.Scope, entry.Value), SegmentAutoDetect.StartRoomOverrides.Keys);
        }

        Assert.Equal("HotM Horizontal", SegmentAutoDetect.SplitCheckpoints[("8a", "Heart of the Mountain")]);
        Assert.Equal("d-08", SegmentAutoDetect.StartRoomOverrides[("8a", "HotM Horizontal")]);
    }

    // an entry keyed on a checkpoint no row anchors would never apply, and the
    // segment would read several tiers too high, in silence. The values are
    // pinned because the game cannot derive them
    [Fact]
    public void EveryUntimedHeadTargetsAKnownCheckpointAndKeepsItsValue() {
        Assert.All(SegmentAutoDetect.UntimedSegmentHead.Keys, key => Assert.Contains(key, Anchors));
        Assert.All(SegmentAutoDetect.UntimedSegmentTail.Keys, key => Assert.Contains(key, Anchors));
        Assert.All(SegmentAutoDetect.AfterLaunchStarts, key => Assert.Contains(key, Anchors));
        Assert.Equal(TimeSpan.FromMilliseconds(5508), SegmentAutoDetect.UntimedSegmentHead[("7a", "Start")]);
        Assert.Equal(TimeSpan.FromMilliseconds(1037), SegmentAutoDetect.UntimedSegmentHead[("Prologue", "Start")]);
        Assert.Equal(TimeSpan.FromMilliseconds(544), SegmentAutoDetect.UntimedSegmentTail[("Prologue", "Start")]);
    }

    // (scope, name) is the address the row table and the exports use, so two
    // sheet rows must never collapse onto one
    [Fact]
    public void ImportedCheckpointsAreUniquelyAddressed() {
        List<(string Chapter, string Name)> duplicates = Fixtures.Imported
            .GroupBy(segment => (segment.Chapter, segment.Name))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    // "Start" exists in nearly every scope: the scope is part of the address
    [Fact]
    public void FindAddressesASegmentByScopeAndName() {
        SheetBlock block = Fixtures.Parsed.Block;

        Assert.Equal("2a", block.Find("2a", "Start")?.Chapter);
        Assert.Null(block.Find("2a", "Hollows"));
    }

    // canary for a restructured sheet: not a full snapshot of the data, just
    // the shape of what gets imported
    [Fact]
    public void ImportsTheExpectedCheckpointsInRouteOrder() {
        Assert.Equal(SheetRows.All.Length, Fixtures.Parsed.SegmentCount);
        Assert.Equal(
            ["Prologue", "1a", "2a", "3a", "4a", "5a", "6a", "7a", "8a",
             "1b", "2b", "3b", "4b", "5b", "6b", "7b", "8b",
             "1c", "2c", "3c", "4c", "5c", "6c", "7c", "8c", "Farewell"],
            Fixtures.Parsed.Block.Segments.Select(segment => segment.Chapter).Distinct());
    }

    // the tier columns are read positionally from the header row, so their
    // names and order are part of the contract with TierComparison's palette
    [Fact]
    public void ReadsTheTierColumnsFromTheHeader() {
        List<string> columns = Fixtures.Parsed.Block.Columns;

        Assert.Equal("Hidden", columns[0]);
        Assert.Equal("WR", columns[1]);
        Assert.Contains("Gold", columns);
        Assert.Equal("Unranked", columns[^1]);
        Assert.All(Fixtures.Imported, segment => Assert.Equal(columns.Count, segment.Times.Count));
    }

    private static List<SheetBlock> BlocksOf(StandardsTab tab) =>
        SheetData.ParseBlocks(Fixtures.ByTab[tab], StandardsTabs.Of(tab).ImplicitChapter);

    // the merged block takes one tab's header for every row, so every block
    // of every tab has to carry the same columns
    [Fact]
    public void EveryBlockOfEveryTabHasTheSameColumns() {
        List<string> columns = Fixtures.Parsed.Block.Columns;

        Assert.All(StandardsTabs.All, tab => {
            List<SheetBlock> blocks = BlocksOf(tab.Tab);
            Assert.NotEmpty(blocks);
            Assert.All(blocks, block => Assert.Equal(columns, block.Columns));
        });
    }

    [Fact]
    public void TheCSidesTabIsOneBlockOfChapters() {
        SheetBlock block = Assert.Single(BlocksOf(StandardsTab.CSides));

        Assert.False(block.HasCheckpoints);
        Assert.Equal(
            ["1c", "2c", "3c", "4c", "5c", "6c", "7c", "8c"],
            block.Segments.Select(segment => segment.Name));
        Assert.All(block.Segments, segment => Assert.Equal(segment.Name, segment.Chapter));
    }

    // checkpoint rows first, then a second header with no Checkpoint column
    // over one row per chapter
    [Theory]
    [InlineData(StandardsTab.Arb)]
    [InlineData(StandardsTab.Fc)]
    public void TheArbAndFcTabsAreCheckpointsThenChapters(StandardsTab tab) {
        List<SheetBlock> blocks = BlocksOf(tab);

        Assert.Equal(2, blocks.Count);
        Assert.True(blocks[0].HasCheckpoints);
        Assert.NotEmpty(blocks[0].Segments);
        Assert.False(blocks[1].HasCheckpoints);
        Assert.NotEmpty(blocks[1].Segments);
    }

    // a trait keyed on a name no row has never applies, and its row would be
    // recorded without its heart or from a walk-in
    [Fact]
    public void EveryRowTraitNamesAnImportedRow() {
        Assert.All(RowTraits.All.Keys, key =>
            Assert.True(SheetRows.TryFind(key.Scope, key.Name, out _), $"{key.Scope}/{key.Name}"));
    }

    // the spawn is read from the checkpoint entity of the anchor's own room:
    // a chapter's Start, or an anchor timed from another room, has none there
    [Fact]
    public void EveryMapSpawnRowIsAnchoredOnACheckpointsOwnRoom() {
        Assert.All(SegmentRules.All.Where(rule => rule.Setup == StartSetup.MapSpawn), rule => {
            Assert.NotEqual("Start", rule.Anchor);
            Assert.DoesNotContain((rule.Scope, rule.Anchor), SegmentAutoDetect.StartRoomOverrides.Keys);
            Assert.DoesNotContain(rule.Anchor, SegmentAutoDetect.SplitCheckpoints.Values);
        });
    }

    [Fact]
    public void TheBerryCliffFaceHasItsOwnEntryRoom() {
        Assert.Equal("c-10", TestRules.Find("4a", "ARB Cliff Face").EntryRoom);
        Assert.Null(TestRules.Find("4a", "Cliff Face").EntryRoom);
        Assert.Null(TestRules.Find("4a", "ARB Old Trail").EntryRoom);
        Assert.All(SegmentRules.All.Where(rule => rule.EntryRoom != null),
            rule => Assert.Equal(StartSetup.NextRoom, rule.Setup));
    }

    // a segment ends on the entry of its own route's next row: with one way
    // into every other start room, that changes nothing but Cliff Face
    [Fact]
    public void OnlyCliffFaceHasTwoWaysIn() {
        var severalWaysIn = SegmentRules.All
            .Where(rule => rule.Setup != StartSetup.MapSpawn)
            .Select(rule => (rule.Scope, rule.Anchor,
                Entry: rule.EntryRoom ?? SegmentAutoDetect.EntryRooms.GetValueOrDefault((rule.Scope, rule.Anchor))))
            .Where(way => way.Entry != null)
            .GroupBy(way => (way.Scope, way.Anchor))
            .Where(group => group.Select(way => way.Entry).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Equal([("4a", "Cliff Face")], severalWaysIn);
    }

    // the chapter row and the checkpoint rows of one chapter cell are read apart
    [Fact]
    public void TheBerryTabsChapterRowIsReadBesideItsCheckpointRows() {
        SheetBlock block = Fixtures.Parsed.Block;
        int wr = block.Columns.IndexOf("WR");

        SheetSegment[] rows = [
            block.Find("2a", "ARB IL"), block.Find("2a", "ARB Start"),
            block.Find("4a", "ARB Cliff Face (from RTM)"), block.Find("4a", "ARB Cliff Face"),
        ];

        Assert.All(rows, row => {
            Assert.NotNull(row);
            Assert.NotNull(row.Times[wr]);
        });
        Assert.Equal(4, rows.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.NotEqual(rows[0].Times[wr], rows[1].Times[wr]);
    }

    // the name above the timer is the scope then the name: a name that
    // carried its scope would read "8a 8a Start"
    [Fact]
    public void NoNameRepeatsItsScope() {
        Assert.All(SheetRows.All, row => {
            Assert.False(row.Name.StartsWith(row.Scope + " ", StringComparison.Ordinal), row.Name);
        });
    }

    // the complete list of what the sheet holds and srs leaves out: a row the
    // sheet adds, or one that stops being imported, fails here by name. Rows are
    // every segment of every block of every tab (RawRows), minus SheetRows.All.
    // "Wake Up" is out for good: its rows time an animation of fixed length
    [Fact]
    public void LeavesTheNotYetSupportedRowsOut() {
        HashSet<(StandardsTab, string, string)> imported = SheetRows.All
            .Select(row => (row.Tab, row.SheetChapter, row.Label)).ToHashSet();
        string berry = "\U0001F353";
        // tab/chapter/label
        string[] expected = [
            $"Arb/2a {berry}/Wake Up",
            $"Arb/5a {berry}/Wake Up",
            "ASides/2a CP/Wake Up",
            "ASides/5a CP/Wake Up",
            "BSides/5b/Wake Up",
        ];
        string[] left = [.. RawRows.Where(row => !imported.Contains(row))
            .Select(row => $"{row.Item1}/{row.Item2}/{row.Item3}").Order(StringComparer.Ordinal)];

        Assert.Equal(expected.Order(StringComparer.Ordinal), left);
    }

    // a Heart Respawn row has its room in the tables, and no entry room: it
    // never opens on a walk-in
    [Fact]
    public void EveryHeartRespawnStartHasARoomAndNoEntry() {
        List<(string, string)> starts = SegmentRules.All.Where(rule => rule.Setup == StartSetup.HeartRespawn)
            .Select(rule => (rule.Scope, rule.Anchor)).Distinct().Order().ToList();

        Assert.Equal(starts, SegmentAutoDetect.HeartRespawns.Select(key => (key.Scope, key.GameName)).Order().ToList());
        Assert.All(SegmentAutoDetect.HeartRespawns, key => {
            Assert.Contains(key, SegmentAutoDetect.StartRoomOverrides.Keys);
            Assert.DoesNotContain(key, SegmentAutoDetect.EntryRooms.Keys);
        });
        Assert.All(SegmentRules.All.Where(rule => rule.Setup == StartSetup.HeartRespawn),
            rule => Assert.Equal(EndKind.ReturnToMap, rule.End));
    }
}
