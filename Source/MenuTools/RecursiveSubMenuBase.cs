// MenuTools requires: IInputHoldingItem.cs
using Microsoft.Xna.Framework;
using Monocle;
using System;
using System.Collections.Generic;
using System.Reflection;
using Celeste.Mod.Core;
using Celeste.Mod.UI;

namespace Celeste.Mod.SpeedrunSheet.MenuTools;

/// <summary>
/// Implement on a <see cref="TextMenu.Item"/> to be told which recursive submenu contains it, for example to call
/// <see cref="RecursiveSubMenuBase.RecalculateSize"/> when the item's size changes. The submenu sets it when it
/// attaches the item: on the add if the submenu is in a menu, otherwise when the submenu is added to one. It resets
/// it to null when the item is removed, on Clear and RemoveFromMenu, and when the submenu is added to a menu again.
/// </summary>
public interface ISubMenuAwareItem {
    /// <summary>
    /// The recursive submenu that holds the item, or null. The submenu writes it; the item only reads it.
    /// </summary>
    RecursiveSubMenuBase ContainingSubMenu { get; set; }
}

/// <summary>
/// Base class for all flavors of recursive submenus. Includes most of the implementation for all of them.
/// </summary>
/// <remarks>
/// A submenu holds a list of menus, each a list of <see cref="TextMenu.Item"/>s; the single-menu flavors have one.
/// Items can be added before or after the submenu is added to a <see cref="TextMenu"/> or to another recursive
/// submenu. They are attached (given a container, measured, drawn, updated) only while the submenu is in a menu. Take
/// a submenu out with <see cref="RemoveFromMenu"/>, not with <see cref="TextMenu.Remove(TextMenu.Item)"/>.
/// <para/>
/// The submenu uses <see cref="TextMenu.Item.OnEnter"/> and <see cref="TextMenu.Item.OnLeave"/> itself: add handlers
/// with <see cref="Enter(Action)"/> and <see cref="Leave(Action)"/>. To make another flavor, subclass it and
/// implement the abstract properties.
/// </remarks>
public abstract class RecursiveSubMenuBase : TextMenu.Item, IInputHoldingItem {
    // ================ Configuration values initialized by corresponding named constructor parameters =================
    /// <summary>Name to display as the title of the submenu</summary>
    public readonly string Label;
    /// <summary>Index of the initial menu to display</summary>
    public readonly int InitialMenuSelection;
    /// <summary>
    /// If false, align left and right columns to the global column division and reserve enough width accordingly.
    /// If true, reserve only enough width to fit each item individually and attempt to return a
    /// <see cref="RightWidth"/> that minimizes the overall width of the whole menu.
    /// </summary>
    public readonly bool CompactMode;
    /// <summary>
    /// Value to return for <see cref="RightWidth"/> when in compact mode.
    /// If initialized to nan, defaults to the right-width of an on-off slider.
    /// </summary>
    public readonly float CompactRightWidth;
    /// <summary>Vertical spacing between items</summary>
    public readonly float ItemSpacing;
    /// <summary>Horizontal indent for the body of the submenu</summary>
    public readonly float ItemIndent;

    // Additional appearance parameters not set by the constructor.
    private readonly MTexture icon            = GFX.Gui["downarrow"];
    private const float optionTextScale       = 0.8f;
    private const float bracketsReservedWidth = 130f;
    private const float iconXPadding          = 10f;

    /// <summary>
    /// How far a submenu has taken over the input from what contains it. A submenu passes the input and the scrolling
    /// on to the nested submenu that has the selection.
    /// </summary>
    public enum FocusType {
        /// <summary>
        /// The submenu handles no input: the selection is elsewhere, or on its title while the menu or submenu that
        /// contains it still handles the input
        /// </summary>
        None,
        /// <summary>
        /// The submenu handles the input with the selection on its title. Only a submenu that is entered by scrolling
        /// (<see cref="AutoEnter"/>) is ever in this state.
        /// </summary>
        Title,
        /// <summary>The submenu handles the input with the selection on one of its items</summary>
        Body,
        /// <summary>The selection is in a submenu nested in this one, which handles the input</summary>
        Child
    };
    /// <summary>
    /// The part this submenu has in the input right now; see <see cref="FocusType"/>. The player's input,
    /// <see cref="MoveSelectionTo"/> and <see cref="SuspendInput"/> change it.
    /// </summary>
    public FocusType Focus { get; protected set; } = FocusType.None;
    /// <summary>
    /// Whether this submenu scrolls the menu to keep the selection in view, in place of the menu itself
    /// </summary>
    public bool AutoScroll { get; protected set; } = false;
    /// <summary>
    /// Whether the submenu is the selected row of what contains it: set by <see cref="DefaultOnEnter"/> and
    /// <see cref="MoveSelectionTo"/>, cleared by <see cref="DefaultOnLeave"/>
    /// </summary>
    protected bool receivedHover          = false;
    /// <summary>
    /// The recursive submenu this one is an attached item of, or null if it sits directly in a
    /// <see cref="TextMenu"/> or in no menu
    /// </summary>
    protected RecursiveSubMenuBase parent = null;
    // Set by MoveSelectionTo, whose autoScroll argument decides AutoScroll: the value taken from whatever scrolled
    // before, which DefaultOnLeave gives back. Null after DefaultOnEnter, where AutoScroll itself is that value.
    private bool? autoScrollToReturn;
    // How many times the submenu stopped being the selected row: tells SuspendInput's restore action that it left
    private int leaveCount;
    // How many times MoveSelectionTo put the selection here: tells SuspendInput's restore action that a handler chose
    // a row meanwhile
    private int selectionMoves;
    // How many SuspendInput calls on this submenu have not been restored yet
    private int suspensions;
    // Whether the selected item's OnLeave is running: a handler that clears or removes the submenu comes back to the
    // exit code, which must not call it again
    private bool leavingItem;

    /// <summary>
    /// A list of items with the label that the menu slider shows for it, on a submenu with several menus
    /// </summary>
    public struct LabeledMenu {
        /// <param name="label">The label of the menu</param>
        /// <param name="menu">The items of the menu, or null for none</param>
        public LabeledMenu(string label, List<TextMenu.Item> menu) {
            Label = label;
            Menu  = menu;
        }
        /// <summary>The label of the menu</summary>
        public string Label;
        /// <summary>The items of the menu</summary>
        public List<TextMenu.Item> Menu;
    }
    // Every menu, whether its items are attached or not
    private List<LabeledMenu> menus = [];
    // The menu Added has attached the items to: given them a Container, wigglers and their own Added. Null until then,
    // and the submenu shows and measures nothing. TextMenu.Remove calls nothing on the submenu, so this stays set, and
    // is the only way back to that menu, until Detach.
    private TextMenu attachedTo;
    // Whether an item can be attached, animated or selected right now
    private bool Attached => attachedTo != null && Container == attachedTo;

    /// <summary>
    /// Index of the menu shown. Until the submenu is first added to a menu it is 0, not
    /// <see cref="InitialMenuSelection"/>, which is applied on that add. Before then only <see cref="SelectMenu"/>
    /// (stored unchecked) and an <see cref="InsertMenu"/> at or before the menu to start on change it; the add clamps
    /// it.
    /// </summary>
    public int MenuIndex { get; private set; } = 0;

    // Menu to start on at the next Added, instead of InitialMenuSelection: chosen with SelectMenu while the items were
    // not attached, or the one shown when they were detached
    private int? pendingMenuSelection;

    /// <summary>
    /// Index of the currently selected <see cref="TextMenu.Item"/> within the selected list, or -1 before any item
    /// of it was selected
    /// </summary>
    public int Selection { get; private set; } = -1;

    /// <summary>
    /// Invoked with the new <see cref="MenuIndex"/> when the menu shown changes while the submenu is in a menu: when
    /// the player presses Left or Right on the title, by <see cref="SelectMenu"/>, and by
    /// <see cref="MoveSelectionTo"/> to an item of another menu. It runs as the switch starts, before its animation.
    /// Not invoked when <see cref="InsertMenu"/> shifts the index, by <see cref="Clear"/>, or while the submenu is
    /// not in a menu.
    /// </summary>
    public Action<int> OnValueChange;

    // Handle wiggling manually rather than letting it go through SelectWiggler
    /// <summary>Wiggles the title; null while the items are not attached to a menu</summary>
    protected Wiggler titleWiggler;
    /// <summary>Wiggles the items as a block; null while the items are not attached to a menu</summary>
    protected Wiggler menuWiggler;

    // Size tracking
    /// <summary>
    /// Height of the items of the menu shown, spacing included, as of the last <see cref="RecalculateSize"/>
    /// </summary>
    protected float menuHeight;
    /// <summary>Height of the title row, as of the last <see cref="RecalculateSize"/></summary>
    protected float titleHeight;
    /// <summary>Widest left part among the title and the items of every menu, indent included</summary>
    protected float leftColumnWidth;
    /// <summary>Widest right part among the items of every menu and the menu slider</summary>
    protected float rightColumnWidth;
    /// <summary>Width reserved for the menu slider</summary>
    protected float optionsWidth;
    /// <summary>Width of the widest row taken as a whole, which compact mode reserves</summary>
    protected float compactWidth;

    // Tracking for wiggling the arrows for the menu select
    private int lastDir;
    private float sine;

    // Handle smoothly switching between menus
    /// <summary>Whether the height animation of a menu switch, an expansion or a collapse is running</summary>
    protected bool switchingMenus;
    /// <summary>Index of the menu the running switch started from</summary>
    protected int switchFromMenuIndex;
    /// <summary>Height the running switch started from</summary>
    protected float switchFromMenuHeight;
    private float switchMenuEase           = 1f;
    private const float maxSwitchMenuTime  = 0.25f;
    private const float switchMenuVelocity = 64f / 0.1f;  // Pixels per second
    private float switchMenuEaseRate       = 1f / maxSwitchMenuTime;  // Per second
    private float EasedMenuHeight {
        get { return switchFromMenuHeight + Ease.QuadOut(switchMenuEase) * (menuHeight - switchFromMenuHeight); }
    }

    // Handle smoothly adding, removing, showing and hiding items
    private enum AnimationKind { Add, Remove, Show, Hide }
    private class AddRemoveItemState {
        private const float maxAddRemoveItemTime  = 0.25f;
        private const float addRemoveItemVelocity = 64f / 0.1f;  // Pixels per second

        public bool Done => ease >= 1f;

        public AddRemoveItemState(TextMenu.Item item, List<TextMenu.Item> menu, AnimationKind kind,
                                  Action<TextMenu.Item> doneCb) {
            this.Item          = item;
            this.Menu          = menu;
            this.Kind          = kind;
            this.DoneCb        = doneCb;
            this.WasVisible    = item.Visible;
            this.WasSelectable = item.Selectable;
            easeRate           = EaseRate(maxAddRemoveItemTime, addRemoveItemVelocity, item.Height());
        }

        public void Update() {
            ease = Math.Min(ease + easeRate * Engine.RawDeltaTime, 1f);
        }

        public float EasedHeight(float itemSpacing) {
            bool growing = Kind is AnimationKind.Add or AnimationKind.Show;
            return (Item.Height() + itemSpacing) * (growing ? Ease.QuadOut(ease) : 1 - Ease.QuadOut(ease));
        }

        public readonly TextMenu.Item Item;
        public readonly List<TextMenu.Item> Menu;  // Held, not its index: InsertMenu moves indices
        public readonly AnimationKind Kind;
        public readonly Action<TextMenu.Item> DoneCb;
        // The item is hidden during the animation; Add and Remove give these back when they end or are cut
        public readonly bool WasVisible;
        public readonly bool WasSelectable;
        private readonly float easeRate;  // Per second
        private float ease = 0f;
    }
    private List<AddRemoveItemState> addRemoveItems = [];

    // The rate, per second, of an animation over a distance in pixels: at the given velocity, but in no more than the
    // given time. Finite when there is no distance, where it ends in one frame: an infinite rate times a frame of
    // zero length would be NaN, and the animation would never end.
    private static float EaseRate(float maxTime, float velocity, float distance) {
        distance = Math.Abs(distance);
        return distance < 1f ? velocity : Math.Max(1f / maxTime, velocity / distance);
    }

    private AddRemoveItemState AnimationOf(TextMenu.Item item) {
        foreach (AddRemoveItemState addRemoveItem in addRemoveItems) {
            if (addRemoveItem.Item == item) {
                return addRemoveItem;
            }
        }
        return null;
    }

    // Reused every Update so items can add or remove items (their own submenu's included) while being updated
    private readonly List<TextMenu.Item> updatingItems = [];

    /// <summary>
    /// The selected set of <see cref="TextMenu.Item"/>s. Empty if the submenu has no menus, and while its items are not
    /// attached to a menu: before it is added, and after <see cref="RemoveFromMenu"/> or removal from a parent submenu.
    /// </summary>
    public List<TextMenu.Item> CurrentMenu {
        get {
            return (attachedTo != null && MenuIndex >= 0 && MenuIndex < menus.Count) ? menus[MenuIndex].Menu : noMenu;
        }
    }
    // Stands in for CurrentMenu while there is nothing to show (the items are not attached to a menu, or there are no
    // menus, as after Clear), so callers don't need null checks. Items are only ever added to real menus.
    private readonly List<TextMenu.Item> noMenu = [];

    /// <summary>
    /// The list of <see cref="TextMenu.Item"/>s that the running menu switch started from, or null when no switch is
    /// animating
    /// </summary>
    protected List<TextMenu.Item> SwitchFromMenu {
        get { return (menus.Count > 0 && switchingMenus) ? menus[switchFromMenuIndex].Menu : null; }
    }

    /// <summary>
    /// The selected <see cref="TextMenu.Item"/>, or null unless the selection is in the items of the submenu
    /// (<see cref="Focus"/> is <see cref="FocusType.Body"/> or <see cref="FocusType.Child"/>)
    /// </summary>
    public TextMenu.Item Current {
        get {
            if (Focus < FocusType.Body || Selection < 0 || Selection >= CurrentMenu.Count) {
                return null;
            }
            return CurrentMenu[Selection];
        }
    }

