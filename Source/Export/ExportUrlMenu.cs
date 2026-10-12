using System;
using System.Globalization;
using Microsoft.Xna.Framework;
using Monocle;
using SDL2;

namespace Celeste.Mod.SpeedrunSheet;

// Mod Options entries for the export target. The URL is the credential
// (ExportTarget): read from the clipboard, never rendered back.
public static class ExportUrlMenu {
    private const string LogTag = "srs";

    // written from the ping's continuation (a thread-pool thread) and read on
    // the game thread in setButton.OnUpdate, which is the only place either is
    // turned into what the screen shows
    private static volatile string message;
    private static volatile bool checking;

    // nothing cancels a ping in flight: a second paste while the first is
    // still out would otherwise let the older answer overwrite the newer
    private static volatile int generation;

    private static readonly Color OutOfDateColor = Calc.HexToColor("FF4040");

    /// Returns the Forget button, which stays hidden while no URL is set.
    public static TextMenu.Item CreateMenuEntries(TextMenu menu) {
        SrsSettings settings = SrsModule.Settings;

        // a check's answer belongs to the visit it was asked in: the menu is
        // rebuilt on every open, and the next one goes back to the detail line.
        // One still out lands on this visit instead
        if (!checking) {
            message = null;
        }

        TextMenu.Button setButton = new(StatusLabel());
        // HeightExtra 0: no top padding, as under any button
        TextMenuExt.SubHeaderExt status = new(DetailLine(settings)) {
            HeightExtra = 0f,
            Visible = ExportTarget.IsSet,
        };
        TextMenu.Button forgetButton = new(Dialog.Clean("SRS_EXPORT_URL_FORGET")) {
            Visible = ExportTarget.IsSet,
        };

        // the only place `message` and `checking` are read. ⚠️ TextMenu calls
        // OnUpdate on every item each frame, visible or not, so this owns
        // `status.Visible`: the master switch must be read here too, or what it
        // sets is undone next frame
        setButton.OnUpdate = () => {
            if (!SrsModule.Settings.Enabled) {
                return;
            }

            // an out-of-date script stops every export, auto-export without a
            // word in game: it stays here, in red, until a read is taken in
            string outOfDate = checking ? null : SheetReader.OutOfDateError;
            // a read answered before Dialog had loaded left the key, not its text
            if (outOfDate != null && Dialog.Has(outOfDate)) {
                outOfDate = Dialog.Clean(outOfDate);
            }

            // a SubHeader never wraps, and one line this long widens the whole
            // menu: its two sentences go on two lines
            int cut = outOfDate?.IndexOf(". ", StringComparison.Ordinal) ?? -1;
            if (cut >= 0) {
                outOfDate = outOfDate[..(cut + 1)].Trim() + "\n" + outOfDate[(cut + 2)..].Trim();
            }

            string shown = checking ? Dialog.Clean("SRS_EXPORT_URL_CHECKING") : outOfDate ?? message;
            status.Title = shown ?? DetailLine(settings);
            status.TextColor = outOfDate != null ? OutOfDateColor : Color.Gray;
            status.Visible = shown != null || ExportTarget.IsSet;
        };

        bool replaceArmed = false;
        setButton.OnLeave = () => {
            replaceArmed = false;
            setButton.Label = StatusLabel();
        };
        setButton.Pressed(() => {
            // one press spends the arming, whatever it then does
            bool armed = replaceArmed;
            replaceArmed = false;
            setButton.Label = StatusLabel();

            string pasted = ReadClipboard();
            if (string.IsNullOrWhiteSpace(pasted)) {
                message = Dialog.Clean("SRS_EXPORT_URL_CLIPBOARD_EMPTY");
                return;
            }

            pasted = pasted.Trim();
            if (!ExportProtocol.IsEndpointUrl(pasted)) {
                // never echo what was on the clipboard: it may be the URL of
                // someone else's sheet, and it may be anything at all
                message = Dialog.Clean("SRS_EXPORT_URL_CLIPBOARD_INVALID");
                return;
            }

            // the same URL pasted again keeps what is held of its sheet
            bool sameSheet = pasted == ExportTarget.Url;

            // another sheet over the one set: press once to arm, again to
            // replace, as for Forget. The clipboard is read again on the second
            // press, so what is replaced is what is there then
            if (ExportTarget.IsSet && !sameSheet && !armed) {
                replaceArmed = true;
                setButton.Label = $"{StatusLabel()}?";
                // an earlier press's complaint about the clipboard no longer holds
                message = null;
                return;
            }
            if (!ExportTarget.Set(pasted)) {
                // a check still out for an earlier paste would answer over this line
                generation++;
                checking = false;
                message = Dialog.Clean("SRS_EXPORT_URL_SAVE_FAILED");
                return;
            }

            // only once the new URL is set: a save still in flight then sees
            // the change, and cannot write the old sheet's times under it
            if (!sameSheet) {
                SheetReader.Forget();
            }

            settings.ExportUrlSetOn = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            SrsModule.TrySaveSettings("the date the sheet URL was set");

            setButton.Label = StatusLabel();
            forgetButton.Visible = true;

            // the check is a read like any other: it warms the script's cache
            // and leaves the export screen data to open on
            BeginCheck();
        });

        // press once to arm ("Forget Sheet URL?"), press again to clear:
        // TextMenu.Button has no confirm dialog, so the two-step is one
        bool forgetArmed = false;
        forgetButton.OnLeave = () => {
            forgetArmed = false;
            forgetButton.Label = Dialog.Clean("SRS_EXPORT_URL_FORGET");
        };
        forgetButton.Pressed(() => {
            if (!forgetArmed) {
                forgetArmed = true;
                forgetButton.Label = $"{Dialog.Clean("SRS_EXPORT_URL_FORGET")}?";
                return;
            }

            if (!ExportTarget.Forget()) {
                forgetArmed = false;
                forgetButton.Label = Dialog.Clean("SRS_EXPORT_URL_FORGET");
                generation++;
                checking = false;
                message = Dialog.Clean("SRS_EXPORT_URL_FORGET_FAILED");
                return;
            }

            settings.ExportUrlSetOn = "";
            SheetReader.Forget();
            SrsModule.TrySaveSettings("the sheet URL forgotten");

            forgetArmed = false;
            generation++;
            message = null;
            checking = false;
            forgetButton.Label = Dialog.Clean("SRS_EXPORT_URL_FORGET");
            forgetButton.Visible = false;
            setButton.Label = StatusLabel();
        });

        menu.Add(setButton);
        menu.Add(status);
        menu.Add(forgetButton);

        return forgetButton;
    }

