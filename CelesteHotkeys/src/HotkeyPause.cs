using System;
using Monocle;

namespace Celeste.Mod.CelesteHotkeys;

/// <summary>When every hotkey keeps out of the way: the player is typing or rebinding, not playing.</summary>
internal static class HotkeyPause {
    /// <summary>
    ///     True while the debug console is open, the window is unfocused, or any mod's remap screen
    ///     built on this module is open.
    /// </summary>
    // The console and focus checks look redundant, because MInput already blanks the keyboard in
    // both cases. They are here for the pad, which MInput keeps reading under the console, and so
    // the rule reads whole in one place.
    //
    // The whole screen, not only while it records. Recording is a subset of "open", so this covers
    // both, and browsing the screen is not playing either: a hotkey firing while the player walks
    // the rows with keys they may have bound is at best noise and at worst a hotkey that wipes
    // something.
    internal static bool Now =>
        RemapScreensOpen > 0
        || Engine.Commands is { Open: true }
        || Engine.Instance is { IsActive: false };

    // ⚠️ ONE COUNT FOR EVERY MOD, NOT ONE PER MOD. This file is compiled into each mod's own DLL, so
    // a static field here would be a separate copy per mod — and pressing keys in one mod's remap
    // screen would still fire another mod's hotkeys. AppContext data is one table per process, so
    // every copy of this module reads the same slot.
    //
    // ⚠️ NEVER CHANGE THIS KEY OR THE MEANING OF ITS VALUE. Each mod carries its own version of this
    // module; an old copy and a new one loaded side by side must still agree on it. A boxed int, counting open screens.
    private const string OpenScreensKey = "Celeste.Mod.CelesteHotkeys.RemapScreensOpen";

    /// <summary>How many remap screens are open right now, across every mod.</summary>
    internal static int RemapScreensOpen => AppContext.GetData(OpenScreensKey) is int count ? count : 0;

    internal static void RemapScreenOpened() => AppContext.SetData(OpenScreensKey, RemapScreensOpen + 1);

    // Clamped, so a screen that somehow closes twice cannot switch every other mod's pause off early
    // — KeybindScreen also guards each instance with a flag.
    internal static void RemapScreenClosed() => AppContext.SetData(OpenScreensKey, Math.Max(0, RemapScreensOpen - 1));
}