    /// <summary>Index of the first selectable item in the currently selected menu, or 0 if it has none</summary>
    public int FirstPossibleSelection {
        get {
            for (int i = 0; i < CurrentMenu.Count; i++) {
                if (CanSelect(CurrentMenu[i])) {
                    return i;
                }
            }
            return 0;
        }
    }

    /// <summary>Index of the last selectable item in the currently selected menu, or 0 if it has none</summary>
    public int LastPossibleSelection {
        get {
            for (int i = CurrentMenu.Count - 1; i >= 0; i--) {
                if (CanSelect(CurrentMenu[i])) {
                    return i;
                }
            }
            return 0;
        }
    }

    // Whether the selection can go to an item of a submenu: it can be hovered, and it is not a submenu that has
    // nothing to select itself
    private static bool CanSelect(TextMenu.Item item) {
        return item != null && item.Hoverable && item is not RecursiveSubMenuBase { NothingToSelect: true };
    }

    // A submenu entered by scrolling whose title cannot hold the selection and whose items, shown as they are, cannot
    // either: it would take the focus and show nothing selected. Not a submenu that shows its items only once
    // entered: entering it is the way to read them.
    private bool NothingToSelect => AutoEnter && !TitleSelectable && ShowMenu && !CurrentMenu.Exists(CanSelect);

    /// <summary>Target Y position for the menu to keep the current item on screen</summary>
    private float ScrollTargetY {
        get {
            if (Container.Height < Container.ScrollableMinSize) {
                return Engine.Height / 2f;
            } else {
                float min = Engine.Height - 150f - Container.Height * Container.Justify.Y;
                float max = 150f + Container.Height * Container.Justify.Y;
                return Calc.Clamp((Engine.Height / 2) + Container.Height * Container.Justify.Y - GetYOffsetOf(Current),
                                  min, max);
            }
        }
    }

    // OuiModOptions.menu is added by Everest (it is not a vanilla Celeste member), so
    // CelesteMod.Publicizer does not publicize it and it is not referenced directly; read through
    // reflection instead, resolved once. Null if Everest renames the field.
    private static readonly FieldInfo ouiModOptionsMenuField =
        typeof(OuiModOptions).GetField("menu", BindingFlags.Instance | BindingFlags.NonPublic);

    // Whether the submenu is in the main menu's Mod Options rather than the in-game one. If Everest renames the field
    // above, this is always false and nothing throws: a Cancel that leaves the whole menu from inside a submenu no
    // longer returns to the main menu.
    private bool InOverworldMenu {
        get { return ouiModOptionsMenuField != null && OuiModOptions.Instance != null &&
                  OuiModOptions.Instance.Selected &&
                  Container == (TextMenu) ouiModOptionsMenuField.GetValue(OuiModOptions.Instance); }
    }

    // ============ Additional knobs for subclasses to alter the behavior and appearance of the submenu ================
    /// <summary>
    /// True to allow selecting the title of the submenu like any other item, false to skip past the title instead
    /// (if false, <see cref="AutoEnter"/> should be true so some part of the submenu is selectable)
    /// </summary>
    protected abstract bool TitleSelectable { get; }
    /// <summary>
    /// Whether to automatically enter the menu body while scrolling, without needing to press Confirm on the title.
    /// Either scroll from the title to the body or skip the title entirely depending on <see cref="TitleSelectable"/>.
    /// </summary>
    protected abstract bool AutoEnter { get; }
    /// <summary>
    /// If true, automatically leave the submenu when selection leaves its bounds, rather than wrapping or stopping
    /// </summary>
    protected abstract bool AutoExit { get; }
    /// <summary>
    /// Whether to exit the parent submenu or container TextMenu when Cancel is pressed (useful for naked / hidden
    /// submenus to preserve the illusion)
    /// </summary>
    protected abstract bool RecursiveExit { get; }
    /// <summary>Whether to display the title of the submenu</summary>
    protected abstract bool ShowTitle { get; }
    /// <summary>Whether to display the arrow icon in the submenu title</summary>
    protected abstract bool ShowIcon { get; }
    /// <summary>Whether to display the option slider for switching between submenus</summary>
    protected abstract bool ShowMenusSlider { get; }
    /// <summary>Whether to show the items in the currently selected submenu</summary>
    protected abstract bool ShowMenu { get; }
    /// <summary>Which submenu to use when rendering items, or null to render no items</summary>
    protected abstract List<TextMenu.Item> RenderingMenu { get; }

    // ================================================= Construction ==================================================
    /// <param name="label">The title of the submenu. Default: empty.</param>
    /// <param name="initialMenuSelection">
    ///     Initializes <see cref="RecursiveSubMenuBase.InitialMenuSelection"/>: the index of the menu shown when the
    ///     submenu is first added to a menu, clamped to the menus it has then. Default: 0, the first menu.
    /// </param>
    /// <param name="compactMode">
    ///     Initializes <see cref="RecursiveSubMenuBase.CompactMode"/>. If true (the default), the submenu reserves
    ///     only the width its widest row needs. If false, its rows line up with the left and right columns of the
    ///     menu, which can widen the whole menu.
    /// </param>
    /// <param name="compactRightWidth">
    ///     Initializes <see cref="RecursiveSubMenuBase.CompactRightWidth"/>: in compact mode, how much of the width
    ///     of the submenu counts toward the right column of the menu. Default: NaN, which takes the right width of an
    ///     On/Off option. The narrowest menu comes from the largest right width among the other rows of the menu.
    /// </param>
    /// <param name="itemSpacing">
    ///     Initializes <see cref="RecursiveSubMenuBase.ItemSpacing"/>: the vertical space between the rows of the
    ///     submenu, in pixels. Default: 4.
    /// </param>
    /// <param name="itemIndent">
    ///     Initializes <see cref="RecursiveSubMenuBase.ItemIndent"/>: how far the items are indented from the title,
    ///     in pixels. Default: 20.
    /// </param>
    public RecursiveSubMenuBase(string label             = "",
                                int initialMenuSelection = 0,
                                bool compactMode         = true,
                                float compactRightWidth  = float.NaN,
                                float itemSpacing        = 4f,
                                float itemIndent         = 20f) : base() {
        Label                = label;
        InitialMenuSelection = initialMenuSelection;
        CompactMode          = compactMode;
        CompactRightWidth    = compactRightWidth;
        ItemSpacing          = itemSpacing;
        ItemIndent           = itemIndent;
        if (float.IsNaN(CompactRightWidth)) {
            CompactRightWidth = Math.Max(ActiveFont.Measure(Dialog.Clean("OPTIONS_ON")).X,
                                         ActiveFont.Measure(Dialog.Clean("OPTIONS_OFF")).X) + 120f;
        }

        Selectable                = true;
        IncludeWidthInMeasurement = true;

        // The submenu needs these two public fields for itself: Enter(Action) and Leave(Action) keep them working
        OnEnter = DefaultOnEnter;
        OnLeave = DefaultOnLeave;

        RecalculateSize();
    }

    /// <summary>
    /// Attaches the items of every menu to the menu the submenu was just added to, in order, each getting its own
    /// <c>Added()</c>. A <see cref="TextMenu"/> or the parent submenu calls it. On a submenu that was in a menu
    /// before, the items are first detached from that one, and the menu shown stays the same.
    /// </summary>
    public override void Added() {
        base.Added();
        // Added again after TextMenu.Remove: the items, and maybe the focus, are still with the menu the submenu was
        // taken out of
        Detach();
        // Add every menu again, so each item gets its Added with only the items before it attached and measured
        List<LabeledMenu> addingMenus = menus;
        menus = [];
        attachedTo = Container;
        // MenuIndex may already point past the menus being re-added here (set by SelectMenu before the add)
        MenuIndex = 0;
        foreach (LabeledMenu menu in addingMenus) {
            AddMenu(menu.Label, menu.Menu);
        }
        MenuIndex = Calc.Clamp(pendingMenuSelection ?? InitialMenuSelection, 0, Math.Max(menus.Count - 1, 0));
        RecalculateSize();

        // Take manual control of wiggling
        SelectWiggler.Init(0f, 0f, null, false, false);
        SelectWiggler.StartZero = true;
        Container.Add(titleWiggler = Wiggler.Create(0.25f, 3f, null, false, false));
        Container.Add(menuWiggler = Wiggler.Create(0.25f, 3f, null, false, false));
        titleWiggler.UseRawDeltaTime = true;
        menuWiggler.UseRawDeltaTime  = true;
    }

    /// <summary>
    /// Set <see cref="OnValueChange"/>, the action invoked with the new menu index when the menu shown changes
    /// </summary>
    /// <param name="onValueChange">The handler; it replaces the one set before</param>
    /// <returns>This submenu</returns>
    public RecursiveSubMenuBase Change(Action<int> onValueChange) {
        OnValueChange = onValueChange;
        return this;
    }

    /// <summary>
    /// Remove all menus and <see cref="TextMenu.Item"/>s from the submenu. If the submenu has focus, it is given back
    /// to the parent first. Items can be added again afterwards; for single-menu submenus, the menu is recreated.
    /// <see cref="OnValueChange"/> is not invoked. An item that was being smoothly added or removed leaves with the
    /// <c>Visible</c> and <c>Selectable</c> it had before, and a show or hide ends at its target; the callbacks of
    /// those animations are not called. An override must call the base method.
    /// </summary>
    public virtual void Clear() {
        ReleaseFocus();
        if (attachedTo != null && Container != attachedTo) {
            // Taken out by TextMenu.Remove while selected: no OnLeave will come to give the AutoScroll back
            DefaultOnLeave();
        }
        foreach (LabeledMenu menu in menus) {
            foreach (TextMenu.Item item in menu.Menu) {
                DetachItem(item);
            }
        }
        menus.Clear();
        foreach (AddRemoveItemState addRemoveItem in addRemoveItems) {
            RestoreItem(addRemoveItem);
        }
        addRemoveItems.Clear();
        MenuIndex            = 0;
        pendingMenuSelection = null;
        Selection            = -1;
        switchingMenus       = false;
        switchMenuEase       = 1f;
        RecalculateSize();
    }

    /// <summary>
    /// Give up any focus held by this submenu or a submenu nested in it, returning focus to the parent
    /// </summary>
    protected void ReleaseFocus() {
        if (Focus == FocusType.Child && Current is RecursiveSubMenuBase child) {
            child.ReleaseFocus();
        }
        if (Focus != FocusType.None) {
            ForceExit();
        }
    }

    /// <summary>
    /// Take this submenu out of the <see cref="TextMenu"/> or the recursive submenu that holds it. This is the way to
    /// remove a submenu that may be hovered or focused: the focus and the <see cref="TextMenu.AutoScroll"/> it holds
    /// go back first, and the selection moves to a neighbouring row. While a page or the room chooser has the
    /// submenu's input (<see cref="SuspendInput"/>), they go back when that closes. On a submenu already taken out
    /// with <see cref="TextMenu.Remove(TextMenu.Item)"/> it gives back what that left with the submenu. Otherwise it
    /// does nothing on a submenu that is not in a menu.
    /// </summary>
    /// <remarks>
    /// Safe from the <c>OnPressed</c> of any item, and from the <c>OnPressed</c> or <c>OnUpdate</c> of an item of this
    /// submenu at any depth. The row disappears at once, but a submenu directly in a <see cref="TextMenu"/> leaves
    /// <see cref="TextMenu.Items"/> only at the end of the frame, hidden and unselectable until then (through its own
    /// <c>Visible</c> and <c>Selectable</c>). Adding it to another menu in that handler with <c>smooth</c> leaves it
    /// hidden, and as the first selectable row of that menu it is skipped by its first selection. Add it after the
    /// frame.
    /// <para/>
    /// From the <c>OnUpdate</c> or <c>Update</c> of another item of the menu, or the submenu's own <c>OnUpdate</c>, it
    /// throws <see cref="InvalidOperationException"/>: the menu is looping over its items there.
    /// <para/>
    /// <see cref="TextMenu.Remove(TextMenu.Item)"/> calls nothing on the item it removes, and neither does
    /// <c>TextMenu.Clear()</c> on the items it drops. Called directly on a
    /// focused submenu it leaves the menu unfocused, where Cancel, ESC and Pause do nothing, until the submenu's
    /// next <see cref="Clear"/> or <see cref="RemoveFromMenu"/>, or until it is added again. Called while a page or
    /// the room chooser has the submenu's input, the menu gets the focus back when that closes.
    /// </remarks>
    public void RemoveFromMenu() {
        if (parent != null) {
            // A submenu updates a copy of its items, so this is safe from a handler too
            int menuIndex = parent.menus.FindIndex((LabeledMenu menu) => menu.Menu.Contains(this));
            if (menuIndex >= 0) {
                parent.RemoveItem(menuIndex, this, false, null);
            }
            return;
        }
        TextMenu container = Container;
        int index          = container?.IndexOf(this) ?? -1;
        if (index < 0) {
            // Not in a menu. TextMenu.Remove may have taken the submenu out with the focus and the items still
            // attached.
            Detach();
            return;
        }
        ReleaseFocus();
        if (container.Selection == index) {
            // Off this row first, as RemoveItem does in a submenu
            MoveMenuSelectionOffCurrent(container);
        }
        // Gives the AutoScroll back if no OnLeave came from the move
        Detach();
        Scene scene = container.Scene ?? Engine.Scene;
        if (updatingMenu != container || scene == null) {
            container.Remove(this);
            ForgetRowAt(container, index);
            return;
        }
        // TextMenu.Update is going through its items and would throw if the list changed. Everything but the list
        // is done now, as TextMenu.Remove would, and the row stays as one that is neither drawn nor selectable.
        Container = null;
        container.Remove(ValueWiggler);
        container.Remove(SelectWiggler);
        if (rowsToRemove++ == 0) {
            wasVisible    = Visible;
            wasSelectable = Selectable;
            Visible       = false;
            Selectable    = false;
        }
        container.RecalculateSize();
        scene.OnEndOfFrame += () => RemoveRow(container);
    }

