using System;
using Monocle;

namespace Celeste.Mod.CelesteHotkeys;

/// <summary>The row in a mod's Mod Options section that opens its remap screen.</summary>
internal static class HotkeyMenu {
    /// <summary>
    ///     A button that opens <see cref="KeybindScreen{TSettings}"/> over <paramref name="menu"/> and
    ///     hands focus back when it closes.
    /// </summary>
    // ⚠️ THE ONLY WAY A [SettingIgnore] BINDING CAN EVER BE BOUND. Every binding this module reads is
    // hidden from Everest's generated key-config rows, so if this row is not in the section — or is
    // left outside the range a master switch shows — the hotkeys are unbindable with nothing on screen
    // to say so. Put it last, at the root of the section, where a player looks for key bindings.
    internal static TextMenu.Button OpenButton<TSettings>(
        TextMenu menu, HotkeySet<TSettings> hotkeys, KeybindScreenText text, Action save)
        where TSettings : class {
        TextMenu.Button button = new(Dialog.Clean(text.HeaderId));
        button.Pressed(() => {
            menu.Focused = false;
            KeybindScreen<TSettings> screen = new(hotkeys, text, save) { OnClose = () => menu.Focused = true };
            // ⚠️ The scene is CAPTURED, not read again at end of frame. Engine.Scene can be replaced
            // between the two, and the lambda would then flush the entity lists of whatever scene came
            // after while this one kept the screen queued.
            Scene scene = Engine.Scene;
            scene.Add(screen);
            scene.OnEndOfFrame += () => scene.Entities.UpdateLists();
        });
        return button;
    }
}
