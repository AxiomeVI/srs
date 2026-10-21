using System;
using System.Collections.Generic;

namespace Celeste.Mod.CelesteHotkeys;

internal enum ChordStep {
    /// <summary>Still waiting for the chord to be let go.</summary>
    Recording,
    /// <summary>An input went down past <see cref="Bindable.MaxComboInputs"/> and was left out.</summary>
    Refused,
    /// <summary>The chord was let go; <see cref="ChordRecorder{T}.Inputs"/> is the binding to save.</summary>
    Done,
}

/// <summary>
///     Records one binding in one pass: every input pressed while recording joins the chord, and the
///     first one let go ends it. No MInput, no Engine — fed the held inputs once per frame.
/// </summary>
// The chord replaces the whole binding. Re-recording a smaller chord is how one input comes off.
internal sealed class ChordRecorder<T> where T : struct {
    /// <summary>How many frames a chord input must stay up before the release counts.</summary>
    // ⚠️ Frames, not the first frame up. A trigger is analog and read as a button past a threshold, and
    // one resting near it flickers up for a frame: ending on that frame saved LT + A as LT alone. A
    // real release lasts far longer than three frames.
    internal const int ReleaseFrames = 3;

    private readonly Func<T, int> leadingRank;
    private readonly List<T> chord = new();
    private readonly List<T> ignored = new();
    private int releasedFor;

    /// <param name="leadingRank">
    ///     Inputs listed before the others, in the order of their rank whatever order they went down
    ///     in, and every other input below zero — the modifiers, so the binding reads "LeftControl G"
    ///     even when both went down on one frame.
    /// </param>
    internal ChordRecorder(Func<T, int> leadingRank = null) => this.leadingRank = leadingRank;

    /// <summary>The chord so far, leading inputs first by rank, then the rest in the order they went down.</summary>
    internal IReadOnlyList<T> Inputs => chord;

    /// <summary>Starts a chord. <paramref name="held"/> is what is held now, which never joins it.</summary>
    // ⚠️ What is held at the start is the press that opened the recording — Enter, or A on a pad —
    // and a Shift still down from it. An input is ignored until it has been let go once, and counts
    // when it goes down again.
    internal void Start(IEnumerable<T> held) {
        chord.Clear();
        ignored.Clear();
        ignored.AddRange(held);
        releasedFor = 0;
    }

    /// <summary>Advances one frame.</summary>
    /// <param name="held">Every input that may be recorded and is held this frame.</param>
    internal ChordStep Update(List<T> held) {
        ignored.RemoveAll(input => !held.Contains(input));

        bool released = false;
        for (int i = 0; i < chord.Count; i++) {
            if (held.Contains(chord[i])) continue;
            released = true;
            break;
        }
        releasedFor = released ? releasedFor + 1 : 0;
        if (releasedFor >= ReleaseFrames) return ChordStep.Done;

        // ⚠️ Nothing joins while a chord input is up. An input brushed while letting go would be saved
        // with a chord it was never held with; one held through a flicker joins when it ends.
        if (released) return ChordStep.Recording;

        // Leading inputs first, so past the cap a modifier is never the one refused when it goes down on
        // the same frame as a letter: the keyboard lists keys in ascending order, letters first.
        bool refused = false;
        for (int pass = 0; pass < 2; pass++) {
            for (int i = 0; i < held.Count; i++) {
                T input = held[i];
                if (IsLeading(input) != (pass == 0)) continue;
                if (ignored.Contains(input) || chord.Contains(input)) continue;
                if (chord.Count >= Bindable.MaxComboInputs) {
                    // Ignored until let go, so one press is refused once and not every frame it is held.
                    ignored.Add(input);
                    refused = true;
                    continue;
                }
                Add(input);
            }
        }
        return refused ? ChordStep.Refused : ChordStep.Recording;
    }

    private int Rank(T input) => leadingRank is null ? -1 : leadingRank(input);

    private bool IsLeading(T input) => Rank(input) >= 0;

    private void Add(T input) {
        int rank = Rank(input);
        if (rank < 0) {
            chord.Add(input);
            return;
        }
        int at = 0;
        while (at < chord.Count && IsLeading(chord[at]) && Rank(chord[at]) <= rank) at++;
        chord.Insert(at, input);
    }
}
