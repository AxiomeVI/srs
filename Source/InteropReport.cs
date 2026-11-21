using System;
using System.Linq;

namespace Celeste.Mod.SpeedrunSheet;

/// One log line per optional interop: an import that bound to nothing looks
/// exactly like a mod that is not installed, so only the installed state tells
/// them apart.
internal static class InteropReport {
    private const string LogTag = "srs";

    /// Call once, from a level: every mod is loaded by then, whatever the order.
    public static void Log(bool tasBound, int? splitVersion) {
        bool tasInstalled = IsInstalled("CelesteTAS");
        if (tasBound) {
            Logger.Log(LogLevel.Info, LogTag, "CelesteTAS interop bound");
        } else if (tasInstalled) {
            Logger.Log(LogLevel.Warn, LogTag,
                "CelesteTAS is installed but its interop did not bind: a TAS cannot be told from a player, so rows a TAS plays are recorded");
        } else {
            Logger.Log(LogLevel.Info, LogTag, "CelesteTAS is not installed");
        }

        if (splitVersion != null) {
            Logger.Log(LogLevel.Info, LogTag, $"SomeSplitButtons interop bound, version {splitVersion}");
        } else if (IsInstalled("SomeSplitButtons")) {
            // an older version has no export: a supported state
            Logger.Log(LogLevel.Info, LogTag, "SomeSplitButtons is installed without an interop version: a split's reload is a new attempt");
        } else {
            Logger.Log(LogLevel.Info, LogTag, "SomeSplitButtons is not installed");
        }
    }

    private static bool IsInstalled(string name) =>
        Everest.Modules.Any(module => module.Metadata?.Name == name);
}
