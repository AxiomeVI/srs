using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Celeste.Mod.SpeedrunTool.Message;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.SpeedrunSheet;

/// One checkbox row of the export screen. TextMenu has no built-in item for this.
internal sealed class UpdateRow : TextMenu.Item {
    // LiveSplit's default ahead/behind colours
    private static readonly Color Ahead = Calc.HexToColor("00CC36");
    private static readonly Color Behind = Calc.HexToColor("CC1200");

    private readonly ExportColumns columns;
    private readonly bool odd;
    private readonly PendingUpdate update;
    private readonly Action onPressed;

    public UpdateRow(PendingUpdate update, ExportColumns columns, bool odd, Action onPressed) {
        this.update = update;
        this.onPressed = onPressed;
        this.columns = columns;
        this.odd = odd;
        // false on the base item: without it the cursor never lands on the row
        Selectable = true;
    }

    public SheetRowRef Row => update.Row;

    public override void ConfirmPressed() {
        // a duplicate, or a FileTime cell srs cannot switch, is never written
        if (!update.Writable) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }

        update.Selected = !update.Selected;
        onPressed();
        Audio.Play(update.Selected ? "event:/ui/main/button_toggle_on" : "event:/ui/main/button_toggle_off");
    }

    public override float LeftWidth() => columns.TotalWidth;
    public override float Height() => ExportColumns.RowHeight;

    public override void Render(Vector2 position, bool highlighted) {
        float alpha = Container.Alpha;

        // both parities banded: one stripe over bare background reads as a
        // tinted list, two read as a grid
        Color band = highlighted
            ? Color.White * (0.22f * alpha)
            : Color.White * ((odd ? 0.09f : 0.04f) * alpha);
        ExportColumns.Band(position, Container.Width, band);

        ExportColumns.Checkbox(position, update.Selected, Color.White * alpha);

        Color text = Color.White * alpha;
        ExportColumns.Text(update.Label, position, columns.LabelX, text, alpha, left: true);
        ExportColumns.Text(update.RemoteText, position, columns.RemoteX, Color.Gray * alpha, alpha);
        ExportColumns.Text(update.AdjustedText, position, columns.AdjustedX, Color.Gray * alpha, alpha);
        ExportColumns.Text(update.LocalText, position, columns.LocalX, text, alpha);
        ExportColumns.Text(update.DeltaText, position, columns.DeltaX, DeltaColor(update) * alpha, alpha);
    }

    // neutral with nothing to compare against and on equal times: "+0.000" in
    // red says a regression that did not happen, and an unreadable cell's "?"
    // is a refusal
    private static Color DeltaColor(PendingUpdate update) => update.Ahead switch {
        true => Ahead,
        false => Behind,
        null => Color.Gray,
    };
}

/// The column titles, repeated over each chapter: TextMenu scrolls the whole
/// menu, so a single header at the top would scroll away.
internal sealed class GroupRow(ExportColumns columns, string label) : TextMenu.Item {
    public override float LeftWidth() => columns.TotalWidth;
    public override float Height() => ExportColumns.RowHeight;

    public override void Render(Vector2 position, bool highlighted) {
        float alpha = Container.Alpha;
        Color color = Color.Gray * alpha;

        ExportColumns.Rule(position, Container.Width, -Height() / 2f, alpha);
        ExportColumns.Rule(position, Container.Width, Height() / 2f, alpha);

        // the chapter the rows below belong to: the row labels do not carry it
        ExportColumns.Text(label, position, columns.LabelX, color, alpha, left: true);
        ExportColumns.Text(Dialog.Clean("SRS_EXPORT_COL_SHEET"), position, columns.RemoteX, color, alpha);
        if (columns.HasAdjusted) {
            ExportColumns.Text(Dialog.Clean("SRS_EXPORT_COL_ADJUSTED"), position, columns.AdjustedX, color, alpha);
        }

        ExportColumns.Text(Dialog.Clean("SRS_EXPORT_COL_LOCAL"), position, columns.LocalX, color, alpha);
        ExportColumns.Text(Dialog.Clean("SRS_EXPORT_COL_DELTA"), position, columns.DeltaX, color, alpha);
    }
}

/// A rule closing the table, so the buttons below read as buttons and not as
/// two more rows.
internal sealed class TableFooter(ExportColumns columns) : TextMenu.Item {
    public override float LeftWidth() => columns.TotalWidth;
    public override float Height() => ExportColumns.RowHeight / 2f;

