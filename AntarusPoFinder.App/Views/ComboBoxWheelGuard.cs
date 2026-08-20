using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AntarusPoFinder.App.Views;

/// <summary>Колесо мыши над закрытым выпадающим списком НЕ меняет его значение.
///
/// Дословная жалоба: «когда в окне QR крутишь столбец „Макет“ и мышка попадает на combobox, то
/// меняется настройка этого combobox». Это поведение WPF по умолчанию: закрытый ComboBox сам
/// обрабатывает MouseWheel и перещёлкивает выбранный пункт. В форме, которую листают колесом
/// (панель макета этикетки, вкладки настроек), это не «удобное листание», а тихая порча настройки:
/// человек прокручивает список параметров, курсор проезжает над «Положение кода» — и положение
/// молча меняется. Заметить это можно только по перерисованному предпросмотру, а на печати —
/// вообще никогда.
///
/// Ставится ОДИН раз на весь класс ComboBox (App.OnStartup), а не по одному обработчику на каждый
/// список в каждом окне: списков в приложении десятки, и забытый — это ровно та же испорченная
/// настройка. Раскрытый список ведёт себя как раньше: там колесо листает пункты, потому что человек
/// сам его открыл и смотрит именно на него.
///
/// Событие не просто гасится, а передаётся выше: иначе прокрутка панели «залипала» бы каждый раз,
/// когда курсор оказывается над списком. Родителю отправляется тот же поворот колеса, и внешний
/// ScrollViewer листает форму дальше, как будто списка под курсором нет.</summary>
public static class ComboBoxWheelGuard
{
    /// <summary>Включить на всё приложение. Вызывается из App.OnStartup до создания окон.</summary>
    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel), handledEventsToo: true);

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox combo || e.Handled) return;
        // Раскрытый список человек открыл сам и смотрит именно в него — там колесо работает штатно.
        if (combo.IsDropDownOpen) return;

        e.Handled = true;

        // Тот же поворот колеса — родителю: прокрутка формы не должна спотыкаться о список.
        // Source — сам ComboBox: внешнему ScrollViewer важно, что событие пришло изнутри него.
        if (VisualTreeHelper.GetParent(combo) is not UIElement parent) return;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = combo,
        });
    }
}
