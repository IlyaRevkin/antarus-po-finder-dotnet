using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(ConfigService.AppData, "crash.log");

    /// <summary>Set right after AppServices is constructed below — null until then, so a crash
    /// during the very earliest startup (before there's a Database/ConfigService to attach a ticket
    /// to) just falls back to crash.log only, same as before this feature existed.</summary>
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogAndShow(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogAndShow(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown error"));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogAndShow(args.Exception);
            args.SetObserved();
        };

        // Applies dark/light title-bar chrome to EVERY window in the app (MainWindow + all
        // dialogs) as soon as it loads, without needing per-window code — Window.SourceInitialized
        // isn't a RoutedEvent so it can't be class-handled; Loaded is close enough (HWND already
        // exists by then) and avoids touching all ~10 dialog code-behind files individually.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window w) DarkTitleBar.Apply(w, ThemeManager.Current == "dark");
            }));

        var services = new AppServices();
        _services = services;
        var window = new MainWindow(services);
        MainWindow = window;
        window.Show();
    }

    private void LogAndShow(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.AppData);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch { /* logging is best-effort */ }

        // crash.log stays the primary/always-on record (above) — this is an additional, more
        // visible channel: an open ticket the administrator sees on the Тикеты page without having
        // to go dig through a log file on a machine they may not even have remote access to.
        // Deliberately isolated in its own try/catch (see ReportCrashAsTicket) — a crash handler
        // must never itself throw, and the app/database can be in a genuinely broken state right
        // when this runs (that's *why* we're here), so a failure to file the ticket is silently
        // swallowed rather than surfaced.
        ReportCrashAsTicket(ex);

        MessageBox.Show(
            $"Произошла ошибка:\n\n{ex.Message}\n\nПодробности записаны в:\n{CrashLogPath}",
            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Best-effort: files an auto-generated ticket (CreatedByRole="system", distinguishing
    /// it from one a person typed in via TicketsView) for an unhandled exception, so crashes surface
    /// on the administrator's Тикеты page instead of only in a per-machine crash.log they'd have to
    /// know to go look at. CreatedBy is still the real Windows login (not a generic "system" user) —
    /// useful for the administrator to know who hit it, and lets that same person see it too under
    /// "свои тикеты" if they open Тикеты themselves.</summary>
    private void ReportCrashAsTicket(Exception ex)
    {
        if (_services is null) return; // crashed before AppServices existed — nothing to attach this to
        try
        {
            var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff");
            var ticket = new Ticket
            {
                Id = Guid.NewGuid().ToString(),
                Type = TicketType.Bug,
                Text = $"[Автоматический отчёт о сбое]\n{ex.GetType().FullName}: {ex.Message}\n\n{ex}",
                Status = TicketStatus.Open,
                CreatedBy = Environment.UserName,
                CreatedByRole = "system",
                CreatedAt = now,
                UpdatedAt = now,
            };
            _services.Db.InsertTicketIfMissing(ticket);

            var (filename, payload) = TicketSyncService.BuildCreateEvent(ticket);
            _services.Db.EnqueueTicketOutbox(filename, payload);

            var root = _services.Cfg.RootPath();
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                TicketSyncService.FlushOutbox(_services, root);
        }
        catch { /* best effort — crash.log above is still the primary record either way */ }
    }
}
