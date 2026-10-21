using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.CelesteHotkeys;

/// <summary>The mod-specific half of the remap screen: its strings and how it saves.</summary>
// Dialog ids are global across every loaded mod, so the strings a mod owns are passed in with its
// own prefix. The generic ones reuse vanilla's ids (KeybindScreen.Vanilla*), which the game already
// translates.
internal sealed class KeybindScreenText {
    /// <summary>The screen's title, e.g. "Hotkeys", and the label of the Mod Options row that opens it.</summary>
    public required string HeaderId { get; init; }

    /// <summary>How to record a combo — hold its inputs together, then let go — drawn on the recording overlay.</summary>
    public required string ComboHintId { get; init; }

    /// <summary>
    ///     What a row can hold, drawn once above the rows: several inputs act as a combo, all held together.
    ///     Read raw through Dialog.Get; <c>{0}</c> is <see cref="Bindable.MaxComboInputs"/>.
    /// </summary>
    // ⚠️ Dialog.Clean deletes every {...} placeholder but {n}, so this one is filled in from Dialog.Get.
    public required string PageComboHintId { get; init; }

    /// <summary>How to clear a whole row: Journal or Delete.</summary>
    public required string ClearHintId { get; init; }

    /// <summary>The countdown while recording. Read raw through Dialog.Get; <c>{0}</c> is the seconds left.</summary>
    // ⚠️ Dialog.Clean deletes every {...} placeholder but {n}, so this one is filled in from Dialog.Get.
    public required string TimeoutFormatId { get; init; }
}

/// <summary>
///     A remap screen built from a keybind table: a keyboard section and a controller section, one
///     row per keybind in each.
/// </summary>
// Open it with HotkeyMenu.OpenButton, which captures the scene.
internal sealed class KeybindScreen<TSettings> : TextMenu where TSettings : class {
    internal const string VanillaKeyboardTitle = "KEY_CONFIG_TITLE";
    internal const string VanillaControllerTitle = "BTN_CONFIG_TITLE";
    internal const string VanillaKeyChanging = "KEY_CONFIG_CHANGING";
    internal const string VanillaButtonChanging = "BTN_CONFIG_CHANGING";

    private const float RecordSeconds = 5f;
    private const float RefocusDelay = 0.25f;
    private const string InvalidSound = "event:/ui/main/button_invalid";

    private readonly HotkeySet<TSettings> hotkeys;
    private readonly KeybindScreenText text;
    private readonly Action save;
    private readonly ChordRecorder<Keys> keyChord = new(Bindable.ModifierRank);
    private readonly ChordRecorder<Buttons> buttonChord = new();

    private bool closing;
    private bool counted;
    private bool hudHideWas;
    private float refocusDelay;
    private bool recording;
    private float recordingEase;
    private Keybind<TSettings> recordingKeybind;
    private bool recordingKeyboard;
    private float timeout;

    // The chord drawn on the overlay, rebuilt only when it grows.
    private List<object> chordIcons = new();
    private int chordIconsFor;

    /// <summary>True while the screen is waiting for the key or button to record.</summary>
    internal bool Recording => recording;

    /// <param name="hotkeys">The mod's hotkey set: its table and its live settings.</param>
    /// <param name="text">The mod's own dialog ids.</param>
    /// <param name="save">
    ///     Usually <c>MyModule.Instance.SaveSettings</c>. Called on every change: Everest saves when
    ///     Mod Options closes, but a crash or a force-quit between the two lost the binding.
    /// </param>
    internal KeybindScreen(HotkeySet<TSettings> hotkeys, KeybindScreenText text, Action save) {
        this.hotkeys = hotkeys;
        this.text = text;
        this.save = save;

        Reload();
        OnESC = OnCancel = () => { Focused = false; closing = true; };
        MinWidth = MinMenuWidth;
        Position.Y = ScrollTargetY;
        Alpha = 0f;
    }

