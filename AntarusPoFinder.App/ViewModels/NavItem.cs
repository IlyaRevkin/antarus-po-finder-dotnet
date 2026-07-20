using CommunityToolkit.Mvvm.ComponentModel;

namespace AntarusPoFinder.App.ViewModels;

public partial class NavItem : ObservableObject
{
    public string PageId { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isActive;
    /// <summary>Pending-count badge, e.g. unmoderated firmware versions on "Модерация тегов" — 0 hides it.</summary>
    [ObservableProperty] private int _badgeCount;

    public NavItem(string pageId, string label)
    {
        PageId = pageId;
        Label = label;
    }
}