    // The menu whose Update is calling the Update of its recursive submenus, so is going through TextMenu.Items
    private static TextMenu updatingMenu;
    // Rows that RemoveFromMenu left in a menu for the end of the frame, and what hiding them replaced
    private int rowsToRemove;
    private bool wasVisible;
    private bool wasSelectable;

    // The part of RemoveFromMenu that waited for the end of the frame. The submenu may be in another menu by now: the
    // row to remove is the one in the menu it was left in.
    private void RemoveRow(TextMenu container) {
        if (--rowsToRemove == 0) {
            Visible    = wasVisible;
            Selectable = wasSelectable;
            RecalculateSize();
            Container?.RecalculateSize();
        }
        int index = container.IndexOf(this);
        if (index >= 0) {
            container.Items.RemoveAt(index);
            ForgetRowAt(container, index);
            container.RecalculateSize();
        }
    }

    // TextMenu.Remove leaves Selection alone: keep it on its row, or on none if the removed row was the only selectable
    // one
    private static void ForgetRowAt(TextMenu container, int index) {
        if (container.Selection == index) {
            container.Selection = -1;
        } else if (container.Selection > index) {
            --container.Selection;
        }
    }

    // =============================================== Add menus =======================================================
    /// <summary>Insert a labeled menu into the list of submenus</summary>
    /// <param name="menuIndex">
    ///     Index at which to insert the menu, from 0 to the number of menus. The number of menus adds it at the end,
    ///     as <see cref="AddMenu"/> does. The menu shown stays the same one, so <see cref="MenuIndex"/> goes up by
    ///     one when the new menu lands at or before it.
    /// </param>
    /// <param name="label">The label of the menu, which the menu slider shows</param>
    /// <param name="items">
    ///     The items of the menu, or null for none. They are added one by one: the list itself is not kept.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="menuIndex"/> is negative or greater than the number of menus
    /// </exception>
    protected void InsertMenu(int menuIndex, string label, List<TextMenu.Item> items) {
        int existing = menus.Count;
        menus.Insert(menuIndex, new LabeledMenu(label, []));
        // An index shifts when the new menu lands at or before the existing menu it names; an append never shifts
        // one. Animations hold their menu, not an index.
        bool Shifts(int index) => menuIndex <= index && index < existing;
        if (attachedTo != null) {
            if (Shifts(MenuIndex)) {
                ++MenuIndex;
            }
            if (switchingMenus && Shifts(switchFromMenuIndex)) {
                ++switchFromMenuIndex;
            }
        } else {
            // The menu Added will start on
            int stored = pendingMenuSelection ?? InitialMenuSelection;
            if (Shifts(stored)) {
                pendingMenuSelection = stored + 1;
                MenuIndex            = stored + 1;
            }
        }
        if (items != null) {
            foreach (TextMenu.Item item in items) {
                AddItem(menuIndex, item, false, null);
            }
        }
        RecalculateSize();
    }

    /// <summary>Add a labeled menu to the end of the list of submenus</summary>
    /// <param name="label">The label of the menu, which the menu slider shows</param>
    /// <param name="items">The items of the menu, or null for none. The list itself is not kept.</param>
    protected void AddMenu(string label, List<TextMenu.Item> items) {
        InsertMenu(menus.Count, label, items);
    }

    // ================================================ Add items ======================================================
    /// <summary>
    ///     Insert an item into an existing menu of the submenu. While the submenu is in a menu the item is attached
    ///     at once; otherwise it is only listed, and attached when the submenu is added to a menu.
    /// </summary>
    /// <param name="menuIndex">Index of the menu into which to insert the item (the menu must already exist)</param>
    /// <param name="itemIndex">
    ///     Index in that menu at which to insert the item, from 0 to the number of items it has
    /// </param>
    /// <param name="item">Item to insert. A recursive submenu is nested this way.</param>
    /// <param name="smooth">
    ///     Whether to do an animation to smoothly add the item. Ignored while the submenu is not in a menu. The item
    ///     is hidden and unselectable while it animates, then gets back the <c>Visible</c> and <c>Selectable</c> it
    ///     came with.
    /// </param>
    /// <param name="smoothAddDoneCb">
    ///     Optional callback called with the item after the smooth add animation completes. Not called when no
    ///     animation ran, when the item is removed while it animates, or after <see cref="Clear"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="menuIndex"/> is not the index of a menu, or <paramref name="itemIndex"/> is out of range
    /// </exception>
    protected void InsertItem(int menuIndex, int itemIndex, TextMenu.Item item, bool smooth,
                              Action<TextMenu.Item> smoothAddDoneCb) {
        if (Attached) {
            if (Focus >= FocusType.Body && menuIndex == this.MenuIndex && itemIndex <= Selection) {
                // Keep the same item selected. Not through MoveSelection: nothing moves, only the indices change.
                ++Selection;
            }
            menus[menuIndex].Menu.Insert(itemIndex, item);
            item.Container = Container;
            if (item is RecursiveSubMenuBase subSubMenu) {
                // Before it has a parent: what it still holds of a menu TextMenu.Remove took it out of goes back to
                // that menu, not to this one
                subSubMenu.Detach();
                subSubMenu.parent = this;
            }
            if (item is ISubMenuAwareItem subMenuAwareItem) {
                subMenuAwareItem.ContainingSubMenu = this;
            }
            Container.Add(item.ValueWiggler = Wiggler.Create(0.25f, 3f, null, false, false));
            Container.Add(item.SelectWiggler = Wiggler.Create(0.25f, 3f, null, false, false));
            item.ValueWiggler.UseRawDeltaTime  = true;
            item.SelectWiggler.UseRawDeltaTime = true;
            item.Added();
            if (smooth) {
                addRemoveItems.Add(new(item, menus[menuIndex].Menu, AnimationKind.Add, smoothAddDoneCb));
                item.Visible    = false;
                item.Selectable = false;
            }
            RecalculateSize();
        } else {
            // Only listed for now: Added attaches it. The smooth request is ignored.
            menus[menuIndex].Menu.Insert(itemIndex, item);
        }
    }

    /// <summary>
    ///     Insert an item into the last menu in the list; for a single-menu submenu, its only menu. Unlike
    ///     <see cref="AddItem(TextMenu.Item, bool, Action{TextMenu.Item})"/>, no argument has a default. If the
    ///     submenu has no menu, <see cref="AddImplicitMenu"/> creates one first.
    /// </summary>
    /// <param name="itemIndex">
    ///     Index in that menu at which to insert the item, from 0 to the number of items it has
    /// </param>
    /// <param name="item"><inheritdoc cref="InsertItem(int, int, TextMenu.Item, bool, Action{TextMenu.Item})"
    ///     path="/param[@name='item']/node()"/></param>
    /// <param name="smooth"><inheritdoc cref="InsertItem(int, int, TextMenu.Item, bool, Action{TextMenu.Item})"
    ///     path="/param[@name='smooth']/node()"/></param>
    /// <param name="smoothAddDoneCb">
    ///     <inheritdoc cref="InsertItem(int, int, TextMenu.Item, bool, Action{TextMenu.Item})"
    ///                 path="/param[@name='smoothAddDoneCb']/node()"/>
    /// </param>
    /// <returns>This submenu</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="itemIndex"/> is out of range</exception>
    public RecursiveSubMenuBase InsertItem(int itemIndex, TextMenu.Item item, bool smooth,
                                           Action<TextMenu.Item> smoothAddDoneCb) {
        InsertItem(LastMenuIndexForAdding(), itemIndex, item, smooth, smoothAddDoneCb);
        return this;
    }

    // Index of the last menu, creating one first if there are none (e.g. after Clear)
    private int LastMenuIndexForAdding() {
        if (menus.Count == 0) {
            AddImplicitMenu();
        }
        return menus.Count - 1;
    }

    /// <summary>
    /// Adds the menu that receives an item added while the submenu has no menus (for example after
    /// <see cref="Clear"/>): an unlabeled one. A subclass whose menus need more than a label overrides this.
    /// </summary>
    protected virtual void AddImplicitMenu() {
        AddMenu("", null);
    }

    /// <summary>
    ///     Add an item to the end of an existing menu of the submenu. While the submenu is in a menu the item is
    ///     attached at once; otherwise it is only listed, and attached when the submenu is added to a menu.
    /// </summary>
    /// <param name="menuIndex">Index of the menu to which to add the item (the menu must already exist)</param>
    /// <param name="item"><inheritdoc cref="InsertItem(int, int, TextMenu.Item, bool, Action{TextMenu.Item})"
    ///     path="/param[@name='item']/node()"/></param>
    /// <param name="smooth"><inheritdoc cref="InsertItem(int, int, TextMenu.Item, bool, Action{TextMenu.Item})"
    ///     path="/param[@name='smooth']/node()"/></param>
    /// <param name="smoothAddDoneCb">
    ///     <inheritdoc cref="InsertItem(int, int, TextMenu.Item, bool, Action{TextMenu.Item})"
    ///                 path="/param[@name='smoothAddDoneCb']/node()"/>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="menuIndex"/> is not the index of a menu
    /// </exception>
    protected void AddItem(int menuIndex, TextMenu.Item item, bool smooth, Action<TextMenu.Item> smoothAddDoneCb) {
        InsertItem(menuIndex, menus[menuIndex].Menu.Count, item, smooth, smoothAddDoneCb);
    }

    /// <summary>
    ///     Add an item to the end of the last menu in the list; for a single-menu submenu, its only menu. If the
    ///     submenu has no menu, <see cref="AddImplicitMenu"/> creates one first. While the submenu is in a menu the
    ///     item is attached at once; otherwise it is only listed, and attached when the submenu is added to a menu.
    /// </summary>
    /// <param name="item"><inheritdoc cref="InsertItem(int, int, TextMenu.Item, bool, Action{TextMenu.Item})"
    ///     path="/param[@name='item']/node()"/></param>
    /// <param name="smooth">
    ///     Whether to animate the add; see
    ///     <see cref="InsertItem(int, int, TextMenu.Item, bool, Action{TextMenu.Item})"/>. Default: false.
    /// </param>
    /// <param name="smoothAddDoneCb">
    ///     <inheritdoc cref="InsertItem(int, int, TextMenu.Item, bool, Action{TextMenu.Item})"
    ///                 path="/param[@name='smoothAddDoneCb']/node()"/>
    /// </param>
    /// <returns>This submenu</returns>
    public RecursiveSubMenuBase AddItem(TextMenu.Item item, bool smooth = false,
                                        Action<TextMenu.Item> smoothAddDoneCb = null) {
        AddItem(LastMenuIndexForAdding(), item, smooth, smoothAddDoneCb);
        return this;
    }

    // ============================================== Remove items =====================================================
    /// <summary>
    ///     Remove an item from a menu. Does nothing if that menu does not hold the item, or if a smooth removal of it
    ///     is already running. An item that is still animating in is removed with the <c>Visible</c> and
    ///     <c>Selectable</c> it came with. If the item has the selection, the selection first moves to a neighbouring
    ///     item, or out of the items if there is none.
    /// </summary>
    /// <param name="menuIndex">Index of the menu from which to remove the item</param>
    /// <param name="item">Item to remove</param>
    /// <param name="smooth">
    ///     Whether to do an animation to smoothly remove the item. Ignored while the submenu is not in a menu. The
    ///     item is hidden and unselectable while it animates, and leaves the submenu with the <c>Visible</c> and
    ///     <c>Selectable</c> it had before.
    /// </param>
    /// <param name="smoothRemoveDoneCb">
    ///     Optional callback called with the item after the smooth remove animation completes, once the item is
    ///     removed. Not called when no animation ran, or after <see cref="Clear"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="menuIndex"/> is not the index of a menu
    /// </exception>
    protected void RemoveItem(int menuIndex, TextMenu.Item item, bool smooth,
                              Action<TextMenu.Item> smoothRemoveDoneCb) {
        if (Attached) {
            int removeIndex = menus[menuIndex].Menu.IndexOf(item);
            bool beingRemoved = addRemoveItems.Exists((AddRemoveItemState addRemoveItem) =>
                                                          addRemoveItem.Item == item &&
                                                          addRemoveItem.Kind == AnimationKind.Remove);
            if (removeIndex < 0 || beingRemoved) {
                // Not in this menu, or already being removed
                return;
            }
            // Removing an item that is still animating in cancels that animation
            foreach (AddRemoveItemState cut in addRemoveItems.FindAll((AddRemoveItemState addRemoveItem) =>
                                                                          addRemoveItem.Item == item)) {
                addRemoveItems.Remove(cut);
                RestoreItem(cut);
            }
            if (menuIndex == this.MenuIndex && Focus >= FocusType.Body && removeIndex == Selection) {
                // Move off the item first, so that its OnLeave and the focus are handled, and so that the selection
                // is not left on an unselectable item during the remove animation
                MoveSelectionOffCurrent();
            }
            if (smooth) {
                // The item is removed when the animation is done: until then it holds its place for the spacer
                addRemoveItems.Add(new(item, menus[menuIndex].Menu, AnimationKind.Remove, smoothRemoveDoneCb));
                item.Visible    = false;
                item.Selectable = false;
                RecalculateSize();
                return;
            }
            if (menuIndex == this.MenuIndex && Focus >= FocusType.Body && removeIndex < Selection) {
                // Keep the same item selected. Not through MoveSelection: nothing moves, only the indices change.
                // This includes the item the selection just moved down to, off the one being removed.
                --Selection;
            }
            menus[menuIndex].Menu.Remove(item);
            DetachItem(item);
            RecalculateSize();
        } else {
            // No animation to play. The item is still attached if TextMenu.Remove took the submenu out after Added.
            if (menus[menuIndex].Menu.Remove(item)) {
                DetachItem(item);
            }
        }
    }

