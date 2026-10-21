using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FMOD.Studio;

namespace Celeste.Mod.SpeedrunSheet;

public class SrsModule : EverestModule {
    public static SrsModule Instance { get; private set; }

    public override Type SettingsType => typeof(SrsSettings);
    public static SrsSettings Settings => (SrsSettings)Instance._Settings;

    public SrsModule() {
        Instance = this;
    }

    public override void Load() {
        // before anything formats a time
        AdoptSpeedrunToolsTimeFormat();

        SheetImporter.Load();
        // before ExportMenu.Load, which asks the sheet at launch
        ExportTarget.Load();
        // the saved times first, and before ExportMenu.Load: the launch read
        // only ever replaces them
        SheetReader.Install(new ReaderHost {
            Fetch = ExportClient.FetchAsync,
            Info = message => Logger.Log(LogLevel.Info, "srs", message),
            Warn = message => Logger.Log(LogLevel.Warn, "srs", message),
            Enabled = () => Settings.Enabled,
            Url = () => ExportTarget.Url,
            CopyPath = Path.Combine(Everest.PathSettings, "srs", "sheet-times.json"),
        });
        ExportSend.Install(new SendHost {
            Post = ExportClient.PostAsync,
            Info = message => Logger.Log(LogLevel.Info, "srs", message),
            Warn = message => Logger.Log(LogLevel.Warn, "srs", message),
        });
        AutoExport.Install(new AutoHost {
            Enabled = () => Settings.Enabled,
            Setting = () => Settings.AutoExport,
            Url = () => ExportTarget.Url,
            ScreenOpen = () => ExportMenu.IsOpen,
            Info = message => Logger.Log(LogLevel.Info, "srs", message),
            Run = action => Task.Run(action),
        });
        SheetReader.LoadCopy();
        // Level.Update hook order: each Load wraps the previous ones, so after
        // orig the frame runs in Load order. Hotkeys polls the input before
        // anything reads it, RunWatcher feeds the tracker, TierComparison reads
        // its latest record, ExportMenu reads its hotkey last
        Hotkeys.Load();
        RunWatcher.Load();
        TierComparison.Load();
        // last: it reads Hotkeys on the frame Hotkeys updated it, and only reads
        // what the others produced
        ExportMenu.Load();
    }

    public override void Unload() {
        // a reloaded module leaves these statics behind: nothing of theirs may send
        AutoExport.Drop();
        ExportMenu.Unload();
        TierComparison.Unload();
        RunWatcher.Unload();
        Hotkeys.Unload();
        SheetImporter.Unload();
    }

    /// Takes Speed Run Tool's formatter rather than copying it. By reflection,
    /// the only way in: RoomTimerData.FormatTime is public, its class is not,
    /// the ModInterop does not export it, and srs's own code reads public members
    /// only. isPbTime false: true returns "" for a zero, Speed Run Tool's absent PB.
    private static void AdoptSpeedrunToolsTimeFormat() {
        try {
            Assembly assembly = Everest.Modules
                .FirstOrDefault(module => module.Metadata?.Name == "SpeedrunTool")?
                .GetType().Assembly;
            MethodInfo method = assembly?
                .GetType("Celeste.Mod.SpeedrunTool.RoomTimer.RoomTimerData")?
                .GetMethod("FormatTime", BindingFlags.Public | BindingFlags.Static,
                    null, [typeof(long), typeof(bool)], null);

            if (method?.CreateDelegate(typeof(Func<long, bool, string>)) is Func<long, bool, string> format) {
                TimeFormat.Format = ticks => format(ticks, false);
                return;
            }
        } catch (Exception e) {
            Logger.Log(LogLevel.Error, "srs", "could not reach SpeedrunTool's time format: " + e);
        }

        // fatal on purpose: a second formatter would drift in silence and write
        // a time into the player's sheet that no longer matches the timer
        throw new InvalidOperationException(
            "SpeedrunTool's RoomTimerData.FormatTime could not be found. It is where srs takes its"
            + " time format from, and srs keeps no copy. This needs a srs update.");
    }

    public override void LoadSettings() {
        base.LoadSettings();

        // see SheetUrls
        MigrateSheetUrls();
    }

    // substitutes the frozen spreadsheet id in the stored tab URLs and saves
    // if any of them carried it. Idempotent: a save that fails changes nothing
    // but the file on disk, and the next launch migrates again
    private void MigrateSheetUrls() {
        SrsSettings settings = Settings;
        bool changed = false;

        foreach (StandardsTabInfo tab in StandardsTabs.All) {
            if (SheetUrls.Migrate(settings.UrlOf(tab.Tab)) is string migrated) {
                settings.SetUrl(tab.Tab, migrated);
                changed = true;
            }
        }

        if (!changed) {
            return;
        }

        Logger.Log(LogLevel.Info, "srs", "Repointed the stored sheet urls at the current reference workbook");
        TrySaveSettings("the migrated sheet urls");
    }

    /// Everest catches the write itself, but not the File.Delete and
    /// CreateDirectory it does first: every save srs asks for goes through
    /// here, or a locked or read-only Saves folder crashes the game from a menu
    /// press or a hook. What was changed still holds in memory.
    internal static void TrySaveSettings(string what) {
        try {
            Instance.SaveSettings();
        } catch (Exception e) {
            Logger.Log(LogLevel.Warn, "srs", $"could not save the settings ({what}): {e.GetType().Name}");
        }
    }

    // only the header comes from base: ModMenu builds every entry by hand, so
    // the master switch can hide them all. The header must come first: entries
    // added before it land in the previous mod's section
    public override void CreateModMenuSection(TextMenu menu, bool inGame, EventInstance snapshot) {
        CreateModMenuSectionHeader(menu, inGame, snapshot);
        ModMenu.CreateMenu(menu, inGame);
    }
}
