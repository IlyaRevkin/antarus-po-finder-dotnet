using System;
using System.Globalization;
using System.Windows.Data;

namespace AntarusPoFinder.App.Converters;

/// <summary>Resolves a ComboBox's closed-state display text the same way its dropdown rows do
/// (via DisplayMemberPath), for use in a custom ControlTemplate. WPF's own SelectionBoxItem/
/// SelectionBoxItemTemplate mechanism does not reliably apply DisplayMemberPath once the default
/// template is replaced, so this recomputes it directly via reflection instead.</summary>
public class DisplayMemberPathConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not { } item) return "";
        var path = values[1] as string;
        if (string.IsNullOrEmpty(path)) return item.ToString() ?? "";

        object? current = item;
        foreach (var segment in path.Split('.'))
        {
            if (current is null) break;
            current = current.GetType().GetProperty(segment)?.GetValue(current);
        }
        return current?.ToString() ?? "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
