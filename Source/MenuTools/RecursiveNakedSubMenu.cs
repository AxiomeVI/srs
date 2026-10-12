// MenuTools requires: RecursiveSubMenuBase.cs
using System;
using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunSheet.MenuTools;

/// <summary>
/// Recursive submenu that displays no title and is expanded and collapsed from code, through <see cref="Expanded"/>.
/// While expanded, its items read as rows of the menu that contains it: scrolling moves into and out of them, and
/// Cancel acts on that menu. While collapsed it takes no space and cannot be selected. Add items with
/// <see cref="RecursiveSubMenuBase.AddItem(TextMenu.Item, bool, Action{TextMenu.Item})"/>.
/// </summary>
public class RecursiveNakedSubMenu : RecursiveSubMenuBase {
    /// <inheritdoc/>
    protected override bool TitleSelectable => false;
    /// <inheritdoc/>
    protected override bool AutoEnter       => true;
    /// <inheritdoc/>
    protected override bool AutoExit        => true;
    protected override bool RecursiveExit   => true;
    protected override bool ShowTitle       => false;
    protected override bool ShowIcon        => false;
    protected override bool ShowMenusSlider => false;
    protected override bool ShowMenu        => expanded;
    protected override List<TextMenu.Item> RenderingMenu =>
        (switchingMenus || !expanded) ? null : CurrentMenu;

    /// <summary>
    /// Whether the items are shown. Set it to expand or collapse the submenu; the height change is animated.
    /// Collapsing while the selection is in the submenu first moves it to a neighbouring row of what contains it.
    /// </summary>
    public bool Expanded {
        get => expanded;
        set {
            bool wasExpanded = expanded;
            expanded         = value;
            if (wasExpanded == value) {
                return;
            }
            if (!value) {
                // Don't leave the selection on items that are no longer shown
                ReleaseFocus();
                MoveSelectionOffSelf();
            }
            Selectable = value;
            StartMenuSwitch(0);
        }
    }
    private bool expanded;

    // Once collapsed the submenu can't be selected, so whatever had its selection on it moves to a neighbouring item
    private void MoveSelectionOffSelf() {
        if (parent != null) {
            if (parent.Focus >= FocusType.Body && parent.Current == this) {
                parent.MoveSelectionOffCurrent();
            }
        } else if (Container != null && Container.Current == this) {
            if (!MoveMenuSelectionOffCurrent(Container)) {
                // Nowhere to go: the row keeps the menu's selection, and is hovered again once it is expanded
                DefaultOnLeave();
            }
        }
    }

    /// <param name="initiallyExpanded">
    ///     Whether the submenu starts expanded; see <see cref="Expanded"/>. Default: false.
    /// </param>
    /// <param name="compactMode"><inheritdoc cref="RecursiveSubMenuBase(string, int, bool, float, float, float)"
    ///     path="/param[@name='compactMode']/node()"/></param>
    /// <param name="compactRightWidth"><inheritdoc cref="RecursiveSubMenuBase(string, int, bool, float, float, float)"
    ///     path="/param[@name='compactRightWidth']/node()"/></param>
    /// <param name="itemSpacing"><inheritdoc cref="RecursiveSubMenuBase(string, int, bool, float, float, float)"
    ///     path="/param[@name='itemSpacing']/node()"/></param>
    /// <param name="itemIndent">
    ///     Initializes <see cref="RecursiveSubMenuBase.ItemIndent"/>: how far the items are indented from the title,
    ///     in pixels. Default: 0.
    /// </param>
    /// <param name="items">
    ///     The items the submenu starts with, added in order as
    ///     <see cref="RecursiveSubMenuBase.AddItem(TextMenu.Item, bool, Action{TextMenu.Item})"/> would. Default:
    ///     null, for none.
    /// </param>
    public RecursiveNakedSubMenu(bool initiallyExpanded    = false,
                                 bool compactMode          = true,
                                 float compactRightWidth   = float.NaN,
                                 float itemSpacing         = 4f,
                                 float itemIndent          = 0f,
                                 List<TextMenu.Item> items = null)
            : base(label                : "",
                   initialMenuSelection : 0,
                   compactMode          : compactMode,
                   compactRightWidth    : compactRightWidth,
                   itemIndent           : itemIndent,
                   itemSpacing          : itemSpacing) {
        expanded   = initiallyExpanded;
        Selectable = initiallyExpanded;
        base.AddMenu("", items);
    }
}
