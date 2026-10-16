using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AntarusPoFinder.App.Converters;

/// <summary>Обратное к BooleanToVisibilityConverter: true прячет, false показывает.
///
/// Понадобилось там, где ОДНО свойство управляет двумя вариантами одного и того же места: заголовок
/// секции меню — кликабельный, пока секции сворачиваются, и обычная подпись, когда нет (в бете
/// секции не сворачиваются вовсе). Держать ради этого второе свойство-зеркало в модели значит
/// завести две правды об одном факте и однажды их рассинхронизировать.</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed or Visibility.Hidden;
}
