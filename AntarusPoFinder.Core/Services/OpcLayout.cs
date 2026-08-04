using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Раскладка ОПЦ — нестандартных прошивок под конкретный шкаф (docs/hierarchy-rework-plan.md,
/// этап 5).
///
/// <b>Как было.</b> «ОПЦ» — одна папка на ПОДТИП, рядом с папками контроллеров, а внутри — папки,
/// названные строкой версии:
/// <code>ПО\&lt;тип&gt;\&lt;подтип&gt;\ОПЦ\2.1.0042.0007.20260422_1348</code>
/// Отсюда два врождённых изъяна. Первый: по пути нельзя понять, к какому контроллеру относится
/// прошивка, поэтому досмотр диска ВЫВОДИЛ контроллер из hw-числа версии, а при неоднозначном hw
/// молча пропускал версию — жалоба «ОПЦ-версия коллеги у меня не появилась» родом отсюда. Второй:
/// человек, открывший диск, видит список номеров версий и не может найти шкаф по номеру заявки или
/// заводскому номеру, хотя ОПЦ заводится ровно под конкретный шкаф.
///
/// <b>Как стало.</b> «ОПЦ» — папка ВНУТРИ контроллера, а её подпапки называются номером заявки и/или
/// заводским SN:
/// <code>ПО\&lt;тип&gt;\&lt;подтип&gt;\&lt;контроллер&gt;\ОПЦ\01312_SN00042</code>
/// Контроллер читается из пути (гадание по hw больше не нужно), а номер версии — из CHANGELOG.md,
/// который мигратор обязан дописать ДО переименования (иначе восстановить его будет неоткуда).
///
/// <b>Цена.</b> Это единственный переезд, который меняет <c>disk_path</c>, а он у совпавшей записи при
/// импорте общего конфига не обновляется никогда (Database.ConfigExchange.cs). Поэтому у коллег путь
/// устареет, и чинится это не синхронизацией, а локальным проходом
/// <see cref="FindMigratedFolder"/> на каждой машине (см. HierarchyService.RepairOpcDiskPaths).</summary>
public static class OpcLayout
{
    /// <summary>Имя папки ОПЦ-версии по её «адресу шкафа». Правило простое и обратимое:
    /// <list type="bullet">
    /// <item><description>есть и заявка, и SN → <c>01312_SN00042</c> (заявка первой: по ней шкаф ищут
    /// чаще, а SN дописывают потом, когда шкаф уже собран);</description></item>
    /// <item><description>только заявка → <c>01312</c>, только SN → <c>SN00042</c>;</description></item>
    /// <item><description>нет ни того, ни другого → строка версии, как было раньше. Такие ОПЦ-версии
    /// на диске есть (заводили без заявки), и оставить их без имени нельзя.</description></item>
    /// </list>
    /// Те же две метки, что и в имени файла (см. FirmwareNaming.BuildFirmwareFilename/ParseOpcMarkers) —
    /// специально, чтобы имя папки и имя файла читались одинаково.</summary>
    public static string FolderName(string? requestNum, string? cabinetSn, string versionRaw)
    {
        var request = (requestNum ?? "").Trim();
        var sn = (cabinetSn ?? "").Trim();
        if (request.Length > 0 && sn.Length > 0) return $"{request}_SN{sn}";
        if (request.Length > 0) return request;
        if (sn.Length > 0) return $"SN{sn}";
        return versionRaw;
    }

    /// <summary>Обратный разбор имени папки: «01312_SN00042» → («01312», «00042»). Нужен досмотру
    /// диска, который видит папку, заведённую на другой машине, и должен восстановить заявку и SN
    /// (в CHANGELOG.md их нет). Имя, не похожее ни на заявку, ни на SN (например, строка версии у
    /// старых записей) — обе строки пустые, и это не ошибка.</summary>
    public static (string RequestNum, string CabinetSn) ParseFolderName(string? folderName)
    {
        var name = (folderName ?? "").Trim();
        if (name.Length == 0 || FwVersionNumber.Parse(name) is not null) return ("", "");

        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var request = "";
        var sn = "";
        foreach (var part in parts)
        {
            if (part.StartsWith("SN", StringComparison.OrdinalIgnoreCase) && part.Length > 2
                && part[2..].All(char.IsDigit))
                sn = part[2..];
            else if (part.All(char.IsDigit) && request.Length == 0)
                request = part;
        }
        return (request, sn);
    }

    /// <summary>Папка «ОПЦ» контроллера — новое место. <paramref name="controllerFolder"/> — обычная
    /// папка контроллера (HierarchyService.ControllerFolder без isOpc).</summary>
    public static string ControllerOpcFolder(string controllerFolder) =>
        Path.Combine(controllerFolder, HierarchyFolders.Opc);

    /// <summary>Старое место — «ОПЦ» на уровне типа/подтипа. Остаётся навсегда как читаемая раскладка:
    /// удалять её нельзя, пока хоть одна машина в конторе не обновилась.</summary>
    public static string SubtypeOpcFolder(string groupSubFolder) =>
        Path.Combine(groupSubFolder, HierarchyFolders.Opc);

    /// <summary>Номер версии ОПЦ-папки, имя которой номером версии больше не является. Порядок
    /// источников: имя папки (старая раскладка — там он и есть), затем заголовок CHANGELOG.md, затем
    /// имя файла прошивки внутри (имя файла строится от version.Raw, см. FirmwareNaming). null — ни
    /// один источник не дал разбираемого номера: такую папку досмотр диска пропускает, а не заводит
    /// запись с выдуманной версией.</summary>
    public static FwVersionNumber? ResolveVersion(string versionDir)
    {
        if (FwVersionNumber.Parse(Path.GetFileName(versionDir.TrimEnd(Path.DirectorySeparatorChar))) is { } fromName)
            return fromName;

        if (ChangelogFile.TryReadVersionRaw(versionDir) is { } fromChangelog
            && FwVersionNumber.Parse(fromChangelog) is { } parsed)
            return parsed;

        foreach (var folder in VersionLayout.FirmwareFolders(versionDir))
        {
            string[] files;
            try { files = Directory.GetFiles(folder); }
            catch (Exception) { continue; }
            foreach (var file in files)
            {
                if (VersionLayout.IsServiceFile(file)) continue;
                // Имя файла = version.Raw + необязательные метки «_(заявка)»/«_SN…»: отрезаем метки и
                // пробуем разобрать остаток. Первый разобравшийся и есть номер версии.
                var stem = Path.GetFileNameWithoutExtension(file);
                var cut = stem.IndexOf("_(", StringComparison.Ordinal);
                if (cut < 0) cut = stem.IndexOf("_SN", StringComparison.OrdinalIgnoreCase);
                var candidate = cut > 0 ? stem[..cut] : stem;
                if (FwVersionNumber.Parse(candidate) is { } fromFile) return fromFile;
            }
        }
        return null;
    }

    /// <summary>Куда переехала ОПЦ-версия, у которой <c>disk_path</c> устарел после этапа 5 — локальная
    /// починка пути на каждой машине (см. класс-док). Ищем в «ОПЦ» указанного контроллера папку, чей
    /// номер версии совпадает с искомым. null — не нашли: путь не трогаем, выдумывать его нельзя.</summary>
    public static string? FindMigratedFolder(string controllerFolder, string versionRaw)
    {
        var opc = ControllerOpcFolder(controllerFolder);
        IEnumerable<string> dirs;
        try
        {
            if (!Directory.Exists(opc)) return null;
            dirs = Directory.EnumerateDirectories(opc).ToList();
        }
        catch (Exception) { return null; }

        foreach (var dir in dirs)
            if (ResolveVersion(dir)?.Raw is { } raw
                && string.Equals(raw, versionRaw, StringComparison.OrdinalIgnoreCase))
                return dir;
        return null;
    }
}
