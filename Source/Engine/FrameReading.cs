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
    // any other Respawn: Speed Run Tool's teleport, into another room or to
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
