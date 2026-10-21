// MenuTools requires: RecursiveSubMenuBase.cs
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Celeste.Mod.SpeedrunSheet.MenuTools;

/// <summary>
/// Interface to render some preview of menu contents at the right side of a collapsed <see cref="RecursiveSubMenu"/>
/// </summary>
public interface ISubMenuPreview {
    /// <summary>The width to reserve for the preview at the right of the title, in pixels</summary>
    public float Width();
    /// <summary>
    /// Draws the preview. The submenu calls it only while its items are not drawn: while it is collapsed, and
    /// during the animation that expands or collapses it.
    /// </summary>
    /// <param name="position">The left edge of the reserved width, at the vertical center of the title</param>
    /// <param name="color">
    ///     The color to draw in: the grey of a disabled item, before <paramref name="alpha"/> is applied
    /// </param>
    /// <param name="alpha">The opacity of the menu</param>
    public void Render(Vector2 position, Color color, float alpha);
}

/// <summary>
/// Recursive submenu that displays a title and single submenu, like <see cref="TextMenuExt.SubMenu"/>. Confirm on the
/// title expands it and moves the selection into its items; Cancel collapses it and goes back to the title. Add
/// items with <see cref="RecursiveSubMenuBase.AddItem(TextMenu.Item, bool, Action{TextMenu.Item})"/>, before or after
/// the submenu is added to a menu.
/// </summary>
public class RecursiveSubMenu : RecursiveSubMenuBase {
    /// <summary>
    /// If true, automatically expands and enters the submenu upon receiving hover,
    /// and automatically leaves and collapses the submenu by scrolling past it.
    /// Cancel then leaves it in one press and acts on whatever contains it, since its title can't hold the selection.
    /// </summary>
    public readonly bool EnableAutoEnterExit;

    /// <summary>
    /// Drawn at the right of the title while the items are not drawn; null for none. Its width is reserved at the next
    /// <see cref="RecursiveSubMenuBase.RecalculateSize"/>: set it before the submenu is added to a menu, or call
    /// that method after.
    /// </summary>
    public ISubMenuPreview Preview { private get; set; }

    /// <inheritdoc/>
    protected override bool TitleSelectable => !EnableAutoEnterExit;
    /// <inheritdoc/>
    protected override bool AutoEnter       => EnableAutoEnterExit;
    /// <inheritdoc/>
    protected override bool AutoExit        => EnableAutoEnterExit;
    protected override bool RecursiveExit   => EnableAutoEnterExit;
    protected override bool ShowTitle       => true;
    protected override bool ShowIcon        => true;
    protected override bool ShowMenusSlider => false;
    protected override bool ShowMenu        => Focus >= FocusType.Body;
    protected override List<TextMenu.Item> RenderingMenu =>
        (switchingMenus || Focus < FocusType.Body) ? null : CurrentMenu;

    /// <param name="label">The title of the submenu. Default: empty.</param>
    /// <param name="autoEnterExit">Initializes <see cref="EnableAutoEnterExit"/>. Default: false.</param>
    /// <param name="compactMode"><inheritdoc cref="RecursiveSubMenuBase(string, int, bool, float, float, float)"
    ///     path="/param[@name='compactMode']/node()"/></param>
    /// <param name="compactRightWidth"><inheritdoc cref="RecursiveSubMenuBase(string, int, bool, float, float, float)"
    ///     path="/param[@name='compactRightWidth']/node()"/></param>
    /// <param name="itemSpacing"><inheritdoc cref="RecursiveSubMenuBase(string, int, bool, float, float, float)"
    ///     path="/param[@name='itemSpacing']/node()"/></param>
    /// <param name="itemIndent"><inheritdoc cref="RecursiveSubMenuBase(string, int, bool, float, float, float)"
    ///     path="/param[@name='itemIndent']/node()"/></param>
    /// <param name="items">
    ///     The items the submenu starts with, added in order as
    ///     <see cref="RecursiveSubMenuBase.AddItem(TextMenu.Item, bool, Action{TextMenu.Item})"/> would. Default:
    ///     null, for none.
    /// </param>
    /// <param name="preview">Initializes <see cref="Preview"/>. Default: null, for none.</param>
    public RecursiveSubMenu(string label              = "",
                            bool autoEnterExit        = false,
                            bool compactMode          = true,
                            float compactRightWidth   = float.NaN,
                            float itemSpacing         = 4f,
                            float itemIndent          = 20f,
                            List<TextMenu.Item> items = null,
                            ISubMenuPreview preview   = null)
            : base(label                : label,
                   initialMenuSelection : 0,
                   compactMode          : compactMode,
                   compactRightWidth    : compactRightWidth,
                   itemSpacing          : itemSpacing,
                   itemIndent           : itemIndent) {
        EnableAutoEnterExit = autoEnterExit;
        this.Preview        = preview;
        base.AddMenu("", items);
    }

    /// <inheritdoc/>
    /// <remarks>Also starts the animation that expands the items.</remarks>
    protected override void Enter(int dir, bool wiggle) {
        base.Enter(dir, wiggle);
        StartMenuSwitch(0);
    }

    /// <inheritdoc/>
    /// <remarks>Also starts the animation that collapses the items.</remarks>
    protected override void Exit(int dir, bool wiggle, bool exitAll, out bool playedScrollSFX, out bool playedBackSFX) {
        base.Exit(dir, wiggle, exitAll, out playedScrollSFX, out playedBackSFX);
        StartMenuSwitch(0);
    }

    /// <inheritdoc/>
    /// <remarks>Also starts the animation that collapses the items.</remarks>
    protected override void ForceExit() {
        base.ForceExit();
        StartMenuSwitch(0);
    }

    /// <inheritdoc/>
    /// <remarks>Reserves the width of the <see cref="Preview"/> at the right of the title.</remarks>
    protected override void TweakSizeCalc(ref float titleHeight, ref float menuHeight, float titleWidth,
                                          ref float leftColumnWidth, ref float rightColumnWidth, ref float optionsWidth,
                                          ref float compactWidth) {
        if (Preview != null) {
            rightColumnWidth = Math.Max(rightColumnWidth, Preview.Width());
            compactWidth     = Math.Max(compactWidth, titleWidth + Preview.Width());
        }
    }

    /// <summary>
    /// Draws the submenu, and the <see cref="Preview"/> at the right of the title while the items are not drawn
    /// </summary>
    public override void Render(Vector2 position, bool highlighted) {
        base.Render(position, highlighted);
        if (Preview != null && RenderingMenu == null) {
            Vector2 titlePosition = new(position.X,
                                        position.Y - Height() / 2 + titleHeight / 2 + titleWiggler.Value * 8f);
            Preview.Render(titlePosition + new Vector2(Container.Width - Preview.Width(), 0),
                           Color.DarkSlateGray, Container.Alpha);
        }
    }
}
