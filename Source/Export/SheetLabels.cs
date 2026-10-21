namespace Celeste.Mod.SpeedrunSheet;

/// A writable row of the personal practice sheet, identified by its tab and its
/// two label columns. Chapter is empty on the Farewell tab, which has none.
public readonly record struct SheetRowRef(string Tab, string Chapter, string Cp);

/// The entry tabs' names, as the sheet spells them.
public static class SheetLabels {
    public const string TabASides = "A Sides";
    public const string TabBCSides = "B+C Sides";
    public const string TabFarewell = "Farewell";
    public const string TabArbFullClear = "ARB/Full Clear";
}