    // ⚠️ Counted on Added and released on Removed OR SceneEnd: a scene change drops the entity
    // without ever calling Removed, and a count left raised would pause every mod's hotkeys until the
    // game restarts. The flag makes the release happen once whichever comes first.
    public override void Added(Scene scene) {
        base.Added(scene);
        if (counted) return;
        counted = true;
        HotkeyPause.RemapScreenOpened();

        // ⚠️ A paused level stops drawing its HUD, this screen included, while Journal is held
        // (Level.Render), and Journal is the gesture that clears a row. Vanilla's Options and
        // Everest's key-config screens turn it off the same way. Put back to what it was, not to
        // true: the Options screen may be open underneath.
        if (scene is Level level) {
            hudHideWas = level.AllowHudHide;
            level.AllowHudHide = false;
        }
    }

    public override void Removed(Scene scene) {
        base.Removed(scene);
        Release(scene);
    }

    public override void SceneEnd(Scene scene) {
        base.SceneEnd(scene);
        Release(scene);
    }

    private void Release(Scene scene) {
        if (!counted) return;
        counted = false;
        HotkeyPause.RemapScreenClosed();
        if (scene is Level level) level.AllowHudHide = hudHideWas;
    }

    private TSettings Settings => hotkeys.Settings();

    private void Reload(int index = -1) {
        Clear();

        Add(new Header(Dialog.Clean(text.HeaderId)));
        // The clear hint on the menu, where the gesture is used; the combo hint on the recording
        // overlay, where it is. A SubHeader is one line, and the combo hint is too long for one.
        // Replace, not string.Format, for the reason the countdown line gives.
        Add(new FittedHint(Dialog.Get(text.PageComboHintId).Replace("{0}", Bindable.MaxComboInputs.ToString())));
        // No top padding on the second: the two hints read as one block.
        Add(new FittedHint(Dialog.Clean(text.ClearHintId), topPadding: false));

        // Both sections walk the same table, so a keyboard row cannot exist without its controller
        // counterpart.
        AddSection(VanillaKeyboardTitle, keyboard: true, binding => binding.Keys,
                   (label, keys) => new Row(label, keys));
        AddSection(VanillaControllerTitle, keyboard: false, binding => binding.Buttons,
                   (label, buttons) => new Row(label, buttons));
        FitBindings();
        if (index >= 0) Selection = index;
    }

    private void AddSection<T>(string titleId, bool keyboard, Func<ButtonBinding, List<T>> inputs,
                               Func<string, List<T>, Row> newRow) where T : struct {
        Add(new SubHeader(Dialog.Clean(titleId)));
        List<(Row, List<T>)> rows = new();
        foreach (Keybind<TSettings> keybind in hotkeys.Keybinds) {
            ButtonBinding binding = keybind.Binding(Settings);
            if (binding is null) continue;
            Row row = newRow(Dialog.Clean(keybind.LabelId), inputs(binding));
            row.Pressed(() => StartRecording(keybind, keyboard))
               .AltPressed(() => ClearRow(keybind, keyboard));
            Add(row);
            if (keybind.DescriptionId is not null) Add(new RowDescription(Dialog.Clean(keybind.DescriptionId), row));
            rows.Add((row, inputs(binding)));
        }
        MarkClashes(rows);
    }

    // TextMenu makes the menu as wide as its widest label plus its widest binding, centres it, and
    // draws every label at its left edge: a wide enough binding pushed the labels off the screen. The
    // bindings get what is left of MaxLineWidth after the label column, and a wider one shrinks.
    // The label column is measured the way TextMenu measures it, over the items it counts.
    private void FitBindings() {
        float labelColumn = 0f;
        foreach (Item item in Items) {
            if (item.IncludeWidthInMeasurement) labelColumn = Math.Max(labelColumn, item.LeftWidth());
        }

        float room = Math.Max(0f, MaxLineWidth - labelColumn);
        foreach (Item item in Items) {
            if (item is Row row) row.BindingRoom = room;
        }
    }

