using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet;

/// The rooms of the area being played, for the rules of its scope. Built from
/// AreaData and the English dialog, so on the game thread only, and cached per
/// area: neither changes while it is played.
internal sealed class RoomMap : IRoomMap {
    private static RoomMap current;

    private readonly AreaKey area;
    private readonly string scope;
    private readonly string firstRoom;
    private readonly CheckpointData[] checkpoints;
    private readonly HashSet<string> warned = [];
    private readonly List<MapBerry> berries = [];
    private readonly Dictionary<int, IReadOnlyCollection<string>> berriesByRule = [];

    private RoomMap(Session session) {
        area = session.Area;
        scope = SegmentAutoDetect.ScopeOf(session);
        firstRoom = session.MapData?.StartLevel()?.Name;
        checkpoints = AreaData.Get(session.Area)?.Mode[(int)session.Area.Mode]?.Checkpoints ?? [];

        if (session.MapData == null) {
            return;
        }

        // a golden is another entity, and a moon berry says so
        foreach (LevelData level in session.MapData.Levels) {
            foreach (EntityData entity in level.Entities) {
                if (entity.Name == "strawberry") {
                    berries.Add(new MapBerry(level.Name, entity.ID, entity.Int("checkpointID"), entity.Bool("moon")));
                }
            }
        }

        // a misspelt entry room never matches, and its segment never opens
        foreach (KeyValuePair<(string Scope, string GameName), string> entry in SegmentAutoDetect.EntryRooms) {
            if (entry.Key.Scope == scope && session.MapData.Get(entry.Value) == null) {
                Logger.Log(LogLevel.Warn, "srs", $"room map: the entry room {entry.Value} of {entry.Key.GameName} is not a room of {scope}");
            }
        }

        // said on entering the chapter, not at the end of a run it costs
        foreach (SegmentRule rule in SegmentRules.All) {
            if (rule.Scope != scope) {
                continue;
            }

            if (rule.EntryRoom != null && session.MapData.Get(rule.EntryRoom) == null) {
                Logger.Log(LogLevel.Warn, "srs", $"room map: the entry room {rule.EntryRoom} of {rule.Name} is not a room of {scope}");
            }

            if (rule.Setup == StartSetup.MapSpawn
                && !(StartRoomOf(rule) is { } start && session.MapData.Get(start) is { HasCheckpoint: true })) {
                Logger.Log(LogLevel.Warn, "srs", $"room map: no checkpoint in the first room of {rule.Name} in {scope}: the row never opens");
            }

            if (rule.Setup == StartSetup.HeartRespawn
                && !(StartRoomOf(rule) is { } heartRoom && session.MapData.Get(heartRoom) is { } heartData
                     && heartData.Triggers.Exists(trigger => trigger.Name == "changeRespawnTrigger"))) {
                Logger.Log(LogLevel.Warn, "srs", $"room map: no respawn trigger in the first room of {rule.Name} in {scope}: the row never opens");
            }

            if (rule.Berries != null) {
                IReadOnlyCollection<string> keys = BerrySets.Resolve(rule.Berries, berries, CheckpointId);
                berriesByRule[rule.Order] = keys;
                if (keys == null) {
                    Logger.Log(LogLevel.Warn, "srs", $"room map: the berries of {rule.Name} are not in the map of {scope}: the row is never recorded");
                }
            }
        }
    }

    public static RoomMap For(Session session) {
        // == and not Equals: AreaKey.Equals(object) always returns false
        if (current == null || current.area != session.Area) {
            current = new RoomMap(session);
        }

        return current;
    }

    public string StartRoomOf(SegmentRule rule) =>
        rule.Scope == scope ? StartRoomOf(rule.Anchor) : null;

    // where a segment ends: exactly where the next one starts, so two segments
    // never overlap. Null = no next checkpoint, the chapter's end ends it
    public string EndRoomOf(SegmentRule rule) {
        if (rule.Scope != scope || checkpoints.Length == 0) {
            return null;
        }

        // a checkpoint the sheet cuts ends where its next piece starts
        if (SegmentAutoDetect.SplitCheckpoints.TryGetValue((scope, rule.Anchor), out string nextPiece)) {
            return StartRoomOf(nextPiece);
        }

        // CheckpointData only lists the non-start checkpoints
        if (rule.Anchor == "Start") {
            return StartRoomOf(EnglishName(checkpoints[0]));
        }

        for (int i = 0; i < checkpoints.Length - 1; i++) {
            if (EnglishName(checkpoints[i]) == rule.Anchor) {
                string next = StartRoomOf(EnglishName(checkpoints[i + 1]));
                if (next == null && warned.Add(rule.Anchor)) {
                    Logger.Log(LogLevel.Warn, "srs", $"room map: no start room for the checkpoint after {rule.Anchor} in {scope}");
                }

                return next;
            }
        }

        return null;
    }

    // a map-spawn or heart-respawn row is never entered; a row with an entry
    // of its own keeps it (4A's berry Cliff Face), the others take their anchor's
    public string EntryRoomOf(SegmentRule rule) {
        if (rule.Scope != scope || rule.Setup is StartSetup.MapSpawn or StartSetup.HeartRespawn) {
            return null;
        }

        return rule.EntryRoom ?? SegmentAutoDetect.EntryRooms.GetValueOrDefault((scope, rule.Anchor));
    }

    public IReadOnlyCollection<string> BerriesOf(SegmentRule rule) =>
        rule.Scope == scope ? berriesByRule.GetValueOrDefault(rule.Order) : null;

    // the map's checkpointID: 0 before the first checkpoint, then the
    // checkpoints in order
    private int CheckpointId(string gameName) {
        if (gameName == "Start") {
            return 0;
        }

        for (int i = 0; i < checkpoints.Length; i++) {
            if (EnglishName(checkpoints[i]) == gameName) {
                return i + 1;
            }
        }

        return -1;
    }

    // the override when the sheet does not start the segment at the
    // checkpoint's own room (2A Awake, 7A Start, a virtual checkpoint), the map's
    // first room for "Start", the checkpoint's room otherwise
    private string StartRoomOf(string gameName) {
        if (SegmentAutoDetect.StartRoomOverrides.TryGetValue((scope, gameName), out string overridden)) {
            return overridden;
        }

        if (gameName == "Start") {
            return firstRoom;
        }

        foreach (CheckpointData checkpoint in checkpoints) {
            if (EnglishName(checkpoint) == gameName) {
                return checkpoint.Level;
            }
        }

        return null;
    }

    // CheckpointData.Name is a dialog key, translated: the tables must not
    // depend on the player's language
    private static string EnglishName(CheckpointData checkpoint) =>
        Dialog.Clean(checkpoint.Name, Dialog.Languages["english"]);
}
