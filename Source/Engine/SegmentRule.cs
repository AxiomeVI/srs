using System;
using System.Collections.Generic;
using System.Linq;

namespace Celeste.Mod.SpeedrunSheet;

/// What a run must collect during a segment, or what ends it.
[Flags]
internal enum Collectibles {
    None = 0,
    Heart = 1,
    Cassette = 2,
    Gem = 4,
}

internal enum StartKind {
    // entering the start room, or a restart in it (RunTracker.Restart)
    Room,
    // once the launch (the intro jump) into the start room is over
    // (SegmentAutoDetect.AfterLaunchStarts)
    AfterLaunch,
}

internal enum StartSetup {
    // valid only when the first room is entered from the checkpoint before
    NextRoom,
    // valid from its start point: in a chain, or from a savestate at the start room's spawn
    CurrentRoom,
    // valid only from the checkpoint's own spawn: the checkpoint loaded from
    // the map, or a savestate there; never by walking in
    MapSpawn,
    // valid only from the respawn of a Save and Quit taken once the heart is
    // collected, or from a savestate there; never by walking in, nor on any
    // other restart or re-entry
    HeartRespawn,
}

internal enum EndKind {
    // entering the next start's room; with no next start, chapter time stopping
    // (the room map says which)
    NextStart,
    // chapter time stopping, whatever checkpoints come between: a whole chapter
    ChapterEnd,
    // the collect itself; with two collectibles, the later of the two
    Collect,
    // Restart Chapter, on the old session's last reading ("RC" rows)
    Restart,
    // a Return to Map and nothing else: not a room entry, not chapter time
    // stopping. A berry still following then is not collected
    ReturnToMap,
}

/// How a run of one imported row is detected. Written in game-checkpoint names,
/// never in rooms: rooms come from AreaData, which only the game thread reads.
internal sealed record SegmentRule(
    string Scope, string Name, string Anchor,
    StartKind Start, StartSetup Setup, EndKind End,
    Collectibles EndsOn, Collectibles Requires,
    // 1 or 2 for Farewell's DTS twins, read at the end (a whole chapter: at
    // the first checkpoint crossed); null = no dash rule
    int? Dashes,
    long HeadTicks, long TailTicks,
    // position in the sheet, the last tie-breaker
    int Order) {
    /// An IL or C-side row: timed from the chapter's start across its
    /// checkpoints, to the chapter's end or to its collect ("RTM").
    public bool ChapterRun { get; init; }

    /// The berries the row requires; null when it requires none.
    public BerrySet Berries { get; init; }

    public bool RequiresBerries => Berries != null;

    /// How many summit gems the row requires: its checkpoint's one, or the
    /// chapter's six.
    public int Gems { get; init; }

    /// The room the row's first room is entered from, where it is not its
    /// anchor's (SegmentAutoDetect.EntryRooms).
    public string EntryRoom { get; init; }

    /// A chapter row whose route returns to the map: a Return to Map and a
    /// chapter's end it does not finish on set it aside (RunTracker.Left).
    public bool SpansLeaves { get; init; }

    /// A row that starts after a wake-up (SegmentAutoDetect.WakeUpSpawns): how
    /// it was opened says whether a Save and Quit came before its run.
    public bool AfterWakeUp { get; init; }

    /// A row whose sheet dropdown cannot say it: its note is always Unknown.
    public bool NoSaveAndQuitNote { get; init; }

    /// Whether this rule asks for everything the other asks for, its
    /// berries included. berriesOf resolves a rule's set against the map
    /// (IRoomMap.BerriesOf); a set that did not resolve holds nothing and is
    /// held by nothing.
    public bool Contains(SegmentRule other, Func<SegmentRule, IReadOnlyCollection<string>> berriesOf) {
        if ((Requires & other.Requires) != other.Requires) {
            return false;
        }

        if (!other.RequiresBerries) {
            return true;
        }

        return RequiresBerries && berriesOf(this) is { } mine && berriesOf(other) is { } theirs
               && theirs.All(mine.Contains);
    }
}

internal static class Specificity {
    /// The index of the most specific rule among the chapter runs, or among
    /// the segments: the first in sheet order that no other asks more than.
    /// -1 when there is none of that kind. berriesOf: SegmentRule.Contains.
    public static int MostSpecific(IReadOnlyList<SegmentRule> rules, bool chapterRun,
        Func<SegmentRule, IReadOnlyCollection<string>> berriesOf) {
        int best = -1;
        for (int i = 0; i < rules.Count; i++) {
            if (rules[i].ChapterRun != chapterRun) {
                continue;
            }

            bool outdone = false;
            for (int j = 0; j < rules.Count && !outdone; j++) {
                outdone = rules[j].ChapterRun == chapterRun
                          && rules[j].Contains(rules[i], berriesOf) && !rules[i].Contains(rules[j], berriesOf);
            }

            if (!outdone && (best < 0 || rules[i].Order < rules[best].Order)) {
                best = i;
            }
        }

        return best;
    }
}
