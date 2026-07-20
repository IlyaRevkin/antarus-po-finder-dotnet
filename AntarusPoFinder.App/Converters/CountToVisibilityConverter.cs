using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AntarusPoFinder.App.Converters;

/// <summary>Collapses a container bound to a collection's Count when it's zero (e.g. hide the
/// "Быстрый доступ" sidebar section entirely until at least one quick-launch app is configured).</summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
