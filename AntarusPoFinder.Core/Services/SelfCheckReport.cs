using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AntarusPoFinder.Core.Services;

/// <summary>Текст, который человек пересылает как есть: в буфер обмена и в тикет. Собирается здесь,
/// а не в окне, по той же причине, по которой здесь же живёт разбор, — окно на сломанной машине
/// может и не открыться, а текст должен быть один и тот же везде и покрываться тестами.
///
/// Смысл тикета: коллеге не нужно ничего объяснять словами. В тексте уже есть версия, роль, как
/// подключён диск, какое правило обновлений сработало и сколько записей ссылается на чужую букву —
/// то есть ровно то, что иначе выясняется тремя раундами переписки.</summary>
public static class SelfCheckReport
{
    /// <summary>Полный отчёт — кнопка «Скопировать».</summary>
    public static string BuildReport(SelfCheckFacts f, IEnumerable<SelfCheckFinding> findings, System.DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Проверка компьютера — {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.Append(DescribeEnvironment(f));
        sb.AppendLine();
        foreach (var x in findings)
        {
            sb.AppendLine($"{SelfCheckAnalyzer.SeverityLabel(x.Severity)} {x.Title}");
            if (x.Target.Length > 0) sb.AppendLine($"    адрес: {x.Target}");
            sb.AppendLine($"    {x.Reason}");
            if (x.Fix.Length > 0) sb.AppendLine($"    что делать: {x.Fix}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Текст готового тикета: сначала коротко «что не работает и что делать», потом
    /// обстановка, потом полный отчёт. Порядок именно такой — тот, кто откроет тикет, должен понять
    /// суть из первых пяти строк, а подробности пусть лежат ниже.</summary>
    public static string BuildTicketText(SelfCheckFacts f, IReadOnlyList<SelfCheckFinding> findings, System.DateTime now)
    {
        var problems = findings.Where(x => x.Severity == SelfCheckSeverity.Problem).ToList();
        var sb = new StringBuilder();

        sb.AppendLine(problems.Count > 0
            ? "Проверка компьютера нашла проблемы."
            : "Проверка компьютера: явных проблем не найдено, отчёт приложен.");
        sb.AppendLine();

        foreach (var p in problems)
        {
            sb.AppendLine($"— {p.Title}: {p.Reason}");
            if (p.Fix.Length > 0) sb.AppendLine($"  Что делать: {p.Fix}");
            sb.AppendLine();
        }

        sb.AppendLine("Обстановка:");
        sb.Append(DescribeEnvironment(f));
        sb.AppendLine();
        sb.AppendLine("Полная проверка:");
        foreach (var x in findings)
        {
            sb.AppendLine($"{SelfCheckAnalyzer.SeverityLabel(x.Severity)} {x.Title}");
            if (x.Target.Length > 0) sb.AppendLine($"    адрес: {x.Target}");
            sb.AppendLine($"    {x.Reason}");
            if (x.Fix.Length > 0) sb.AppendLine($"    что делать: {x.Fix}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Факты о машине в том виде, в каком их читают глазами. Отдельно от списка проверок:
    /// разбор может ошибиться в выводе, а исходные факты — это то, по чему вывод перепроверяют.</summary>
    public static string DescribeEnvironment(SelfCheckFacts f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Программа: Antarus ПО Finder {f.AppVersion}");
        sb.AppendLine($"Компьютер: {f.MachineName}, пользователь Windows: {f.WindowsUser}, в программе: {f.AppUser}, роль: {f.RoleLabel}");
        sb.AppendLine($"Запуск от имени администратора: {(f.Elevated ? "да" : "нет")}");

        var disk = f.RootPath.Length == 0 ? "не задан" : f.RootPath;
        if (f.RootPath.Length > 0)
        {
            disk += f.RootExists ? (f.RootReadable ? " — доступен" : " — есть, но не читается") : " — НЕ НАЙДЕН";
            if (f.RootUnc.Length > 0) disk += $"; за буквой диска {f.RootUnc}";
            if (f.RootMappedInSession is { } m) disk += $"; в сеансе Windows {m.Letter} подключён к {m.RemotePath}";
            if (f.RootError.Length > 0) disk += $"; ошибка: {f.RootError}";
        }
        sb.AppendLine($"Рабочий диск: {disk}");
        sb.AppendLine($"Второй диск: {(f.SecondDiskPath.Length == 0 ? "не задан" : f.SecondDiskPath + (f.SecondDiskExists ? " — доступен" : " — НЕ НАЙДЕН"))}");
        sb.AppendLine($"Офисная сеть: {f.OfficeNetworkReachable switch { true => "доступна", false => "недоступна ни одним способом", _ => "не проверялась" }}");

        var a = f.StoredPaths;
        sb.AppendLine(f.StoredPathsChecked
            ? $"Пути в базе: всего {a.Records}, с чужого корня {a.Foreign} ({StoredPathAudit.DescribeRoots(a.ForeignRoots)}); приводятся к своему диску {a.Rescued}, не приводятся {a.Broken}"
              + (a.BrokenSample.Length > 0 ? $"; пример непривязываемого: {a.BrokenSample}" : "")
            : "Пути в базе: не проверялись");

        sb.AppendLine($"Обновления: правило «{SelfCheckAnalyzer.UpdateRuleText(f)}»");
        sb.AppendLine($"    папка этой машины: {Or(f.UpdatePathLocal)}");
        sb.AppendLine($"    общая папка: {Or(f.UpdatePathShared)}");
        sb.AppendLine($"    получилось: {Or(f.UpdatePathEffective)} — {(f.UpdatePathEffective.Length == 0 ? "папка не используется" : f.UpdateFolderReachable ? "доступна" : "НЕДОСТУПНА" + Suffix(f.UpdateFolderProblem))}");
        sb.AppendLine($"    GitHub: {(f.GitHubReachable ? "доступен" : "НЕДОСТУПЕН" + Suffix(f.GitHubProblem))}");
        sb.AppendLine($"    автоустановка: {(f.UpdateAutoInstall ? "включена" : "ВЫКЛЮЧЕНА")}");
        sb.AppendLine($"    папка программы: {Or(f.InstallDir)} — {(f.InstallDir.Length == 0 ? "не определена" : f.InstallDirWritable ? "запись есть" : "ЗАПИСЬ НЕДОСТУПНА" + Suffix(f.InstallDirWriteError) + (f.InstallUnderProgramFiles ? "; под Program Files" : ""))}");
        if (f.LastUpdateFailure.Length > 0) sb.AppendLine($"    прошлая неудача: {f.LastUpdateFailure}");

        sb.AppendLine($"Обмен: {(f.SyncTransport == "server" ? "служба обмена" : "общая папка на диске")} — {Or(f.SyncTarget)}, {(f.SyncReachable ? "доступен" : "НЕДОСТУПЕН")}");
        sb.AppendLine($"Хранилище: {(!f.StorageEnabled ? "выключено" : !f.StorageHasAddress ? "включено, адрес не задан" : !f.StorageHasCredentials ? $"включено ({f.StorageTarget}), ключи не загружены" : $"настроено ({f.StorageTarget})")}");
        return sb.ToString();
    }

    private static string Or(string value) => value.Length == 0 ? "не задана" : value;

    private static string Suffix(string problem) => problem.Length == 0 ? "" : $" ({problem})";
}
