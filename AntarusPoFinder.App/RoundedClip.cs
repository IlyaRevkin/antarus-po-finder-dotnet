using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AntarusPoFinder.App;

/// <summary>Обрезать содержимое по СКРУГЛЁННЫМ углам рамки, а не по прямоугольнику.
///
/// <b>Зачем это понадобилось.</b> У <see cref="Border"/> есть <c>CornerRadius</c> и есть
/// <c>ClipToBounds</c> — и легко решить, что вместе они дают скруглённую обрезку. Не дают:
/// <c>ClipToBounds</c> режет по прямоугольным границам, углы остаются квадратными. Поэтому
/// подсветка половины разделённой кнопки «Загрузить в ПЛК» вылезала за скругление, и в тикете это
/// описано ровно так: «при наведении подсвечивает странно — не по контуру кнопки, а квадратиком».
///
/// Здесь к рамке привязывается настоящая геометрия со скруглением, и она пересчитывается при каждой
/// смене размера: радиус приходит из темы (ButtonCorner), а размер кнопки зависит от надписи и
/// масштаба экрана — посчитать это один раз нельзя.
///
/// Радиус берётся у самой рамки, поэтому тема с другим скруглением работает без правок здесь.</summary>
public static class RoundedClip
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(RoundedClip),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;

        border.SizeChanged -= OnSizeChanged;
        border.Loaded -= OnLoaded;
        if (e.NewValue is not true)
        {
            border.Clip = null;
            return;
        }

        border.SizeChanged += OnSizeChanged;
        // Размер к этому моменту ещё нулевой; первая настоящая обрезка — после раскладки.
        border.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply(sender as Border);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply(sender as Border);

    private static void Apply(Border? border)
    {
        if (border is null) return;
        if (border.ActualWidth <= 0 || border.ActualHeight <= 0)
        {
            border.Clip = null;
            return;
        }

        // Один радиус на все углы: у кнопок этого приложения разные углы не встречаются, а брать
        // максимум из четырёх — значит незаметно врать в том редком случае, когда они разные.
        var radius = border.CornerRadius.TopLeft;
        var geometry = new RectangleGeometry(
            new Rect(0, 0, border.ActualWidth, border.ActualHeight), radius, radius);
        geometry.Freeze();
        border.Clip = geometry;
    }
}
