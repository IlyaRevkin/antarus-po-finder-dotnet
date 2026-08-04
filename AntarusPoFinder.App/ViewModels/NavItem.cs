using CommunityToolkit.Mvvm.ComponentModel;

namespace AntarusPoFinder.App.ViewModels;

/// <summary>В каком блоке бокового меню живёт пункт. Доступ ролей от этого не зависит вовсе (см.
/// RolesConfig.RoleAccess) — только место кнопки.</summary>
public enum NavSection
{
    /// <summary>Основной список сверху: то, ради чего программу открывают каждый день.</summary>
    Main,

    /// <summary>Свёрнутая секция «ДЛЯ НАЛАДЧИКА»: параметры ПЧ/УПП, наклейки, паспорта, модерация
    /// прошивок. Просьба Ильи дословно: «давай сделаем доп меню какое-нибудь которое типа для
    /// наладчика но не часто используется, там будет параметры ПЧ/УПП, наклейки, паспорта как раз и
    /// модерацию мб туда вынести». До этого они были раскиданы: часть висела в основном меню наравне
    /// с Поиском, часть лежала в «ДОПОЛНИТЕЛЬНО» вперемешку с настройками и тикетами, где, по его же
    /// словам, «наклейки и паспорта в том блоке не в тему».</summary>
    Setup,

    /// <summary>Свёрнутая секция «ДОПОЛНИТЕЛЬНО»: Настройки, Сетевые диски, Тикеты — «заглянуть раз в
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

    /// <summary>"Тикеты"/"Сетевые диски" — used rarely (checked once in a while, not the everyday
    /// pages), so they render in a small secondary strip near the bottom of the sidebar instead of
    /// alongside Поиск/Осмотр/Загрузка ПО etc. in the main list — same role access as before (see
    /// RolesConfig.RoleAccess), this only changes WHERE a role that can see the page finds its
    /// button.</summary>
    public bool IsCompact => Section == NavSection.More;

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
    public bool ShowInCompactList => IsVisible && Section == NavSection.More;

    partial void OnIsVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInMainList));
        OnPropertyChanged(nameof(ShowInSetupList));
        OnPropertyChanged(nameof(ShowInCompactList));
    }
}
