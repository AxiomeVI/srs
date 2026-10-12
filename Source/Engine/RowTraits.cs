using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet;

/// What a row's label does not say, by srs's (scope, name): a start from
/// the checkpoint's map spawn, a heart that ends the row or is only required,
/// an end on a Return to Map, an entry room of its own, a berry set that is
/// not its checkpoint's or none at all, a dash count, a chapter row whose
/// route returns to the map. A Full Clear label says nothing: all it
/// requires is here.
internal readonly record struct RowTraits(
    bool MapSpawn = false, bool EndsOnHeart = false, Collectibles Requires = Collectibles.None,
    string EntryRoom = null, BerrySet Berries = null, bool NoBerries = false, int? Dashes = null,
    bool EndsOnReturnToMap = false, bool SpansLeaves = false) {
    private const Collectibles Heart = Collectibles.Heart;
    private const Collectibles Cassette = Collectibles.Cassette;
    private const Collectibles Gem = Collectibles.Gem;

    // 3000m's first piece holds its berries in its second room, on both berry tabs
    private static readonly BerrySet DowndraftBerries = new(Rooms: ["g-00b"]);

    // 1A's routes that return to the map, each set shared with its Full
    // Clear twin. 11 and 10zb are Chasm's far berries, taken from Crossing
    private static readonly BerrySet CrossingToRtmBerries = new("Crossing", Rooms: ["11", "10zb"], Except: ["7zb"]);
    // less what Crossing to Heart takes before the heart
    private static readonly BerrySet HeartToRtmBerries = new("Crossing", Rooms: ["11", "10zb"], Except: ["7zb", "6", "s1"]);
    private static readonly BerrySet ChasmTwoBerries = new(Rooms: ["11", "10zb"]);

    // rooms as the game names them. A key no row carries never applies
    // (SheetConsistencyTests checks each one)
    internal static readonly Dictionary<(string Scope, string Name), RowTraits> All = new() {
        // 7zb is Crossing's far berry, taken from Start on that route
        [("1a", "ARB Start to RTM")] = new(EndsOnReturnToMap: true, Berries: new BerrySet("Start", Rooms: ["7zb"])),
        [("1a", "ARB Crossing to RTM")] = new(MapSpawn: true, EndsOnReturnToMap: true, Requires: Heart, Berries: CrossingToRtmBerries),
        [("1a", "ARB Heart to RTM")] = new(EndsOnReturnToMap: true, Berries: HeartToRtmBerries),
        [("1a", "ARB Crossing-2")] = new(MapSpawn: true, EndsOnReturnToMap: true, Berries: new BerrySet(Rooms: ["7zb"])),
        [("1a", "ARB Chasm-2")] = new(MapSpawn: true, EndsOnReturnToMap: true, Berries: ChasmTwoBerries),
        [("1a", "ARB Start to Heart")] = new(EndsOnHeart: true, Berries: new BerrySet("Start", Rooms: ["6", "s1"])),
        [("1a", "ARB Crossing to Heart")] = new(MapSpawn: true, EndsOnHeart: true, Berries: new BerrySet(Rooms: ["6", "s1"])),
        // 7zb, and 11 and 10zb, belong to the rows that return to the map
        [("1a", "ARB Crossing")] = new(MapSpawn: true, Requires: Heart, Berries: new BerrySet("Crossing", Except: ["7zb"])),
        [("1a", "ARB Chasm")] = new(MapSpawn: true, Berries: new BerrySet("Chasm", Except: ["11", "10zb"])),
        [("1a", "ARB IL")] = new(Requires: Heart, SpansLeaves: true),
        [("3a", "ARB Huge Mess")] = new(Requires: Heart),
        [("3a", "ARB IL")] = new(Requires: Heart),
        [("4a", "ARB Shrine")] = new(Requires: Heart),
        [("4a", "ARB Old Trail (to RTM)")] = new(EndsOnReturnToMap: true, Berries: new BerrySet("Old Trail", Rooms: ["d-00b"])),
        // d-00b is taken on the way out of Old Trail on that route
        [("4a", "ARB Cliff Face (from RTM)")] = new(MapSpawn: true, Berries: new BerrySet("Cliff Face", Except: ["d-00b"])),
        // d-00 is entered from c-08 on the A-side, and from the berry room here
        [("4a", "ARB Cliff Face")] = new(EntryRoom: "c-10"),
        [("4a", "ARB IL")] = new(Requires: Heart, SpansLeaves: true),
        // the route walks into Depths' room and back before it returns to the map
        [("5a", "ARB Start")] = new(EndsOnReturnToMap: true),
        [("5a", "ARB Depths")] = new(MapSpawn: true),
        [("5a", "ARB IL")] = new(SpansLeaves: true),
        // f-01 is 2500m's left berry: the first piece skips it, the second fetches it
        [("7a", "ARB 2500m-1")] = new(Berries: new BerrySet("2500 M", Except: ["f-01"])),
        [("7a", "ARB 2500m-2")] = new(MapSpawn: true, EndsOnReturnToMap: true, Berries: new BerrySet(Rooms: ["f-01"])),
        // 3000m's pieces are no checkpoint of the map: their berries by room
        [("7a", "ARB Downdraft")] = new(Berries: DowndraftBerries),
        [("7a", "ARB Updraft")] = new(Berries: new BerrySet(Rooms: ["g-01"])),
        [("7a", "ARB Nodraft")] = new(Berries: new BerrySet(Rooms: ["g-03"])),
        [("7a", "ARB IL")] = new(SpansLeaves: true),
        [("1a", "FC Heart to RTM")] = new(EndsOnReturnToMap: true, Requires: Cassette, Berries: HeartToRtmBerries),
        [("1a", "FC Crossing to RTM")] = new(MapSpawn: true, EndsOnReturnToMap: true, Requires: Heart | Cassette, Berries: CrossingToRtmBerries),
        [("1a", "FC Chasm-2")] = new(MapSpawn: true, EndsOnReturnToMap: true, Requires: Cassette, Berries: ChasmTwoBerries),
        [("1a", "FC IL")] = new(Requires: Heart | Cassette, SpansLeaves: true),
        // the heart of 2A's Start is a row of its own, ended by Restart Chapter
        [("2a", "FC Start")] = new(Requires: Cassette),
        [("2a", "FC IL")] = new(Requires: Cassette),
        [("3a", "FC Elevator Shaft")] = new(Requires: Cassette),
        [("3a", "FC IL")] = new(Requires: Heart | Cassette),
        [("4a", "FC Start")] = new(Requires: Cassette),
        [("4a", "FC IL")] = new(Requires: Heart | Cassette, SpansLeaves: true),
        [("5a", "FC Depths")] = new(MapSpawn: true, Requires: Heart | Cassette),
        [("5a", "FC IL")] = new(Requires: Heart | Cassette, SpansLeaves: true),
        // the route took 6A's cassette and heart before returning to the map
        [("6a", "FC Hollows (from RTM)")] = new(MapSpawn: true, NoBerries: true),
        [("6a", "FC IL")] = new(Requires: Heart | Cassette, NoBerries: true, SpansLeaves: true),
        [("7a", "FC Start")] = new(Requires: Gem),
        [("7a", "FC 500m")] = new(Requires: Gem),
        [("7a", "FC 1000m")] = new(Requires: Gem),
        [("7a", "FC 1500m")] = new(Requires: Gem | Cassette),
        [("7a", "FC 2000m")] = new(Requires: Gem),
        [("7a", "FC 2500m")] = new(Requires: Gem),
        [("7a", "FC 3000m")] = new(Requires: Heart),
        [("7a", "FC Downdraft")] = new(Requires: Heart, Berries: DowndraftBerries),
        [("7a", "FC IL")] = new(Requires: Gem | Cassette | Heart),
        // no berry in HotM's horizontal half; 8A's heart ends the chapter
        [("8a", "HotM Horizontal Tape")] = new(Requires: Cassette, NoBerries: true),
        [("8a", "FC IL")] = new(Requires: Cassette),
        [("Farewell", "FC Moon Berry")] = new(Berries: BerrySet.MoonBerry),
        [("Farewell", "FC DTS IL")] = new(Berries: BerrySet.MoonBerry, Dashes: 2),
        [("Farewell", "FC No DTS IL")] = new(Berries: BerrySet.MoonBerry, Dashes: 1),
    };

    public static RowTraits Of(string scope, string name) => All.GetValueOrDefault((scope, name));
}
