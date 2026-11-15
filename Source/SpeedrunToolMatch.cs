using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Celeste.Mod.SpeedrunSheet;

/// Looks up Speed Run Tool's time formatter and every member srs's code names
/// outside the ModInterop, before anything that names one is installed. A
/// member renamed in a Speed Run Tool release would throw inside a hook, where
/// the method naming it is first compiled, and without the formatter srs has no
/// time format at all: on any miss srs installs nothing and says so.
/// ⚠️ Reflection only. No Speed Run Tool type may reach this class's code: it
/// runs before anything knows those types are there. A Speed Run Tool member
/// newly named in srs joins Members.
internal static class SpeedrunToolMatch {
    private const string Namespace = "Celeste.Mod.SpeedrunTool.";

    // srs compares against Off as compiled: a constant is folded into the
    // compiled code, so these two name no Speed Run Tool type at run time
    private const long RoomTimerOff = (long) SpeedrunTool.RoomTimer.RoomTimerType.Off;
    private const long FreezeOff = (long) SpeedrunTool.FreezeAfterLoadStateType.Off;

    private static readonly (string Name, Func<Assembly, bool> Found)[] Members = [
        ("SpeedrunToolSettings.Instance", tool =>
            HasProperty(tool, "SpeedrunToolSettings", "Instance", BindingFlags.Static, Find(tool, "SpeedrunToolSettings"))),
        ("SpeedrunToolSettings.Enabled", tool =>
            HasProperty(tool, "SpeedrunToolSettings", "Enabled", BindingFlags.Instance, typeof(bool))),
        ("SpeedrunToolSettings.RoomTimerType", tool =>
            HasProperty(tool, "SpeedrunToolSettings", "RoomTimerType", BindingFlags.Instance, Find(tool, "RoomTimer.RoomTimerType"))),
        ("SpeedrunToolSettings.FreezeAfterLoadStateType", tool =>
            HasProperty(tool, "SpeedrunToolSettings", "FreezeAfterLoadStateType", BindingFlags.Instance, Find(tool, "FreezeAfterLoadStateType"))),
        ("RoomTimerType.Off", tool => IsValue(Find(tool, "RoomTimer.RoomTimerType"), "Off", RoomTimerOff)),
        ("FreezeAfterLoadStateType.Off", tool => IsValue(Find(tool, "FreezeAfterLoadStateType"), "Off", FreezeOff)),
        ("PopupMessageUtils.Show", tool =>
            Find(tool, "Message.PopupMessageUtils")?.GetMethod("Show", BindingFlags.Public | BindingFlags.Static,
                null, [typeof(string), typeof(string)], null)?.ReturnType == typeof(void)),
    ];

    // a Debug build started with this marker file in place stands down as on a miss
#if DEBUG
    private static readonly bool testForcesMiss =
        File.Exists(Path.Combine(Everest.PathSettings, "srs", "test-speedruntool-mismatch"));
#endif

    /// What was not found, null while everything was.
    internal static string Missing { get; private set; }

    /// Installs Speed Run Tool's formatter and returns true, or logs the one
    /// Error and returns false.
    internal static bool Adopt() {
        Func<long, bool, string> format = null;
        List<string> missing = [];
        try {
            Assembly tool = Everest.Modules
                .FirstOrDefault(module => module.Metadata?.Name == "SpeedrunTool")?
                .GetType().Assembly;
            if (tool == null) {
                missing.Add("the SpeedrunTool module");
            } else {
                // public, in a class that is not, and not in the ModInterop.
                // isPbTime false: true returns "" for a zero, Speed Run Tool's absent PB
                MethodInfo formatTime = Find(tool, "RoomTimer.RoomTimerData")?
                    .GetMethod("FormatTime", BindingFlags.Public | BindingFlags.Static,
                        null, [typeof(long), typeof(bool)], null);
                if (formatTime?.ReturnType == typeof(string)) {
                    format = formatTime.CreateDelegate<Func<long, bool, string>>();
                } else {
                    missing.Add("RoomTimerData.FormatTime");
                }

                missing.AddRange(Members.Where(member => !member.Found(tool)).Select(member => member.Name));
            }
        } catch (Exception e) {
            missing.Add("its members (" + e.GetType().Name + ")");
        }

#if DEBUG
        if (testForcesMiss) {
            missing.Add("nothing (forced by Saves/srs/test-speedruntool-mismatch)");
        }
#endif

        if (missing.Count > 0) {
            Missing = string.Join(", ", missing);
            // never a formatter of srs's own: a second one would drift in
            // silence and write a time into the player's sheet that no longer
            // matches the timer
            Logger.Log(LogLevel.Error, "srs", "Speed Run Tool does not match this srs, not found: " + Missing
                + ". srs stays off: it records, shows and exports nothing until a srs update.");
            return false;
        }

        Missing = null;
        TimeFormat.Format = ticks => format(ticks, false);
        return true;
    }

    private static Type Find(Assembly tool, string name) => tool.GetType(Namespace + name);

    private static bool HasProperty(Assembly tool, string type, string name, BindingFlags kind, Type propertyType) =>
        propertyType != null
        && Find(tool, type)?.GetProperty(name, BindingFlags.Public | kind) is { GetMethod.IsPublic: true } property
        && property.PropertyType == propertyType;

    private static bool IsValue(Type enumType, string name, long value) =>
        enumType?.GetField(name, BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } field
        && Convert.ToInt64(field.GetRawConstantValue()) == value;
}