    // Two rows of one section on the same inputs, in any order, both fire from one press. The screen
    // shows it rather than prevents it: which one to change is the player's call. A shorter combo
    // inside a longer one is not a clash; the longer one wins.
    private static void MarkClashes<T>(List<(Row Row, List<T> Inputs)> rows) where T : struct {
        for (int i = 0; i < rows.Count; i++) {
            for (int j = 0; j < rows.Count; j++) {
                if (i == j || !SameInputs(rows[i].Inputs, rows[j].Inputs)) continue;
                rows[i].Row.Clashes = true;
                break;
            }
        }
    }

    private static bool SameInputs<T>(List<T> a, List<T> b) where T : struct =>
        a.Count > 0 && new HashSet<T>(a).SetEquals(b);

    private void StartRecording(Keybind<TSettings> keybind, bool keyboard) {
        // ⚠️ Vanilla's rule: a pad is recorded only while the last input came from one. Confirming a
        // controller row from the keyboard would otherwise wait on a pad nobody is holding.
        if (!keyboard && !Input.GuiInputController()) {
            Audio.Play(InvalidSound);
            return;
        }

        recording = true;
        recordingKeybind = keybind;
        recordingKeyboard = keyboard;
        timeout = RecordSeconds;
        Focused = false;
        chordIcons = new List<object>();
        chordIconsFor = 0;
        if (keyboard) keyChord.Start(Bindable.BindableKeys(MInput.Keyboard.CurrentState.GetPressedKeys()));
        else buttonChord.Start(Bindable.HeldButtons(MInput.GamePads[Input.Gamepad].CurrentState));
    }

    private void Step<T>(ChordRecorder<T> chord, List<T> held, List<T> binding) where T : struct {
        int before = chord.Inputs.Count;
        ChordStep step = chord.Update(held);
        // The countdown measures how long the screen has waited for the player, not how long a chord
        // takes to build: otherwise a slow hand finding its next key loses the whole chord at zero.
        if (chord.Inputs.Count > before) timeout = RecordSeconds;

        switch (step) {
            case ChordStep.Refused:
                Audio.Play(InvalidSound);
                break;
            case ChordStep.Done:
                recording = false;
                refocusDelay = RefocusDelay;
                binding.Clear();
                binding.AddRange(chord.Inputs);
                Changed();
                break;
        }
    }

    // Clearing a whole row is the Journal action — vanilla's own gesture, wired the same way
    // (KeyboardConfigUI → AltPressed → Clear), so a player who rebound Journal keeps one gesture for
    // "clear a binding" across the game and every mod — and Delete, handled in Update.
    private void ClearRow(Keybind<TSettings> keybind, bool keyboard) {
        ButtonBinding binding = keybind.Binding(Settings);
        if (binding is null) return;

        // Already empty: say so the way vanilla says it rather than redraw an identical list.
        if ((keyboard ? binding.Keys.Count : binding.Buttons.Count) == 0) {
            Audio.Play(InvalidSound);
            return;
        }
        if (keyboard) binding.Keys.Clear();
        else binding.Buttons.Clear();
        Changed();
    }

    private void Changed() {
        save?.Invoke();
        // Belt and braces: the screen being open already pauses every hotkey while tracking what is
        // held, so the input just recorded cannot fire when it closes. This covers a mod polling
        // somewhere that runs before the screen is counted.
        hotkeys.Resync();
        Reload(Selection);
    }

