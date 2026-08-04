using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Где лежат шаблоны паспортов и как чистить название для имени файла. Паспорт больше НЕ
/// отдельная сущность с записями в базе: это просто общая папка с файлами-шаблонами (как наклейки),
/// а «Сформировать паспорт» подставляет в копию выбранного шаблона название шкафа и печатает —
/// ничего никуда не сохраняя (см. Views.PassportPrintWindow).</summary>
public static class PassportService
{
    /// <summary>Имя файла паспорта: название, как его ввёл оператор, с заменой символов, которые
    /// файловая система не примет. Пустое/полностью «неудобное» название — «Паспорт», чтобы имя
    /// всё равно получилось.</summary>
    public static string FolderName(string name)
    {
        var cleaned = new string((name ?? "").Trim()
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
        // Точка в конце имени папки Windows молча отбрасывает — уберём сами.
        cleaned = cleaned.TrimEnd('.', ' ');
        return cleaned.Length == 0 ? HierarchyFolders.Passports : cleaned;
    }

    /// <summary>Общая папка с шаблонами паспортов. По умолчанию — <c>&lt;диск&gt;\Конфиг\Паспорта</c>,
    /// рядом с <c>Конфиг\Наклейки</c>: она уже доступна всем машинам и раздавать её отдельно не
    /// нужно.</summary>
    public const string DefaultTemplatesSubfolder = @"Конфиг\Паспорта";

    /// <summary>Куда складывать и откуда читать шаблоны паспортов. <paramref name="configured"/> — то,
    /// что задано в настройках (см. SharedFolderPath). null — корень диска не настроен.</summary>
    public static string? TemplatesFolder(string? diskRoot, string? configured) =>
        SharedFolderPath.Resolve(diskRoot, configured, DefaultTemplatesSubfolder);
}
