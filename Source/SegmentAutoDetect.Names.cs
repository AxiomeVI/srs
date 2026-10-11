using System;
using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet;

// The checkpoint name tables. Game-free half: the tests compile it.
public static partial class SegmentAutoDetect {
    // (scope, checkpoint) -> the virtual checkpoint the sheet inserts after
    // it: the game's "Heart of the Mountain" is two segments on the sheet, and
    // 7A's 3000m is also timed as three, beside the whole. A virtual one is
    // no checkpoint of the map: every table here that keys it must be filled
    // by hand. The last of a chain must close its chapter: nothing resolves
    // the end room of one followed by a real checkpoint
    internal static readonly Dictionary<(string Scope, string GameName), string> SplitCheckpoints = new() {
        [("7a", "Downdraft")] = "Updraft",
        [("7a", "Updraft")] = "Nodraft",
        [("8a", "Heart of the Mountain")] = "HotM Horizontal",
    };

    // (scope, game checkpoint) -> the room a segment really starts in, where
    // the sheet does not time it from the checkpoint's own room. The segment
    // before ends there too: the two never overlap
    internal static readonly Dictionary<(string Scope, string GameName), string> StartRoomOverrides = new() {
        // no checkpoint of the map: the room 1A's heart is collected in
        [("1a", "Heart")] = "s1",
        // the sheet times Awake from the wake-up in end_0, three rooms before
        // the checkpoint's end_3: Intervention ends on entering end_0
        [("2a", "Awake")] = "end_0",
        // neither a-00-intro nor the landing in a-00 is timed: the sheet adds
        // them (UntimedSegmentHead)
        [("7a", "Start")] = "a-00",
        // 3000m's three pieces: Downdraft starts where 3000m does, the two
        // others in rooms with no checkpoint
        [("7a", "Downdraft")] = "g-00",
        [("7a", "Updraft")] = "g-01",
        [("7a", "Nodraft")] = "g-02",
        // 7B starts like 7A
        [("7b", "Start")] = "a-00",
        // HotM Horizontal has no checkpoint of its own: d-08 is where the chase
        // to the right begins
        [("8a", "HotM Horizontal")] = "d-08",
    };

    // (scope, game checkpoint) -> the sheet's own constant for the head of a
    // segment srs does not time, added to the reading. 7A is timed from a-00,
    // after the intro room and the landing; the Prologue as file time, which
    // counts the 61 frames of the intro before its savestate
    internal static readonly Dictionary<(string Scope, string GameName), TimeSpan> UntimedSegmentHead = new() {
        [("7a", "Start")] = new TimeSpan(0, 0, 0, 5, 508),
        [("7b", "Start")] = new TimeSpan(0, 0, 0, 5, 508),
        [("Prologue", "Start")] = new TimeSpan(0, 0, 0, 1, 37),
    };

    // (scope, game checkpoint) -> the end of a segment the timer does not
    // count, added like a head: the Prologue's file time runs 32 frames past
    // the frame that stops the timer, to the LevelExit, skipped or watched
    internal static readonly Dictionary<(string Scope, string GameName), TimeSpan> UntimedSegmentTail = new() {
        [("Prologue", "Start")] = new TimeSpan(0, 0, 0, 0, 544),
    };

    // (scope, game checkpoint) of the segments that open once the launch into
    // their room is over: 7A's head already counts the landing in a-00, and
    // opening on the entry would count it twice
    internal static readonly HashSet<(string Scope, string GameName)> AfterLaunchStarts = [
        ("7a", "Start"),
        ("7b", "Start"),
    ];