    /// <summary>
    /// Move the selection off the selected item before it disappears: up if an item above can be selected, else down,
    /// else out of the items, giving the focus back to what contains the submenu
    /// </summary>
    protected internal void MoveSelectionOffCurrent() {
        if (Focus == FocusType.Child) {
            // A nested submenu whose input is suspended gives no focus back when the selection leaves it
            Focus = FocusType.Body;
        }
        // Not by comparing with the first and last selectable rows: the selected row may be one that is not
        // selectable itself, a nested submenu left with nothing to select
        bool above = false, below = false;
        for (int i = 0; i < CurrentMenu.Count; ++i) {
            if (i != Selection && CanSelect(CurrentMenu[i])) {
                above |= i < Selection;
                below |= i > Selection;
            }
        }
        if (above) {
            MoveSelection(-1, false, true, out _);
        } else if (below) {
            MoveSelection(1, false, true, out _);
        } else {
            // It's the only selectable item, so there's nothing left to select in here
            ForceExit();
        }
    }

    // Leave the item as the animation ends or is cut short. The animation hid the item and made it unselectable for
    // its length, so an item that leaves the submenu gets back what it had before (Add and Remove), and a show or
    // hide ends at what it was going to.
    private static void RestoreItem(AddRemoveItemState addRemoveItem) {
        TextMenu.Item item = addRemoveItem.Item;
        item.Selectable = addRemoveItem.WasSelectable;
        item.Visible    = addRemoveItem.Kind switch {
            AnimationKind.Show => true,
            AnimationKind.Hide => false,
            _                  => addRemoveItem.WasVisible,
        };
    }

    // Apply the end state of an animation that has been taken out of addRemoveItems
    private void FinishAnimation(AddRemoveItemState addRemoveItem) {
        TextMenu.Item item = addRemoveItem.Item;
        if (addRemoveItem.Kind == AnimationKind.Remove) {
            int menuIndex = menus.FindIndex((LabeledMenu menu) => menu.Menu == addRemoveItem.Menu);
            if (menuIndex >= 0) {
                RemoveItem(menuIndex, item, false, null);
            }
        }
        RestoreItem(addRemoveItem);
        addRemoveItem.DoneCb?.Invoke(item);
    }

    // ============================================== Show / hide items ================================================
    /// <summary>
    ///     Show or hide an item of this submenu. A hidden item takes no space and can't be selected. Hiding the
    ///     selected item first moves the selection to a neighbouring item, or out of the items if there is none.
    /// </summary>
    /// <remarks>
    ///     While the submenu is not in a menu, or for an item it does not hold, this only sets the item's
    ///     <c>Visible</c>. An item that is being smoothly added or removed is left alone, and so is one already on
    ///     its way to what a smooth call asks: the call may be repeated every frame. A call that is not smooth ends
    ///     the animation at once.
    /// </remarks>
    /// <param name="item">The item to show or hide, in any of the submenu's menus</param>
    /// <param name="visible">Whether the item should be shown</param>
    /// <param name="smooth">
    ///     Whether to animate the item's height, like <see cref="AddItem(TextMenu.Item, bool, Action{TextMenu.Item})"/>
    ///     and <see cref="RemoveItem(TextMenu.Item, bool, Action{TextMenu.Item})"/> do. Default: false.
    /// </param>
    public void SetItemVisible(TextMenu.Item item, bool visible, bool smooth = false) {
        int menuIndex = menus.FindIndex((LabeledMenu menu) => menu.Menu.Contains(item));
        if (!Attached || menuIndex < 0) {
            // Not in a menu (or not ours): nothing to animate or move the selection off
            item.Visible = visible;
            return;
        }
        if (addRemoveItems.Exists((AddRemoveItemState addRemoveItem) =>
                                      addRemoveItem.Item == item &&
                                      addRemoveItem.Kind is AnimationKind.Add or AnimationKind.Remove)) {
            // Being added or removed, which decides its visibility
            return;
        }
        if (smooth && AnimationOf(item)?.Kind == (visible ? AnimationKind.Show : AnimationKind.Hide)) {
            // Already on its way there: a caller may say the same every frame
            return;
        }
        // Settle a show or hide that's still animating before starting over
        bool settled = false;
        foreach (AddRemoveItemState addRemoveItem in addRemoveItems.FindAll((AddRemoveItemState addRemoveItem) =>
                                                                                addRemoveItem.Item == item)) {
            addRemoveItems.Remove(addRemoveItem);
            FinishAnimation(addRemoveItem);
            settled = true;
        }
        if (item.Visible == visible) {
            if (settled) {
                // The height still counts the part of the animation that was cut short
                RecalculateSize();
            }
            return;
        }
        if (!visible && menuIndex == MenuIndex && Focus >= FocusType.Body && CurrentMenu.IndexOf(item) == Selection) {
            MoveSelectionOffCurrent();
        }
        if (smooth) {
            AnimationKind kind = visible ? AnimationKind.Show : AnimationKind.Hide;
            addRemoveItems.Add(new(item, menus[menuIndex].Menu, kind, null));
            item.Visible    = false;
            item.Selectable = false;
        } else {
            item.Visible = visible;
        }
        RecalculateSize();
    }

    // Undo what InsertItem did to attach the item. Nothing to undo for an item that is only listed.
    private void DetachItem(TextMenu.Item item) {
        // The menu its wigglers are in: not always this Container after TextMenu.Remove. Without one the item is only
        // listed, and its wiggler fields may name wigglers that Wiggler.Create has since given to another item.
        TextMenu itemMenu = item.Container;
        if (itemMenu == null) {
            return;
        }
        if (item is RecursiveSubMenuBase submenuItem) {
            // Before its Container is cleared, which it needs to settle its animations
            submenuItem.Detach();
            submenuItem.parent = null;
        }
        item.Container = null;
        if (item is ISubMenuAwareItem subMenuAwareItem && subMenuAwareItem.ContainingSubMenu == this) {
            subMenuAwareItem.ContainingSubMenu = null;
        }
        itemMenu.Remove(item.ValueWiggler);
        itemMenu.Remove(item.SelectWiggler);
    }

    // Undo Added: leave nothing of ours in the menu the items were attached to. They stay listed in menus, for the
    // next Added to attach again.
    private void Detach() {
        if (attachedTo == null) {
            return;
        }
        // TextMenu.Remove may have taken the submenu out while selected or focused, and told neither it nor that menu
        ReleaseFocus();
        DefaultOnLeave();
        // Settle the animations while RemoveItem still has a container to remove from
        List<AddRemoveItemState> settling = addRemoveItems;
        addRemoveItems = [];
        foreach (AddRemoveItemState addRemoveItem in settling) {
            FinishAnimation(addRemoveItem);
        }
        // A done callback may have started others, which will not run: their items get back what they hid
        foreach (AddRemoveItemState addRemoveItem in addRemoveItems) {
            RestoreItem(addRemoveItem);
        }
        addRemoveItems.Clear();
        foreach (LabeledMenu menu in menus) {
            foreach (TextMenu.Item item in menu.Menu) {
                DetachItem(item);
            }
        }
        // RemoveSelf and not Container.Remove: after TextMenu.Remove, Container is not the menu these two are in
        titleWiggler?.RemoveSelf();
        menuWiggler?.RemoveSelf();
        // Wiggler.Create reuses removed wiggler objects, so a kept reference could end up removing someone else's
        titleWiggler = null;
        menuWiggler  = null;
        pendingMenuSelection = MenuIndex;
        Selection            = -1;
        switchingMenus       = false;
        switchMenuEase       = 1f;
        attachedTo           = null;
    }

    /// <summary>
    ///     Remove an item from the menu that holds it; for a single-menu submenu, its only menu. Does nothing if
    ///     no menu holds the item, or if a smooth removal of the item is already running. If the item has the selection, the selection first moves to a neighbouring item, or out of the
    ///     items if there is none.
    /// </summary>
    /// <param name="item"><inheritdoc cref="RemoveItem(int, TextMenu.Item, bool, Action{TextMenu.Item})"
    ///     path="/param[@name='item']/node()"/></param>
    /// <param name="smooth">
    ///     Whether to animate the removal; see
    ///     <see cref="RemoveItem(int, TextMenu.Item, bool, Action{TextMenu.Item})"/>. Default: false.
    /// </param>
    /// <param name="smoothRemoveDoneCb"><inheritdoc cref="RemoveItem(int, TextMenu.Item, bool, Action{TextMenu.Item})"
    ///     path="/param[@name='smoothRemoveDoneCb']/node()"/></param>
    /// <returns>This submenu</returns>
    public RecursiveSubMenuBase RemoveItem(TextMenu.Item item, bool smooth = false,
                                           Action<TextMenu.Item> smoothRemoveDoneCb = null) {
        int menuIndex = menus.FindIndex((LabeledMenu menu) => menu.Menu.Contains(item));
        if (menuIndex >= 0) {
            RemoveItem(menuIndex, item, smooth, smoothRemoveDoneCb);
        }
        return this;
    }

    // ============================================ Move selection =====================================================
    /// <summary>
    /// Set the selection to the first possible <see cref="TextMenu.Item"/> in the currently selected list. Does
    /// nothing unless the selection is in the items (<see cref="Focus"/> is Body or Child): use
    /// <see cref="MoveSelectionTo"/> to bring it there.
    /// </summary>
    /// <param name="wiggle">Whether to wiggle the item being selected</param>
    public void FirstSelection(bool wiggle) {
        if (Focus < FocusType.Body) {
            return;
        }
        Selection = -1;
        if (CurrentMenu.Count > 0) {
            MoveSelection(1, false, wiggle, out _);
        }
    }

    /// <summary>
    /// Set the selection to the last possible <see cref="TextMenu.Item"/> in the currently selected list. Does
    /// nothing unless the selection is in the items (<see cref="Focus"/> is Body or Child).
    /// </summary>
    /// <param name="wiggle">Whether to wiggle the item being selected</param>
    public void LastSelection(bool wiggle) {
        if (Focus < FocusType.Body) {
            return;
        }
        // Past the end, where no row is: the move below must not take a row for the one being left
        Selection = CurrentMenu.Count;
        if (CurrentMenu.Count > 0) {
            MoveSelection(-1, false, wiggle, out _);
        }
        if (Selection >= CurrentMenu.Count) {
            // Nothing to select
            Selection = -1;
        }
    }

    /// <summary>
    ///     Returns true if a move in direction <paramref name="dir"/> would cause the menu selection to wrap
    ///     around, false otherwise.
    /// </summary>
    /// <param name="dir">Which direction to move in (down is positive)</param>
    /// <param name="allowExit">Whether a submenu with auto-exit is allowed to use it for this move</param>
    private bool MoveWouldWrap(int dir, bool allowExit) {
        if ((dir == 1 && Selection != LastPossibleSelection) ||
                (dir == -1 && Selection != FirstPossibleSelection)) {
            // Just moving inside this submenu without wrapping
            return false;
        }
        if (!(AutoExit && allowExit)) {
            // This submenu will wrap the move
            return true;
        }
        if (parent == null) {
            // The move would exit into the top-level menu, check if it would wrap
            return (dir == 1 && Container.IndexOf(this) == Container.LastPossibleSelection ||
                    dir == -1 && Container.IndexOf(this) == Container.FirstPossibleSelection);
        } else {
            // The move would exit into the parent submenu, check if it would wrap
            return parent.MoveWouldWrap(dir, allowExit);
        }
    }

    /// <summary>
    /// Move the selection to the next possible item in the currently selected list. Does nothing unless the selection
    /// is in the items (<see cref="Focus"/> is Body or Child).
    /// </summary>
    /// <param name="direction">The direction of movement (Down is positive)</param>
    /// <param name="allowExit">Whether a submenu with auto-exit is allowed to use it for this move</param>
    /// <param name="wiggle">Whether to wiggle the item being selected</param>
    /// <param name="playedScrollSFX">
    ///     Whether the container TextMenu played a scroll up/down SFX
    ///     (submenus themselves don't play the SFX here to avoid double-playing it,
    ///      but vanilla TextMenus do if this move ends up triggering a move in the container)
    /// </param>
    public void MoveSelection(int direction, bool allowExit, bool wiggle, out bool playedScrollSFX) {
        playedScrollSFX = false;
        if (Focus < FocusType.Body) {
            return;
        }
        int selection   = Selection;
        direction       = Math.Sign(direction);
        int count       = 0;
        foreach (TextMenu.Item item in CurrentMenu) {
            if (CanSelect(item)) {
                count++;
            }
        }
        do {
            Selection += direction;
            if (!(AutoExit && allowExit)) {
                if (count > 1) {
                    if (Selection < 0) {
                        Selection = CurrentMenu.Count - 1;
                    } else if (Selection >= CurrentMenu.Count) {
                        Selection = 0;
                    }
                } else if (Selection < 0 || Selection > CurrentMenu.Count - 1) {
                    Selection = Calc.Clamp(Selection, 0, CurrentMenu.Count - 1);
                    break;
                }
            }
        } while (Selection >= 0 && Selection < CurrentMenu.Count && !CanSelect(Current));

        if (Selection != selection) {
            if (Selection < 0 || Selection >= CurrentMenu.Count) {
                // Exiting into parent from auto-exit submenu
                Selection = selection;
                Exit(direction, wiggle, false, out playedScrollSFX, out _);
                return;
            }
            if (!CanSelect(Current)) {
                Selection = selection;
            }
            if (Selection != selection && Current != null) {
                if (selection >= 0 && selection < CurrentMenu.Count) {
                    LeaveItem(CurrentMenu[selection]);
                }
                // The OnLeave handler may have cleared the submenu or taken it out of its menu
                if (Current is not TextMenu.Item entered) {
                    return;
                }
                entered.OnEnter?.Invoke();
                if (wiggle) {
                    entered.SelectWiggler.Start();
                }
            }
        }
    }

