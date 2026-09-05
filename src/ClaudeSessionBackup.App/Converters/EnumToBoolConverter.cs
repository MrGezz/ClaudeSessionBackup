using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeSessionBackup.App.Converters;

/// <summary>
/// Two-way binding between an enum property and a group of RadioButtons.
/// Each RadioButton's <c>ConverterParameter</c> names the enum member it
/// represents; the one whose parameter matches the current value is checked.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConvertBack"/> returns <see cref="Binding.DoNothing"/> when the
/// radio is being UNCHECKED. Without this, returning a parsed enum from the
/// unchecked radio would overwrite the property with the old page before the
/// new radio's own ConvertBack runs, leaving the selection flickering between
/// the two.
/// </para>
/// <para>
/// <see cref="Convert"/> compares via <c>ToString()</c> rather than casting to
/// <c>int</c> so the XAML parameter is human-readable ("Dashboard", "Catalog")
/// and not a fragile ordinal that breaks when someone reorders the enum.
/// </para>
/// </remarks>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter is string name)
        {
            return Enum.Parse(targetType, name);
        }

        // The radio being unchecked: do nothing, let the newly-checked radio
        // supply the value.
        return Binding.DoNothing;
    }
}
