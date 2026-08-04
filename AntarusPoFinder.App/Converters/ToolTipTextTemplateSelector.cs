using System.Windows;
using System.Windows.Controls;

namespace AntarusPoFinder.App.Converters;

/// <summary>Выбирает шаблон для содержимого подсказки: текстовое — рисуем своим TextBlock с
/// переносом по строкам, готовый элемент (если подсказку когда-нибудь соберут из контролов) —
/// оставляем как есть.
///
/// <b>Зачем это вообще понадобилось.</b> Подсказки задаются строкой (ToolTip="…"), а строку WPF
/// рисует сгенерированным TextBlock'ом из СВОЕГО шаблона ContentPresenter. Неявный стиль TextBlock,
/// положенный рядом с ContentPresenter (в ресурсы Border внутри шаблона ToolTip), на такой TextBlock
/// не действует: внутри шаблона неявные стили ищутся в ресурсах самого шаблона и приложения, а не в
/// окружающем дереве. Именно поэтому две прошлые попытки «включить переносы всем подсказкам»
/// ничего не изменили — TextWrapping до сгенерированного TextBlock просто не доезжал, длинная
/// подсказка оставалась одной строкой и обрезалась по MaxWidth.
///
/// Явный шаблон обходит разрешение неявных стилей полностью — тем же приёмом, что уже применён к
/// содержимому кнопок в Styles.xaml.</summary>
public class ToolTipTextTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is null or UIElement ? null : TextTemplate;
}
