using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AntarusPoFinder.App.Views;

/// <summary>Guards DataGrid MouseDoubleClick handlers against firing on clicks that never touched a
/// data row — e.g. repeatedly clicking a column header to sort. MouseDoubleClick is attached to the
/// whole DataGrid (there's no per-row equivalent), so without this check, two quick clicks on a
/// header re-open whatever was SelectedItem from an earlier real row click, appearing to "open a
/// firmware the operator never clicked".</summary>
public static class DataGridClickGuard
{
    public static bool IsOverDataRow(RoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is DataGridColumnHeader) return false;
            if (source is DataGridRow) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
}
