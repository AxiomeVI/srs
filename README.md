# Speedrun Sheet (srs)

An [Everest](https://everestapi.github.io/) mod for Celeste that grades your practice against a community practice sheet. Finish a segment and srs shows your time above [Speedrun Tool](https://gamebanana.com/tools/6597)'s room timer, colored by the tier it reached. You don't have to tell it what you're practicing: it works that out from where you play.

Settings are in **Mod Options → Speedrun Sheet**, and the [changelog](CHANGELOG.md) lists what changed in each version.

## Requirements

Everest 1.6397 or newer and Speedrun Tool 3.27.17 or newer. If a future Speedrun Tool update changes something srs depends on, srs turns itself off instead of showing wrong times, and Mod Options shows a red line under its name until srs catches up.

Times come from the chapter timer, so it doesn't matter how your room timer is set up.

## What it looks like

When you finish a segment, two lines show up above the room timer. The top one is the segment's name, like `1a Crossing`. Under it you get your time and the tier you reached (`Gold`, `Purple 2`, ...) in that tier's color, or `Unranked` if you're slower than every tier. If you've connected your own copy of the sheet and your time beats what's in it, there's also your improvement in gold (`PB -0.214`). Last, in red, how far you are from the next tier: `+0.140 to Purple 1`.

Differences of 0.1 s or less are shown in frames, 17 ms each, so a PB by three frames reads `PB -3f`. For the next tier, it's the number of frames you still have to save: a time exactly on the threshold reads `+1f`, and 0.1 s behind reads `+6f`.

Like on the sheet, you need to be strictly under a threshold to get that tier. Gold is the best you can get. The WR column is only there for reference, so a Gold time has nothing left to chase and shows no gap.

The lines stay until you leave the room or make or load a savestate, and only while the room timer is visible. If you finish a chapter and come back to one of its checkpoints from the map, they're still there. When several segments end at the same time, srs shows the most specific one.

You can turn each piece off in Mod Options: checkpoint name, time, tier name (the time stays colored), PB improvement and the gap to the next tier.

## How detection works

There's nothing to select. srs keeps track of every segment you could be running at once, so playing three checkpoints in a row gives you three times.

A segment starts when you walk into its first room from the room before it. How you got there doesn't matter: chaining checkpoints, a savestate, the debug map, `console load` or Speedrun Tool's room teleport all work. Walk back out to the previous room and that segment is dropped, and it won't start again until you restart, load a savestate or go back to the map. A chapter's first segment, and the one right after a wake-up, can also start from a savestate made where you appeared in the room, as long as you hadn't moved. Even a one-frame tap counts as moving. Entering a chapter only starts its first segment on the chapter's own spawn.

Loading or making a savestate, or teleporting, is a new attempt, so whatever you were running isn't recorded. Making a savestate counts as loading it right away: a segment that can start from a savestate starts from the one you just made, with the same time.

Save and Quit then Continue is fine: your segments keep going, and the time the game counts during the screen wipe is included. Anything else after a Save and Quit (picking a checkpoint on the map, switching file or chapter, `console load`, closing the game) is a new attempt.

Nothing is recorded with an assist or variant on (game speed, invincibility, extra dashes, Mirror Mode and so on), since those times can't be compared. Play as Badeline is fine, and so is a file with Assist or Variant Mode enabled as long as every option is at its default.

Rows that ask for something specific (`Heart`, `Tape`, `Gem`, `ARB`, `FC`, `DTS`) are matched by what you collected, and in Farewell by how many dashes you have left (for its ILs, how many you carry into Singular). An `ARB` row needs every red berry it lists. A berry still following you at the end counts, except on a Return to Map, where the game drops it. An `FC` row also needs whatever the full clear picks up there: a cassette, a heart, a summit gem, or in Farewell the moon berry.

<details>
<summary>Rows with their own start or end</summary>

- **8A's Start rows** (`8a Start`, `8a IL`, `8a IL Tape Clear`, `8arb IL`, `8afc IL`) only start when Core loads from the beginning: chapter select, `console load`, Restart Chapter, or Continue after a Save and Quit in the first room. Savestates, teleports and the debug map don't start them.
- **Routes that begin by picking a checkpoint on the map**, like 5A's `FC Depths`, 6A's `FC Hollows (from RTM)` or 7A's `ARB 2500m-2`, start only on that checkpoint's spawn: pick it on the map, or load a savestate made there before you moved. Walking in won't start them. A few start both ways, on the spawn or when you walk in from the previous checkpoint: 1A's Crossing and Chasm rows on both tabs (`ARB Crossing`, `ARB Crossing to Heart`, `ARB Crossing to RTM`, `ARB Crossing-2`, `ARB Chasm`, `ARB Chasm-2`, `FC Crossing to RTM`, `FC Chasm-2`) and 5A's `ARB Depths`.
- **Routes that end on a Return to Map**, like 1A's `ARB Start to RTM` or 7A's `ARB 2500m-2`, end once the screen has wiped after you confirm. 5A's `ARB Start` can end two ways: return to the map, or grab every berry and walk into Depths (which also starts `ARB Depths`).
- **1A's `ARB Heart to RTM` and `FC Heart to RTM`** start when you respawn next to the heart after a Save and Quit with the heart collected, or from a savestate made there before you moved.
- **2A's `Start Heart RC`** ends when you touch the heart.
- **Eight chapter rows survive a Return to Map**: `1arb IL`, `1afc IL`, `4arb IL`, `4afc IL`, `5arb IL`, `5afc IL`, `6afc IL` and `7arb IL`. Start the chapter from the beginning. After a Return to Map, or after finishing the chapter, pick a checkpoint you already reached in that run and the row carries on, adding up the chapter time of each piece. It ends with the chapter once you have everything it needs, or, if you finished the chapter first, on the Return to Map that finds everything collected. 7A's credits and Epilogue don't break it. Starting over, picking a checkpoint you hadn't reached, or switching chapter is a new attempt.
- **Four checkpoint rows survive a Return to Map taken inside their checkpoint**: 1A's `Chasm Tape Clear`, 7A's `2000m Gem`, `FC 2000m` and `FC 2500m`. Pick the same checkpoint again and the row goes on, timed as both pieces together. Any other checkpoint is a new attempt.

</details>

### SomeSplitButtons

With a version of SomeSplitButtons that reports its splits to other mods, srs reads its buttons as the real thing. Its Save and Quit button plus the room reload counts as Save and Quit then Continue (and if nothing reloads, your segments just keep going). Its Return to Map button is a Return to Map. Picking a checkpoint from its list, or from the one its Skip Cutscene button opens at the end of a chapter, is the same as picking it on the map. Closing that list without picking anything is a new attempt, and a chapter row in progress is dropped.

With an older version, or without the mod, a split's reload is just a new attempt.

## Hotkeys

Both are unbound by default. Set them under **Keybinds** at the bottom of the srs section; a hotkey can be a combo of several keys held together.

**Cycle Shown Segment** goes back through the segments of your current attempt when you've played several checkpoints in a row. The `2/3` before the name tells you which one you're looking at. The list survives a Save and Quit and Continue, and picking a checkpoint on the map while a chapter row is still going.

**Open Sheet Export** opens the export screen (see below).

## Exporting to your own sheet

This part is optional. srs can write your times into your own copy of the practice sheet, once you've added a small script to it. It takes a few minutes and you only do it once: [AppsScript/SETUP.md](AppsScript/SETUP.md) walks you through it.

The export screen lists everything you ran this session, across all chapters, next to the time your sheet already has. Improvements are at the top and already ticked, and one export sends everything ticked. srs keeps a local copy of your sheet's times, so the screen and the PB line work even offline or before your sheet has answered. The screen tells you how old that copy is.

If you'd rather not open the screen at all, turn on **Auto Export on PB** (it shows up under **Your Sheet** once your web app URL is set). Every time a segment ends with a time the export screen would have ticked, srs sends it straight away. You won't see anything when it works. If a row is refused or the sheet doesn't answer, you get one message saying how many rows didn't go through, and they stay ticked on the export screen. Auto-export doesn't ask before sending, so anything srs gets wrong (see the known limits below) goes to your sheet as well.

Segments CelesteTAS played even a single frame of are never recorded, even if you finished them yourself, so they're never listed or sent.

## Reference times

srs downloads the sheet's times when the game starts, or when you press **Update Standards**, and saves a copy in `Saves/srs/` (one CSV per tab) so it also works offline. Highlighting the button shows when they were last updated. If an update fails, a red line stays under the button and srs keeps using what it already had. You can also drop CSVs in that folder yourself and srs will read them as they are.

## Troubleshooting

Celeste's `log.txt` has srs's messages, tagged `[srs]`.

If a run didn't count and you want to know why, turn on the trace. Enable Everest's **Debug Mode**, open the console with `~` and type `setloglevel srs verbose`. Until you close the game, srs writes a `trace` line every time a segment starts, waits for you to have control, finishes, or gets dropped or skipped, with the reason. To keep it on between sessions, close the game and change `LogLevels: {}` to `LogLevels: {srs: Verbose}` in `modsettings-Everest.celeste`, in Celeste's `Saves` folder.

## Known limits

- srs assumes the state of the chapter (doors opened, keys used, blocks broken) is what a real run would have at that point. It can't tell if it isn't.
- After a Return to Map, dying in the checkpoint's first room before you bank a berry or change room resets the game's chapter timer. A chapter row in progress, or one of the checkpoint rows that survives a Return to Map (1A's `Chasm Tape Clear`, 7A's `2000m Gem`, `FC 2000m` or `FC 2500m`), keeps the time you had before the death, not counting the respawn. Rows that start on that checkpoint start over.
