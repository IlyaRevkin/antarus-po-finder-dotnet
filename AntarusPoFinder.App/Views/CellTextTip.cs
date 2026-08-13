using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AntarusPoFinder.App.Views;

/// <summary>Подсказка с полным текстом ячейки — но только тогда, когда текст в неё не влез.
///
/// Ячейки таблиц приложения однострочные и обрезаются многоточием (см. стиль <c>DataGridCellText</c> в
/// Themes/Styles.xaml): длинный адрес на хостинге, путь на диске или причина отказа видны наполовину, и
/// прочитать их можно было только растянув столбец или скопировав строку. Отсюда правило: при
/// наведении на ячейку показывать полный текст, если он в неё не влез.
///
/// Оговорка «если не влез» здесь не украшение: подсказка на КАЖДОЙ ячейке — это всплывающее окно над
/// таблицей при любом движении мыши, повторяющее ровно то, что и так написано. Поэтому сама подсказка
/// привязана в стиле (иначе событие <see cref="FrameworkElement.ToolTipOpening"/> не приходит вовсе —
/// WPF не открывает пустую подсказку), а здесь она отменяется в момент открытия, если текст помещается
/// целиком.</summary>
public static class CellTextTip
{
    public static readonly DependencyProperty OnlyWhenTrimmedProperty = DependencyProperty.RegisterAttached(
        "OnlyWhenTrimmed", typeof(bool), typeof(CellTextTip), new PropertyMetadata(false, OnChanged));

    public static void SetOnlyWhenTrimmed(DependencyObject element, bool value) =>
        element.SetValue(OnlyWhenTrimmedProperty, value);

    public static bool GetOnlyWhenTrimmed(DependencyObject element) =>
        (bool)element.GetValue(OnlyWhenTrimmedProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text) return;
        text.ToolTipOpening -= Opening;
        if (e.NewValue is true) text.ToolTipOpening += Opening;
    }

    private static void Opening(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock text) return;
        if (!IsTrimmed(text)) e.Handled = true; // Handled = подсказку не показывать
    }

    /// <summary>Текст не помещается в отведённую ширину. Меряем сами, а не спрашиваем у WPF: свойства
    /// «текст обрезан» в WPF нет (в отличие от UWP), а <see cref="TextBlock.ActualWidth"/> у обрезанной
    /// строки равен ширине ячейки и о переполнении молчит.</summary>
    private static bool IsTrimmed(TextBlock text)
    {
        if (string.IsNullOrEmpty(text.Text) || text.ActualWidth <= 0) return false;
        try
        {
            var formatted = new FormattedText(
                text.Text,
                CultureInfo.CurrentCulture,
                text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(text).PixelsPerDip);

            // Половина пикселя запаса: ширина ячейки и ширина текста считаются по-разному, и точное
            // равенство у ровно помещающейся строки время от времени оказывается «больше на 0,0001».
            return formatted.WidthIncludingTrailingWhitespace > text.ActualWidth + 0.5;
        }
        catch (Exception)
        {
            // Шрифта нет, текст с суррогатной парой без глифа — подсказку в таком случае лучше
            // показать, чем уронить открытие таблицы.
            return true;
        }
    }
}
