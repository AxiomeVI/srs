using System.Collections.Generic;
using System.Linq;

namespace Celeste.Mod.SpeedrunSheet;

/// The rooms of the area being played, for the rules of its scope. Built from
/// AreaData and the English dialog, so on the game thread only, and cached per
/// area: neither changes while it is played.
internal sealed class RoomMap : IRoomMap {
    private static RoomMap current;

    private readonly AreaKey area;
    private readonly string scope;
    private readonly CheckpointRooms rooms;
    private readonly HashSet<string> warned = [];
    private readonly List<MapBerry> berries = [];
    private readonly Dictionary<int, IReadOnlyCollection<string>> berriesByRule = [];

    private RoomMap(Session session) {
        area = session.Area;
        scope = SegmentAutoDetect.ScopeOf(session);
        // a map the sheet does not cover resolves no checkpoint name
        CheckpointData[] checkpoints = scope == null ? []
            : AreaData.Get(session.Area)?.Mode[(int)session.Area.Mode]?.Checkpoints ?? [];
        rooms = new CheckpointRooms(scope, session.MapData?.StartLevel()?.Name,
            checkpoints.Select(checkpoint => new CheckpointRoom(EnglishName(checkpoint), checkpoint.Level)).ToArray());

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
                && !(rooms.StartRoomOf(rule) is { } start && session.MapData.Get(start) is { HasCheckpoint: true })) {
                Logger.Log(LogLevel.Warn, "srs", $"room map: no checkpoint in the first room of {rule.Name} in {scope}: the row never opens");
            }

            if (rule.Setup == StartSetup.HeartRespawn
                && !(rooms.StartRoomOf(rule) is { } heartRoom && session.MapData.Get(heartRoom) is { } heartData
                     && heartData.Triggers.Exists(trigger => trigger.Name == "changeRespawnTrigger"))) {
                Logger.Log(LogLevel.Warn, "srs", $"room map: no respawn trigger in the first room of {rule.Name} in {scope}: the row never opens");
            }

            if (rule.Berries != null) {
                IReadOnlyCollection<string> keys = BerrySets.Resolve(rule.Berries, berries, rooms.CheckpointId);
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

    public string StartRoomOf(SegmentRule rule) => rooms.StartRoomOf(rule);

    public string EndRoomOf(SegmentRule rule) {
        string end = rooms.EndRoomOf(rule, out bool noRoomAfter);
        if (noRoomAfter && warned.Add(rule.Anchor)) {
            Logger.Log(LogLevel.Warn, "srs", $"room map: no start room for the checkpoint after {rule.Anchor} in {scope}");
        }

        return end;
    }

    public string EntryRoomOf(SegmentRule rule) => rooms.EntryRoomOf(rule);

    public IReadOnlyCollection<string> BerriesOf(SegmentRule rule) =>
        rule.Scope == scope ? berriesByRule.GetValueOrDefault(rule.Order) : null;

    // CheckpointData.Name is a dialog key, translated: the tables must not
    // depend on the player's language
    private static string EnglishName(CheckpointData checkpoint) =>
        Dialog.Clean(checkpoint.Name, Dialog.Languages["english"]);
}
