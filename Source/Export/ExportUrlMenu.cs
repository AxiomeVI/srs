using System;
using System.Globalization;
using Celeste.Mod.SpeedrunSheet.MenuTools;
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

    internal static readonly Color ErrorColor = Calc.HexToColor("FF4040");

    /// On the closed Your Sheet row while something in it stops an export: an
    /// out-of-date script, or a last read that failed. The lines that say
    /// which are inside the group.
    internal sealed class TroubleMark : ISubMenuPreview {
        private const string Mark = "!";
        // the group's title draws its arrow this far after the label, and the mark follows the arrow
        private const float Gap = 10f;

        /// The group the mark is on: set once it exists.
        public RecursiveSubMenu Group { private get; set; }

        // drawn beside the label, where the eye is, not in the column a preview is given
        public float Width() => 0f;

        public void Render(Vector2 position, Color color, float alpha) {
            if (Group?.Container == null || !ExportTarget.IsSet
                || (SheetReader.OutOfDateError == null && RemoteBests.Error == null)) {
                return;
            }

            // position is the row's right edge: back to its left, then past the label and its arrow
            float x = position.X - Group.Container.Width + ActiveFont.Measure(Group.Label).X
                + GFX.Gui["downarrow"].Width + 2f * Gap;
            ActiveFont.DrawOutline(Mark, new Vector2(x, position.Y), new Vector2(0f, 0.5f), Vector2.One,
                ErrorColor * alpha, 2f, Color.Black * (alpha * alpha * alpha));
        }
    }

    /// Adds the URL entry, its status line and the Forget button, which stays
    /// hidden while no URL is set.
    internal static void CreateMenuEntries(RecursiveSubMenuBase group) {
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
            Visible = !ExportTarget.IsSet,
        };
        TextMenu.Button forgetButton = new(Dialog.Clean("SRS_EXPORT_URL_FORGET")) {
            Visible = ExportTarget.IsSet,
        };

        bool selected = false;
        // the only place `message` and `checking` are read. OnUpdate runs on
        // every item each frame, visible or not, so this owns the status line
        setButton.OnUpdate = () => {
            // this visit's answer first: a complaint about the clipboard must
            // show over anything held. Then what stops an export, in red until a
            // read is taken in: an out-of-date script, or the last read that failed
            string trouble = null;
            if (!checking) {
                string unread = ExportTarget.IsSet ? Localized(RemoteBests.Error) : null;
                trouble = message ?? Localized(SheetReader.OutOfDateError)
                    ?? unread;
                // a SubHeader never wraps, and one line this long widens the whole
                // menu: one sentence a line
                trouble = trouble?.Replace(". ", ".\n");
            }

            string title = checking ? Dialog.Clean("SRS_EXPORT_URL_CHECKING") : trouble ?? DetailLine(settings);
            if (status.Title != title) {
                status.Title = title;
                // the line can go from one line to two: the group does not measure it again by itself
                group.RecalculateSize();
            }

            status.TextColor = trouble != null && trouble != Dialog.Clean("SRS_EXPORT_URL_CHECK_OK")
                ? ErrorColor
                : Color.Gray;
            // a URL set and nothing to report: the line is a description, shown
            // while the button is selected. Anything else stays up
            ModMenu.Show(group, status, selected || checking || trouble != null || !ExportTarget.IsSet);
        };

        bool replaceArmed = false;
        setButton.OnEnter = () => selected = true;
        setButton.OnLeave = () => {
            selected = false;
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
            group.SetItemVisible(forgetButton, true);

            // the check is a read like any other: it warms the script's cache
            // and leaves the export screen data to open on
            BeginCheck();
        });

        // press once to arm (the label gains a "?"), press again to clear:
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
            group.SetItemVisible(forgetButton, false);
            setButton.Label = StatusLabel();
        });

        group.AddItem(setButton);
        group.AddItem(status);
        group.AddItem(forgetButton);
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
                    ReadKind.Unreachable => outcome.Error,
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

    // the label is the action, not the state: the state is the line below it
    private static string StatusLabel() =>
        Dialog.Clean(!ExportTarget.IsSet
            ? "SRS_EXPORT_URL_FROM_CLIPBOARD"
            : "SRS_EXPORT_URL_REPLACE_FROM_CLIPBOARD");

    // a read answered before Dialog had loaded left the key, not its text,
    // alone or before a value ("KEY 500.")
    private static string Localized(string error) {
        if (error == null) {
            return null;
        }

        int space = error.IndexOf(' ');
        string key = space < 0 ? error : error[..space];
        // srs's own keys only: Dialog.Has ignores case, and a sentence can start with a vanilla key
        return key.StartsWith("SRS_", StringComparison.Ordinal) && Dialog.Has(key)
            ? Dialog.Clean(key) + (space < 0 ? "" : error[space..])
            : error;
    }

    // never includes the URL itself: only that one is set, and when. With none
    // set, what to copy before pressing and where the way to get it is written
    private static string DetailLine(SrsSettings settings) {
        if (!ExportTarget.IsSet) {
            return $"{Dialog.Clean("SRS_EXPORT_URL_HINT")}\n{Dialog.Clean("SRS_EXPORT_URL_HINT_GUIDE")}";
        }

        // no date for a URL file dropped by hand
        string set = Dialog.Clean("SRS_EXPORT_URL_DETAIL");
        return string.IsNullOrEmpty(settings.ExportUrlSetOn)
            ? set.TrimEnd(':', ' ')
            : $"{set} {settings.ExportUrlSetOn}";
    }
}
