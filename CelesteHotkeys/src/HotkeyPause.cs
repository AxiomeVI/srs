#nullable disable
using System;
using System.Reflection;
using Monocle;

namespace Celeste.Mod.CelesteHotkeys;

/// <summary>When every hotkey keeps out of the way: the player is typing or rebinding, not playing.</summary>
internal static class HotkeyPause {
    /// <summary>
    ///     True while the debug console is open, the window is unfocused, a text box is reading the
    ///     keyboard, or any mod's remap screen built on this module is open.
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
        || Engine.Instance is { IsActive: false }
        || TextInputTyping;

    /// <summary>Whether a text box is reading the keyboard: a bare letter is being typed, not pressed.</summary>
    // Everest's TextInput.OnInput has a subscriber exactly while a box types: the Mod Options search,
    // map search, file naming, string and number entry, the console, and any mod's own box. Not
    // MInput.Disabled, which a paused level resets and vanilla cutscenes set, and not a list of
    // screens, which misses a mod's own box. The handlers are a private event field, hence the
    // reflection.
    internal static bool TextInputTyping {
        get {
            if (TextInputHandlers?.GetValue(null) is not Delegate handlers) {
                lastHandlers = null;
                return false;
            }
            // A delegate is immutable, so the same one gives the same answer: no array per frame.
            if (!ReferenceEquals(handlers, lastHandlers)) {
                lastHandlers = handlers;
                lastTyping = IsTyping(handlers);
            }
            return lastTyping;
        }
    }

    private static Delegate lastHandlers;
    private static bool lastTyping;

    /// <summary>Whether any of <paramref name="handlers"/> is a text box typing.</summary>
    internal static bool IsTyping(Delegate handlers) =>
        handlers != null && Array.Exists(handlers.GetInvocationList(), handler => !IsAlwaysSubscribed(handler));

    // ⚠️ ImGuiHelper subscribes once for the whole session, so counting it would silence every hotkey
    // for good. CelesteTAS skips the same handler for its own hotkeys (Hotkeys.UpdateMeta, 3.47.1).
    private static bool IsAlwaysSubscribed(Delegate handler) =>
        handler.Target?.GetType().FullName == "Celeste.Mod.ImGuiHelper.ImGuiRenderer+<>c";

    // Null if a later Everest renames the field: the hotkeys then stay live while typing, and the
    // log says so once.
    private static readonly FieldInfo TextInputHandlers = FindTextInputHandlers(typeof(TextInput));

    internal static FieldInfo FindTextInputHandlers(Type textInput) {
        FieldInfo field = textInput.GetField("_OnInput", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (field == null || !typeof(Delegate).IsAssignableFrom(field.FieldType)) {
            Logger.Warn("CelesteHotkeys",
                $"{textInput.FullName}._OnInput was not found: hotkeys will fire while typing in a text box.");
            return null;
        }
        return field;
    }

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
