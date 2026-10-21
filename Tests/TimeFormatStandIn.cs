using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Celeste.Mod.SpeedrunSheet.Tests;

/// A stand-in for SpeedrunTool's time format, which this project cannot
/// reference.
///
/// ⚠️ A transcription, not a specification. An assertion on an exact time
/// string is an assertion about this file; assert what surrounds the format.
internal static class TimeFormatStandIn {
    [ModuleInitializer]
    internal static void Install() {
        TimeFormat.Format = ticks => {
            TimeSpan span = TimeSpan.FromTicks(ticks);
            string format = span.TotalSeconds < 60 ? "s\\.fff" : "m\\:ss\\.fff";
            return span.ToString(format, CultureInfo.InvariantCulture);
        };
    }
}
