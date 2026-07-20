using CommunityToolkit.Mvvm.ComponentModel;

namespace AntarusPoFinder.App.ViewModels;

public partial class NavItem : ObservableObject
{
    public string PageId { get; }
    public string Label { get; }

    /// <summary>"Тикеты"/"Сетевые диски" — used rarely (checked once in a while, not the everyday
    /// pages), so they render in a small secondary strip near the bottom of the sidebar instead of
    /// alongside Поиск/Осмотр/Загрузка ПО etc. in the main list — same role access as before (see
    /// RolesConfig.RoleAccess), this only changes WHERE a role that can see the page finds its
    /// button. See MainWindow.xaml: two ItemsControls bound to the same NavItems collection, split
    /// by ShowInMainList/ShowInCompactList below.</summary>
    public bool IsCompact { get; }

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isActive;
    /// <summary>Pending-count badge, e.g. unmoderated firmware versions on "Модерация тегов" — 0 hides it.</summary>
    [ObservableProperty] private int _badgeCount;

    public NavItem(string pageId, string label, bool isCompact = false)
    {
        PageId = pageId;
        Label = label;
        IsCompact = isCompact;
    }

    public bool ShowInMainList => IsVisible && !IsCompact;
    public bool ShowInCompactList => IsVisible && IsCompact;

    partial void OnIsVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInMainList));
        OnPropertyChanged(nameof(ShowInCompactList));
    }
}
