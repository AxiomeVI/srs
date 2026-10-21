using System;
using System.Collections.Generic;

namespace Celeste.Mod.CelesteHotkeys;

/// <summary>A mod's hotkeys, polled together once per frame.</summary>
// Owns the per-frame order, which every hotkey of every mod must follow the same way:
//   1. one input snapshot, so every hotkey answers the same frame;
//   2. if paused, every hotkey tracks what is held and none fires;
//   3. otherwise each updates, then a shorter combo gives way to a longer one on the same frame.
// Acting on the presses is the mod's business, after Update: query Pressed.
internal sealed class HotkeySet<TSettings> where TSettings : class {
    private readonly Keybind<TSettings>[] keybinds;
    private readonly ComboHotkey[] hotkeys;

    /// <param name="settings">
    ///     The mod's live settings, read on every poll — usually <c>() => MyModule.Settings</c>.
    /// </param>
    /// <param name="keybinds">The mod's keybind table, in the order the remap screen lists it.</param>
    internal HotkeySet(Func<TSettings> settings, IReadOnlyList<Keybind<TSettings>> keybinds) {
        Settings = settings;
        this.keybinds = new Keybind<TSettings>[keybinds.Count];
        hotkeys = new ComboHotkey[keybinds.Count];
        for (int i = 0; i < keybinds.Count; i++) {
            Keybind<TSettings> keybind = keybinds[i];
            this.keybinds[i] = keybind;
            hotkeys[i] = new ComboHotkey(() => keybind.Binding(settings()));
        }
    }

    internal Func<TSettings> Settings { get; }

    internal IReadOnlyList<Keybind<TSettings>> Keybinds => keybinds;

    /// <summary>Polls every hotkey. Call once per frame, then query <see cref="Pressed"/>.</summary>
    /// <param name="enabled">
    ///     The mod's master switch. Off counts as paused rather than as "not polled", so a combo held
    ///     while the mod is switched back on does not fire.
    /// </param>
    internal void Update(bool enabled = true) => Update(HotkeyInput.Current(), !enabled || HotkeyPause.Now);

    // ⚠️ Frames after a pause that still count as paused. MInput blanks the keyboard while the console
    // is open and the window unfocused, and it decides that in MInput.Update, BEFORE the console
    // handles the key that closes it — so the frame the console closes still reads blank, and a key
    // held through it only reappears the frame after, where it reads as a fresh press. Tracking what
    // is held cannot help when what is held is hidden. Two frames covers a poll before or after the
    // scene updates; a genuine press landing in them is lost, which nobody can time anyway.
    private const int UnpauseGrace = 2;

    // One at the start: the first poll has no earlier frame to compare with, so a combo already held
    // when the set is built would read as a fresh press.
    private int graceFrames = 1;

    /// <summary>The seam under <see cref="Update(bool)"/>: no MInput, no Engine.</summary>
    internal void Update(in HotkeyInput input, bool paused) {
        if (paused) {
            graceFrames = UnpauseGrace;
        } else if (graceFrames > 0) {
            graceFrames--;
            paused = true;
        }

        for (int i = 0; i < hotkeys.Length; i++) hotkeys[i].Update(input, paused);
        if (!paused) ComboHotkey.SuppressSubsetPresses(hotkeys);
    }

    /// <summary>Whether <paramref name="keybind"/> fired on the last <see cref="Update(bool)"/>.</summary>
    /// <exception cref="ArgumentException">The keybind is not one of this set's rows.</exception>
    internal bool Pressed(Keybind<TSettings> keybind) {
        for (int i = 0; i < keybinds.Length; i++) {
            if (ReferenceEquals(keybinds[i], keybind)) return hotkeys[i].Pressed;
        }
        throw new ArgumentException($"{keybind?.Property} is not in this hotkey set.", nameof(keybind));
    }

    /// <summary>Swallows whatever is held right now, for every hotkey in the set.</summary>
    internal void Resync() => Resync(HotkeyInput.Current());

    internal void Resync(in HotkeyInput input) {
        for (int i = 0; i < hotkeys.Length; i++) hotkeys[i].Resync(input);
    }
}
