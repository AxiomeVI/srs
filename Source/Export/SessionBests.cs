using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet;

/// The best of every row run this session, fed by RunWatcher and kept until
/// the game closes, every chapter alike: the export screen lists them all.
internal static class SessionBests {
    private const string LogTag = "srs";

    private static readonly RunBook book = new();

    public static IReadOnlyCollection<RunBook.Run> All => book.All;

    // deliberately NOT registered with SpeedrunTool's save states: a time run
    // is a fact about the session, and a load must not take it back
    public static void Record(IReadOnlyList<SegmentRecord> records, Session session) {
        string scope = session == null ? null : SegmentAutoDetect.ScopeOf(session);
        List<SegmentRecord> here = [];
        foreach (SegmentRecord record in records) {
            if (record.Rule.Scope == scope) {
                here.Add(record);
            }
        }

        foreach (RunBook.Run run in book.Offer(here)) {
            Logger.Log(LogLevel.Info, LogTag, $"session best {run.Scope}/{run.Name} {TimeFormat.FromTicks(run.Ticks)}");
            if (run.Note != SaveAndQuitNote.Without) {
                Logger.Log(LogLevel.Info, LogTag, $"session note {run.Scope}/{run.Name} {run.Note}");
            }
        }
    }
}