    public override void Update() {
        base.Update();

        // ⚠️ RawDeltaTime throughout, never DeltaTime. These timers measure how long the player has
        // been looking at a menu, and DeltaTime carries Engine.TimeRate and Assist Mode's game speed:
        // at 50% the five-second timeout became ten real seconds.
        if (refocusDelay > 0f && !recording) {
            refocusDelay -= Engine.RawDeltaTime;
            if (refocusDelay <= 0f) Focused = true;
        }

        recordingEase = Calc.Approach(recordingEase, recording ? 1f : 0f, Engine.RawDeltaTime * 4f);

        if (recordingEase > 0.5f && recording) {
            // ⚠️ Escape and the timeout ONLY — never Input.MenuCancel. Cancelling on it made the
            // player's own cancel input unbindable: B on a controller, and whatever their keyboard
            // cancel is, were consumed as "stop recording" and could never be recorded. Vanilla's and
            // Everest's remap screens take Escape or the timeout for the same reason.
            //
            // A pad recording also stops when the pad loses focus: an unplugged pad reads every button
            // up, which would be saved as the chord being let go.
            if (Input.ESC.Pressed || timeout <= 0f || (!recordingKeyboard && !Input.GuiInputController())) {
                Input.ESC.ConsumePress();
                recording = false;
                Focused = true;
            } else if (recordingKeyboard) {
                Keys[] held = MInput.Keyboard.CurrentState.GetPressedKeys();
                if (Bindable.RefusedKeyWentDown(held, MInput.Keyboard.Pressed)) Audio.Play(InvalidSound);
                Step(keyChord, Bindable.BindableKeys(held), recordingKeybind.Binding(Settings).Keys);
            } else {
                Step(buttonChord, Bindable.HeldButtons(MInput.GamePads[Input.Gamepad].CurrentState),
                     recordingKeybind.Binding(Settings).Buttons);
            }
            timeout -= Engine.RawDeltaTime;
        }

        // Journal already clears the selected row: TextMenu.Update dispatches OnAltPressed on
        // Input.MenuJournal.Pressed inside its own `if (Focused)`, so it cannot fire mid-recording.
        // This widens the gesture to Delete through the row's own closure.
        //
        // ⚠️ Delete and NOT Backspace. Vanilla claims Backspace as menu cancel, so base.Update() above
        // has already started closing the screen by the time a Backspace check would run. Focused is
        // false for a quarter second after a recording, so a Delete just recorded cannot also clear
        // the row.
        if (Focused && !recording && !closing && Current?.OnAltPressed != null
            && MInput.Keyboard.Pressed(Keys.Delete)) {
            Current.OnAltPressed();
        }

        Alpha = Calc.Approach(Alpha, closing ? 0f : 1f, Engine.RawDeltaTime * 8f);
        if (!closing || Alpha > 0f) return;

        // Close() runs OnClose itself; invoking it here as well would run it twice.
        Close();
    }

    // The overlay's lines are the mod's own text, one line each. A line wider than this is drawn
    // smaller instead of running off both edges of the screen.
    internal const float MaxLineWidth = 1760f;

    /// <summary><paramref name="preferred"/>, or less if the line would be wider than <see cref="MaxLineWidth"/>.</summary>
    internal static float FitScale(string line, float preferred) {
        float width = ActiveFont.Measure(line).X * preferred;
        return width <= MaxLineWidth ? preferred : preferred * MaxLineWidth / width;
    }

    // Vanilla's keyboard config is 881 px wide with its default bindings. The same floor puts the labels
    // where vanilla's are while nothing is bound, and a first binding does not move them.
    internal const float MinMenuWidth = 880f;

    // TextMenu puts the longest label and the widest binding side by side with nothing between them.
    internal const float LabelGap = 64f;

    private sealed class Row : Setting {
        /// <summary>The widest this row's binding may be drawn. Set by the screen once every row exists.</summary>
        internal float BindingRoom = float.MaxValue;

        /// <summary>Another row of the section holds the same inputs. The binding is drawn red.</summary>
        internal bool Clashes;

        internal Row(string label, List<Keys> keys) : base(label, keys) { }
        internal Row(string label, List<Buttons> buttons) : base(label, buttons) { }
        public override float LeftWidth() => base.LeftWidth() + LabelGap;

        // Capped, so the menu's width, the widest label plus this, stays within MaxLineWidth.
        public override float RightWidth() => Math.Min(base.RightWidth(), BindingRoom);