    /// <summary>Move the item selection to a particular y position</summary>
    /// <param name="toY">
    ///     Y position to move to. This is in the reference frame used by <see cref="GetYOffsetOf"/>
    /// </param>
    /// <param name="dir">
    ///     Intended direction of motion, any "backwards" moves where toY and dir disagree will be ignored
    /// </param>
    /// <param name="allowExit">
    ///     Whether a submenu with auto-exit is allowed to use it this move. A move that leaves the submenu short of
    ///     toY goes on in what contains it.
    /// </param>
    private void MoveToY(float toY, int dir, bool allowExit) {
        float startY  = GetYOffsetOf(Current);
        float travelY = toY - startY;
        if (Math.Sign(travelY) != dir) {
            // Avoid infinite oscillations from multiple submenus trying to get to a point in between them
            return;
        }
        (float ToY, int Dir)? pageMoveBefore = pageMove;
        if (allowExit) {
            pageMove = (toY, dir);
        }
        try {
            // Each pass moves the selection one row or changes Focus once, so a move never needs more passes than
            // there are rows, plus one for the title and one to exit. The bound ends the states with nowhere to go
            // (Selection of -1 on a body with nothing selectable), where no pass changes anything.
            for (int passes = CurrentMenu.Count + 2;
                    passes > 0 && Container != null && Math.Abs(GetYOffsetOf(Current) - startY) < Math.Abs(travelY) &&
                        !MoveWouldWrap(dir, allowExit) && Focus != FocusType.None;
                    --passes) {
                if (Focus == FocusType.Title) {
                    if (dir > 0 && AutoEnter && CurrentMenu.Count > 0) {
                        Enter(dir, false);
                    } else {
                        Exit(dir, false, false, out _, out _);
                    }
                } else {
                    MoveSelection(dir, allowExit, false, out _);
                }
            }
            if (allowExit && Focus == FocusType.None && Container != null) {
                CarryPageOn(toY, dir);
            }
        } finally {
            pageMove = pageMoveBefore;
        }
    }

    // Where a page move that may leave its submenu is going, in the frame of GetYOffsetOf, and which way, while it is
    // made: an auto-enter submenu hovered on the way goes on to there (DefaultOnEnter)
    private static (float ToY, int Dir)? pageMove;

    // The rest of a page move that left this submenu short of toY, in what got the selection: as far as the paging of
    // TextMenu.Update goes, and never round the end of the menu. Nothing to do when a submenu hovered on the way took
    // the focus: it has carried the move on itself.
    private void CarryPageOn(float toY, int dir) {
        RecursiveSubMenuBase holder = parent;
        while (holder != null && holder.Focus == FocusType.None) {
            holder = holder.parent;
        }
        if (holder != null) {
            if (holder.Focus != FocusType.Child) {
                holder.MoveToY(toY, dir, true);
            }
            return;
        }
        while (Container.Focused && Math.Sign(toY - Container.GetYOffsetOf(Container.Current)) == dir &&
                   MoveMenuSelectionToward(Container, dir)) {
        }
    }

    // ======================================= Size / position calcs ===================================================
    /// <summary>
    /// Recalculate the necessary width to hold all items in any list, and
    /// the height to hold all items in the currently selected list
    /// </summary>
    public void RecalculateSize() {
        titleHeight      = (ShowTitle || ShowMenusSlider) ? ActiveFont.LineHeight: 0f;
        menuHeight       = 0f;
        // The icon is drawn iconXPadding to the right of the label (see Render)
        float titleWidth = ShowTitle ? ActiveFont.Measure(Label).X + (ShowIcon ? icon.Width + iconXPadding : 0f)
                                     : 0f;
        leftColumnWidth  = titleWidth;
        rightColumnWidth = 0f;
        optionsWidth     = 0f;
        compactWidth     = titleWidth;

        // Calculate width for the title and all submenus. An item that is only listed may need its Container to answer.
        foreach (LabeledMenu menu in attachedTo != null ? menus : []) {
            if (ShowMenusSlider) {
                float optionWidth = optionTextScale * ActiveFont.Measure(menu.Label).X + bracketsReservedWidth;
                rightColumnWidth  = Math.Max(rightColumnWidth, optionWidth);
                optionsWidth      = Math.Max(optionsWidth, optionWidth);
                compactWidth      = Math.Max(compactWidth, titleWidth + optionWidth);
            }
            foreach (TextMenu.Item item in menu.Menu) {
                if (item.Container != null && item.IncludeWidthInMeasurement) {
                    float leftWidth  = ItemIndent + item.LeftWidth();
                    float rightWidth = item.RightWidth();
                    leftColumnWidth  = Math.Max(leftColumnWidth, leftWidth);
                    rightColumnWidth = Math.Max(rightColumnWidth, rightWidth);
                    compactWidth     = Math.Max(compactWidth, leftWidth + rightWidth);
                }
            }
        }
        // Calculate height for the current menu
        bool first = true;
        if (ShowMenu) {
            foreach (TextMenu.Item item in CurrentMenu) {
                if (item.Container != null && item.Visible) {
                    menuHeight += item.Height();
                    if (!first || ShowTitle || ShowMenusSlider) {
                        // Add space between items, and between the title and the first item if the title is shown
                        menuHeight += ItemSpacing;
                    }
                    first = false;
                }
            }
            foreach (AddRemoveItemState addRemoveItem in addRemoveItems) {
                if (addRemoveItem.Menu == CurrentMenu) {
                    menuHeight += addRemoveItem.EasedHeight((!first || ShowTitle || ShowMenusSlider) ? ItemSpacing
                                                                                                     : 0f);
                    first = false;
                }
            }
        }
        // Allow subclass to modify the results of the size calculation
        TweakSizeCalc(ref titleHeight, ref menuHeight, titleWidth, ref leftColumnWidth, ref rightColumnWidth,
                      ref optionsWidth, ref compactWidth);
        // A submenu that shows nothing takes a negative height, which cancels the spacing its row gets. Visible
        // would do it too, but then height transitions aren't smooth.
        if (titleHeight == 0f && menuHeight == 0f) {
            titleHeight = (parent != null) ? -parent.ItemSpacing
                                           : (Container != null) ? -Container.ItemSpacing : 0f;
        }
        parent?.RecalculateSize();
    }

    /// <summary>
    /// Hook to allow subclasses to modify the results of <see cref="RecalculateSize"/>
    /// (for example, to reserve additional space for displaying things the base class doesn't know about)
    /// </summary>
    /// <param name="titleHeight">Height of the title row</param>
    /// <param name="menuHeight">Height of the items of the menu shown, spacing included</param>
    /// <param name="titleWidth">Width of the title and its icon</param>
    /// <param name="leftColumnWidth">Widest left part among the title and the items of every menu</param>
    /// <param name="rightColumnWidth">Widest right part among the items of every menu and the menu slider</param>
    /// <param name="optionsWidth">Width reserved for the menu slider</param>
    /// <param name="compactWidth">Width of the widest row taken as a whole, which compact mode reserves</param>
    protected virtual void TweakSizeCalc(ref float titleHeight, ref float menuHeight, float titleWidth,
                                         ref float leftColumnWidth, ref float rightColumnWidth, ref float optionsWidth,
                                         ref float compactWidth) {}

    /// <summary>
    /// The width the submenu asks for in the left column of the menu: in compact mode, what its widest row needs
    /// beyond <see cref="CompactRightWidth"/>
    /// </summary>
    public override float LeftWidth() {
        return CompactMode ? compactWidth - CompactRightWidth : leftColumnWidth;
    }

    /// <summary>
    /// The width the submenu asks for in the right column of the menu: in compact mode,
    /// <see cref="CompactRightWidth"/>
    /// </summary>
    public override float RightWidth() {
        return CompactMode ? CompactRightWidth : rightColumnWidth;
    }

    /// <summary>
    /// The height of the title and of the items shown, following the animation while a menu switch is running
    /// </summary>
    public override float Height() {
        return titleHeight + EasedMenuHeight;
    }

    /// <summary>
    ///     Get the Y position of a <see cref="TextMenu.Item"/> relative to the menu position. The submenu must be in
    ///     a menu.
    /// </summary>
    /// <param name="item">
    ///     An item contained in the currently selected list, or null to get the offset of the title
    /// </param>
    /// <returns>
    ///     The offset of the vertical center of the item, in the frame of <see cref="TextMenu.GetYOffsetOf"/>
    /// </returns>
    public float GetYOffsetOf(TextMenu.Item item) {
        float offset = (parent != null ? parent.GetYOffsetOf(this) : Container.GetYOffsetOf(this)) -
                           Height() * 0.5f;
        if (item == null) {
            // The offset of the title itself
            return offset + titleHeight * 0.5f;
        }
        offset += titleHeight;
        if (ShowTitle || ShowMenusSlider) {
            // The space Render leaves between the title and the first item
            offset += ItemSpacing;
        }
        foreach (TextMenu.Item child in CurrentMenu) {
            if (child.Visible) {
                offset += child.Height() + ItemSpacing;
            }
            if (child == item) {
                break;
            }
        }
        return offset - item.Height() * 0.5f - ItemSpacing;
    }

    // ============================================= Handle hover ======================================================
    /// <summary>
    ///     Like <see cref="TextMenu.Item.Enter"/>, but runs <paramref name="onEnter"/> after
    ///     <see cref="DefaultOnEnter"/> instead of replacing it
    /// </summary>
    /// <param name="onEnter">Called when the submenu becomes the selected row; null for none</param>
    /// <returns>This submenu</returns>
    /// <remarks>
    ///     Only takes effect when called on a submenu-typed reference. Called through a <see cref="TextMenu.Item"/>
    ///     reference, the base method replaces <see cref="TextMenu.Item.OnEnter"/> and breaks the submenu.
    /// </remarks>
    public new RecursiveSubMenuBase Enter(Action onEnter) {
        OnEnter = () => {
            DefaultOnEnter();
            onEnter?.Invoke();
        };
        return this;
    }

    /// <summary>
    ///     Like <see cref="TextMenu.Item.Leave"/>, but runs <paramref name="onLeave"/> before
    ///     <see cref="DefaultOnLeave"/> instead of replacing it
    /// </summary>
    /// <param name="onLeave">Called when the selection leaves the submenu's row; null for none</param>
    /// <returns>This submenu</returns>
    /// <remarks>
    ///     Only takes effect when called on a submenu-typed reference. Called through a <see cref="TextMenu.Item"/>
    ///     reference, the base method replaces <see cref="TextMenu.Item.OnLeave"/> and breaks the submenu.
    /// </remarks>
    public new RecursiveSubMenuBase Leave(Action onLeave) {
        OnLeave = () => {
            onLeave?.Invoke();
            DefaultOnLeave();
        };
        return this;
    }

    /// <summary>
    ///     Tasks that must be done from <see cref="TextMenu.Item.OnEnter"/> for the submenu implementation
    ///     to function properly
    /// </summary>
    /// <remarks>
    ///     If you assign an action to <see cref="TextMenu.Item.OnEnter"/>, you must call this method
    ///     from that action for submenus to continue working properly.
    ///     Code that writes <see cref="TextMenu.Selection"/> directly calls no <c>OnEnter</c>: the submenu then
    ///     calls this method itself on its next update, and not the assigned action.
    /// </remarks>
    // Set while the submenu hovers a row itself: the direction DefaultOnEnter then uses in place of the pressed keys.
    // Static, to reach the submenus nested under the one that sets it.
    private static int? hoverDirection;
    // An auto-enter submenu hovered while its menu was unfocused: it enters on its first update with the menu focused
    private bool enterWhenFocused;

    private void HoverFrom(int direction) {
        int? hoverDirectionBefore = hoverDirection;
        hoverDirection = direction;
        try {
            DefaultOnEnter();
        } finally {
            hoverDirection = hoverDirectionBefore;
        }
    }

    public void DefaultOnEnter() {
        if (!Attached) {
            // Vanilla calls OnEnter before Added (but after setting this.Container, hence the separate check) on
            // the first selectable item of a menu. That call is ignored: a TextMenuPage calls OnEnter again when it
            // is entered. On a move from another menu the items are still attached to that one.
            return;
        }
        // Everest sometimes calls OnEnter several times in a row, so this must be idempotent
        if (!receivedHover) {
            // Take the autoscroll over from what contains the submenu
            if (parent == null) {
                AutoScroll           = Container.AutoScroll;
                Container.AutoScroll = false;
            } else {
                AutoScroll        = parent.AutoScroll;
                parent.AutoScroll = false;
            }
            receivedHover = true;
            // A hover no press caused has no paging to continue, and the direction its caller gives
            bool paging   = hoverDirection == null &&
                            (pageMove != null ||
                             CoreModule.Settings.MenuPageDown.Pressed || CoreModule.Settings.MenuPageUp.Pressed);
            if (AutoEnter && parent == null && !Container.Focused) {
                // The search of Everest's Mod Options hovers its match while its text box has the focus, and a
                // handler can move the selection here under a page. Entering now would handle keys meant for those:
                // Update enters once the menu is focused.
                enterWhenFocused = true;
            } else if (AutoEnter) {
                // A move of the library's own may go against a key that is held
                int direction = hoverDirection ?? moveDirection ?? pageMove?.Dir ??
                                    (Input.MenuDown.Pressed || CoreModule.Settings.MenuPageDown.Pressed
                                         ? 1
                                         : Input.MenuUp.Pressed || CoreModule.Settings.MenuPageUp.Pressed
                                             ? -1
                                             : 0);
                // The way the move that hovered this row is going: the library's own, or else the pressed key's
                int moving = moveDirection ?? (hoverDirection == null ? direction : 0);
                if (moving != 0 && !paging && PassOver(moving)) {
                    return;
                }
                // Only AutoEnter submenus take the focus on hover: keeping it with the parent where possible helps
                // page up and page down
                Enter(direction, !paging);
                // Make page up / page down reasonably continuous when auto-entering submenus
                // Using the current view position as an (imperfect) estimate for the starting page position.
                // That position is in a different reference frame compared to the one used by MoveToY, so this math
                // does the conversion
                float offsetSpaceContainerY = (Engine.Height / 2) + Container.Height * Container.Justify.Y -
                                                  Container.Position.Y;
                if (paging && pageMove is (float toY, int dir) && !NothingToSelect) {
                    // A page move that came out of another submenu knows where it is going, and may leave this one
                    // too. Not one with nothing to select: at the end of the menu, leaving it would go round.
                    MoveToY(toY, dir, true);
                } else if (paging) {
                    MoveToY(offsetSpaceContainerY + (CoreModule.Settings.MenuPageDown.Pressed ? 1080f : -1080f),
                            CoreModule.Settings.MenuPageDown.Pressed ? 1 : -1, false);
                }
                Input.MenuDown.ConsumePress();
                Input.MenuUp.ConsumePress();
            } else if (!paging) {
                titleWiggler.Start();
            }
        }
    }

