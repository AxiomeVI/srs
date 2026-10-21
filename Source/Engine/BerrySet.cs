using System;
using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet;

/// A red or moon berry of the map: its room, its entity id, and the
/// checkpoint the map files it under (0 = before the first checkpoint).
internal readonly record struct MapBerry(string Room, int Id, int Checkpoint, bool Moon = false) {
    /// The game's EntityID key.
    public string Key => $"{Room}:{Id}";
}

/// Which berries a row requires, in map terms: every red berry of the
/// chapter, or those of the named game checkpoint ("Start" = before the
/// first) plus those of Rooms, less those of Except; or the chapter's moon
/// berry, which no red set holds.
internal sealed record BerrySet(
    string Checkpoint = null, string[] Rooms = null, string[] Except = null, bool Chapter = false,
    bool Moon = false) {
    public static readonly BerrySet WholeChapter = new(Chapter: true);
    public static readonly BerrySet MoonBerry = new(Moon: true);
}

internal static class BerrySets {
    /// The keys of the set's berries. Null when the map does not hold what
    /// the set names (an unknown checkpoint, a room with no berry, an
    /// exception that removes nothing) or when nothing is left: a set read
    /// wrong must never pass for a set met.
    public static IReadOnlyCollection<string> Resolve(BerrySet set, IReadOnlyList<MapBerry> berries,
        Func<string, int> checkpointOf) {
        int? checkpoint = null;
        if (set.Checkpoint != null) {
            checkpoint = checkpointOf(set.Checkpoint);
            if (checkpoint < 0) {
                return null;
            }
        }

        HashSet<string> added = [];
        HashSet<string> removed = [];
        List<string> keys = [];
        foreach (MapBerry berry in berries) {
            if (berry.Moon != set.Moon) {
                continue;
            }

            bool named = Has(set.Rooms, berry.Room);
            if (named) {
                added.Add(berry.Room);
            }

            if (!set.Chapter && !set.Moon && !named && berry.Checkpoint != checkpoint) {
                continue;
            }

            if (Has(set.Except, berry.Room)) {
                removed.Add(berry.Room);
                continue;
            }

            keys.Add(berry.Key);
        }

        bool everyRoomFound = added.Count == (set.Rooms?.Length ?? 0) && removed.Count == (set.Except?.Length ?? 0);
        return everyRoomFound && keys.Count > 0 ? keys : null;
    }

    private static bool Has(string[] rooms, string room) => rooms != null && Array.IndexOf(rooms, room) >= 0;
}
