using System;
using Microsoft.Win32;

namespace AntarusPoFinder.App.Services;

/// <summary>Настройки → Общие → «Автозапуск с Windows» — a per-user HKCU Run-key entry, no admin
/// rights needed (matches the per-user MSI install / admin-less self-update elsewhere in this app).
/// Deliberately NOT mirrored into ConfigService/the sqlite settings table: the registry key itself is
/// the single source of truth, so toggling autostart off in Task Manager's Startup tab (or any other
/// tool) is reflected here immediately, with nothing left to fall out of sync.</summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AntarusPoFinder";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Points the Run-key entry at THIS process's current exe path — safe to call again
    /// after every app start (e.g. right after Настройки loads) since a self-update replacing the
    /// exe in place at the same path never invalidates it, but a self-update that also moved the
    /// install location would otherwise leave a stale path behind.</summary>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        key.SetValue(ValueName, $"\"{exePath}\"");
    }
}
