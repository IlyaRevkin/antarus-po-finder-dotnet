using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>СЛЕПОК ДИСКА ИЗ КОМАНДНОЙ СТРОКИ — чтобы его можно было снимать по расписанию.
///
/// <b>Зачем, если есть кнопка.</b> Кнопку надо нажать, а человек, который её нажимает, — тот самый
/// человек, которого мы стараемся не трогать. Слепок полезен ровно тогда, когда он СВЕЖИЙ: раз в
/// сутки ночью, планировщиком Windows, с машины, у которой диск подключён. Кнопка остаётся для
/// разового «посмотри прямо сейчас».
///
/// <b>Почему прямо в exe программы, а не отдельной утилитой.</b> Слепок — это диск ПЛЮС база
/// Finder, и обе половины живут здесь: раскладку знает <see cref="VersionLayout"/>, профиль и путь
/// к диску — настройки программы. Отдельная утилита либо повторила бы это всё, либо разошлась бы с
/// оригиналом; так уже было с двумя списками расширений панели.
///
/// <b>Окна не показывает.</b> Запуск уходит в <see cref="App.OnStartup"/> до создания главного окна
/// и до мьютекса единственного экземпляра: прогон по расписанию не должен ни будить окно
/// работающей программы, ни быть ею отвергнутым (правило проекта — службы работают молча).</summary>
public static class DiskSnapshotCli
{
    /// <summary>Ключ запуска. Полное слово, а не буква: эту строку пишут один раз в задание
    /// планировщика и читают через год.</summary>
    public const string Switch = "--disk-snapshot";

    public static bool IsRequested(string[]? args) =>
        args is not null && args.Any(a => string.Equals(a?.Trim(), Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>Журнал прогонов. По расписанию запускается то, за чем никто не смотрит, и без следа
    /// «почему слепков нет» выяснялось бы гаданием (та же причина, что у backup.log).</summary>
    public static string LogPath => Path.Combine(ConfigService.AppData, "disk-snapshot.log");

    private const int LogKeepLines = 400;

    /// <summary>Снять слепок и отправить. Возвращает код возврата процесса: 0 — отправлено,
    /// 1 — не отправлено. Планировщик умеет показывать только это число, поэтому подробности
    /// уезжают в журнал и в консоль (если нас запустили из неё).</summary>
    public static int Run()
    {
        AttachToParentConsole();

        try
        {
            var db = new Database(ConfigService.DbPath);
            var cfg = new ConfigService(db);
            var root = cfg.RootPath();

            if (string.IsNullOrWhiteSpace(root))
            {
                Report("рабочий диск в настройках не задан — снимать нечего");
                return 1;
            }

            var result = DiskSnapshotService.SendToDropAsync(
                db, root, Environment.MachineName, AppUpdateService.CurrentVersionText,
                cfg.DiagnosticsDropUrl(), cfg.DiagnosticsDropCode(),
                new Progress<string>(Report)).GetAwaiter().GetResult();

            Report(result.Ok
                ? $"готово: {result.FileName}, {result.Bytes / 1024} КБ"
                : $"НЕ ОТПРАВЛЕНО: {result.Error}");
            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            // Падать некому: процесс запущен планировщиком, окно показывать некому и нечему.
            Report("сорвалось: " + ex.Message);
            return 1;
        }
    }

    private static void Report(string message)
    {
        try { Console.WriteLine(message); } catch { /* консоли нет — не беда */ }
        Log(message);
        // И в общий журнал приложения: disk-snapshot.log лежит на машине и читается только
        // вручную, а общий журнал уезжает сам. Ночной прогон по расписанию — ровно тот случай, за
        // которым никто не смотрит, и его след полезнее всего именно там.
        AppLog.Info(AppLog.AreaSnapshot, "по расписанию: " + message);
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.AppData);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);

            var lines = File.ReadAllLines(LogPath);
            if (lines.Length > LogKeepLines * 2)
                File.WriteAllLines(LogPath, lines[^LogKeepLines..], Encoding.UTF8);
        }
        catch { /* журнал, который мешает работать, хуже отсутствующего журнала */ }
    }

    /// <summary>Приложение собрано как оконное (WinExe), и своей консоли у него нет. Если его
    /// запустили ИЗ консоли руками, подключаемся к ней — иначе человек, проверяющий задание
    /// планировщика, не увидел бы ни строчки и решил бы, что ничего не произошло. Запуск по
    /// расписанию родительской консоли не имеет, вызов просто не удаётся — и это нормально.</summary>
    private static void AttachToParentConsole()
    {
        try { AttachConsole(AttachParentProcess); }
        catch (Exception) { /* консоли нет — весь вывод остаётся в журнале */ }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
