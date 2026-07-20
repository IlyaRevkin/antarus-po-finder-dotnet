using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(ConfigService.AppData, "crash.log");

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
        var window = new MainWindow(services);
        MainWindow = window;
        window.Show();
    }

    private static void LogAndShow(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.AppData);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch { /* logging is best-effort */ }

        MessageBox.Show(
            $"Произошла ошибка:\n\n{ex.Message}\n\nПодробности записаны в:\n{CrashLogPath}",
            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
