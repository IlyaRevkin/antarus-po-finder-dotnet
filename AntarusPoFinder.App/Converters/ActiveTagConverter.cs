using System;
using System.Globalization;
using System.Windows.Data;

namespace AntarusPoFinder.App.Converters;

/// <summary>Converts IsActive (bool) into the "Active" Tag value the NavButton style triggers on.</summary>
public class ActiveTagConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "Active" : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
