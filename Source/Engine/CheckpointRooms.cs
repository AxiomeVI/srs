using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet;

/// A checkpoint as the lookups read it: its English name and the room it starts in.
internal readonly record struct CheckpointRoom(string Name, string Room);

/// Where a scope's segments start, end and are entered, from its ordered
/// checkpoints (the non-start ones, as CheckpointData lists them) and the map's
/// first room. Game-free; RoomMap builds it from the game's data.
internal sealed class CheckpointRooms(string scope, string firstRoom, IReadOnlyList<CheckpointRoom> checkpoints) {
    public string StartRoomOf(SegmentRule rule) =>
        rule.Scope == scope ? StartRoomOf(rule.Anchor) : null;

    // where a segment ends: exactly where the next one starts, so two segments
    // never overlap. Null = no next checkpoint, the chapter's end ends it.
    // noRoomAfter: the anchor has a next checkpoint that gives no start room
    public string EndRoomOf(SegmentRule rule, out bool noRoomAfter) {
        noRoomAfter = false;
        if (rule.Scope != scope || checkpoints.Count == 0) {
            return null;
        }

        // a checkpoint the sheet cuts ends where its next piece starts
        if (SegmentAutoDetect.SplitCheckpoints.TryGetValue((scope, rule.Anchor), out string nextPiece)) {
            return StartRoomOf(nextPiece);
        }

        // the list holds only the non-start checkpoints
        if (rule.Anchor == "Start") {
            return StartRoomOf(checkpoints[0].Name);
        }

        for (int i = 0; i < checkpoints.Count - 1; i++) {
            if (checkpoints[i].Name == rule.Anchor) {
                string next = StartRoomOf(checkpoints[i + 1].Name);
                noRoomAfter = next == null;
                return next;
            }
        }

        return null;
    }

    // a map-spawn or heart-respawn row is never entered, unless a walk-in opens
    // it too (5A's berry Depths); a row with an entry
    // of its own keeps it (4A's berry Cliff Face), the others take their anchor's
    public string EntryRoomOf(SegmentRule rule) {
        if (rule.Scope != scope || !rule.OpensOnWalkIn) {
            return null;
        }

        return rule.EntryRoom ?? SegmentAutoDetect.EntryRooms.GetValueOrDefault((scope, rule.Anchor));
    }

    // the map's checkpointID: 0 before the first checkpoint, then the
    // checkpoints in order
    public int CheckpointId(string gameName) {
        if (gameName == "Start") {
            return 0;
        }

        for (int i = 0; i < checkpoints.Count; i++) {
            if (checkpoints[i].Name == gameName) {
                return i + 1;
            }
        }

        return -1;
    }

    // the override when the sheet does not start the segment at the
    // checkpoint's own room (2A Awake, 7A Start, a virtual checkpoint), the map's
    // first room for "Start", the checkpoint's room otherwise
    public string StartRoomOf(string gameName) {
        if (SegmentAutoDetect.StartRoomOverrides.TryGetValue((scope, gameName), out string overridden)) {
            return overridden;
        }

        if (gameName == "Start") {
            return firstRoom;
        }

        foreach (CheckpointRoom checkpoint in checkpoints) {
            if (checkpoint.Name == gameName) {
                return checkpoint.Room;
            }
        }

        return null;
    }
}
