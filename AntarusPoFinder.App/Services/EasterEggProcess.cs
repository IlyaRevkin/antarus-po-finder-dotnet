using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Пасхалка живёт ОТДЕЛЬНЫМ процессом того же exe, запущенным с ключом
/// <see cref="Flag"/>. Причин две, и обе про честность.
///
/// Первая: окно намеренно не закрывается крестиком и висит поверх всех. Это ровно повадки
/// блокировщика экрана, поэтому у человека обязан быть путь наружу, который не зависит от того,
/// откликается ли программа. Отдельный процесс снимается в диспетчере задач — и Finder при этом
/// остаётся жив.
///
/// Вторая: в диспетчере задач приложение подписано заголовком своего окна. Заголовок здесь —
/// «Пасхалка», так что в списке видно ровно то, что человек и ожидает увидеть, а не второй
/// «Antarus ПО Finder», который страшно снимать.
///
/// ⚠️ Копировать exe под именем «Пасхалка.exe» СОЗНАТЕЛЬНО не стали: копия исполняемого файла во
/// временную папку — поведение вредоноса и повод для антивируса. Здесь тот же файл, тот же путь,
/// та же подпись — меняется только заголовок окна.</summary>
public static class EasterEggProcess
{
    public const string Flag = "--easter-egg";

    /// <summary>Заголовок окна. Он же — имя, под которым процесс виден в диспетчере задач.</summary>
    public const string WindowTitle = "Пасхалка";

    /// <summary>Мы — тот самый отдельный процесс? Тогда показать ленту и завершиться, минуя
    /// главное окно и мьютекс единственного экземпляра (иначе второй процесс просто закрылся бы,
    /// разбудив первый).</summary>
    public static bool TryRunAsEasterEgg(string[] args, Func<string, bool> show)
    {
        if (args is null || args.Length < 2) return false;
        if (!string.Equals(args[0], Flag, StringComparison.OrdinalIgnoreCase)) return false;

        var root = args[1];
        try { show(root); }
        catch (Exception) { /* пасхалка тихая: не открылось — просто закрываемся */ }
        return true;
    }

    /// <summary>Запустить отдельным процессом. Возвращает false, если запустить не удалось —
    /// вызывающий тогда покажет ленту по-старому, окном внутри программы.</summary>
    public static bool TryLaunch(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add(Flag);
            psi.ArgumentList.Add(root);
            return Process.Start(psi) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
