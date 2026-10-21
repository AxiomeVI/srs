using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.CelesteHotkeys;

/// <summary>One frame of the input a hotkey is allowed to look at.</summary>
// ⚠️ MInput, on Input.Gamepad — never Keyboard.GetState() and never "the first connected pad".
// The remap screen records against Input.Gamepad, so a hotkey reading any other pad can be bound on
// one controller and watched on another, which is a hotkey that never fires. MInput also blanks the keyboard while the
// debug console is open and while the window is unfocused, which raw XNA state does not.
internal readonly record struct HotkeyInput(KeyboardState Keyboard, GamePadState Pad) {
    internal static HotkeyInput Current() => new(
        MInput.Keyboard.CurrentState,
        MInput.GamePads[Input.Gamepad].CurrentState
    );
}

/// <summary>
///     A combo over one ButtonBinding: every bound key held at once, or every bound button held at
///     once. Rising edge only — <see cref="Pressed"/> is true for exactly one frame per activation.
/// </summary>
// The keys and the buttons of a binding are alternatives: a keyboard combo OR a controller combo,
// never a mix. That is how the remap screen presents them (two sections from one table).
//
// ⚠️ This is NOT Everest's meaning of a ButtonBinding. Everest's VirtualButton, and its own key
// config screen, treat several bound keys as "any one of them". Every binding this module reads is
// [SettingIgnore], so Everest's screen never shows it and the two readings cannot meet.
internal sealed class ComboHotkey {
    private readonly Func<ButtonBinding> binding;
    private HotkeyInput last;

    /// <param name="binding">
    ///     Read every frame rather than captured, because a settings object can be replaced under a
    ///     running mod, and a captured binding would keep answering for the old object.
    /// </param>
    internal ComboHotkey(Func<ButtonBinding> binding) => this.binding = binding;

    public bool Pressed { get; private set; }

    // Which half of the binding fired, for SuppressSubsetPresses: a subset only counts on the device
    // both presses came from.
    private bool firedOnKeys;
    private bool firedOnPad;

    /// <summary>Advances one frame.</summary>
    /// <param name="paused">
    ///     True while something other than gameplay owns the input. The held state is still tracked,
    ///     so a combo held through the pause does not read as a fresh press the frame it ends.
    /// </param>
    public void Update(in HotkeyInput input, bool paused) {
        ButtonBinding current = paused ? null : binding();
        firedOnKeys = current is not null && FiresOnKeys(current.Keys, last.Keyboard, input.Keyboard);
        firedOnPad = current is not null && FiresOnPad(current.Buttons, last.Pad, input.Pad);
        Pressed = firedOnKeys || firedOnPad;
        last = input;
    }

    /// <summary>Swallows whatever is held right now: no edge until it is released and pressed again.</summary>
    public void Resync(in HotkeyInput input) => Update(input, paused: true);

    // Indexed loops rather than LINQ throughout: IsKeyDown and IsButtonDown are instance methods on
    // structs, so a method group passed to All boxes the state and allocates a delegate — per hotkey,
    // per device, every frame, for the whole session.
    //
    // A combo fires when it is satisfied now AND one of its own inputs went down this frame. Edge-
    // detecting the satisfied state alone is not enough: releasing an unbound modifier while the key
    // is held would flip the state to satisfied and fire at a moment the player did not choose.
    private static bool FiresOnKeys(List<Keys> keys, in KeyboardState previous, in KeyboardState now) =>
        KeysDown(keys, now) && AnyKeyWentDown(keys, previous, now);

    private static bool FiresOnPad(List<Buttons> buttons, in GamePadState previous, in GamePadState now) =>
        ButtonsDown(buttons, now) && AnyButtonWentDown(buttons, previous, now);

    private static bool AnyKeyWentDown(List<Keys> keys, in KeyboardState previous, in KeyboardState now) {
        for (int i = 0; i < keys.Count; i++) {
            if (keys[i] != Keys.None && now.IsKeyDown(keys[i]) && !previous.IsKeyDown(keys[i])) return true;
        }
        return false;
    }

