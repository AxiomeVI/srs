using System;
using System.Globalization;

namespace Celeste.Mod.SpeedrunSheet;

/// The one place a tick count becomes a string a player sees or the sheet
/// receives.
public static class TimeFormat {
    /// Speed Run Tool's own formatter, installed by SpeedrunToolMatch, so a time
    /// reads as its timer would show it. A delegate rather than a call, for the
    /// reason ExportProtocol.Localize is one.
    public static Func<long, string> Format;

    /// From an hour up the format is ours: Speed Run Tool's prints minutes and
    /// seconds only, and would show the time an hour short.
    public static string FromTicks(long ticks) =>
        ticks >= TimeSpan.TicksPerHour ? WithHours(ticks)
        : Format is { } format
            ? format(ticks)
            // srs installs nothing without it, so this is the
            // test project having forgotten to install its stand-in
            : throw new InvalidOperationException(
                "TimeFormat.Format was never set; SpeedrunTool's formatter is the only one srs has");

    // 1:05:03.250, the milliseconds cut and not rounded, as under an hour
    private static string WithHours(long ticks) {
        TimeSpan span = TimeSpan.FromTicks(ticks);
        return ((long) span.TotalHours).ToString(CultureInfo.InvariantCulture)
             + span.ToString("\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture);
    }

    /// Chapter time moves by 17 ms a frame, not 1/60 s: the game rounds each
    /// frame to the millisecond.
    public const long FrameTicks = 17 * TimeSpan.TicksPerMillisecond;

    // up to this, a difference reads in frames: 0.1 s itself is 6f
    private const long FramesUpTo = TimeSpan.TicksPerSecond / 10;

    /// A signed difference: "+1.250", "-1:13.500", "-1:04:47.491", and up to
    /// 0.1 s in frames, "-3f". A sheet time typed by hand need not be a whole
    /// number of frames: it rounds to the nearest, and to 1f rather than 0f.
    public static string Delta(long ticks) {
        string sign = ticks < 0 ? "-" : "+";
        long size = Math.Abs(ticks);
        if (size > FramesUpTo) {
            return sign + FromTicks(size);
        }

        long frames = (size + FrameTicks / 2) / FrameTicks;
        return $"{sign}{(size > 0 ? Math.Max(frames, 1) : 0)}f";
    }

    /// How far a time is behind a threshold it must be strictly under, as
    /// Delta writes it; up to 0.1 s, the frames to save: a tie needs one, and
    /// 34 ms behind needs three, since two would tie.
    public static string Behind(long ticks) =>
        ticks is >= 0 and <= FramesUpTo ? $"+{ticks / FrameTicks + 1}f" : Delta(ticks);
}
