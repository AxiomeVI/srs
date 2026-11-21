using System.Globalization;
using System.Text;

namespace Celeste.Mod.SpeedrunSheet;

/// What happened to a row, for the log (RunTracker.Trace).
internal enum TraceKind {
    Opened,
    // reached without control: opens when control comes back in its room
    Waiting,
    // a start waiting for control that will not open
    StoppedWaiting,
    // closed with its time
    Closed,
    // closed without a time
    NotRecorded,
    // open or set aside, and gone without a time
    Dropped,
    // walked into its start room, and not opened
    NotOpened,
    // can no longer be recorded, but stays open
    Disqualified,
    // a split may have hidden a Save and Quit inside it: its note is unknown
    Unsure,
    // reached its end room short of something, and goes on
    PassedEnd,
    // kept across a leave, until the level is entered again
    SetAside,
    CarriedOn,
    NotCarriedOn,
    // kept across a death that zeroed chapter time
    Kept,
}

internal enum TraceReason {
    None,

    // how a row opened or came to wait
    WalkIn,
    Restart,
    Reentry,
    ControlReturned,
    LaunchEnded,
    Appearance,

    // why a row closed without a time
    Disqualified,
    NoTime,
    MissingHeart,
    MissingCassette,
    MissingGem,
    MissingGems,
    WrongDashes,
    MissingBerries,
    // entered its end room from elsewhere than the next row's entry room
    NotFromNextEntry,
    // its end room was passed once already, short of something
    PassedEndBefore,
    // a Restart Chapter row whose end room was walked into
    EndRoomWalkedInto,

    // why a row stopped
    NewAttempt,
    TimelineChanged,
    RestartChapter,
    BackedOut,
    LeftLevel,
    // a piece from the map that fetched nothing the row lacked
    TryFetchedNothing,
    LeftRoom,
    NotAtStart,
    ControlElsewhere,
    Fall,
    TasPlayed,
    SplitStayed,

    // why a walk-in into a start room did not open a row
    WrongEntry,

    // how a leave or a re-entry treated a row
    SaveAndQuit,
    ReturnToMap,
    ChapterEnd,
    RoomNotWalked,
    NotItsCheckpoint,
    FirstRoomDeath,
}

/// One trace event. Reading is chapter time in ticks, null where the call
/// had none; Record is set on Closed; Room, From and Entry where they apply.
internal readonly record struct TraceEvent(
    TraceKind Kind, SegmentRule Rule, TraceReason Reason, long? Reading,
    SegmentRecord? Record = null, string Room = null, string From = null, string Entry = null);

/// The trace's line, one per event: the kind, the row, the reason, chapter
/// time in ticks ("-" where the call had none), the record's time and
/// notes, the rooms, and what the adapter was feeding (via). Every value is
/// raw, so two builds replaying the same input write the same lines.
internal static class TraceLine {
    public static string Format(TraceEvent e, string via) {
        StringBuilder line = new("trace ");
        line.Append(e.Kind).Append(" \"").Append(e.Rule.Scope).Append('/').Append(e.Rule.Name).Append('"')
            .Append(" reason=").Append(e.Reason)
            .Append(" at=").Append(e.Reading is { } reading ? reading.ToString(CultureInfo.InvariantCulture) : "-");
        if (e.Record is { } record) {
            line.Append(" ticks=").Append(record.Ticks.ToString(CultureInfo.InvariantCulture))
                .Append(" note=").Append(record.Note).Append(" rtm=").Append(record.ReturnToMap);
        }

        Field(line, "room", e.Room);
        Field(line, "from", e.From);
        Field(line, "entry", e.Entry);
        Field(line, "via", via);
        return line.ToString();
    }

    private static void Field(StringBuilder line, string key, string value) {
        if (value != null) {
            line.Append(' ').Append(key).Append('=').Append(value);
        }
    }
}
