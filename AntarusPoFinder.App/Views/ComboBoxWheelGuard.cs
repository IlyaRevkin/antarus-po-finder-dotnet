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
/// Ставится ОДИН раз на всё приложение (App.OnStartup), а не по одному обработчику на каждый
/// список в каждом окне: списков в приложении десятки, и забытый — это ровно та же испорченная
/// настройка. Раскрытый список ведёт себя как раньше: там колесо листает пункты, потому что человек
/// сам его открыл и смотрит именно на него.
///
/// Событие не просто гасится, а передаётся выше: иначе прокрутка панели «залипала» бы каждый раз,
/// когда курсор оказывается над списком. Родителю отправляется тот же поворот колеса, и внешний
/// ScrollViewer листает форму дальше, как будто списка под курсором нет.
///
/// ПОЧЕМУ ОБРАБОТЧИК ВИСИТ НА ОКНЕ, А НЕ НА ComboBox. Так и было сделано сначала — и не работало:
/// жалоба вернулась на живой сборке. PreviewMouseWheel — туннелирующее событие, а внутри одного
/// элемента WPF вызывает обработчики классов в порядке от базового к производному. Штатное
/// перещёлкивание пункта сидит в ComboBox.OnPreviewMouseWheel, куда ведёт класс-обработчик,
/// зарегистрированный ещё на UIElement, — и он отрабатывает РАНЬШЕ любого обработчика,
/// зарегистрированного на typeof(ComboBox). К моменту нашей проверки пункт уже переключён, а
/// событие помечено обработанным. Экземплярный обработчик на самом списке не помог бы по той же
/// причине: обработчики классов всегда идут перед экземплярными.
///
/// Окно — корень туннеля, оно получает событие первым и заведомо раньше списка. Отсюда и проверка
/// «под курсором закрытый ComboBox?» по визуальному дереву от OriginalSource: когда список
/// раскрыт, его пункты живут в отдельном дереве Popup, подъём до ComboBox не доходит, и колесо
/// внутри раскрытого списка работает штатно само собой.</summary>
public static class ComboBoxWheelGuard
{
    /// <summary>Включить на всё приложение. Вызывается из App.OnStartup до создания окон.</summary>
    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.OriginalSource is not DependencyObject source) return;
        if (FindClosedComboBox(source) is not { } combo) return;

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

    /// <summary>Закрытый ComboBox над курсором, или null. Поднимаемся по визуальному дереву:
    /// курсор почти всегда стоит не на самом списке, а на его внутренностях — тексте, стрелке,
    /// рамке.
    ///
    /// Шаг вверх разный для разных узлов не для красоты: OriginalSource бывает и текстовым
    /// элементом (Run внутри TextBlock), а VisualTreeHelper.GetParent на таком бросает
    /// исключение — уронили бы приложение ровно там, где чинили порчу настройки.</summary>
    private static ComboBox? FindClosedComboBox(DependencyObject source)
    {
        for (var node = source; node is not null;)
        {
            if (node is ComboBox combo)
                return combo.IsDropDownOpen ? null : combo;

            node = node switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
                FrameworkContentElement content => content.Parent,
                _ => null,
            };
        }
        return null;
    }
}
