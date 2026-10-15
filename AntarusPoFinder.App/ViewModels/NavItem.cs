using CommunityToolkit.Mvvm.ComponentModel;

namespace AntarusPoFinder.App.ViewModels;

/// <summary>В каком блоке бокового меню живёт пункт. Доступ ролей от этого не зависит вовсе (см.
/// RolesConfig.RoleAccess) — только место кнопки.</summary>
public enum NavSection
{
    /// <summary>Основной список сверху: то, ради чего программу открывают каждый день.</summary>
    Main,

    /// <summary>Свёрнутая секция «ДЛЯ НАЛАДЧИКА»: параметры ПЧ/УПП, наклейки, паспорта, модерация
    /// прошивок. Отдельная секция для того, что нужно наладчику, но используется не каждый день.
    /// До этого всё это было раскидано: часть висела в основном меню наравне с Поиском, часть лежала
    /// в «ДОПОЛНИТЕЛЬНО» вперемешку с настройками и тикетами, где наклейкам и паспортам совсем не
    /// место.</summary>
    Setup,

    /// <summary>Свёрнутая секция «ДЛЯ ПРОГРАММИСТА» — ровно та же мысль, что и Setup, но про другую
    /// работу: загрузка ПО и баги прошивок, по которым программист и правит.
    ///
    /// Просьба Ильи (бета 2.0): «вкладку тикетов для программиста сделай отображаемой слева как у
    /// наладчика есть параметры ПЧ упп наклейки и та». До этого баги прошивок жили второй пилюлей
    /// внутри страницы «Тикеты», в «Дополнительно», — то есть в двух кликах и не на виду у того, кому
    /// они адресованы.</summary>
    Dev,

    /// <summary>Свёрнутая секция «ДОПОЛНИТЕЛЬНО»: Настройки, Хранилище, Тикеты — «заглянуть раз в
    /// месяц».</summary>
    More,
}

public partial class NavItem : ObservableObject
{
    public string PageId { get; }
    public string Label { get; }

    /// <summary>В каком блоке сайдбара рисуется кнопка — см. <see cref="NavSection"/>. Три
    /// ItemsControl'а в MainWindow.xaml привязаны к ОДНОЙ коллекции NavItems и разбираются по
    /// ShowIn*-свойствам ниже.</summary>
    public NavSection Section { get; }

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isActive;
    /// <summary>Pending-count badge, e.g. unmoderated firmware versions on "Модерация прошивок" — 0 hides it.</summary>
    [ObservableProperty] private int _badgeCount;

    public NavItem(string pageId, string label, NavSection section = NavSection.Main)
    {
        PageId = pageId;
        Label = label;
        Section = section;
    }

    public bool ShowInMainList => IsVisible && Section == NavSection.Main;
    public bool ShowInSetupList => IsVisible && Section == NavSection.Setup;
    public bool ShowInDevList => IsVisible && Section == NavSection.Dev;
    public bool ShowInCompactList => IsVisible && Section == NavSection.More;

    partial void OnIsVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInMainList));
        OnPropertyChanged(nameof(ShowInSetupList));
        OnPropertyChanged(nameof(ShowInDevList));
        OnPropertyChanged(nameof(ShowInCompactList));
    }
}
