using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework.Input;

namespace Celeste.Mod.CelesteHotkeys;

/// <summary>What the remap screen may record, and the rules it records by. No MInput, no Engine.</summary>
internal static class Bindable {
    /// <summary>Whether a key can be bound at all.</summary>
    // ⚠️ Keys.None is not "no key". FNA returns it for any key absent from its SDL→XNA table — most
    // of AZERTY's digit row, and ")" — and reports it held, so a binding carrying it fires on all of
    // them at once.
    //
    // F1, F2, F3 and F5 are Everest's debug keys, which is what a player reaches for when a mod
    // misbehaves; a hotkey on one would fire every time they did.
    //
    // Escape cancels recording, so it is never recorded on purpose. It could only reach a chord by going
    // down before the overlay starts reading input and still being held when it does.
    internal static bool IsBindable(Keys key) =>
        key != Keys.None && key != Keys.Escape
        && key != Keys.F1 && key != Keys.F2 && key != Keys.F3 && key != Keys.F5;

    /// <summary>The keys a chord may take from what is held this frame — <c>KeyboardState.GetPressedKeys()</c>.</summary>
    internal static List<Keys> BindableKeys(Keys[] held) {
        List<Keys> keys = new();
        foreach (Keys key in held) {
            if (IsBindable(key)) keys.Add(key);
        }
        return keys;
    }

    /// <summary>Whether a key that cannot be bound went down this frame. Say so rather than stay silent.</summary>
    /// <param name="held">Every key held now — <c>KeyboardState.GetPressedKeys()</c>.</param>
    /// <param name="newlyPressed">Whether a key went down this frame — <c>MInput.Keyboard.Pressed</c>.</param>
    // The edge test is a parameter so this stays a pure function. In the game it is
    // MInput.Keyboard.Pressed, which never reports Keys.None going down: an unmappable key is not
    // refused audibly, it is silent.
    internal static bool RefusedKeyWentDown(Keys[] held, Func<Keys, bool> newlyPressed) {
        foreach (Keys key in held) {
            if (!IsBindable(key) && newlyPressed(key)) return true;
        }
        return false;
    }

    /// <summary>
    ///     The modifiers: a binding lists them first, in this order, and a combo is blocked by one it does
    ///     not name.
    /// </summary>
    // Ctrl, Shift, Alt: the order shortcuts are written in.
    internal static readonly Keys[] Modifiers = {
        Keys.LeftControl, Keys.RightControl,
        Keys.LeftShift, Keys.RightShift,
        Keys.LeftAlt, Keys.RightAlt,
    };

    /// <summary>Where a modifier goes in a binding, or -1 for any other key.</summary>
    internal static int ModifierRank(Keys key) => Array.IndexOf(Modifiers, key);

    /// <summary>Every button the remap screen listens for, in the order it prefers them.</summary>
    // A stick direction is a button to FNA past a deadzone near vanilla's 0.25, and vanilla's own
    // controller screen records it. It is also movement: a hotkey on one alone fires as the player walks.
    internal static readonly Buttons[] RecordableButtons = {
        Buttons.A, Buttons.B, Buttons.X, Buttons.Y,
        Buttons.LeftShoulder, Buttons.RightShoulder,
        Buttons.LeftTrigger, Buttons.RightTrigger,
        Buttons.Back, Buttons.Start,
        Buttons.LeftStick, Buttons.RightStick,
        Buttons.DPadUp, Buttons.DPadDown, Buttons.DPadLeft, Buttons.DPadRight,
        Buttons.LeftThumbstickUp, Buttons.LeftThumbstickDown, Buttons.LeftThumbstickLeft, Buttons.LeftThumbstickRight,
        Buttons.RightThumbstickUp, Buttons.RightThumbstickDown, Buttons.RightThumbstickLeft, Buttons.RightThumbstickRight,
    };

    /// <summary>The recordable buttons held on a pad, in <see cref="RecordableButtons"/> order.</summary>
    internal static List<Buttons> HeldButtons(in GamePadState pad) {
        List<Buttons> buttons = new();
        foreach (Buttons button in RecordableButtons) {
            if (pad.IsButtonDown(button)) buttons.Add(button);
        }
        return buttons;
    }

    /// <summary>The most inputs one binding may hold. Every one must be held for the combo to fire.</summary>
    // More than a hand holds at once is not a combo anyone can press, and the cap also bounds how wide
    // the remap screen has to draw a binding.
    internal const int MaxComboInputs = 4;

    /// <summary>
    ///     Strips Keys.None from every ButtonBinding property of a settings object. Call once, after
    ///     the settings load.
    /// </summary>
    /// <returns>How many entries were removed.</returns>
    // Reflected, not driven by the keybind table: a property the table forgot is exactly the one
    // that would keep Keys.None. The enumeration matches Everest's own OnInputInitialize —
    // GetProperties(), CanRead, IsAssignableFrom — so the two cannot disagree about which properties
    // are bindings.
    //
    // A null binding is left null. Everest creates it later, in OnInputInitialize, and only reads
    // [DefaultButtonBinding] when it is still null. (That attribute never seeds Keys.None itself:
    // Everest skips a default key of 0. A settings file can carry it anyway, from an older build or a
    // hand edit.)
    internal static int Sanitize(object settings) {
        if (settings is null) return 0;

        int removed = 0;
        foreach (PropertyInfo property in settings.GetType().GetProperties()) {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
            if (!typeof(ButtonBinding).IsAssignableFrom(property.PropertyType)) continue;
            if (property.GetValue(settings) is not ButtonBinding binding) continue;

            binding.Keys ??= new List<Keys>();
            binding.Buttons ??= new List<Buttons>();
            removed += binding.Keys.RemoveAll(key => key == Keys.None);
        }
        return removed;
    }
}
