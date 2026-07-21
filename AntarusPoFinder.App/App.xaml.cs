using System;
using System.IO;
using System.Threading;
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

    // ── Single instance ──────────────────────────────────────────────────────
    // Root cause of "обновление ставится, а после перезапуска всё равно старая версия": nothing
    // ever stopped the app being launched more than once — a double-click of the desktop shortcut
    // that looked like it did nothing (because the previous launch was already sitting minimized in
    // the tray) just spawned another full process. AppUpdateService.InstallAndRestart only waits for the
    // ONE process that triggered the update to exit before moving the staged .exe into place — if a
    // second copy was still alive, it still had the same self-contained single-file exe open/mapped,
    // so Move-Item failed (silently, until the earlier "самообновление молча падало" fix) and the
    // update never actually landed. A named Mutex makes sure only one instance is ever running: a
    // second launch just asks the first one to come to the foreground (see ShowRequestEventName) and
    // exits immediately instead of starting a second process.
    private const string InstanceMutexName = "AntarusPoFinder_SingleInstance_5B2E1A";
    private const string ShowRequestEventName = "AntarusPoFinder_ShowRequest_5B2E1A";
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using var showRequest = EventWaitHandle.OpenExisting(ShowRequestEventName);
                showRequest.Set();
            }
            catch { /* first instance hasn't created the event yet (very first startup race) — nothing to signal, not worth retrying for a launcher click */ }
            Shutdown();
            return;
        }

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

        // Set BEFORE Show() so the window never flashes visible-then-minimizes — see
        // ConfigService.AppStartMinimized (per-machine, applies regardless of whether this launch
        // came from a double-click or the Windows autostart Run-key entry, see AutostartService).
        // If "закрытие окна -> трей" is also on, MainWindow's own StateChanged handler (already
        // subscribed inside the constructor above, i.e. before Show() runs) hides it straight to
        // the tray the moment Show() realizes this Minimized state — no separate code path needed.
        if (services.Cfg.AppStartMinimized())
            window.WindowState = WindowState.Minimized;

        window.Show();

        StartShowRequestListener(window);
    }

    /// <summary>Background thread that waits on the named event a second (blocked) launch signals
    /// in OnStartup above, and brings this — the only — instance to the foreground in response.
    /// AutoReset so it keeps listening across repeated shortcut clicks for the lifetime of the app.</summary>
    private static void StartShowRequestListener(MainWindow window)
    {
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                showRequest.WaitOne();
                window.Dispatcher.Invoke(window.RestoreFromTray);
            }
        })
        { IsBackground = true };
        thread.Start();
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
                CreatedBy = _services.CurrentUserName,
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
