using System.Windows;
using System.Windows.Controls;

namespace AntarusPoFinder.App.Views;

/// <summary>ПАНЕЛЬ НАСТРОЕК, ПОКАЗАННАЯ ОТДЕЛЬНЫМ ОКНОМ.
///
/// Жалоба Ильи (бета 2.0): «пелюль сверху стало уже слишком много, ту же вкладку теги можно сделать
/// кнопкой в прошивка или иерархии где логичней и то что на той вкладке открывать в отдельном окне».
///
/// Главное решение здесь — панель НЕ копируется и не переписывается: настоящий элемент вынимается
/// из настроек, показывается в окне и возвращается на место при закрытии. Поэтому весь код вкладки
/// (а у тегов это переименование по двойному щелчку, фильтр, слова-исключения) продолжает работать
/// как работал, без единой правки и без второго экземпляра, который неизбежно разъехался бы с
/// первым.
///
/// Возврат делается и при закрытии крестиком, и при Esc — иначе панель осталась бы висеть в
/// закрытом окне, а на вкладке настроек появилась бы пустота.</summary>
public partial class DetachedPanelWindow : Window
{
    private readonly Panel _origin;
    private readonly UIElement _panel;
    private readonly int _originIndex;
    private readonly Visibility _originVisibility;
    private bool _returned;

    private DetachedPanelWindow(string title, Panel origin, UIElement panel)
    {
        InitializeComponent();
        Title = title;
        _origin = origin;
        _panel = panel;
        _originIndex = origin.Children.IndexOf(panel);
        _originVisibility = panel.Visibility;

        origin.Children.Remove(panel);
        panel.Visibility = Visibility.Visible;
        Host.Content = panel;

        Closed += (_, _) => ReturnPanel();
    }

    /// <summary>Вернуть панель ровно туда, откуда взяли: на то же место в том же контейнере и с той
    /// же видимостью. Индекс важен — порядок карточек в настройках осмысленный, а не случайный.</summary>
    private void ReturnPanel()
    {
        if (_returned) return;
        _returned = true;

        Host.Content = null;
        if (_originIndex >= 0 && _originIndex <= _origin.Children.Count)
            _origin.Children.Insert(_originIndex, _panel);
        else
            _origin.Children.Add(_panel);
        _panel.Visibility = _originVisibility;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Показать панель окном. Модально: это настройки, и менять их в двух местах
    /// одновременно незачем.</summary>
    public static void Show(Window? owner, string title, Panel origin, UIElement panel)
    {
        var window = new DetachedPanelWindow(title, origin, panel) { Owner = owner };
        window.ShowDialog();
    }
}