    public override void Render(Vector2 position, bool highlighted) =>
        ExportColumns.Rule(position, Container.Width, 0f, Container.Alpha);
}

/// Column geometry and the primitives every row of the table draws with.
/// TextMenu hands an item the vertical CENTRE of its slot, so everything here
/// is anchored on that: text justifies at y = 0.5, bands and rules are centred.
internal sealed class ExportColumns {
    public const float Gap = 18f;

    // the table's own margin: the banding and rules span the whole menu, so
    // without it the checkbox and the delta column touch its edges
    private const float Pad = 24f;

    // the journal's table scale. TextMenu.SubHeader's 0.6 is sized for menu
    // chrome, not for a table
    private const float Scale = 0.5f;
    private const float BandRatio = 0.9f;   // a gutter survives between stripes
    private const float RuleHeight = 2f;
    private const float BoxRatio = 0.45f;

    public static float RowHeight => ActiveFont.LineHeight * Scale * 1.2f;

    public float LabelX { get; private init; }
    public float RemoteX { get; private init; }
    // on RemoteX, with no width, when no row has an adjusted time
    public float AdjustedX { get; private init; }
    public bool HasAdjusted => AdjustedX > RemoteX;
    public float LocalX { get; private init; }
    public float DeltaX { get; private init; }
    public float TotalWidth => DeltaX + Pad;

    /// x is the left edge for the label column, the right edge for the times:
    /// digits only line up when they are anchored on the right.
    public static void Text(string text, Vector2 position, float x, Color color, float alpha, bool left = false) {
        ActiveFont.DrawOutline(text, position + new Vector2(x, 0f),
            new Vector2(left ? 0f : 1f, 0.5f), Vector2.One * Scale, color,
            2f, Color.Black * (alpha * alpha * alpha));
    }

    public static void Band(Vector2 position, float width, Color color) {
        float height = RowHeight * BandRatio;
        Draw.Rect(position.X, MathF.Floor(position.Y - height / 2f), width, height, color);
    }

    public static void Rule(Vector2 position, float width, float offsetY, float alpha) {
        Draw.Rect(position.X, MathF.Floor(position.Y + offsetY - RuleHeight / 2f),
            width, RuleHeight, Color.White * (0.3f * alpha));
    }

    /// The atlas has no tick sprite. An outlined box that fills when ticked is
    /// what reads as a checkbox; dot_outline reads as a bullet.
    public static void Checkbox(Vector2 position, bool ticked, Color color) {
        float size = RowHeight * BoxRatio;
        float x = position.X + Pad;
        float y = MathF.Floor(position.Y - size / 2f);
        Draw.HollowRect(x, y, size, size, color);
        if (ticked) {
            float inset = size * 0.25f;
            Draw.Rect(x + inset, y + inset, size - inset * 2f, size - inset * 2f, color);
        }
    }

    private static float Width(string text) => ActiveFont.Measure(text).X * Scale;

    public static ExportColumns Measure(List<PendingUpdate> updates) {
        // floors, so a column does not resize when the fetch lands and the
        // sheet column goes from "" to real times
        float floor = Width("00:00.000");
        // no floor on the label column: it has no header to stay as wide as
        float label = 0f;
        float remote = Math.Max(floor, Width(Dialog.Clean("SRS_EXPORT_COL_SHEET")));
        float local = Math.Max(floor, Width(Dialog.Clean("SRS_EXPORT_COL_LOCAL")));
        float delta = Math.Max(floor, Width(Dialog.Clean("SRS_EXPORT_COL_DELTA")));
        // floored and titled only once a row fills it: see adjustedX
        float adjusted = 0f;
        foreach (PendingUpdate u in updates) {
            label = Math.Max(label, Width(u.Label));
            remote = Math.Max(remote, Width(u.RemoteText));
            adjusted = Math.Max(adjusted, Width(u.AdjustedText));

            local = Math.Max(local, Width(u.LocalText));
            delta = Math.Max(delta, Width(u.DeltaText));
        }
        float labelX = Pad + RowHeight * BoxRatio + Gap;
        float remoteX = labelX + label + Gap + remote;   // right edge
        // right edge; on RemoteX when empty
        float adjustedX = adjusted > 0f
            ? remoteX + Gap + Math.Max(adjusted, Math.Max(floor, Width(Dialog.Clean("SRS_EXPORT_COL_ADJUSTED"))))
            : remoteX;
        float localX = adjustedX + Gap + local;          // right edge
        return new ExportColumns {
            LabelX = labelX,
            RemoteX = remoteX,
            AdjustedX = adjustedX,
            LocalX = localX,
            DeltaX = localX + Gap + delta,               // right edge
        };
    }
}

