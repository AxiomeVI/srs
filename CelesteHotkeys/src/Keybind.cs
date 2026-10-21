using System;
using System.Reflection;

namespace Celeste.Mod.CelesteHotkeys;

/// <summary>
///     One hotkey, declared once: its label on the remap screen and the settings property it binds.
/// </summary>
// A row is the whole declaration. The remap screen draws its keyboard and controller sections from
// the same table and HotkeySet polls from it, so a new hotkey is one row and a ButtonBinding property,
// and nothing else.
//
// The property is named, not read through a lambda, so a row cannot name one binding and read
// another: `new(DialogIds.ToggleX, nameof(Settings.ToggleX))` puts both on one line, nameof makes the
// compiler check the property exists, and the getter is built from that name.
internal sealed class Keybind<TSettings> where TSettings : class {
    /// <summary>Dialog id of the row's label, drawn in both sections and on the recording overlay.</summary>
    public string LabelId { get; }

    /// <summary>
    ///     Dialog id of a line saying what the hotkey does, shown under the row while it is selected.
    ///     Null for none.
    /// </summary>
    public string DescriptionId { get; }

    /// <summary>Name of the ButtonBinding property on <typeparamref name="TSettings"/>.</summary>
    public string Property { get; }

    private readonly Func<TSettings, ButtonBinding> getter;

    /// <exception cref="ArgumentException">
    ///     The property does not exist, is not a readable public instance ButtonBinding, or takes an
    ///     index. Thrown at construction, so a bad row fails when the table is built — at load, and in
    ///     the unit suite — rather than the first time someone opens the screen.
    /// </exception>
    public Keybind(string labelId, string property, string descriptionId = null) {
        LabelId = labelId;
        DescriptionId = descriptionId;
        Property = property;

        PropertyInfo info = typeof(TSettings).GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (info is null || info.GetIndexParameters().Length > 0 || info.GetGetMethod() is null
            || !typeof(ButtonBinding).IsAssignableFrom(info.PropertyType)) {
            throw new ArgumentException(
                $"{typeof(TSettings).Name}.{property} is not a readable public ButtonBinding property.",
                nameof(property));
        }

        // A compiled getter rather than PropertyInfo.GetValue: this runs once per hotkey per frame.
        getter = info.GetGetMethod()!.CreateDelegate<Func<TSettings, ButtonBinding>>();
    }

    /// <summary>The binding on <paramref name="settings"/>; null before Everest has initialized it.</summary>
    public ButtonBinding Binding(TSettings settings) => settings is null ? null : getter(settings);
}