    /// Reads the sheet through the one reader and says what came back. A wrong
    /// or unauthorised deployment also answers 200, so the check is that the
    /// answer parses.
    private static void BeginCheck() {
        int fetch = ++generation;
        checking = true;
        message = null;

        _ = SheetReader.Refresh("a sheet URL was just set").ContinueWith(task => {
            if (fetch != generation) {
                return;
            }

            // nothing above this continuation observes a throw: the status
            // would stay on "Asking the sheet..." for the rest of the visit
            try {
                ReadOutcome outcome = task.Result;
                message = outcome.Kind switch {
                    ReadKind.Accepted => Dialog.Clean("SRS_EXPORT_URL_CHECK_OK"),
                    ReadKind.Unreachable => $"{Dialog.Clean("SRS_EXPORT_URL_CHECK_FAILED")} {outcome.Error}",
                    ReadKind.OutOfDate => outcome.Error,
                    ReadKind.NoRows => Dialog.Clean("SRS_EXPORT_URL_CHECK_NO_ROWS"),
                    ReadKind.NotTheScript => Dialog.Clean("SRS_EXPORT_URL_CHECK_NOT_SHEET"),
                    ReadKind.Refused => $"{Dialog.Clean("SRS_EXPORT_URL_CHECK_REFUSED")} {outcome.Error}",
                    // the URL moved again, or the mod went off: a newer visit says it
                    _ => null,
                };
            } catch (Exception e) {
                Logger.Log(LogLevel.Warn, LogTag, "a sheet URL check could not be read: " + e.GetType().Name);
                message = Dialog.Clean("SRS_EXPORT_URL_CHECK_NOT_SHEET");
            } finally {
                checking = false;
            }
        });
    }

    /// SDL owns the clipboard; FNA exposes it and nothing in Celeste wraps it.
    /// Never throws: a clipboard that cannot be read is an empty one.
    private static string ReadClipboard() {
        try {
            return SDL.SDL_GetClipboardText();
        } catch (Exception e) {
            Logger.Log(LogLevel.Warn, LogTag, "clipboard unreadable: " + e.Message);
            return null;
        }
    }

    // the label is the action, not the state: the state is the line below it,
    // which only appears once there is one
    private static string StatusLabel() =>
        Dialog.Clean(!ExportTarget.IsSet
            ? "SRS_EXPORT_URL_FROM_CLIPBOARD"
            : "SRS_EXPORT_URL_REPLACE_FROM_CLIPBOARD");

    // never includes the URL itself: only that one is set, and when
    private static string DetailLine(SrsSettings settings) =>
        $"{Dialog.Clean("SRS_EXPORT_URL_DETAIL")} {settings.ExportUrlSetOn}";
}