/// The review screen: which of this session's times to push to the sheet, with
/// nothing written before it is confirmed. It pauses the level, and the hotkey
/// that opens it closes it. SheetReader owns every read; this file only draws.
///
/// ⚠️ Hook order: see SrsModule.Load.
internal static class ExportMenu {
    private const string LogTag = "srs";

    private enum Screen { None, Loading, Table, Writing, Summary }

    private static TextMenu menu;
    private static Screen screen;

    // the Level the screen is open on, and whether it was paused before Open()
    // forced it. ⚠️ A Level is normally never held across frames: OnLevelUpdate
    // closes the screen when this one is replaced under it
    private static Level openLevel;
    private static bool pausedBeforeOpen;

    // read by Hotkeys: the level is paused because this screen paused it, so
    // the hotkey that opened it must keep being read in order to close it
    internal static bool HoldsThePause => openLevel != null;

    // the answer the table was built on, and the rows the player pressed: a
    // rebuild on a newer answer keeps those
    private static int builtOnAccepts;
    private static readonly Dictionary<SheetRowRef, bool> pressed = [];

    // the buttons of the table on screen, to keep the cursor on one through a rebuild
    private static TextMenu.Button exportButtonOnScreen;
    private static TextMenu.Button cancelButtonOnScreen;
    // the Writing screen's line, which counts the rows answered
    private static TextMenu.SubHeader writingLine;

    // how stale a fresh answer has to be before opening the screen asks again
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromSeconds(60);

    // what a worker hands the game thread, drained in OnLevelUpdate: a TextMenu
    // is never touched off it. Each action reads the screen as it is when it runs
    private static readonly ConcurrentQueue<Action> gameThread = new();

    // bumped by every Open(). Close() cancels nothing in flight: a POST's
    // continuation checks this to know its screen is gone
    private static volatile int generation;

    // read by AutoExport's worker: it sends nothing while a screen is up
    internal static volatile bool IsOpen;

    public static void Load() {
        // Dialog loads after the mods do, and the launch read can be answered
        // before it, when Dialog.Clean throws: the key is logged instead
        ExportProtocol.Localize = key => Dialog.Language == null ? key : Dialog.Clean(key);

        On.Celeste.Level.Update += OnLevelUpdate;

        // about a second of the round trip is Google's dispatch whatever the
        // script does; starting here is what opens the screen on data
        SheetReader.Refresh("launch");
    }

    public static void Unload() {
        On.Celeste.Level.Update -= OnLevelUpdate;
        Close();
    }

    private static void OnLevelUpdate(On.Celeste.Level.orig_Update orig, Level self) {
        orig(self);

        // held back under a wipe, which would cover the popup; taken with the
        // mod off, so a count is dropped then
        int failed = self.Wipe == null || !SrsModule.Settings.Enabled ? AutoExport.TakeFailed() : 0;

        // a level replaced under an open screen would leave `menu` set and
        // Open() refusing for the session. Speed Run Tool's hotkeys refuse a load
        // while paused, but a console load does it
        if (menu != null && menu.Scene != self) {
            Logger.Log(LogLevel.Warn, LogTag, "the level was replaced under the export screen; closed it");
            Close();
        }

        // switched off with the screen open: a menu left behind could still
        // submit an export while the mod is inert
        if (!SrsModule.Settings.Enabled) {
            if (menu != null) {
                Close();
            }

            gameThread.Clear();
            return;
        }

        if (failed > 0) {
            PopupMessageUtils.Show(
                Dialog.Clean("SRS_AUTOEXPORT_FAILED_1") + " " + failed + " " + Dialog.Clean("SRS_AUTOEXPORT_FAILED_2"), null);
        }

        // Hotkeys holds the combo at rest behind the pause menu but not behind
        // this screen's own pause: one press opens, the next closes
        if (Hotkeys.Pressed(Hotkeys.OpenExportMenu)) {
            if (menu != null) {
                Close();
            } else {
                Open(self);
            }
        }

        while (gameThread.TryDequeue(out Action action)) {
            action();
        }

        // an answer taken in since the table or the loading screen went up
        if ((screen is Screen.Table or Screen.Loading) && RemoteBests.IsResolved
            && RemoteBests.Accepts != builtOnAccepts) {
            BuildTable(self);
        }
    }