        public override void Render(Vector2 position, bool highlighted) {
            float natural = base.RightWidth();
            if (natural <= BindingRoom && !Clashes) {
                base.Render(position, highlighted);
                return;
            }

            // Setting.Render, with the binding drawn smaller when it is wider than BindingRoom, and red
            // when it clashes.
            float scale = Math.Min(1f, BindingRoom / natural);
            float alpha = Container.Alpha;
            Color stroke = Color.Black * (alpha * alpha * alpha);
            Color color = Disabled ? Color.DarkSlateGray : (highlighted ? Container.HighlightColor : Color.White) * alpha;
            ActiveFont.DrawOutline(Label, position, new Vector2(0f, 0.5f), Vector2.One, color, 2f, stroke);

            Color icon = (Clashes ? Color.Red : Color.White) * alpha;
            Color text = (Clashes ? Color.Red : Color.LightGray) * alpha;
            float x = Container.Width - natural * scale;
            foreach (object value in Values) {
                if (value is MTexture texture) {
                    texture.DrawJustified(position + new Vector2(x, 0f), new Vector2(0f, 0.5f), icon, scale);
                    x += texture.Width * scale;
                } else if (value is string name) {
                    float width = (ActiveFont.Measure(name).X * 0.7f + 16f) * scale;
                    ActiveFont.DrawOutline(name, position + new Vector2(x + width * 0.5f, 0f), new Vector2(0.5f, 0.5f),
                                           Vector2.One * (0.7f * scale), text, 2f, stroke);
                    x += width;
                }
            }
        }
    }

    // What a row's hotkey does: a grey line under the row, open only while the row is selected. Like
    // FittedHint it sizes nothing, and it is drawn smaller when wider than MaxLineWidth.
    private sealed class RowDescription : SubHeader {
        private readonly Row row;
        private float ease;

        internal RowDescription(string title, Row row) : base(title, topPadding: false) {
            this.row = row;
            IncludeWidthInMeasurement = false;
        }

        public override void Update() {
            ease = Calc.Approach(ease, Container.Current == row ? 1f : 0f, Engine.RawDeltaTime * 4f);
        }

        // Closed, it gives back the spacing TextMenu puts after every item: a described row sits as
        // close to the next one as any other row does.
        public override float Height() =>
            -Container.ItemSpacing + (base.Height() + Container.ItemSpacing) * Ease.CubeOut(ease);

        public override void Render(Vector2 position, bool highlighted) {
            if (ease <= 0f) return;
            float alpha = Container.Alpha * ease;
            ActiveFont.DrawOutline(Title, position, new Vector2(0f, 0.5f), Vector2.One * FitScale(Title, 0.6f),
                                   Color.Gray * alpha, 2f, Color.Black * (alpha * alpha * alpha));
        }
    }

    // ⚠️ The clear hint is the mod's own text, of any length in any language. A plain SubHeader counts
    // its whole width into the menu's left column, and TextMenu adds that to the widest binding on the
    // right: a long hint pushed every row off both edges of the screen. This one sizes nothing. It is
    // centred on the menu, which is centred on the screen, and drawn smaller when wider than MaxLineWidth.
    private sealed class FittedHint : SubHeader {
        internal FittedHint(string title, bool topPadding = true) : base(title, topPadding) {
            IncludeWidthInMeasurement = false;
        }

        // SubHeader's own drawing, at its own 0.6 scale and colours, but centred and fitted.
        public override void Render(Vector2 position, bool highlighted) {
            if (Title.Length == 0) return;
            float alpha = Container.Alpha;
            ActiveFont.DrawOutline(Title, position + new Vector2(Container.Width * 0.5f, TopPadding ? 32f : 0f),
                                   new Vector2(0.5f, 0.5f), Vector2.One * FitScale(Title, 0.6f),
                                   Color.Gray * alpha, 2f, Color.Black * (alpha * alpha * alpha));
        }
    }

