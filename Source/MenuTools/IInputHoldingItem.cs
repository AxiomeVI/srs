// MenuTools requires: nothing else
using System;
using System.Runtime.CompilerServices;

namespace Celeste.Mod.SpeedrunSheet.MenuTools;

/// <summary>
/// A <see cref="TextMenu.Item"/> that handles input itself while it has the selection, as recursive submenus do, so
/// unfocusing the <see cref="TextMenu"/> it is in doesn't stop it. Pages and the room chooser take input away from
/// such an item while they are open through this interface, which lets them be used without the submenu classes.
/// </summary>
public interface IInputHoldingItem {
    /// <summary>The innermost item that handles input, this one or one nested in it, or null if none does</summary>
    IInputHoldingItem InputHolder { get; }

    /// <summary>Stop the item from handling input until the returned action is called</summary>
    /// <param name="stopAutoScroll">Also stop it from scrolling its menu in the meantime</param>
    /// <returns>The action that gives the item back the input state it had</returns>
    Action SuspendInput(bool stopAutoScroll);
}

/// <summary>
/// The menus whose input a page or the room chooser has taken. While a menu is blocked, an
/// <see cref="IInputHoldingItem"/> in it must handle no button and must not focus the menu: a handler can still move
/// the selection there, and the row it lands on acts once the block is released.
/// </summary>
/// <remarks>
/// Two limits. A block that is never released stays for as long as the menu exists: a page whose scene ends without
/// <c>Return()</c> leaves its parent blocked, so that menu must not be shown again in another scene. And each mod
/// compiles its own copy of this class, so in a menu several mods add rows to, such as Mod Options, a page of one mod
/// does not block the submenus of another.
/// </remarks>
public static class MenuInputBlock {
    private static readonly ConditionalWeakTable<TextMenu, StrongBox<int>> blocks = new();

    /// <summary>Block a menu until the returned action is called. Blocks add up.</summary>
    /// <param name="menu">The menu; null blocks nothing</param>
    /// <returns>The action that releases this block; calling it again does nothing</returns>
    public static Action Block(TextMenu menu) {
        if (menu == null) {
            return () => {};
        }
        StrongBox<int> count = blocks.GetOrCreateValue(menu);
        ++count.Value;
        bool released = false;
        return () => {
            if (!released) {
                released = true;
                --count.Value;
            }
        };
    }

    /// <summary>
    /// The item that handles input in a menu's selected row, or null if the menu handles it. Puts a selection that is
    /// past the last row back on the last selectable one first, or on none:
    /// <see cref="TextMenu.Remove(TextMenu.Item)"/> leaves it there, where reading <see cref="TextMenu.Current"/>
    /// throws.
    /// </summary>
    /// <param name="menu">The menu; null has no holder</param>
    public static IInputHoldingItem HolderIn(TextMenu menu) {
        if (menu == null) {
            return null;
        }
        if (menu.Selection >= menu.Items.Count) {
            menu.Selection = -1;
            for (int i = menu.Items.Count - 1; i >= 0; --i) {
                if (menu.Items[i].Hoverable) {
                    menu.Selection = i;
                    break;
                }
            }
        }
        return (menu.Current as IInputHoldingItem)?.InputHolder;
    }

    /// <summary>Whether a menu has a block that was not released</summary>
    /// <param name="menu">The menu; null is not blocked</param>
    public static bool IsBlocked(TextMenu menu) {
        return menu != null && blocks.TryGetValue(menu, out StrongBox<int> count) && count.Value > 0;
    }
}
