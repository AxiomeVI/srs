using Celeste.Mod.CelesteHotkeys;

namespace Celeste.Mod.SpeedrunSheet;

// the two rebindable hotkeys, as a CelesteHotkeys table polled once per
// frame. The module reads a binding as a combo, which Everest's key config
// screen cannot express: every binding is [SettingIgnore] and bound from the
// module's own screen (ModMenu)
public static class Hotkeys {
    internal static readonly Keybind<SrsSettings> OpenExportMenu =
        new("MODOPTIONS_SRS_OPENEXPORTMENU", nameof(SrsSettings.OpenExportMenu));

    internal static readonly Keybind<SrsSettings> PreviousSegment =
        new("MODOPTIONS_SRS_PREVIOUSSEGMENT", nameof(SrsSettings.PreviousSegment),
            "MODOPTIONS_SRS_PREVIOUSSEGMENT_DESC");

    internal static readonly Keybind<SrsSettings>[] All =
        [PreviousSegment, OpenExportMenu];

    internal static readonly HotkeySet<SrsSettings> Set = new(() => SrsModule.Settings, All);

    internal static readonly KeybindScreenText Text = new() {
        HeaderId = "SRS_KEYBINDS",
        ComboHintId = "SRS_KEYBIND_COMBO_HINT",
        PageComboHintId = "SRS_KEYBIND_PAGE_COMBO_HINT",
        ClearHintId = "SRS_KEYBIND_CLEAR_HINT",
        TimeoutFormatId = "SRS_KEYBIND_TIMEOUT",
    };

    private static bool levelPaused;

    public static void Load() {
        // Keys.None out of what is already on disk: FNA reports it held for
        // every key absent from its SDL -> XNA table, and a settings file
        // written through Everest's own rebind screen holds it unfiltered
        Bindable.Sanitize(SrsModule.Settings);

        // hook order: see SrsModule.Load
        On.Celeste.Level.Update += LevelOnUpdate;
    }

    public static void Unload() {
        On.Celeste.Level.Update -= LevelOnUpdate;
    }

    /// Whether the hotkey fired this frame. While the level is paused only the
    /// export screen's hotkey answers, and only behind that screen's own pause,
    /// so that it can close it. Polled through the pause, not skipped: a combo
    /// held across it must not fire when it ends.
    internal static bool Pressed(Keybind<SrsSettings> keybind) =>
        Set.Pressed(keybind)
        && (!levelPaused || (keybind == OpenExportMenu && ExportMenu.HoldsThePause));

    private static void LevelOnUpdate(On.Celeste.Level.orig_Update orig, Level self) {
        orig(self);

        levelPaused = self.Paused;
        // the module counts the master switch, the debug console, an
        // unfocused window and any open remap screen as a pause
        Set.Update(SrsModule.Settings.Enabled);
    }
}