    public override void Render() {
        Draw.Rect(-10f, -10f, 1940f, 1100f, Color.Black * Ease.CubeOut(Alpha));
        base.Render();
        if (recordingEase <= 0f) return;

        Draw.Rect(-10f, -10f, 1940f, 1100f, Color.Black * 0.95f * Ease.CubeInOut(recordingEase));
        Vector2 centre = new Vector2(1920f, 1080f) * 0.5f;
        Color grey = Color.LightGray * Ease.CubeIn(recordingEase);

        string hint = Dialog.Clean(text.ComboHintId);
        ActiveFont.Draw(hint, centre + new Vector2(0f, -32f),
                        new Vector2(0.5f, 2f), Vector2.One * FitScale(hint, 0.7f), grey);
        ActiveFont.Draw(Dialog.Clean(recordingKeyboard ? VanillaKeyChanging : VanillaButtonChanging),
                        centre + new Vector2(0f, -8f), new Vector2(0.5f, 1f), Vector2.One * 0.7f, grey);
        string label = Dialog.Clean(recordingKeybind.LabelId);
        float labelScale = FitScale(label, 2f);
        ActiveFont.Draw(label, centre + new Vector2(0f, 8f),
                        new Vector2(0.5f, 0f), Vector2.One * labelScale, Color.White * Ease.CubeIn(recordingEase));
        // The screen used to give up after five seconds with nothing said, which reads as the binding
        // having failed. Ceiling, so the first thing the player sees is the full five and the last
        // whole second is not skipped.
        //
        // ⚠️ Placed from the font, not by a constant: the label above is top-justified at labelScale
        // from +8, so it ends labelScale × LineHeight lower, and any fixed offset crosses it at some
        // font size or label length.
        float belowLabel = 8f + ActiveFont.LineHeight * labelScale + 8f;
        // Replace, not string.Format: the line is a translator's text, and Format throws on a stray brace
        // in it — from Render, on every frame.
        string seconds = ((int) Math.Ceiling(Math.Max(0f, timeout))).ToString();
        ActiveFont.Draw(Dialog.Get(text.TimeoutFormatId).Replace("{0}", seconds),
                        centre + new Vector2(0f, belowLabel), new Vector2(0.5f, 0f), Vector2.One * 0.7f, grey);

        // Below the countdown rather than above it, so the countdown does not move when the first input
        // goes down.
        float belowCountdown = belowLabel + ActiveFont.LineHeight * 0.7f + 16f;
        DrawChord(centre + new Vector2(0f, belowCountdown + ActiveFont.LineHeight * 0.5f), Ease.CubeIn(recordingEase));
    }

    // The inputs held so far, drawn the way a row draws its binding, so the player sees what letting go
    // will save. At most MaxComboInputs of them, which fit well within MaxLineWidth.
    private void DrawChord(Vector2 middle, float alpha) {
        int count = recordingKeyboard ? keyChord.Inputs.Count : buttonChord.Inputs.Count;
        if (count == 0) return;
        if (count != chordIconsFor) {
            // Setting builds the icons, or the names where the game has no icon, from a list.
            chordIcons = recordingKeyboard
                ? new Setting("", new List<Keys>(keyChord.Inputs)).Values
                : new Setting("", new List<Buttons>(buttonChord.Inputs)).Values;
            chordIconsFor = count;
        }

        float width = 0f;
        foreach (object value in chordIcons) width += IconWidth(value);

        Color stroke = Color.Black * (alpha * alpha * alpha);
        float x = middle.X - width * 0.5f;
        foreach (object value in chordIcons) {
            if (value is MTexture texture) {
                texture.DrawJustified(new Vector2(x, middle.Y), new Vector2(0f, 0.5f), Color.White * alpha, 1f);
            } else if (value is string name) {
                ActiveFont.DrawOutline(name, new Vector2(x + IconWidth(value) * 0.5f, middle.Y), new Vector2(0.5f, 0.5f),
                                       Vector2.One * 0.7f, Color.LightGray * alpha, 2f, stroke);
            }
            x += IconWidth(value);
        }
    }

    // Setting's own measure: an icon's width, or a name at 0.7 plus 16.
    private static float IconWidth(object value) => value switch {
        MTexture texture => texture.Width,
        string name => ActiveFont.Measure(name).X * 0.7f + 16f,
        _ => 0f,
    };
}
