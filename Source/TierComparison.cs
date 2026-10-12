using System;
using System.Collections.Generic;
using Celeste.Mod.SpeedrunTool;
using Celeste.Mod.SpeedrunTool.Message;
using Celeste.Mod.SpeedrunTool.RoomTimer;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.SpeedrunSheet;

// Two rows above Speed Run Tool's timer: the segment's name, over its time,
// tier, PB gain and delta to the next tier. Shown until the player leaves
// RunWatcher.LatestRoom (the room the segment closed in, or the one a
// re-entry that kept it landed in), and not again on coming back.
public static class TierComparison {
    private static SrsSettings Settings => SrsModule.Settings;

    // recomputed every frame from RunWatcher.Attempt, so a sheet re-import shows
    // at once; session display, not registered with save states
    private static TierRows? rows;
    private static string segmentName = "";
    private static Color tierColor = Color.White;

    // the record the player walked away from
    private static int dismissedSerial = -1;

    // the gold of Speed Run Tool's timer on a best time. Plain text, not its
    // renderer: that one draws decimals at 70 %, unreadable at this size
    private static readonly Color PbColor = Calc.HexToColor("fad768");

    // LiveSplit's default behind color
    private static readonly Color BehindColor = Calc.HexToColor("cc1200");

    // the player's sheet time for each record of the attempt, by serial, read
    // when the record lands (or when the sheet first answers after it), never
    // again: exporting this very time must not take its PB away. Each entry also
    // holds the rank offset: what the row's standard adds to a time before ranking
    private static readonly Dictionary<int, (bool Resolved, long? Ticks, long? RankOffsetTicks)> sheetTimes = [];
    private static int sheetTimesFrom = -1;

    // whether the rows were drawn last frame: the hotkey steps only what shows
    private static bool drawn;

    // how far back from the latest record the rows show, and its place in
    // the attempt ("2/3"), empty when the attempt has one record
    private static int stepsBack;
    private static int steppedSerial = -1;
    private static string place = "";

    public static void Load() {
        // hook order: see SrsModule.Load
        On.Celeste.Level.Update += LevelOnUpdate;
        On.Celeste.SpeedrunTimerDisplay.Render += SpeedrunTimerDisplayOnRender;
    }

    public static void Unload() {
        On.Celeste.Level.Update -= LevelOnUpdate;
        On.Celeste.SpeedrunTimerDisplay.Render -= SpeedrunTimerDisplayOnRender;
    }

    private static void LevelOnUpdate(On.Celeste.Level.orig_Update orig, Level self) {
        orig(self);

        if (!Settings.Enabled) {
            return;
        }

        // flip the toggle and confirm with Speed Run Tool's popup; Hotkeys
        // answers false while paused
        if (Hotkeys.Pressed(Hotkeys.ToggleShowTier)) {
            Settings.ShowTier = !Settings.ShowTier;
            SrsModule.TrySaveSettings("Show Tier");
            PopupMessageUtils.ShowOptionState(Dialog.Clean("MODOPTIONS_SRS_SHOWTIER"),
                Dialog.Clean(Settings.ShowTier ? DialogIds.On : DialogIds.Off));
        }

        // a new record shows itself; the hotkey steps back from it and wraps,
        // while the rows are up
        if (RunWatcher.LatestSerial != steppedSerial) {
            steppedSerial = RunWatcher.LatestSerial;
            stepsBack = 0;
        }

        if (Hotkeys.Pressed(Hotkeys.PreviousSegment) && drawn && RunWatcher.Attempt.Count > 1) {
            stepsBack = (stepsBack + 1) % RunWatcher.Attempt.Count;
        }

        ComputeTier(self.Session);
    }

