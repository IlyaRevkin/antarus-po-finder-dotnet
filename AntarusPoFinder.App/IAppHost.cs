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

    /// <summary>Called right after the root disk path is changed (NetworkSyncView.SaveRoot_Click):
    /// creates the folder structure on the new path if it's missing AND refreshes the footer disk
    /// indicator immediately. Without this the "Диск: …" footer only recomputed on the periodic
    /// RunSync tick (every sync_interval_min, or never when that's 0), so saving a valid path left
    /// the footer contradicting the "Путь сохранён" toast for minutes, and a fresh machine's disk
    /// stayed empty until the first upload happened to create the tree.</summary>
    void OnRootPathChanged();

    /// <summary>Recomputes the footer disk indicator now — call after writing to the disk (e.g. a
    /// successful firmware upload) so the availability/file count isn't stale until the next
    /// periodic RunSync tick.</summary>
    void RefreshDiskStatus();

    /// <summary>Отправляет общий справочник (иерархия, производители ПЧ/УПП, теги, расширения) на
    /// сетевой диск сразу после того, как администратор его изменил — см.
    /// MainWindowViewModel.PushCatalogChange. what — что именно поменялось, попадает в статус-строку.</summary>
    void PushCatalogChange(string what);
}