    public static void Open(Level level) {
        if (menu != null) {
            return;
        }

        // a wipe keeps running under the pause, and the one after a death ends
        // in Level.Reload, which removes the screen with every other entity
        if (level.Wipe != null) {
            return;
        }

        if (!ExportTarget.IsSet) {
            PopupMessageUtils.Show(Dialog.Clean("SRS_EXPORT_NEEDS_URL"), null);
            return;
        }

        // read before the data it describes, so an answer landing meanwhile still triggers a rebuild
        int accepts = RemoteBests.Accepts;
        List<PendingUpdate> updates = ExportSource.Collect(SessionBests.All, SegmentAutoDetect.ScopeOf(level.Session));
        Logger.Log(LogLevel.Info, LogTag,
            $"export: {updates.Count} rows of {SessionBests.All.Count} run, sheet times {RemoteBests.Source}");
        // nothing run this session, or runs that map to no row
        if (updates.Count == 0) {
            PopupMessageUtils.Show(Dialog.Clean("SRS_EXPORT_NOTHING"), null);
            return;
        }

        pausedBeforeOpen = level.Paused;
        openLevel = level;
        level.Paused = true;
        generation++;
        pressed.Clear();

        if (RemoteBests.IsResolved) {
            BuildTable(level, updates, accepts);
        } else {
            ShowLoading(level, accepts);
        }

        // a saved copy, a failed read or an unanswered export say nothing about
        // the sheet now; an answer under a minute old does, and asking three
        // times in a minute costs three calls against the player's script
        if (RemoteBests.Source != HeldSource.Fresh || RemoteBests.Error != null
            || SheetReader.UnansweredWrite || RemoteBests.Age > AskAgainAfter) {
            SheetReader.Refresh("the export screen opened");
        }
    }

    public static void Close() {
        menu?.RemoveSelf();
        menu = null;
        screen = Screen.None;
        pressed.Clear();
        exportButtonOnScreen = null;
        cancelButtonOnScreen = null;
        gameThread.Clear();

        if (openLevel != null) {
            openLevel.Paused = pausedBeforeOpen;
            openLevel = null;
        }

        IsOpen = false;
        AutoExport.Wake();
    }

    /// Puts a screen up in place of whatever is there. Every screen goes through
    /// here so the three ways out are wired once: a screen forgetting one traps
    /// the player in a paused level.
    private static void Show(Level level, TextMenu newMenu, Screen kind) {
        newMenu.OnCancel = Close;
        newMenu.OnESC = Close;
        newMenu.OnPause = Close;

        menu?.RemoveSelf();
        level.Add(newMenu);
        menu = newMenu;
        screen = kind;
        IsOpen = true;
    }

    private static void BuildTable(Level level) {
        // before the data, as in Open
        int accepts = RemoteBests.Accepts;
        BuildTable(level, ExportSource.Collect(SessionBests.All, SegmentAutoDetect.ScopeOf(level.Session)), accepts);
    }

    private static void BuildTable(Level level, List<PendingUpdate> updates, int accepts) {
        ExportTable.KeepPressed(updates, pressed);
        bool wasTable = screen == Screen.Table;
        SheetRowRef? cursor = wasTable && menu?.Current is UpdateRow { } current ? current.Row : null;
        bool onExport = wasTable && exportButtonOnScreen != null && menu?.Current == exportButtonOnScreen;
        bool onCancel = wasTable && cancelButtonOnScreen != null && menu?.Current == cancelButtonOnScreen;
        builtOnAccepts = accepts;

        ExportColumns columns = ExportColumns.Measure(updates);
        TextMenu newMenu = new();
        newMenu.Add(new TextMenu.Header(Dialog.Clean("SRS_EXPORT_TITLE")));
        AddStatus(newMenu, columns.TotalWidth);

        string chapter = null;
        bool improves = true;
        bool odd = false;
        UpdateRow cursorRow = null;
        foreach (PendingUpdate update in updates) {
            string group = string.IsNullOrEmpty(update.Row.Chapter) ? update.Row.Tab : update.Row.Chapter;
            // the header comes again where the rows that beat the sheet end, so
            // that the boundary shows within one chapter too
            if (group != chapter || update.Improves != improves) {
                chapter = group;
                improves = update.Improves;
                newMenu.Add(new GroupRow(columns, ExportTable.GroupLabel(update.Row)));
            }

            UpdateRow row = new(update, columns, odd, () => pressed[update.Row] = update.Selected);
            newMenu.Add(row);
            if (update.Row == cursor) {
                cursorRow = row;
            }

            odd = !odd;
        }

        newMenu.Add(new TableFooter(columns));

        TextMenu.Button exportButton = new(ExportLabel(updates)) { Disabled = !CanExport() };
        exportButton.OnUpdate = () => {
            exportButton.Label = ExportLabel(updates);
            exportButton.Disabled = !CanExport();
        };
        exportButton.Pressed(() => Submit(level, updates));
        newMenu.Add(exportButton);

        TextMenu.Button cancelButton = new(Dialog.Clean("SRS_EXPORT_CANCEL"));
        cancelButton.Pressed(Close);
        newMenu.Add(cancelButton);

        Show(level, newMenu, Screen.Table);
        exportButtonOnScreen = exportButton;
        cancelButtonOnScreen = cancelButton;
        if (cursorRow != null) {
            newMenu.Selection = newMenu.IndexOf(cursorRow);
        } else if (onExport) {
            newMenu.Selection = newMenu.IndexOf(exportButton);
        } else if (onCancel) {
            newMenu.Selection = newMenu.IndexOf(cancelButton);
        }
    }

