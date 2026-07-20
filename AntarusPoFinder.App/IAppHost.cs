namespace AntarusPoFinder.App;

/// <summary>WPF equivalent of the Python pages' <c>self._mw</c> back-reference — cross-cutting
/// operations a page needs to trigger on the main window/shell without owning the shell itself.</summary>
public interface IAppHost
{
    void ShowStatus(string message, int ms = 4000);
    void SetSyncIntervalMinutes(int minutes);
    void ReloadSidebarApps();
    void SwitchRole(string role);
    void Navigate(string pageId);

    /// <summary>Restarts the administrator auto-push timer against the current config_auto_push/
    /// config_push_interval_min settings — called from NetworkSyncView right after either is saved,
    /// so the change takes effect immediately instead of waiting for the next role switch.</summary>
    void RefreshConfigSync();
}
