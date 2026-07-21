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
        if (string.IsNullOrEmpty(path))
        {
            // A ComboBox populated with literal <ComboBoxItem> children (no ItemsSource/
            // DisplayMemberPath at all — e.g. Осмотр's "Качество сканирования") has SelectedItem
            // itself BE the ComboBoxItem, whose own ToString() is just the default Object one
            // (the type name, truncated to something like "Syste…" at this box's width) since
            // ComboBoxItem doesn't override it. Read .Content instead for that case — plain
            // data-bound items (the DisplayMemberPath branch below, and non-ContentControl items
            // here) are unaffected.
            return item is System.Windows.Controls.ContentControl cc ? cc.Content?.ToString() ?? "" : item.ToString() ?? "";
        }

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
