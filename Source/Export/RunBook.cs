using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet;

/// The session's best of every row, game-free so the tests can pin what a run
/// replaces. A row's best is replaced only by a faster run of that same row.
internal sealed class RunBook {
    internal readonly record struct Run(string Scope, string Name, long Ticks, SaveAndQuitNote Note);

    private readonly Dictionary<(string Scope, string Name), Run> best = [];

    public IReadOnlyCollection<Run> All => best.Values;

    /// Offers what one event closed; returns the runs that improved.
    public List<Run> Offer(IReadOnlyList<SegmentRecord> records) {
        List<Run> improved = [];
        foreach (SegmentRecord record in records) {
            SegmentRule rule = record.Rule;
            if (best.TryGetValue((rule.Scope, rule.Name), out Run held) && record.Ticks >= held.Ticks) {
                continue;
            }

            Run run = new(rule.Scope, rule.Name, record.Ticks, record.Note);
            best[(rule.Scope, rule.Name)] = run;
            improved.Add(run);
        }

        return improved;
    }
}