    // no export while one is out or its outcome is unknown: rows it wrote would
    // be offered again. IsWriting is read first: EndWrite changes both in one
    // locked section, and this order leaves no frame where both read false
    private static bool CanExport() =>
        RemoteBests.IsResolved && !SheetReader.IsWriting && !SheetReader.UnansweredWrite
        && !SheetReader.ScriptOutOfDate;

    private static string ExportLabel(List<PendingUpdate> updates) =>
        $"{Dialog.Clean("SRS_EXPORT_CONFIRM")} ({updates.Count(u => u.Selected && u.Writable)})";

    /// The two lines under the title, recomputed every frame: what is held,
    /// then what the read did (hidden, with no height, when empty). Both
    /// wrap at maxWidth: a wider line widens the menu and shifts the table.
    private static void AddStatus(TextMenu newMenu, float maxWidth) {
        (string one, string two) = StatusLines();
        TextMenu.SubHeader first = new(Wrap(one, maxWidth));
        TextMenu.SubHeader second = new(Wrap(two, maxWidth), topPadding: false) { Visible = two != "" };
        first.OnUpdate = () => {
            (string nowOne, string nowTwo) = StatusLines();
            if (nowOne != one) {
                one = nowOne;
                first.Title = Wrap(nowOne, maxWidth);
                // a title's line count is its height, which nothing else recomputes
                newMenu.RecalculateSize();
            }

            if (nowTwo != two) {
                two = nowTwo;
                second.Title = Wrap(nowTwo, maxWidth);
                second.Visible = nowTwo != "";
                newMenu.RecalculateSize();
            }
        };
        newMenu.Add(first);
        newMenu.Add(second);
    }

    private static (string One, string Two) StatusLines() {
        string error = RemoteBests.Error ?? Dialog.Clean("SRS_EXPORT_UNREAD");
        string noAnswer = $"{Dialog.Clean("SRS_EXPORT_NO_ANSWER")} {error}";
        string checking = Dialog.Clean("SRS_EXPORT_CHECKING");
        string mayBeWriting = Dialog.Clean("SRS_EXPORT_MAY_BE_WRITING");
        return ExportTable.StatusOf(RemoteBests.Source, SheetReader.IsReading, RemoteBests.Error,
                SheetReader.UnansweredWrite, SheetReader.IsWriting) switch {
            ScreenStatus.Loading => (Dialog.Clean("SRS_EXPORT_LOADING"), ""),
            ScreenStatus.LoadFailed => (noAnswer, ""),
            ScreenStatus.Writing => (Dialog.Clean("SRS_EXPORT_WRITING"), ""),
            ScreenStatus.WritingChecking => (mayBeWriting, checking),
            ScreenStatus.WritingFailed => (mayBeWriting, noAnswer),
            ScreenStatus.SavedChecking => (Aged("SRS_EXPORT_SAVED_COPY"), checking),
            ScreenStatus.SavedFailed => (Aged("SRS_EXPORT_SAVED_COPY"), noAnswer),
            ScreenStatus.FreshFailed => (Aged("SRS_EXPORT_SHEET_TIMES"), noAnswer),
            _ => ("", ""),
        };
    }

