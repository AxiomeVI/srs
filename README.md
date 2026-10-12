# Speedrun Sheet (srs)

[Everest](https://everestapi.github.io/) mod for Celeste that requires [Speedrun Tool](https://gamebanana.com/tools/6597). It imports a community practice sheet's reference times, compares your time on a checkpoint with them, and shows the tier you reached in its color. It detects the segment you run on its own: there is nothing to pick.

Options under **Mod Options → Speedrun Sheet**. What changed in each version is in [CHANGELOG.md](CHANGELOG.md).

## Getting started

**Requirements.** Everest 1.6397 or later, and Speedrun Tool 3.27.17 or later. srs times segments with the chapter time, so records and the export do not depend on the room timer's type.

**What you see.** When you complete a segment, srs adds two rows above Speedrun Tool's room timer. On top, the segment it detected (`1a Crossing`). Below it, your time and the tier it reached, in that tier's color (`Gold`, `Purple 2`, ...), or `Unranked` past the last tier; then `PB` and what you gained (`PB -0.214`), in gold, when your own sheet is set up for export and holds a slower time; then how far you are from the next tier (`+0.140 to Purple 1`) in red. A tier needs a time strictly under its threshold, as on the sheet. `Gold` is the best tier: the sheet's WR column is a reference and ranks nothing, so a Gold time shows no gap. When several segments complete together, it shows the most specific one. The rows stay until you leave the room or load a savestate, and show only while the timer is on. After a chapter's end they are still there when you enter one of its checkpoints from the map. Mod Options hide each part: **Show Checkpoint Name**, **Show Time**, **Show Tier** (the tier's name; the time keeps its color), **Show PB Improvement** and **Show Delta to Next Tier**, all on by default.

**Detection.** There is nothing to select. srs times every segment that can be running, so a chain of checkpoints records each segment of the chain.

- A segment is recorded when you walk into its first room from the room before it, however you reached that room: a chain, a savestate, the debug map, `console load` or Speedrun Tool's room teleport. Walking in from anywhere else records nothing.
- A chapter's first segment, and the segment after a wake-up, also start from a savestate made where you appeared in that room, if you had not moved since. A tap of a single frame is a move. Entering a chapter starts its first segment only on the chapter's own spawn.
- The all red berries and full clear rows whose route picks a checkpoint on the map (1A's `ARB Crossing-2`, 5A's `FC Depths`, 6A's `FC Hollows (from RTM)` and others) start only on that checkpoint's own spawn, by picking the checkpoint on the map or from a savestate made there before you moved: walking in does not start them.
- A row whose route ends on a Return to Map (1A's `ARB Start to RTM`, 5A's `ARB Start`, 7A's `ARB 2500m-2` and others) ends when the screen has wiped after you confirm Return to Map.
- 1A's `ARB Heart to RTM` and `FC Heart to RTM` start when you reappear after a Save and Quit taken once the heart is collected, on the spawn by the heart, or from a savestate made there before you moved. Nothing else starts them.
- Eight chapter rows carry on across a Return to Map: `1a ARB IL`, `1a FC IL`, `4a ARB IL`, `4a FC IL`, `5a ARB IL`, `5a FC IL`, `6a FC IL` and `7a ARB IL`. Start at the chapter's start; after a Return to Map, or after the chapter's end, pick a checkpoint of that chapter you already reached in the run, and the row goes on. Its time is the chapter time of every piece added up. It ends with the chapter once everything it asks for is collected, or, if the chapter was finished first, on the Return to Map that finds everything collected. 7A's credits and the Epilogue after them do not break the row. Starting the chapter over, a checkpoint further on than the run went, or another chapter is a new attempt.
- A savestate load or a room teleport starts a new attempt: the segment you were in is not recorded.
- A Save and Quit followed by Continue does not: the segments you were in carry on, with the time the game counts while the screen wipes. Anything else after a Save and Quit (a checkpoint picked on the map, another file, another chapter, `console load`, closing the game) is a new attempt.
- The `Heart`, `Tape`, `Gem`, `ARB`, `FC` and `DTS` rows are told apart by what you collected, and in Farewell by the dashes you have left (for its ILs, the dashes you carry into Singular). An `ARB` row needs every red berry it asks for; one still following you at the end counts, except at a Return to Map, where the game loses it. An `FC` row needs those berries and what the full clear route collects there: a cassette, a heart, a summit gem, or in Farewell the moon berry. 2A's `Start Heart RC` runs until Restart Chapter.

**Hotkeys.** Three, all unbound by default: Toggle Tier Display, Previous Segment and Open Sheet Export. After several checkpoints run in a row, Previous Segment steps the rows above the timer back through the segments of that attempt, and `2/3` before the name says which one is shown. Bind them from **Keybinds**, at the bottom of srs's section. A hotkey can be a combo: all of its keys must be held together.

**Exporting your times (optional).** srs can write the segments you ran into your own copy of the practice sheet. It needs a script deployed on that copy, which takes a few minutes once: see [AppsScript/SETUP.md](AppsScript/SETUP.md). The export screen lists every segment you ran this session, in every chapter, next to the time your sheet holds; the ones you improved start ticked, and one export sends every row you tick. srs keeps a copy of your sheet's times, so the screen and the PB on the timer work before your sheet answers and offline, and the screen says how old that copy is.

**The reference times.** srs downloads them at launch, or when you press **Update Standards**, and keeps a copy in `Saves/srs/` (one CSV per standards tab), so it works offline. CSVs dropped there by hand are read as they are.

**When something looks wrong**, look in Celeste's `log.txt` for lines tagged `[srs]`.

**Known limits.**

- Not every row of the sheet is imported: the `Wake Up` rows are left out.
- srs trusts that your progress in the chapter (opened doors, used keys, broken blocks) is what a real run would have at that point.
- `1a ARB IL` and `1a FC IL` show their time without a tier: their standards are in file time, and srs measures chapter time. Where an all red berries or full clear chapter row has a Timing cell on your sheet, set it to `IL` before exporting: the time srs writes is the chapter time of the pieces.
- After a Return to Map, dying in the checkpoint's first room before you bank a berry or change room resets the game's chapter time. A chapter row in progress keeps the time played before that death, without the respawn; the rows that start on that checkpoint start again.
- A piece started from the map that returns to the map, or reaches the chapter's end once the row had completed, without banking anything the chapter row still lacked drops that row: tries at one piece do not add up.