    // (scope, game checkpoint) -> the room a segment's first room must be
    // entered from to open it, and to close the one before with a time. Every
    // Next Room and after-wake-up segment has one; a chapter's Start and 7A's
    // have none. A cutscene's room is entered from where the cutscene starts
    internal static readonly Dictionary<(string Scope, string GameName), string> EntryRooms = new() {
        [("1a", "Crossing")] = "5",
        [("1a", "Chasm")] = "9",
        [("2a", "Intervention")] = "3x",
        [("2a", "Awake")] = "13",
        [("3a", "Huge Mess")] = "07-a",
        [("3a", "Elevator Shaft")] = "09-b",
        [("3a", "Presidential Suite")] = "02-d",
        [("4a", "Shrine")] = "a-09",
        [("4a", "Old Trail")] = "b-08",
        // d-00 is also entered from c-10, the berry room beside c-08: the
        // berry row's own entry (RowTraits)
        [("4a", "Cliff Face")] = "c-08",
        [("1b", "Contraption")] = "03",
        [("1b", "Scrap Pit")] = "07",
        [("2b", "Combination Lock")] = "02",
        [("2b", "Dream Altar")] = "07",
        [("3b", "Staff Quarters")] = "05",
        [("3b", "Library")] = "10",
        [("3b", "Rooftop")] = "12",
        [("4b", "Stepping Stones")] = "a-04",
        [("4b", "Gusty Canyon")] = "b-04",
        [("4b", "Eye of the Storm")] = "c-04",
        [("5a", "Depths")] = "a-13",
        [("5a", "Unravelling")] = "void",
        [("5a", "Search")] = "c-13",
        // e-00 is also reached up the shaft of d-01, which no route takes
        [("5a", "Rescue")] = "d-20",
        [("5b", "Central Chamber")] = "a-02",
        [("5b", "Through the Mirror")] = "b-09",
        [("5b", "Mix Master")] = "c-04",
        // 00 shares an edge only with a room of its own segment: it is reached
        // by the fall from start, watched or skipped
        [("6a", "Lake")] = "start",
        [("6a", "Hollows")] = "02b",
        [("6a", "Reflection")] = "20",
        [("6a", "Rock Bottom")] = "b-03",
        [("6a", "Resolution")] = "boss-20",
        [("6b", "Reflection")] = "a-06",
        [("6b", "Rock Bottom")] = "b-10",
        [("6b", "Reprieve")] = "c-04",
        [("7a", "500 M")] = "a-06",
        [("7a", "1000 M")] = "b-09",
        [("7a", "1500 M")] = "c-09",
        [("7a", "2000 M")] = "d-11",
        [("7a", "2500 M")] = "e-13",
        [("7a", "3000 M")] = "f-11",
        [("7a", "Downdraft")] = "f-11",
        [("7a", "Updraft")] = "g-00b",
        [("7a", "Nodraft")] = "g-01",
        [("7b", "500 M")] = "a-03",
        [("7b", "1000 M")] = "b-03",
        [("7b", "1500 M")] = "c-03",
        [("7b", "2000 M")] = "d-03",
        [("7b", "2500 M")] = "e-03",
        [("7b", "3000 M")] = "f-03",
        [("8a", "Into the Core")] = "02",
        [("8a", "Hot and Cold")] = "b-07",
        [("8a", "Heart of the Mountain")] = "c-04",
        [("8a", "HotM Horizontal")] = "d-07",
        [("8b", "Into the Core")] = "01",
        [("8b", "Burning or Freezing")] = "a-05",
        [("8b", "Heartbeat")] = "b-05",
        [("Farewell", "Singular")] = "intro-03-space",
        [("Farewell", "Power Source")] = "b-07",
        [("Farewell", "Remembered")] = "e-00y",
        [("Farewell", "Event Horizon")] = "e-08",
        [("Farewell", "Determination")] = "g-06",
        [("Farewell", "Stubbornness")] = "h-10",
        [("Farewell", "Reconciliation")] = "i-05",
        [("Farewell", "Farewell")] = "j-15",
    };

    // (scope, game checkpoint) -> the spawn a wake-up puts the player on, in
    // world coordinates. A segment keyed here is valid from its start point,
    // as a chapter's "Start" is, and opens on a restart from this spawn only.
    // Not the default spawn: in 5A's and 5B's c-00 that is the bottom one, and
    // the wake-up uses the top one
    internal static readonly Dictionary<(string Scope, string GameName), (int X, int Y)> WakeUpSpawns = new() {
        [("2a", "Awake")] = (144, 1880),
        [("5a", "Unravelling")] = (-832, 1688),
        [("5b", "Through the Mirror")] = (3680, -984),
    };

    // (scope, virtual checkpoint) of the segments that start where a Save and
    // Quit taken after the heart puts the player back, or from a savestate
    // there, and on nothing else. The spawn is the one the room's respawn
    // trigger sets, read from the map by RunWatcher: no coordinates here
    internal static readonly HashSet<(string Scope, string GameName)> HeartRespawns = [
        ("1a", "Heart"),
    ];

    // (scope, game checkpoint) -> where the player stands, relative to a spawn
    // of the start room, on the first frame with control; every other Current
    // Room start measured is on the spawn
    internal static readonly Dictionary<(string Scope, string GameName), (int X, int Y)> SpawnOffsets = new() {
        [("Farewell", "Start")] = (8, 0),
        // the campfire cutscene ends with the player at the bonfire, watched
        // or skipped (CS06_Campfire.OnEnd), 172 px right of the room's spawn
        [("6a", "Start")] = (172, 0),
    };
}