    // How many submenus with nothing to select are passing a move on to the next row, one inside the other
    private static int passingOver;

    // The direction of a move of the menu's selection that the library makes itself, while it makes it. A row hovered
    // by it must not take the direction from the keys: the move may go against a key that is held.
    private static int? moveDirection;

    /// <summary>
    /// Moves the selection of a menu, as <see cref="TextMenu.MoveSelection"/> does, telling the submenus hovered on
    /// the way which way the move goes
    /// </summary>
    /// <param name="menu">The menu whose selection moves</param>
    /// <param name="direction">Negative for up, positive for down</param>
    /// <param name="wiggle">Whether to play the scroll sound and wiggle the row that gets the selection</param>
    protected static void MoveMenuSelection(TextMenu menu, int direction, bool wiggle) {
        int? moveDirectionBefore = moveDirection;
        moveDirection = Math.Sign(direction);
        try {
            menu.MoveSelection(direction, wiggle);
        } finally {
            moveDirection = moveDirectionBefore;
        }
    }

    /// <summary>
    /// Moves the selection of a menu off its selected row before that row goes: to the closest row above that can
    /// hold it, else to the closest below. Never round the end of the menu.
    /// </summary>
    /// <param name="menu">The menu whose selection moves</param>
    /// <returns>Whether a row took the selection. If not, it stays where it was, and no handler is called.</returns>
    protected static bool MoveMenuSelectionOffCurrent(TextMenu menu) {
        return MoveMenuSelectionToward(menu, -1) || MoveMenuSelectionToward(menu, 1);
    }

    // Moves the selection of a menu to the closest row that can hold it on one side of the selected row. Not
    // TextMenu.MoveSelection: it stops on a submenu that has nothing to select, and goes round the end of the menu.
    private static bool MoveMenuSelectionToward(TextMenu menu, int direction) {
        int from = menu.Selection, target = -1;
        for (int i = Calc.Clamp(from, -1, menu.Items.Count) + direction; i >= 0 && i < menu.Items.Count; i += direction) {
            if (CanSelect(menu.Items[i])) {
                target = i;
                break;
            }
        }
        if (target < 0) {
            return false;
        }
        TextMenu.Item left       = from >= 0 && from < menu.Items.Count ? menu.Items[from] : null;
        int? moveDirectionBefore = moveDirection;
        moveDirection = direction;
        try {
            // As TextMenu.MoveSelection does
            menu.Selection = target;
            left?.OnLeave?.Invoke();
            if (menu.Selection >= 0 && menu.Selection < menu.Items.Count) {
                menu.Current.OnEnter?.Invoke();
            }
        } finally {
            moveDirection = moveDirectionBefore;
        }
        return true;
    }

    // A menu looks for its next row by itself and stops on a submenu that has nothing to select, which would take
    // the focus and show nothing selected: the move that hovered it goes on to the next row instead. A submenu that
    // holds such a row leaves it out of its own search (CanSelect). Returns false, with this submenu still hovered,
    // when it has something to select or the move has nowhere else to go.
    private bool PassOver(int direction) {
        // A menu of nothing but such submenus would pass the move round for ever
        if (parent != null || !NothingToSelect || !Container.Focused || passingOver >= Container.Items.Count) {
            return false;
        }
        ++passingOver;
        try {
            MoveMenuSelection(Container, direction, false);
            // Nowhere to go on, in a menu too short to go round: back to the closest row the move came past
            return Container.Current != this || MoveMenuSelectionToward(Container, -direction);
        } finally {
            --passingOver;
        }
    }

    /// <summary>
    ///     Tasks that must be done from <see cref="TextMenu.Item.OnLeave"/> for the submenu implementation
    ///     to function properly
    /// </summary>
    /// <remarks>
    ///     If you assign an action to <see cref="TextMenu.Item.OnLeave"/>, you must call this method
    ///     from that action for autoscrolling to continue working properly.
    ///     Code that writes <see cref="TextMenu.Selection"/> or <see cref="TextMenu.Current"/> directly calls no
    ///     OnLeave. A submenu placed directly in the TextMenu notices on its next update and calls this method
    ///     itself; an action assigned to <see cref="TextMenu.Item.OnLeave"/> is not called in that case.
    /// </remarks>
    public void DefaultOnLeave() {
        // Everest sometimes calls OnLeave several times in a row, so this must be idempotent
        if (receivedHover) {
            // Exit if that has not happened yet
            // (this happens during page up / page down for auto-exit submenus)
            if (Focus != FocusType.None) {
                ForceExit();
            }
            // Return the autoscroll to the menu it was taken from, which TextMenu.Remove may have taken the submenu
            // out of since
            bool autoScrollOwed = autoScrollToReturn ?? AutoScroll;
            if (parent != null) {
                parent.AutoScroll = autoScrollOwed;
            } else if (attachedTo != null) {
                attachedTo.AutoScroll = autoScrollOwed;
            }
            AutoScroll         = false;
            autoScrollToReturn = null;
            receivedHover      = false;
            ++leaveCount;
        }
    }

    // ============================================= Enter / exit ======================================================
    /// <summary>
    /// Take the focus from what contains the submenu and put the selection on the title or in the items. Called
    /// with <see cref="Focus"/> at <see cref="FocusType.Title"/>, it moves the selection into the items. An override
    /// must call the base method.
    /// </summary>
    /// <param name="dir">
    ///     Where the selection comes from: positive when moving down or on Confirm, to start at the top; negative
    ///     when moving up, to start at the bottom; 0 when there is no direction, which starts at the top
    /// </param>
    /// <param name="wiggle">Whether to wiggle the row that gets the selection</param>
    protected virtual void Enter(int dir, bool wiggle) {
        if (parent == null) {
            Container.Focused = false;
        } else {
            parent.Focus = FocusType.Child;
        }
        if (Focus == FocusType.None) {
            // Not selected at all: decide between the title and the body
            if (dir >= 0) {
                // Moving down or entering with confirm, go to the top (title or top of body)
                if (TitleSelectable && AutoEnter) {
                    // An AutoEnter submenu takes the focus on hover: the selection starts on the title when the
                    // title can hold it
                    Focus = FocusType.Title;
                    if (wiggle) {
                        titleWiggler.Start();
                    }
                } else {
                    // Without AutoEnter the submenu is entered by Confirm, which goes to the body. With AutoEnter
                    // and a title that can't be selected, the body is the only place to go.
                    Focus = FocusType.Body;
                    FirstSelection(wiggle);
                }
            } else {
                // Moving up, go to the bottom (bottom of body or title)
                if (AutoEnter && CurrentMenu.Count > 0) {
                    // Go to the bottom of the body when it can be entered
                    Focus = FocusType.Body;
                    LastSelection(wiggle);
                } else {
                    // The body can't be entered: go to the title. This assumes TitleSelectable is true here.
                    Focus = FocusType.Title;
                    if (wiggle) {
                        titleWiggler.Start();
                    }
                }
            }
        } else if (Focus == FocusType.Title) {
            // The title was selected: go to the body
            Focus = FocusType.Body;
            FirstSelection(wiggle);
        }
        // Shouldn't get called with FocusType >= Body
    }

    /// <summary>
    /// Move the selection out of the items or off the title: to the title, or out of the submenu to what contains
    /// it. An override must call the base method.
    /// </summary>
    /// <param name="dir">
    ///     Positive when scrolling down past the last item: the selection moves on in what contains the submenu.
    ///     Negative when scrolling up past the first item: it goes to the title if the submenu keeps the focus there
    ///     (<see cref="TitleSelectable"/> and <see cref="AutoEnter"/>), else moves on upward. 0 for Cancel, ESC or
    ///     Pause: it goes to the title, or with <see cref="RecursiveExit"/>, or from the title, out of the submenu,
    ///     and the press is passed on to what contains it.
    /// </param>
    /// <param name="wiggle">Whether to wiggle the row that gets the selection</param>
    /// <param name="exitAll">
    ///     Whether to leave every submenu up to the <see cref="TextMenu"/> and pass the press on to it, as ESC and
    ///     Pause do
    /// </param>
    /// <param name="playedScrollSFX">Whether what contains the submenu played the scroll sound for the move</param>
    /// <param name="playedBackSFX">
    ///     Whether the press was passed on to the <see cref="TextMenu"/>, whose handler is expected to play the back
    ///     sound
    /// </param>
    protected virtual void Exit(int dir, bool wiggle, bool exitAll, out bool playedScrollSFX, out bool playedBackSFX) {
        playedBackSFX         = false;
        playedScrollSFX       = false;
        bool shouldExitParent = exitAll;
        FocusType focusBefore = Focus;
        LeaveItem(Current);
        if (Focus == FocusType.None && focusBefore != FocusType.None) {
            // The OnLeave handler cleared the submenu or took it out of its menu, which gave the focus back. Not any
            // change of Focus: a nested submenu that leaves sets this one to Body, and the exit goes on.
            return;
        }

        // First decide how far to exit
        if (Focus == FocusType.Body) {
            // Body was selected, need to decide whether to move to the title or outside the submenu entirely
            if (dir > 0) {
                // Moving down, exit entirely and move in the parent
                Focus = FocusType.None;
            } else if (dir < 0) {
                // Moving up, either move to the title or skip it and move in the parent
                if (TitleSelectable) {
                    Focus = FocusType.Title;
                } else {
                    Focus = FocusType.None;
                }
            } else {
                // Exiting with Cancel, exit to title or recursively exit
                if (!RecursiveExit) {
                    // Note: forcing title selection even if !TitleSelectable here
                    // Thus menus without a title to select should always set RecursiveExit
                    Focus = FocusType.Title;
                } else {
                    Focus            = FocusType.None;
                    shouldExitParent = true;
                }
            }
        } else if (Focus == FocusType.Title) {
            // Title was selected, definitely exit entirely
            Focus = FocusType.None;
            if (dir == 0) {
                // Act as if the parent had the focus: exit the parent as well
                shouldExitParent = true;
            }
        }

        // The destination is chosen: finish the exit
        if (Focus == FocusType.Title) {
            if (wiggle) {
                titleWiggler.Start();
            }
            if (!AutoEnter) {
                // Exit all the way unless the title keeps the focus
                Focus = FocusType.None;
            }
        }
        if (Focus == FocusType.None) {
            bool stuck = false;
            if (parent == null) {
                Container.Focused = !InputSuspended(Container);
                if (dir != 0) {
                    MoveMenuSelection(Container, dir, wiggle);
                    playedScrollSFX = true;
                    // Not when the move came back here by itself, past a row with nothing to select: that hover
                    // has entered the submenu already
                    stuck           = Container.Current == this && Focus == FocusType.None;
                }
            } else {
                parent.Focus = FocusType.Body;
                if (dir != 0) {
                    parent.MoveSelection(dir, true, wiggle, out playedScrollSFX);
                    stuck = parent.Focus == FocusType.Body && parent.Current == this;
                }
            }
            if (stuck && AutoEnter) {
                // The move went nowhere: TextMenu.MoveSelection wraps only with more than two selectable rows. The
                // selection would stay on this row with nothing highlighted, so go back in at the end it left from.
                // The press is still there, and a nested submenu hovered on the way must not read it as a move in.
                int? hoverDirectionBefore = hoverDirection;
                hoverDirection = -dir;
                try {
                    Enter(-dir, false);
                } finally {
                    hoverDirection = hoverDirectionBefore;
                }
                playedScrollSFX = true;
            }
        }
        if (shouldExitParent) {
            if (parent == null) {
                // Assuming the container will play the back button SFX
                playedBackSFX = true;
                if (Input.MenuCancel.Pressed) {
                    Container.OnCancel?.Invoke();
                } else if (Input.ESC.Pressed) {
                    Container.OnESC?.Invoke();
                } else if (Input.Pause.Pressed) {
                    Container.OnPause?.Invoke();
                }
                // The main-menu version of the mod options menu doesn't use OnCancel to exit: OuiModOptions checks
                // the button itself, so its handling is repeated here
                if (InOverworldMenu) {
                    OuiModOptions.Instance.Overworld.Goto<OuiMainMenu>();
                    // Container doesn't play the back button SFX in this case
                    playedBackSFX = false;
                }
            } else {
                parent.Exit(0, wiggle, exitAll, out _, out playedBackSFX);
            }
        }
    }

    /// <summary>
    /// Give the focus back to what contains the submenu at once: no move to the title, no press passed on. The
    /// selected item gets its <c>OnLeave</c>. An override must call the base method.
    /// </summary>
    protected virtual void ForceExit() {
        LeaveItem(Current);
        Focus = FocusType.None;
        // The focus goes back to the menu it was taken from, which TextMenu.Remove may have taken the submenu out of
        // since
        if (parent != null) {
            parent.Focus = FocusType.Body;
        } else if (attachedTo != null && !InputSuspended(attachedTo)) {
            // Under a page the menu stays unfocused: the restore action of SuspendInput focuses it
            attachedTo.Focused = true;
        }
    }

    // Calls an item's OnLeave, unless one is already running in this submenu
    private void LeaveItem(TextMenu.Item item) {
        if (leavingItem) {
            return;
        }
        leavingItem = true;
        try {
            item?.OnLeave?.Invoke();
        } finally {
            leavingItem = false;
        }
    }

    // While a page or the room chooser has a menu's input, no submenu in it handles input or gives it the focus: a
    // handler can remove, hide or clear a submenu there, which moves the selection and the focus under the page
    private static bool InputSuspended(TextMenu menu) {
        return MenuInputBlock.IsBlocked(menu);
    }

    // =============================== Give up input to a page or overlay opened on top ================================
    /// <summary>
    ///     The innermost submenu that handles the input, this one or one nested in it. Null when no submenu in
    ///     the chain handles input: <see cref="Focus"/> is None here, or the nested holder was suspended.
    /// </summary>
    public IInputHoldingItem InputHolder {
        get {
            RecursiveSubMenuBase subMenu = this;
            while (subMenu is { Focus: FocusType.Child }) {
                subMenu = subMenu.Current as RecursiveSubMenuBase;
            }
            return (subMenu != null && subMenu.Focus != FocusType.None) ? subMenu : null;
        }
    }