    private static bool AnyButtonWentDown(List<Buttons> buttons, in GamePadState previous, in GamePadState now) {
        for (int i = 0; i < buttons.Count; i++) {
            if (now.IsButtonDown(buttons[i]) && !previous.IsButtonDown(buttons[i])) return true;
        }
        return false;
    }

    // ⚠️ Keys.None is skipped, not matched. FNA hands it back for any key absent from its SDL→XNA
    // table — most of AZERTY's digit row — and then reports it held, so a binding carrying it would
    // fire on all of them. The remap screen never records it and Bindable.Sanitize strips it on load;
    // this is the third line, for a settings file edited by hand.
    private static bool KeysDown(List<Keys> keys, in KeyboardState keyboard) {
        if (keys is null) return false;

        int real = 0;
        for (int i = 0; i < keys.Count; i++) {
            if (keys[i] == Keys.None) continue;
            if (!keyboard.IsKeyDown(keys[i])) return false;
            real++;
        }
        // "Every bound key is held" over an empty set is true.
        return real > 0 && NoUnboundModifier(keys, keyboard);
    }

    private static bool ButtonsDown(List<Buttons> buttons, in GamePadState pad) {
        if (buttons is null || buttons.Count == 0) return false;
        for (int i = 0; i < buttons.Count; i++) {
            if (!pad.IsButtonDown(buttons[i])) return false;
        }
        return true;
    }

    /// <summary>False when a modifier is held that this binding does not name.</summary>
    // What keeps F from firing on Ctrl+F — including when Ctrl+F belongs to another mod, which
    // SuppressSubsetPresses below cannot see.
    //
    // Modifiers only — never "nothing else is held". These hotkeys are pressed mid-run with movement,
    // jump and dash down, so a rule about every held key would switch them off in exactly the
    // situation they exist for.
    private static bool NoUnboundModifier(List<Keys> keys, in KeyboardState keyboard) {
        Keys[] modifiers = Bindable.Modifiers;
        for (int i = 0; i < modifiers.Length; i++) {
            if (keyboard.IsKeyDown(modifiers[i]) && !keys.Contains(modifiers[i])) return false;
        }
        return true;
    }

    /// <summary>
    ///     Clears the press of any hotkey whose binding is a strict subset of another hotkey's that
    ///     fired on the same frame. Call once per frame, after every hotkey has updated.
    /// </summary>
    // Covers what NoUnboundModifier cannot: two bindings differing by something that is not a
    // modifier — LB against LB+RB on a pad. Whichever names more inputs is the one the player meant.
    //
    // Decided on what each hotkey fired on, which this pass never clears, and not on Pressed, which it
    // does: clearing in place would let the survivor of three nested bindings depend on iteration order.
    internal static void SuppressSubsetPresses(ComboHotkey[] hotkeys) {
        for (int i = 0; i < hotkeys.Length; i++) {
            if (!hotkeys[i].Pressed) continue;
            ButtonBinding smaller = hotkeys[i].binding();
            for (int j = 0; j < hotkeys.Length; j++) {
                if (i == j) continue;
                if (!GivesWay(hotkeys[i], smaller, hotkeys[j], hotkeys[j].binding())) continue;
                hotkeys[i].Pressed = false;
                break;
            }
        }
    }

    // Per device, and only on a device both fired on: the keys and the buttons are alternatives inside
    // one binding, so a keyboard subset says nothing about which pad combo the player meant.
    private static bool GivesWay(ComboHotkey shorter, ButtonBinding smaller, ComboHotkey longer, ButtonBinding larger) =>
        smaller is not null && larger is not null
        && ((shorter.firedOnKeys && longer.firedOnKeys && IsStrictSubset(smaller.Keys, larger.Keys))
            || (shorter.firedOnPad && longer.firedOnPad && IsStrictSubset(smaller.Buttons, larger.Buttons)));

    private static bool IsStrictSubset<T>(List<T> smaller, List<T> larger) {
        if (smaller is null || larger is null) return false;
        if (smaller.Count == 0 || smaller.Count >= larger.Count) return false;
        for (int i = 0; i < smaller.Count; i++) {
            if (!larger.Contains(smaller[i])) return false;
        }
        return true;
    }
}
