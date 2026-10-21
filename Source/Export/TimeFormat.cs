using System;
using System.Globalization;

namespace Celeste.Mod.SpeedrunSheet;

/// The one place a tick count becomes a string a player sees or the sheet
/// receives.
public static class TimeFormat {
    /// Speed Run Tool's own formatter, installed by SrsModule.Load, so a time
    /// reads as its timer would show it. A delegate rather than a call, for the
    /// reason ExportProtocol.Localize is one.
    public static Func<long, string> Format;

    /// From an hour up the format is ours: Speed Run Tool's prints minutes and
    /// seconds only, and would show the time an hour short.
    public static string FromTicks(long ticks) =>
        ticks >= TimeSpan.TicksPerHour ? WithHours(ticks)
        : Format is { } format
            ? format(ticks)
            // SrsModule fails at load rather than reach here, so this is the
            // test project having forgotten to install its stand-in
            : throw new InvalidOperationException(
                "TimeFormat.Format was never set; SpeedrunTool's formatter is the only one srs has");

    // 1:05:03.250, the milliseconds cut and not rounded, as under an hour
    private static string WithHours(long ticks) {
        TimeSpan span = TimeSpan.FromTicks(ticks);
        return ((long) span.TotalHours).ToString(CultureInfo.InvariantCulture)
             + span.ToString("\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture);
    }

    /// A signed difference, written as a time: "+1.250", "-1:13.500",
    /// "-1:04:47.491".
    public static string Delta(long ticks) =>
        (ticks < 0 ? "-" : "+") + FromTicks(Math.Abs(ticks));
}