    // the shown record's row, looked up by name (see SheetBlock.Find), while
    // the player is still in the room the latest closed in
    private static void ComputeTier(Session session) {
        rows = null;
        if (session.Level != RunWatcher.LatestRoom) {
            dismissedSerial = RunWatcher.LatestSerial;
        }

        // the held sheet times are the current attempt's only. A re-entry
        // trims the attempt from its head: what stays keeps the time read
        // when it landed, and is not read again
        IReadOnlyList<(SegmentRecord Record, int Serial)> attempt = RunWatcher.Attempt;
        int from = attempt.Count > 0 ? attempt[0].Serial : int.MaxValue;
        if (from != sheetTimesFrom) {
            foreach (int serial in new List<int>(sheetTimes.Keys)) {
                if (serial < from) {
                    sheetTimes.Remove(serial);
                }
            }

            sheetTimesFrom = from;
        }

        SheetBlock block = SheetImporter.Data?.Block;
        if (attempt.Count == 0 || block == null) {
            return;
        }

        foreach ((SegmentRecord landed, int serial) in attempt) {
            if ((!sheetTimes.TryGetValue(serial, out var held) || (!held.Resolved && RemoteBests.IsResolved))
                && block.Find(landed.Rule.Scope, landed.Rule.Name) is { } row) {
                (long? ticks, long? rankOffset) = SheetTimeOf(landed.Rule, row);
                sheetTimes[serial] = (RemoteBests.IsResolved, ticks, rankOffset);
            }
        }

        // every new record resets stepsBack before this; the guard is for the index
        stepsBack = Math.Min(stepsBack, attempt.Count - 1);
        (SegmentRecord record, int shownSerial) = attempt[attempt.Count - 1 - stepsBack];
        if (RunWatcher.LatestSerial == dismissedSerial
            || block.Find(record.Rule.Scope, record.Rule.Name) is not { } segment) {
            return;
        }

        (_, long? sheetTicks, long? rankOffsetTicks) = sheetTimes[shownSerial];
        TierRows built = TierLine.Build(block.Columns, segment.Times, record.Ticks, sheetTicks, rankOffsetTicks);
        rows = built;
        segmentName = TierLine.NameOf(record.Rule.Scope, record.Rule.Name);
        place = attempt.Count > 1 ? $"{attempt.Count - stepsBack}/{attempt.Count}" : "";
        tierColor = built.Tier == SheetData.Unranked ? Color.Gray : TierColors.GetValueOrDefault(built.Tier, Color.White);
    }

    // the time the player's own sheet holds for the row, in the basis srs
    // measures as the export screen compares it, and the row's rank offset;
    // no time without an export URL or before the sheet answered
    private static (long? Ticks, long? RankOffsetTicks) SheetTimeOf(SegmentRule rule, SheetSegment segment) {
        RemoteRow remote = SheetRows.TryFind(segment.Chapter, segment.Name, out SheetRow row)
            && RemoteBests.TryGet(SheetRows.TargetOf(row), out RemoteRow found) ? found : null;
        return (SheetTiming.Comparable(remote).Ticks, TierLine.RankOffsetTicks(rule, remote));
    }

    // tier colors, copied from the sheet: each tier's cell fill, keyed by the
    // full column name (the rank suffix is significant). Gold takes its ink,
    // since its fill is near black; Unranked is grey, as on the
    // sheet; unknown columns fall back white
    private static readonly Dictionary<string, Color> TierColors =
        new(StringComparer.OrdinalIgnoreCase) {
            ["Gold"] = Calc.HexToColor("ffbf00"),
            ["Pink"] = Calc.HexToColor("a64d79"),
            ["Purple 1"] = Calc.HexToColor("351c75"),
            ["Purple 2"] = Calc.HexToColor("674ea7"),
            ["Purple 3"] = Calc.HexToColor("8e7cc3"),
            ["Indigo 1"] = Calc.HexToColor("4d31bf"),
            ["Indigo 2"] = Calc.HexToColor("7980f7"),
            ["Indigo 3"] = Calc.HexToColor("a2a7fe"),
            ["Blue 1"] = Calc.HexToColor("073763"),
            ["Blue 2"] = Calc.HexToColor("0b5394"),
            ["Blue 3"] = Calc.HexToColor("3d85c6"),
            ["Cyan 1"] = Calc.HexToColor("003d3b"),
            ["Cyan 2"] = Calc.HexToColor("105755"),
            ["Cyan 3"] = Calc.HexToColor("458e8c"),
            ["Green 1"] = Calc.HexToColor("274e13"),
            ["Green 2"] = Calc.HexToColor("38761d"),
            ["Green 3"] = Calc.HexToColor("6aa84f"),
            ["Olive 1"] = Calc.HexToColor("88995f"),
            ["Olive 2"] = Calc.HexToColor("a0b275"),
            ["Olive 3"] = Calc.HexToColor("bacc85"),
            ["Yellow 1"] = Calc.HexToColor("bf9000"),
            ["Yellow 2"] = Calc.HexToColor("dcac18"),
            ["Yellow 3"] = Calc.HexToColor("f1c232"),
            ["Orange 1"] = Calc.HexToColor("b45f06"),
            ["Orange 2"] = Calc.HexToColor("d17618"),
            ["Orange 3"] = Calc.HexToColor("e69138"),
            ["Red 1"] = Calc.HexToColor("990000"),
            ["Red 2"] = Calc.HexToColor("cc0000"),
            ["Red 3"] = Calc.HexToColor("e06666"),
        };

