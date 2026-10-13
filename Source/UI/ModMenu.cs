using System;
using Celeste.Mod.CelesteHotkeys;
using Celeste.Mod.SpeedrunSheet.MenuTools;
using Monocle;

namespace Celeste.Mod.SpeedrunSheet;

// the whole Mod Options section, built by hand: the master switch has to hide
// every other entry
internal static class ModMenu {
    internal static void CreateMenu(TextMenu menu, bool inGame) {
        SrsSettings settings = SrsModule.Settings;

        // everything under the master switch but the hotkeys, open while it is on
        RecursiveNakedSubMenu section = new(initiallyExpanded: settings.Enabled);

        // one switch per part of the rows above the timer, in the order drawn
        RecursiveSubMenu display = new(Dialog.Clean("MODOPTIONS_SRS_GROUP_DISPLAY"));
        AddSwitch(display, "MODOPTIONS_SRS_SHOWCHECKPOINTNAME", settings.ShowCheckpointName,
            on => settings.ShowCheckpointName = on);
        AddSwitch(display, "MODOPTIONS_SRS_SHOWTIME", settings.ShowTime, on => settings.ShowTime = on);
        AddSwitch(display, "MODOPTIONS_SRS_SHOWTIER", settings.ShowTier, on => settings.ShowTier = on);
        TextMenu.Item pbImprovement = AddSwitch(display, "MODOPTIONS_SRS_SHOWPBIMPROVEMENT",
            settings.ShowPbImprovement, on => settings.ShowPbImprovement = on);
        // the PB is the sheet's: without a URL the switch does nothing, and nothing else says why
        TextMenuExt.SubHeaderExt pbNeedsSheet = new(Dialog.Clean("MODOPTIONS_SRS_SHOWPBIMPROVEMENT_DESC")) {
            HeightExtra = 0f,
            Visible = false,
        };
        display.AddItem(pbNeedsSheet);
        pbImprovement.OnEnter = () => display.SetItemVisible(pbNeedsSheet, true, smooth: true);
        pbImprovement.OnLeave = () => display.SetItemVisible(pbNeedsSheet, false, smooth: true);
        AddSwitch(display, "MODOPTIONS_SRS_SHOWDELTA", settings.ShowDelta, on => settings.ShowDelta = on);
        section.AddItem(display);

        Action updateStandards = SheetImporter.CreateMenuEntries(section);

        // the player's own sheet. The URL entries come last, Forget closing the group
        RecursiveSubMenu sheet = new(Dialog.Clean("MODOPTIONS_SRS_GROUP_YOURSHEET"),
            preview: new ExportUrlMenu.TroubleMark());

        // in game only: the export screen needs a level, and it saves binding a
        // hotkey just to reach it
        if (inGame) {
            // Visible from the start, or the row shows for a frame before OnUpdate hides it
            TextMenu.Button openExport = new(Dialog.Clean("MODOPTIONS_SRS_OPENEXPORTMENU")) {
                Visible = ExportTarget.IsSet,
            };
            openExport.Pressed(() => {
                if (Engine.Scene is not Level level) {
                    return;
                }

                // Unpause tears the pause menu down (coroutine, settings save,
                // sound). Opening after it lets that finish, and ExportMenu
                // records an unpaused level: closing the export screen returns
                // to the game, not to a pause with no menu
                level.Unpause();
                Engine.Scene.OnEndOfFrame += () => ExportMenu.Open(level);
            });
            // nothing to open without a sheet: shown once a URL is set, which can
            // happen during this visit
            openExport.OnUpdate = () => Show(sheet, openExport, ExportTarget.IsSet);
            sheet.AddItem(openExport);
        }

        // also from the title screen; shown once a URL is set, like the button above
        TextMenu.OnOff autoExport = new(Dialog.Clean("MODOPTIONS_SRS_AUTOEXPORT"), settings.AutoExport) {
            Visible = ExportTarget.IsSet,
        };
        autoExport.Change(on => {
            settings.AutoExport = on;
            if (!on) {
                AutoExport.Drop();
            }

            // at once: left on in the file after being switched off, it would write to the sheet
            SrsModule.TrySaveSettings("auto-export");
        });
        autoExport.OnUpdate = () => Show(sheet, autoExport, ExportTarget.IsSet);
        sheet.AddItem(autoExport);

        ExportUrlMenu.CreateMenuEntries(sheet);
        section.AddItem(sheet);

        // at the root, never in a submenu: one holding the selection keeps reading
        // input under the screen this opens
        TextMenu.Button hotkeys = HotkeyMenu.OpenButton(
            menu, Hotkeys.Set, Hotkeys.Text, () => SrsModule.TrySaveSettings("the hotkeys"));
        hotkeys.Visible = settings.Enabled;

        TextMenu.OnOff enabled = new(Dialog.Clean("MODOPTIONS_SRS_ENABLED"), settings.Enabled);
        enabled.Change(on => {
            settings.Enabled = on;
            RunWatcher.Forget();
            section.Expanded = on;
            hotkeys.Visible = on;
            if (on) {
                // the startup refresh is skipped while the mod is off, so this
                // is the first chance to pick up a sheet retimed in the meantime.
                // Run as the button would be, so the status line follows it
                updateStandards();
                SheetReader.Refresh("the mod was switched back on");
            }
        });

        // a recursive submenu must not be a menu's first item: the switch is
        menu.Add(enabled);
        menu.Add(section);
        menu.Add(hotkeys);
    }

    /// Shows or hides a row of a group, from an OnUpdate that restates what it
    /// wants each frame: the group is asked only on a change, since asking
    /// allocates. A row sliding in still reads hidden, and is asked again.
    internal static void Show(RecursiveSubMenuBase group, TextMenu.Item item, bool visible) {
        if (item.Visible != visible) {
            group.SetItemVisible(item, visible, smooth: true);
        }
    }

    private static TextMenu.Item AddSwitch(RecursiveSubMenu group, string label, bool value, Action<bool> change) {
        TextMenu.OnOff item = new(Dialog.Clean(label), value);
        item.Change(change);
        group.AddItem(item);
        return item;
    }
}
