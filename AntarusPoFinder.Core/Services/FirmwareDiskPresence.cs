using System.Linq;
using System.Text.RegularExpressions;

namespace AntarusPoFinder.Core.Services;

/// <summary>Отвечает выдаче на один вопрос: «лежит ли эта версия на диске, даже если её папку
/// переименовали?». Точная папка версии (disk_path) может не найтись НЕ потому, что прошивку
/// удалили, а потому, что папку переименовали прямо на диске: откат дописывает суффикс «_ОТКАТАНО»,
/// правка hw переписывает номер в середине имени, перезалив меняет дату, а disk_path в базе остаётся
/// прежним, пока досмотр диска не сверит его заново. Файлы при этом никуда не делись — они в той же
/// папке контроллера, в соседней папке ТОЙ ЖЕ сборки. Прятать такую версию как «удалённую с диска»
/// нельзя: это ровно жалоба «прошивка есть на диске, а поиск пишет, что её нет, и прячет».</summary>
public static class FirmwareDiskPresence
{
    /// <summary>True, если файлы версии реально есть на диске: либо прямо в <paramref name="firmwareDir"/>,
    /// либо в соседней папке того же контроллера, которая опознаётся как ТА ЖЕ сборка — по совпадению
    /// НОМЕРА версии (eq.sub.hw.sw) ИЛИ метки даты-времени сборки (yyyyMMdd_HHmm) в имени. Пустая
    /// строка/недоступная папка — false: присутствие мы не подтвердили (решение о показе принимает
    /// вызывающий по остальным признакам — локальной копии, доступности диска, «наш ли это корень»).</summary>
    public static bool VersionPresentOnDisk(string? firmwareDir, string versionRaw)
    {
        if (string.IsNullOrEmpty(firmwareDir)) return false;
        if (HasFiles(firmwareDir)) return true;
        // Точной папки с файлами нет — но, может, её просто переименовали. Ищем соседа той же сборки.
        return FindSiblingBuildDir(firmwareDir, versionRaw) is not null;
    }

    /// <summary>Реальная папка версии на диске — то, что надо ОТКРЫВАТЬ и КОПИРОВАТЬ, а не просто
    /// «есть/нет». Возвращает точный <paramref name="firmwareDir"/>, если он существует; иначе соседнюю
    /// папку той же сборки в папке контроллера (папку переименовали прямо на диске — откат дописал
    /// суффикс, правка hw переписала номер, перезалив сменил дату, disk_path устарел после синхры — а
    /// файлы лежат рядом под другим именем). null — ни точной папки, ни узнаваемого соседа: открывать
    /// и копировать нечего.
    ///
    /// Отличие от <see cref="VersionPresentOnDisk"/>: там точная папка засчитывается только с файлами
    /// (вопрос «прятать ли карточку»), здесь достаточно СУЩЕСТВОВАНИЯ точной папки — открытие само
    /// разберётся с её содержимым, а пустую папку показать полезнее, чем сказать «не найдено».</summary>
    public static string? ResolveVersionDir(string? firmwareDir, string versionRaw)
    {
        if (string.IsNullOrEmpty(firmwareDir)) return null;
        if (Directory.Exists(firmwareDir)) return firmwareDir;
        return FindSiblingBuildDir(firmwareDir, versionRaw);
    }

    /// <summary>Сосед в папке контроллера (родителе папки версии), опознаваемый как ТА ЖЕ сборка — по
    /// совпадению НОМЕРА версии (eq.sub.hw.sw) ИЛИ метки даты-времени сборки (yyyyMMdd_HHmm) в имени.
    /// Метка сборки уникальна и НЕ меняется при переименовании hw/sw: она и есть отпечаток конкретной
    /// сборки. Совпадение по номеру ловит перезалив с другой датой (тот же eq.sub.hw.sw), по метке —
    /// правку hw/sw прямо на диске (номер в имени переписали, а дату-время — нет). Настоящее удаление
    /// не даёт ни того, ни другого: соседи — ДРУГИЕ сборки, с другим номером И другой датой.</summary>
    private static string? FindSiblingBuildDir(string firmwareDir, string versionRaw)
    {
        var parent = Path.GetDirectoryName(firmwareDir);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return null;

        var core = VersionCore(versionRaw);
        var stamp = VersionStamp(versionRaw);
        if (core is null && stamp is null) return null;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(parent))
            {
                var name = Path.GetFileName(dir);
                var sameNumber = core is not null && core == VersionCore(name);
                var sameBuild = stamp is not null && name.Contains(stamp, System.StringComparison.OrdinalIgnoreCase);
                if ((sameNumber || sameBuild) && HasFiles(dir)) return dir;
            }
        }
        catch { /* недоступная папка — соседа не подтверждаем, но и не отрицаем */ }
        return null;
    }

    private static readonly Regex BuildStamp = new(@"\d{8}_\d{4,6}", RegexOptions.Compiled);

    /// <summary>Метка даты-времени сборки в строке версии/имени папки — «yyyyMMdd_HHmm» (реже с
    /// секундами), см. FwVersionNumber. Это отпечаток конкретной сборки: он остаётся прежним, когда
    /// на диске переписывают hw/sw в номере, поэтому по нему сборку можно опознать под переименованным
    /// именем. null — метки нет (версия без даты): тогда опираемся только на номер.</summary>
    private static string? VersionStamp(string name)
    {
        var m = BuildStamp.Match(name);
        return m.Success ? m.Value : null;
    }

    private static bool HasFiles(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    /// <summary>Номер версии — первые четыре сегмента «eq.sub.hw.sw» строки версии или имени папки,
    /// без хвоста с датой и любых суффиксов после него. Сравниваем как есть, в той же записи, что и на
    /// диске (hw/sw в именах папок не всегда с ведущими нулями), поэтому «1.1.4.38.&lt;дата&gt;» и
    /// «1.1.4.38.&lt;дата&gt;_ОТКАТАНО» дают ОДИН номер, а «1.1.4.40.…» — уже другой. null, если сегментов
    /// меньше четырёх (не похоже на номер версии).</summary>
    private static string? VersionCore(string name)
    {
        var parts = name.Split('.');
        if (parts.Length < 4) return null;
        return string.Join('.', parts[0], parts[1], parts[2], parts[3]);
    }
}
