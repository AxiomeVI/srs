namespace Celeste.Mod.SpeedrunSheet;

/// How the room was loaded since the last fed frame, read from LoadLevel.
/// Ordered weakest first: when several loads land in one gap, the strongest
/// wins, and a gap below WalkIn walked into nothing.
public enum RoomLoad {
    None,
    // Respawn from Level.Reload: a death
    Respawn,
    // any intro but Respawn: a transition, or a room change the game makes
    // in a cutscene
    WalkIn,
    // any other Respawn: SpeedrunTool's teleport, into another room or to
    // a summit flag of this one
    Teleport,
    // a level from the loader: entering a chapter, a restart, console load,
    // the debug map
    Loader,
}

internal static class RoomLoads {
    /// One LoadLevel call. Level.Reload is the game's only LoadLevel(Respawn).
    public static RoomLoad Of(bool fromLoader, bool respawnIntro, bool reloading) =>
        fromLoader ? RoomLoad.Loader
        : !respawnIntro ? RoomLoad.WalkIn
        : reloading ? RoomLoad.Respawn
        : RoomLoad.Teleport;

    /// The gap's kind once one more load landed in it.
    public static RoomLoad Add(RoomLoad gap, RoomLoad kind) => kind > gap ? kind : gap;
}

/// The player as a frame's decisions test them: the state machine reduced to
/// the flags the adapter reads, and the exact position, remainder included.
/// Present false: no player, every flag false.
internal readonly record struct PlayerSide(bool Present, bool Control, bool Launching, bool Fall, bool Arriving,
    float X, float Y);

/// RunWatcher.Saved's four fields, as a copy: the stamp written on every fed
/// frame, the room the next walk-in comes from, where the player last
/// appeared and whether they have moved since.
internal readonly record struct SavedValues(long Stamp, string From, float SpawnX, float SpawnY, bool Moved);

/// One fed frame as the adapter read it: before the game's update, after it,
/// and what the savestate-registered fields held on each side.
internal readonly record struct FrameReading(
    long Before, string RoomBefore, PlayerSide PlayerBefore, bool StoppedBefore, bool CompletedBefore,
    // a player load's own wipe, not the one a load restored with the level
    bool LoadWiping,
    SavedValues SavedBefore, bool TasBefore,
    long After, string Room, string Scope, PlayerSide Player, bool Stopped, bool Completed,
    bool InCredits, (int Id, int Mode) Area, EndState End,
    SavedValues SavedAfter, bool TasAfter);