    /// <summary>
    /// Takes the focus away from this submenu until the returned action is called, which restores it. Until then no
    /// recursive submenu of the same <see cref="TextMenu"/> handles a button, and none gives that menu the focus: a
    /// handler may move the selection there, and the row it lands on acts once the action is called. The action must
    /// be called: a menu whose action is dropped keeps its submenus blocked (<see cref="MenuInputBlock"/>).
    /// </summary>
    /// <param name="stopAutoScroll">Also turn off <see cref="AutoScroll"/> in the meantime</param>
    /// <returns>
    ///     The action that gives the submenu back the focus and the scrolling it had; calling it again does nothing.
    ///     If the submenu stopped being
    ///     the selected row in the meantime (it was removed, hidden or collapsed, or the selection moved), the
    ///     action leaves it alone, and so it does if <see cref="TextMenu.Remove(TextMenu.Item)"/> took it out, after
    ///     giving back what that left with it: the menu gets the focus unless its selected row handles the input, and
    ///     the scrolling goes to what has the selection, a hovered submenu, the submenu around this one or the menu.
    /// </returns>
    public Action SuspendInput(bool stopAutoScroll) {
        FocusType savedFocus        = Focus;
        TextMenu.Item savedItem     = Current;
        List<TextMenu.Item> savedIn = CurrentMenu;
        bool savedAutoScroll        = AutoScroll;
        bool? savedToReturn         = autoScrollToReturn;
        bool autoScrollOwed         = autoScrollToReturn ?? AutoScroll;
        TextMenu menu               = attachedTo;
        RecursiveSubMenuBase holder = parent;
        int leaves                  = leaveCount;
        int moves                   = selectionMoves;
        Action releaseMenu          = MenuInputBlock.Block(menu);
        Focus                       = FocusType.None;
        ++suspensions;
        if (stopAutoScroll) {
            if (receivedHover) {
                // A leave while suspended must give back what the submenu took, not the false set here
                autoScrollToReturn = autoScrollOwed;
            }
            AutoScroll = false;
        }
        bool restored = false;
        return () => {
            if (restored) {
                return;
            }
            restored = true;
            releaseMenu();
            --suspensions;
            // TextMenu.Remove tells the row it takes out nothing: let go here of what it still holds in the menu
            RecursiveSubMenuBase root = this;
            while (root.parent != null) {
                root = root.parent;
            }
            bool takenOut = menu != null && root.attachedTo == menu && root.Container != menu;
            if (takenOut) {
                root.Detach();
                if (MenuInputBlock.HolderIn(menu) != null) {
                    // Detach focused the menu, and a handler has since given the input to another row
                    menu.Focused = false;
                }
            }
            if (takenOut || leaveCount != leaves) {
                // Left its row while suspended: the caller restored a menu that this submenu no longer holds
                GiveBackSuspended(menu, holder, stopAutoScroll, autoScrollOwed);
                return;
            }
            if (selectionMoves != moves) {
                // A handler put the selection here with MoveSelectionTo meanwhile: the focus and the row it chose
                // stand. The menu gets the focus if that left the input outside.
                if (menu != null && MenuInputBlock.HolderIn(menu) == null && !InputSuspended(menu)) {
                    menu.Focused = true;
                }
                RecalculateSize();
                return;
            }
            Focus = savedFocus;
            if (stopAutoScroll) {
                AutoScroll         = savedAutoScroll;
                autoScrollToReturn = savedToReturn;
            }
            if (savedItem != null) {
                ReselectAfterSuspension(savedItem, savedIn);
            }
            // After the reselect, which puts Selection back on its row. A nested submenu whose own suspension is not
            // over has no focus and still holds the input.
            if (Focus == FocusType.Child &&
                    !(Current is RecursiveSubMenuBase child && (child.Focus != FocusType.None || child.suspensions > 0))) {
                // The nested submenu that had the input left or gave it back meanwhile: this one handles it
                Focus = FocusType.Body;
            }
            // A size calculated while Focus was None left out a body that is only shown with the focus
            RecalculateSize();
        };
    }

    // While the input was suspended, Focus was None, and nothing kept Selection on its row: a handler may have
    // inserted, removed or hidden rows, or switched menus. Puts the selection back on the row that had it. If that
    // row is gone or can't be selected any more, it gets the OnLeave it never had, and the selection goes to the
    // closest selectable row, above first, or out of the items.
    private void ReselectAfterSuspension(TextMenu.Item item, List<TextMenu.Item> itemMenu) {
        int index = CurrentMenu.IndexOf(item);
        if (index >= 0 && CanSelect(item)) {
            Selection = index;
            return;
        }
        // Where the row was, or would be now, for the search of a neighbour
        int from = index >= 0 ? index : (itemMenu == CurrentMenu ? Calc.Clamp(Selection, 0, CurrentMenu.Count) : 0);
        int neighbour = -1;
        for (int i = Math.Min(from, CurrentMenu.Count) - 1; i >= 0 && neighbour < 0; --i) {
            if (CanSelect(CurrentMenu[i])) {
                neighbour = i;
            }
        }
        for (int i = from; i < CurrentMenu.Count && neighbour < 0; ++i) {
            if (CanSelect(CurrentMenu[i])) {
                neighbour = i;
            }
        }
        Selection = -1;
        if (Focus == FocusType.Child) {
            // The row was the nested submenu that had the input, and it is gone: this one handles it now
            Focus = FocusType.Body;
        }
        LeaveItem(item);
        if (Focus < FocusType.Body) {
            // The OnLeave handler took the focus away
            return;
        }
        if (neighbour >= 0 && neighbour < CurrentMenu.Count && CanSelect(CurrentMenu[neighbour])) {
            Selection = neighbour;
            Current.OnEnter?.Invoke();
        } else {
            ForceExit();
        }
    }

    // The focus and the scrolling of a submenu that left its row while suspended go to what has the selection now.
    // A hovered submenu there holds them already and is left alone.
    private static void GiveBackSuspended(TextMenu menu, RecursiveSubMenuBase holder, bool stopAutoScroll,
                                          bool autoScrollOwed) {
        if (menu == null) {
            return;
        }
        // Read first: it puts back a selection that TextMenu.Remove left past the last row
        if (MenuInputBlock.HolderIn(menu) == null && !InputSuspended(menu)) {
            menu.Focused = true;
        }
        if (!stopAutoScroll) {
            // The leave gave the scrolling back, and nothing took it away since
            return;
        }
        if (holder != null && holder.receivedHover) {
            if (holder.Current is not RecursiveSubMenuBase { receivedHover: true }) {
                holder.AutoScroll = autoScrollOwed;
            }
        } else if (menu.Current is not RecursiveSubMenuBase { receivedHover: true }) {
            menu.AutoScroll = autoScrollOwed;
        }
    }

    // ================================= Force enter from somewhere else in the menu ===================================
    /// <summary>
    ///     Moves focus and selection to this submenu (the title or an item within it), recursively setting
    ///     parent selections and focus to maintain a valid state
    /// </summary>
    /// <param name="item">
    ///     The item within this submenu to move the selection to, or null to move the selection to the submenu title.
    ///     If the item is in a menu other than the one shown, the submenu switches to that menu. If it is not in this
    ///     submenu at all, or the submenu is not in a menu, nothing happens. The item is not checked: one the player
    ///     could not select (hidden, disabled, not selectable) is selected all the same.
    /// </param>
    /// <param name="autoScroll">Whether this submenu should autoscroll after taking focus</param>
    /// <param name="snapScroll">
    ///     Whether this submenu should instantly snap the scroll position to the new value rather than waiting for
    ///     the smooth autoscroll to apply (<paramref name="autoScroll"/> must be true for this to apply)
    /// </param>
    public void MoveSelectionTo(TextMenu.Item item, bool autoScroll, bool snapScroll) {
        if (!Attached) {
            // No item to select yet, and no menu to take the selection in
            return;
        }
        int itemMenuIndex = -1;
        if (item != null) {
            itemMenuIndex = menus.FindIndex((LabeledMenu menu) => menu.Menu.Contains(item));
            if (itemMenuIndex < 0) {
                // Not an item of this submenu
                return;
            }
        }
        // Take the selection away from wherever it is now, like the TextMenu does when its selection moves
        RecursiveSubMenuBase topSubMenu = this;
        // The submenus on the way whose items are drawn now: releasing the focus may start to collapse them
        List<RecursiveSubMenuBase> shown = [];
        for (RecursiveSubMenuBase subMenu = this; subMenu != null; subMenu = subMenu.parent) {
            topSubMenu = subMenu;
            if (subMenu.ShowMenu && !subMenu.switchingMenus) {
                shown.Add(subMenu);
            }
        }
        // The AutoScroll value to give back when the selection leaves: whatever scrolls now, which is the container
        // unless the top submenu has taken its place
        bool autoScrollOwed;
        if (Container.Current != topSubMenu) {
            Container.Current?.OnLeave?.Invoke();
            autoScrollOwed = Container.AutoScroll;
        } else {
            topSubMenu.ReleaseFocus();
            autoScrollOwed = topSubMenu.receivedHover ? (topSubMenu.autoScrollToReturn ?? topSubMenu.AutoScroll)
                                                      : Container.AutoScroll;
        }
        // Only now: releasing the focus calls OnLeave on the selected item, which is found in the menu shown
        bool switchedMenu = itemMenuIndex >= 0 && itemMenuIndex != MenuIndex;
        if (switchedMenu) {
            StartMenuSwitch(itemMenuIndex - MenuIndex);
        }

        ++selectionMoves;
        receivedHover      = true;
        Focus              = (item != null) ? FocusType.Body
                                            : (TitleSelectable && AutoEnter ? FocusType.Title : FocusType.None);
        AutoScroll         = autoScroll;
        autoScrollToReturn = autoScrollOwed;
        if (item != null) {
            Selection = CurrentMenu.IndexOf(item);
            Current.OnEnter?.Invoke();
        }
        // On the title, an auto-enter submenu handles the input itself; otherwise what contains it does
        bool inputStaysOutside = Focus == FocusType.None;
        if (parent == null) {
            Container.Focused    = inputStaysOutside && !InputSuspended(Container);
            Container.AutoScroll = false;
            Container.Selection  = Container.IndexOf(this);
        } else {
            parent.MoveSelectionToChild(this, inputStaysOutside);
        }
        foreach (RecursiveSubMenuBase subMenu in shown) {
            if (subMenu.ShowMenu && !(subMenu == this && switchedMenu)) {
                // Still drawn after the move: the collapse that the release started would blank the items meanwhile
                subMenu.switchingMenus = false;
                subMenu.switchMenuEase = 1f;
            }
        }
        RecalculateSize();
        Container.RecalculateSize();
        if (autoScroll && snapScroll) {
            Container.Position.Y = ScrollTargetY;
        }
    }

    private void MoveSelectionToChild(TextMenu.Item item, bool focus) {
        ++selectionMoves;
        receivedHover      = true;
        Focus              = focus ? FocusType.Body : FocusType.Child;
        AutoScroll         = false;
        autoScrollToReturn = null;  // The child that called holds it
        Selection          = CurrentMenu.IndexOf(item);
        if (parent == null) {
            Container.Focused    = false;
            Container.AutoScroll = false;
            Container.Selection  = Container.IndexOf(this);
        } else {
            parent.MoveSelectionToChild(this, false);
        }
    }

    // =============================== Handle button presses when the parent has focus =================================
    /// <summary>
    /// What the search of Everest's Mod Options matches: the title, then the search labels of the items of every
    /// menu, one per line, so that the search reaches the submenu that holds a match. Null with neither.
    /// </summary>
    public override string SearchLabel() {
        List<string> labels = [];
        if (!string.IsNullOrEmpty(Label)) {
            labels.Add(Label);
        }
        foreach (LabeledMenu menu in menus) {
            foreach (TextMenu.Item item in menu.Menu) {
                if (item.SearchLabel() is {Length: > 0} label) {
                    labels.Add(label);
                }
            }
        }
        return labels.Count > 0 ? string.Join("\n", labels) : null;
    }

    /// <summary>
    /// Confirm on the title: moves the selection into the items, if one of them can be selected. A submenu that
    /// only shows its items once entered is entered whatever they are, so that they can be read: nothing is selected
    /// then, and Cancel leaves.
    /// </summary>
    public override void ConfirmPressed() {
        // A body already shown with nothing to select has nothing to offer: the focus stays outside
        if (CurrentMenu.Exists(CanSelect) || (!ShowMenu && CurrentMenu.Count > 0)) {
            Audio.Play(SFX.ui_main_button_select);
            Enter(1, true);
            Input.MenuConfirm.ConsumePress();
            // TextMenu.Update holds Pause back only while Confirm is pressed: a key bound to both would close the menu
            Input.Pause.ConsumePress();
        }
    }

    /// <summary>
    /// Left on the title: switches to the previous menu, if there is one, and invokes <see cref="OnValueChange"/>
    /// </summary>
    public override void LeftPressed() {
        if (attachedTo != null && MenuIndex > 0) {
            Audio.Play(SFX.ui_main_button_toggle_off);
            StartMenuSwitch(-1);
        }
    }

    /// <summary>
    /// Right on the title: switches to the next menu, if there is one, and invokes <see cref="OnValueChange"/>
    /// </summary>
    public override void RightPressed() {
        if (attachedTo != null && MenuIndex < menus.Count - 1) {
            Audio.Play(SFX.ui_main_button_toggle_on);
            StartMenuSwitch(1);
        }
    }