    private static void SpeedrunTimerDisplayOnRender(On.Celeste.SpeedrunTimerDisplay.orig_Render orig, SpeedrunTimerDisplay self) {
        orig(self);
        drawn = false;

        // hidden along with the room timer itself, and with the whole mod
        if (!Settings.Enabled || rows is not { } shown
            || SpeedrunToolSettings.Instance is not { Enabled: true } settings
            || settings.RoomTimerType == RoomTimerType.Off || self.DrawLerp <= 0f) {
            return;
        }

        // the time keeps the tier's color without the tier
        List<(string Text, Color Color)> parts = [];
        string ranked = $"{(Settings.ShowTime ? shown.Time : "")} {(Settings.ShowTier ? shown.Tier : "")}".Trim();
        if (ranked.Length > 0) {
            parts.Add((ranked, tierColor));
        }

        if (Settings.ShowPbImprovement && shown.Pb != null) {
            parts.Add((shown.Pb, PbColor));
        }

        if (Settings.ShowDelta && shown.Gap != null) {
            parts.Add((shown.Gap, BehindColor));
        }

        drawn = true;
        int slot = 0;
        if (parts.Count > 0) {
            DrawRow(self, slot++, parts);
        }

        if (Settings.ShowCheckpointName) {
            DrawRow(self, slot, place.Length > 0
                ? [(place, Color.Gray), (segmentName, Color.White)]
                : [(segmentName, Color.White)]);
        }
    }

    private const string PartSeparator = "  ";

    // two rows fit above the timer at this size, below the top of the screen
    private const float Scale = 0.54f;

    // the timer's digits rise above its background: the rows end above them
    private const float DigitRise = 10f;

    // art shows between the two bands, or the rows read as one block
    private const float RowGap = 4f;

    // Speed Run Tool's text placement sits the ink high in the band: lowered
    // to its middle
    private const float TextDrop = 2f;

    // the black ends this far before the text does, so the fade's tail runs
    // the same length past every row: the look of Speed Run Tool's PB row
    // on a time of eight characters
    private const float FadeLead = 78f;

    // slot 0 sits right above the timer, slot 1 above it; same background and
    // sliding animation as Speed Run Tool's PB row, scaled down. One band for
    // the whole row, sized on what is written
    private static void DrawRow(SpeedrunTimerDisplay self, int slot, List<(string Text, Color Color)> parts) {
        const float timeMarginLeft = 32f;
        float ratio = Scale / 0.6f;

        // measured on the size DrawOutline picks for this scale, which is not
        // the base size when the font has several loaded
        PixelFont font = Dialog.Languages["english"].Font;
        float baseSize = Dialog.Languages["english"].FontFaceSize;
        PixelFontSize size = font.Get(baseSize * Scale);
        float Width(string text) => size.Measure(text).X * Scale * baseSize / size.Size;

        MTexture bg = GFX.Gui["strawberryCountBG"];
        float rowHeight = bg.Height * Scale + 1f;
        float x = -300f * Ease.CubeIn(1f - self.DrawLerp);
        float y = self.Y - DigitRise - rowHeight - slot * (rowHeight + RowGap);

        string whole = string.Join(PartSeparator, parts.ConvertAll(part => part.Text));
        float width = Math.Max(0f, timeMarginLeft + Width(whole) - FadeLead);
        Draw.Rect(x, y, width + 2f, rowHeight, Color.Black);
        bg.Draw(new Vector2(x + width, y), Vector2.Zero, Color.White, Scale);

        Vector2 at = new(x + timeMarginLeft, y + 28.4f * ratio + TextDrop);
        foreach ((string part, Color color) in parts) {
            font.DrawOutline(baseSize, part, at, new Vector2(0f, 1f), Vector2.One * Scale, color, 2f, Color.Black);
            at.X += Width(part + PartSeparator);
        }
    }
}