    // "Saved copy (2 h)." in every language: the age never moves in the sentence
    private static string Aged(string key) {
        (int value, string unit) = ExportTable.AgeOf(RemoteBests.Age);
        return $"{Dialog.Clean(key)} ({value} {Dialog.Clean(unit)}).";
    }

    private static void Submit(Level level, List<PendingUpdate> updates) {
        List<PendingUpdate> selected = updates.Where(u => u.Selected && u.Writable).ToList();
        List<ExportUpdate> sent = SheetTiming.Updates(selected);
        if (sent.Count == 0) {
            PopupMessageUtils.Show(Dialog.Clean("SRS_EXPORT_NONE_TICKED"), null);
            return;
        }

        int submission = generation;
        string url = ExportTarget.Url;

        WriteRefusal refusal = ExportSend.TryStart(sent, builtOnAccepts, "manual",
            () => SrsModule.Settings.Enabled && ExportTarget.Url == url,
            done => gameThread.Enqueue(() => {
                if (submission == generation && screen == Screen.Writing && writingLine != null) {
                    writingLine.Title = $"{Dialog.Clean("SRS_EXPORT_WRITING")} {done}/{sent.Count}";
                }
            }),
            outcome => {
                List<string> lines;
                // a throw here would leave the screen on "Writing..."
                try {
                    lines = outcome.Results.Count > 0 ? ExportTable.SummaryLines(outcome.Results) : [];
                    if (outcome.Error != null) {
                        lines.Insert(0, outcome.Error);
                    }
                } catch (Exception e) {
                    Logger.Log(LogLevel.Warn, LogTag, "an export answer could not be shown: " + e);
                    lines = [$"{Dialog.Clean("SRS_EXPORT_ERR_UNREADABLE")} {e.GetType().Name}"];
                }

                if (submission != generation) {
                    Logger.Log(LogLevel.Info, LogTag, "export resolved after its screen was replaced");
                    return;
                }

                gameThread.Enqueue(() => {
                    if (submission != generation || menu == null) {
                        return;
                    }

                    ShowSummary(level, lines);
                });
            });
        if (refusal != WriteRefusal.None) {
            return;
        }

        ShowWorking(level);
    }

    /// Up while nothing is held, in place of the table: rows built before the
    /// sheet answers would pre-tick as improvements.
    private static void ShowLoading(Level level, int accepts) {
        builtOnAccepts = accepts;
        TextMenu newMenu = new();
        newMenu.Add(new TextMenu.Header(Dialog.Clean("SRS_EXPORT_TITLE")));
        AddStatus(newMenu, SummaryMaxWidth);

        // a way out without knowing Back closes it: the one screen the player
        // may want to leave before it has done anything
        TextMenu.Button cancelButton = new(Dialog.Clean("SRS_EXPORT_CANCEL"));
        cancelButton.Pressed(Close);
        newMenu.Add(cancelButton);

        Show(level, newMenu, Screen.Loading);
    }

    private static void ShowWorking(Level level) {
        TextMenu newMenu = new();
        newMenu.Add(new TextMenu.Header(Dialog.Clean("SRS_EXPORT_TITLE")));
        // not SRS_EXPORT_LOADING: that one belongs to the read, and announcing
        // a read while the sheet is being written to is the wrong promise
        writingLine = new TextMenu.SubHeader(Dialog.Clean("SRS_EXPORT_WRITING"));
        newMenu.Add(writingLine);

        Show(level, newMenu, Screen.Writing);
    }

    // the widest a line of the loading and summary screens may be
    private const float SummaryMaxWidth = 1600f;

    private static string Wrap(string line, float maxWidth) =>
        ExportTable.Wrap(line, maxWidth, text => ActiveFont.Measure(text).X * TextMenu.SubHeader.Scale);

    private static void ShowSummary(Level level, List<string> lines) {
        TextMenu newMenu = new();
        newMenu.Add(new TextMenu.Header(Dialog.Clean("SRS_EXPORT_DONE")));
        foreach (string line in lines) {
            newMenu.Add(new TextMenu.SubHeader(Wrap(line, SummaryMaxWidth), topPadding: false));
        }

        // not "Cancel": the rows above are already written, and offering to
        // cancel them is a promise this screen cannot keep
        TextMenu.Button closeButton = new(Dialog.Clean("SRS_EXPORT_CLOSE"));
        closeButton.Pressed(Close);
        newMenu.Add(closeButton);

        Show(level, newMenu, Screen.Summary);
    }
}