    /// <summary>
    ///     Switch to another of the submenu's menus, as if the player had pressed left or right on the title, and
    ///     invoke <see cref="OnValueChange"/>. If the selection is in the items of the submenu, it is given back to
    ///     what contains the submenu first. While the submenu is not in a menu, this only records
    ///     <paramref name="menuIndex"/> as the menu to show once it is added, and invokes nothing.
    /// </summary>
    /// <param name="menuIndex">
    ///     Index of the menu to switch to. Ignored if it is out of range or already the menu shown. While the submenu
    ///     is not in a menu it is recorded unchecked, and clamped to the menus that exist when the submenu is added.
    /// </param>
    public void SelectMenu(int menuIndex) {
        if (!Attached) {
            // Not in a menu: the next Added starts on this menu
            pendingMenuSelection = menuIndex;
            MenuIndex            = menuIndex;
            return;
        }
        if (menuIndex < 0 || menuIndex >= menus.Count || menuIndex == MenuIndex) {
            return;
        }
        if (Focus >= FocusType.Body) {
            ReleaseFocus();
        }
        StartMenuSwitch(menuIndex - MenuIndex);
    }

    /// <summary>
    /// Start the height animation toward the menu <paramref name="dir"/> places after the one shown, and make it the
    /// menu shown. With a non-zero <paramref name="dir"/>, invokes <see cref="OnValueChange"/> with its index.
    /// </summary>
    /// <param name="dir">
    ///     How many menus to move by; the result is not checked against the number of menus. 0 animates a height
    ///     change of the menu shown, as when a single-menu submenu expands or collapses.
    /// </param>
    protected void StartMenuSwitch(int dir) {
        switchingMenus       = true;
        switchFromMenuIndex  = MenuIndex;
        switchFromMenuHeight = EasedMenuHeight;
        switchMenuEase       = 0f;
        MenuIndex += dir;
        lastDir = dir;
        RecalculateSize();
        switchMenuEaseRate = EaseRate(maxSwitchMenuTime, switchMenuVelocity, menuHeight - switchFromMenuHeight);
        if (RenderingMenu == CurrentMenu) {
            menuWiggler.Start();
        }
        if (dir != 0) {
            ValueWiggler.Start();
            OnValueChange?.Invoke(MenuIndex);
        }
    }

    // ==================================================== Update =====================================================
    /// <summary>
    /// Handles the input while the submenu has the focus, advances its animations, and updates the items of every
    /// menu, calling the <c>OnUpdate</c> and then the <c>Update</c> of each
    /// </summary>
    public override void Update() {
        if (parent != null) {
            UpdateSelf();
            return;
        }
        // Called from the loop of TextMenu.Update over its items: RemoveFromMenu must not change that list meanwhile
        TextMenu wasUpdating = updatingMenu;
        updatingMenu         = Container;
        try {
            UpdateSelf();
        } finally {
            updatingMenu = wasUpdating;
        }
    }

    // The buttons, while the submenu has the focus. Not called while a page or the room chooser has the menu's input.
    private void HandleInput() {
        if (Focus == FocusType.Body && !switchingMenus) {
            if (Input.MenuDown.Pressed) {
                if (!Input.MenuDown.Repeating || !MoveWouldWrap(1, true)) {
                    TextMenu.Item selectedBefore = Current;
                    MoveSelection(1, true, true, out bool playedScrollSFX);
                    // No sound for a press that moved nothing, as with nothing selectable
                    if (!playedScrollSFX && (Current != selectedBefore || Focus != FocusType.Body)) {
                        Audio.Play(SFX.ui_main_roll_down);
                    }
                    Input.MenuDown.ConsumePress();
                }
            } else if (Input.MenuUp.Pressed) {
                if (!Input.MenuUp.Repeating || !MoveWouldWrap(-1, true)) {
                    TextMenu.Item selectedBefore = Current;
                    MoveSelection(-1, true, true, out bool playedScrollSFX);
                    if (!playedScrollSFX && (Current != selectedBefore || Focus != FocusType.Body)) {
                        Audio.Play(SFX.ui_main_roll_up);
                    }
                    Input.MenuUp.ConsumePress();
                }
            }
            // Any of these handlers may clear the submenu or give its focus to a page, after which Current is null,
            // so it is read again for each button. Confirm's two calls go to the same item.
            if (Input.MenuLeft.Pressed) {
                Current?.LeftPressed();
            }
            if (Input.MenuRight.Pressed) {
                Current?.RightPressed();
            }
            if (Input.MenuConfirm.Pressed && Current is TextMenu.Item pressed) {
                // A key bound to Confirm and to Pause leaves Pause buffered, and would exit the submenu next frame
                Input.Pause.ConsumePress();
                pressed.ConfirmPressed();
                pressed.OnPressed?.Invoke();
            }
            if (Input.MenuJournal.Pressed) {
                Current?.OnAltPressed?.Invoke();
            }
        } else if (Focus == FocusType.Title) {
            // In title focus, listen to all the same inputs but handle them differently
            if (Input.MenuDown.Pressed) {
                bool playedScrollSFX = false;
                if (AutoEnter && CurrentMenu.Count > 0) {
                    Enter(1, true);
                } else {
                    Exit(1, true, false, out playedScrollSFX, out _);
                }
                if (!playedScrollSFX) {
                    Audio.Play(SFX.ui_main_roll_down);
                }
                Input.MenuDown.ConsumePress();
            } else if (Input.MenuUp.Pressed) {
                Exit(-1, true, false, out bool playedScrollSFX, out _);
                if (!playedScrollSFX) {
                    Audio.Play(SFX.ui_main_roll_up);
                }
                Input.MenuUp.ConsumePress();
            }
            if (Input.MenuLeft.Pressed) {
                LeftPressed();
            }
            if (Input.MenuRight.Pressed) {
                RightPressed();
            }
            if (Input.MenuConfirm.Pressed) {
                // As a menu does for its selected row
                ConfirmPressed();
                OnPressed?.Invoke();
            }
            if (Input.MenuJournal.Pressed) {
                OnAltPressed?.Invoke();
            }
        }
        if (Focus == FocusType.Title || Focus == FocusType.Body) {
            // Common behavior between title and body focus since MoveToY and Exit handle the differences
            if (CoreModule.Settings.MenuPageDown.Pressed) {
                MoveToY(GetYOffsetOf(Current) + 1080f, 1, true);
                Audio.Play(SFX.ui_main_roll_down);
                CoreModule.Settings.MenuPageDown.ConsumePress();
            } else if (CoreModule.Settings.MenuPageUp.Pressed) {
                MoveToY(GetYOffsetOf(Current) - 1080f, -1, true);
                Audio.Play(SFX.ui_main_roll_up);
                CoreModule.Settings.MenuPageUp.ConsumePress();
            }
            bool exitTopMenu = Input.ESC.Pressed || Input.Pause.Pressed;
            if (!Input.MenuConfirm.Pressed && (Input.MenuCancel.Pressed || exitTopMenu)) {
                Exit(0, true, exitTopMenu, out _, out bool playedBackSFX);
                if (!playedBackSFX) {
                    Audio.Play(SFX.ui_main_button_back);
                }
                Input.MenuCancel.ConsumePress();
                Input.ESC.ConsumePress();
                Input.Pause.ConsumePress();
            }
        }
    }

    private void UpdateSelf() {
        // TextMenu.Selection is a public field, and code that writes it calls no OnLeave. Without this the submenu
        // would keep the focus and the container's AutoScroll while the selection is elsewhere.
        if (receivedHover && parent == null && Container.IndexOf(this) != Container.Selection) {
            DefaultOnLeave();
        }
        // The other way round: Everest's Mod Options writes the field when it rebuilds its menu. Not while the menu
        // is unfocused, as when it slides in or a page is over it.
        // A handler of an earlier row may have taken this submenu out of the menu, and the written row may be one
        // nothing can select.
        if (!receivedHover && parent == null && Container != null && Container.Focused && Container.Current == this &&
                Hoverable) {
            HoverFrom(0);
        }
        if (enterWhenFocused && Container is {Focused: true}) {
            enterWhenFocused = false;
            if (receivedHover && parent == null && Focus == FocusType.None && Container.Current == this && Hoverable) {
                Enter(0, false);
            }
        }
        if (switchMenuEase < 1f) {
            switchMenuEase = Calc.Approach(switchMenuEase, 1f, switchMenuEaseRate * Engine.RawDeltaTime);
            if (switchMenuEase == 1f) {
                if (RenderingMenu != CurrentMenu) {
                    menuWiggler.Start();
                }
                switchingMenus = false;
            }
            parent?.RecalculateSize();
        }
        if (addRemoveItems.Count > 0) {
            foreach (AddRemoveItemState addRemoveItem in addRemoveItems) {
                addRemoveItem.Update();
            }
            // Take finished animations out of the list before finishing them, since the done callbacks may start new
            // ones
            List<AddRemoveItemState> finished = addRemoveItems.FindAll((AddRemoveItemState addRemoveItem) =>
                                                                          addRemoveItem.Done);
            addRemoveItems.RemoveAll((AddRemoveItemState addRemoveItem) => addRemoveItem.Done);
            foreach (AddRemoveItemState addRemoveItem in finished) {
                FinishAnimation(addRemoveItem);
            }
            RecalculateSize();
        }
        sine += Engine.RawDeltaTime;
        base.Update();

        if (!InputSuspended(attachedTo)) {
            HandleInput();
        }

        updatingItems.Clear();
        foreach (LabeledMenu menu in menus) {
            updatingItems.AddRange(menu.Menu);
        }
        foreach (TextMenu.Item item in updatingItems) {
            if (item.Container == null) {
                // Removed by an item updated earlier this frame
                continue;
            }
            item.OnUpdate?.Invoke();
            item.Update();
        }

        // A handler above may have taken the submenu out of the menu
        if (AutoScroll && Container != null) {
            Container.Position.Y += (ScrollTargetY - Container.Position.Y) *
                                        (1f - (float) Math.Pow(0.01f, Engine.RawDeltaTime));
        }
    }

    // =================================================== Rendering ===================================================
    /// <summary>Draws the title with its icon, the menu slider, and the items of <see cref="RenderingMenu"/></summary>
    public override void Render(Vector2 position, bool highlighted) {
        Vector2 top = new Vector2(position.X, position.Y - (Height() / 2));

        float alpha      = Container.Alpha;
        Color titleColor = Disabled ? Color.DarkSlateGray
                                    : ((highlighted || Focus == FocusType.Title ? Container.HighlightColor
                                                                                : Color.White) * alpha);
        Color strokeColor = Color.Black * (alpha * alpha * alpha);

        Vector2 titlePosition = top + (Vector2.UnitY * titleHeight / 2) + new Vector2(0f, titleWiggler.Value * 8f);
        Vector2 justify       = new Vector2(0f, 0.5f);
        Vector2 iconJustify   = new Vector2(ActiveFont.Measure(Label).X + 0.5f * icon.Width + iconXPadding, 5f);
        if (ShowIcon) {
            Color iconColor = Disabled || CurrentMenu.Count < 1
                                  ? Color.DarkSlateGray
                                  : (Focus != FocusType.None ? Container.HighlightColor : Color.White);
            icon.DrawOutlineCentered(titlePosition + iconJustify, iconColor * alpha);
        }
        if (ShowTitle) {
            ActiveFont.DrawOutline(Label, titlePosition, justify, Vector2.One, titleColor, 2f, strokeColor);
        }

        if (menus.Count > 0 && ShowMenusSlider) {
            ActiveFont.DrawOutline(menus[MenuIndex].Label,
                                   titlePosition + new Vector2(Container.Width - optionsWidth * 0.5f +
                                                                   lastDir * ValueWiggler.Value * 8f,
                                                               0f),
                                   new Vector2(0.5f, 0.5f), Vector2.One * optionTextScale, titleColor, 2f, strokeColor);
            Vector2 wiggle = Vector2.UnitX * (highlighted ? ((float) Math.Sin(sine * 4f) * 4f)
                                                          : 0f);
            Color arrowColor = MenuIndex > 0 ? titleColor : (Color.DarkSlateGray * alpha);
            Vector2 arrowPosition =
                titlePosition + new Vector2(Container.Width - optionsWidth + 40f +
                                                ((lastDir < 0) ? (-ValueWiggler.Value * 8f) : 0f),
                                            0f) -
                (MenuIndex > 0 ? wiggle : Vector2.Zero);
            ActiveFont.DrawOutline("<", arrowPosition, new Vector2(0.5f, 0.5f),
                                   Vector2.One, arrowColor, 2f, strokeColor);

            arrowColor = MenuIndex < menus.Count - 1 ? titleColor : (Color.DarkSlateGray * alpha);
            arrowPosition =
                titlePosition + new Vector2(Container.Width - 40f +
                                                ((lastDir > 0) ? (ValueWiggler.Value * 8f) : 0f),
                                            0f) +
                (MenuIndex < menus.Count - 1 ? wiggle : Vector2.Zero);
            ActiveFont.DrawOutline(">", arrowPosition, new Vector2(0.5f, 0.5f),
                                   Vector2.One, arrowColor, 2f, strokeColor);
        }

        if (RenderingMenu != null) {
            Vector2 menuPosition = new Vector2(top.X + ItemIndent, top.Y + titleHeight) +
                                       new Vector2(0f, menuWiggler.Value * 8f);
            if (ShowTitle || ShowMenusSlider) {
                // Add spacing between title and first item if the title is shown
                menuPosition += new Vector2(0f, ItemSpacing);
            }
            foreach (TextMenu.Item item in RenderingMenu) {
                if (item.Visible) {
                    float height = item.Height();
                    Vector2 itemPosition = menuPosition + new Vector2(0f,
                                                                      height * 0.5f + item.SelectWiggler.Value * 8f);
                    if (itemPosition.Y + height * 0.5f > 0f &&
                            itemPosition.Y - height * 0.5f < Engine.Height) {
                        // Temporarily spoof Container.Width to properly handle the right-alignment of indented items
                        // since existing items only look at the top-level menu width to do right-alignment
                        Container.Width -= ItemIndent;
                        try {
                            item.Render(itemPosition, Focus == FocusType.Body && Current == item);
                        } finally {
                            Container.Width += ItemIndent;
                        }
                    }
                    menuPosition.Y += height + ItemSpacing;
                } else if (AnimationOf(item) is AddRemoveItemState addRemoveItem) {
                    // Add spacing for the smooth add/remove animation
                    menuPosition.Y += addRemoveItem.EasedHeight(ItemSpacing);
                }
            }
        }
    }
}
