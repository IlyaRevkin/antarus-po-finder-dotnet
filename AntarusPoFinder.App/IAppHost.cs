using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App;

/// <summary>WPF equivalent of the Python pages' <c>self._mw</c> back-reference — cross-cutting
/// operations a page needs to trigger on the main window/shell without owning the shell itself.</summary>
public interface IAppHost
{
    /// <summary>category defaults to General so existing call sites keep working unchanged — pass
    /// an explicit category where it's known (see NotificationCategory) so the user's Настройки →
    /// Уведомления filter can actually apply to it. If the category is disabled, the message is
    /// fully suppressed: no status-bar flash, no history entry (see MainWindowViewModel.ShowStatus).</summary>
    void ShowStatus(string message, int ms = 4000, NotificationCategory category = NotificationCategory.General);
    void SetSyncIntervalMinutes(int minutes);
    void ReloadSidebarApps();
    void SwitchRole(string role);
    void Navigate(string pageId);

    /// <summary>Restarts the administrator auto-push timer against the current
    /// config_push_interval_min setting — called from NetworkSyncView right after it's saved, so the
    /// change takes effect immediately instead of waiting for the next role switch.</summary>
    void RefreshConfigSync();
}
