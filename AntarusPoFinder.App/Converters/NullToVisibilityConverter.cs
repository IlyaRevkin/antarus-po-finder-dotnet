using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AntarusPoFinder.App.Converters;

/// <summary>Collapses an element when the bound value is null (e.g. hide a quick-app's icon
/// <see cref="System.Windows.Controls.Image"/> when its icon couldn't be extracted).</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
