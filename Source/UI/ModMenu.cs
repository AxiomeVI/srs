using System;
using System.Collections.Generic;
using Celeste.Mod.CelesteHotkeys;
using Monocle;

namespace Celeste.Mod.SpeedrunSheet;

// the whole Mod Options section, built by hand: the master switch has to hide
// every other entry
internal static class ModMenu {
    internal static void CreateMenu(TextMenu menu, bool inGame) {
        SrsSettings settings = SrsModule.Settings;

        TextMenu.OnOff enabled = new(Dialog.Clean("MODOPTIONS_SRS_ENABLED"), settings.Enabled);
        menu.Add(enabled);

        // everything added below the master switch is one of its sub-options
        int first = menu.Items.Count;

        // one switch per part of the rows above the timer, in the order drawn
        AddSwitch(menu, "MODOPTIONS_SRS_SHOWCHECKPOINTNAME", settings.ShowCheckpointName,
            on => settings.ShowCheckpointName = on);
        AddSwitch(menu, "MODOPTIONS_SRS_SHOWTIME", settings.ShowTime, on => settings.ShowTime = on);
        AddSwitch(menu, "MODOPTIONS_SRS_SHOWTIER", settings.ShowTier, on => settings.ShowTier = on);
        AddSwitch(menu, "MODOPTIONS_SRS_SHOWPBIMPROVEMENT", settings.ShowPbImprovement,
            on => settings.ShowPbImprovement = on);
        AddSwitch(menu, "MODOPTIONS_SRS_SHOWDELTA", settings.ShowDelta, on => settings.ShowDelta = on);

        Action updateStandards = SheetImporter.CreateMenuEntries(menu);

        // ExportUrlMenu keeps the Forget button hidden until an export URL is set
        TextMenu.Item forgetUrl = ExportUrlMenu.CreateMenuEntries(menu);

        // in game only: the export screen needs a level, and it saves binding a
        // hotkey just to reach it
        if (inGame) {
            TextMenu.Button openExport = new(Dialog.Clean("MODOPTIONS_SRS_OPENEXPORTMENU"));
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
            // happen during this visit. OnUpdate runs for hidden items too, and
            // the master switch is read here so it is not undone the next frame
            openExport.OnUpdate = () => openExport.Visible = settings.Enabled && ExportTarget.IsSet;
            menu.Add(openExport);
        }

        // also from the title screen; shown once a URL is set, like the button above
        TextMenu.OnOff autoExport = new(Dialog.Clean("MODOPTIONS_SRS_AUTOEXPORT"), settings.AutoExport);
        autoExport.Change(on => {
            settings.AutoExport = on;
            if (!on) {
                AutoExport.Drop();
            }

            // at once: left on in the file after being switched off, it would write to the sheet
            SrsModule.TrySaveSettings("auto-export");
        });
        autoExport.OnUpdate = () => autoExport.Visible = settings.Enabled && ExportTarget.IsSet;
        menu.Add(autoExport);

        // never in a submenu, which keeps reading input under the screen it opens
        menu.Add(HotkeyMenu.OpenButton(menu, Hotkeys.Set, Hotkeys.Text, () => SrsModule.TrySaveSettings("the hotkeys")));

        // a range, not a list: several builders add entries
        List<TextMenu.Item> subOptions = menu.Items.GetRange(first, menu.Items.Count - first);

        enabled.Change(on => {
            settings.Enabled = on;
            RunWatcher.Forget();
            ShowSubOptions(subOptions, forgetUrl, on);
            if (on) {
                // the startup refresh is skipped while the mod is off, so this
                // is the first chance to pick up a sheet retimed in the meantime.
                // Run as the button would be, so the status line follows it
                updateStandards();
                SheetReader.Refresh("the mod was switched back on");
            }
        });

        ShowSubOptions(subOptions, forgetUrl, settings.Enabled);
    }

    // the master switch hides everything, but turning the mod back on must not
    // reveal the Forget button, which stays hidden until a URL is set
    private static void ShowSubOptions(List<TextMenu.Item> subOptions, TextMenu.Item forgetUrl, bool on) {
        foreach (TextMenu.Item item in subOptions) {
            item.Visible = on;
        }

        forgetUrl.Visible = on && ExportTarget.IsSet;
    }

    private static void AddSwitch(TextMenu menu, string label, bool value, Action<bool> change) {
        TextMenu.OnOff item = new(Dialog.Clean(label), value);
        item.Change(change);
        menu.Add(item);
    }
}
