namespace Celeste.Mod.SpeedrunSheet;

/// Where the player last appeared and whether they have moved since: a load
/// opens a Current Room segment only for one who has not. Positions are exact,
/// sub-pixel remainder included: a tap shorter than a pixel is a move.
internal readonly record struct Stillness(float X, float Y, bool Moved) {
    /// One fed frame, after the update. A disturbance (loader, teleport, death,
    /// srs switched off) comes before an appearance on the same frame, and the
    /// move check comes last. No player (at is null): no appearance, no move.
    public Stillness After(bool disturbed, bool appeared, (float X, float Y)? at) {
        Stillness next = disturbed ? this with { Moved = true } : this;
        if (at is not { } position) {
            return next;
        }

        if (appeared) {
            return new Stillness(position.X, position.Y, false);
        }

        return next.Moved || (position.X == next.X && position.Y == next.Y) ? next : next with { Moved = true };
    }
}
