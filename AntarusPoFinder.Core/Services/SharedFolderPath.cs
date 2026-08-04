using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Общая папка на диске предприятия, которую администратор может переназначить: наклейки,
/// типовые паспорта и всё, что появится дальше в этом же духе. Правило одно на всех и записано
/// здесь, а не копией в каждой сущности: пусто — подпапка по умолчанию от корня диска,
/// относительный путь — тоже от корня (буква диска у каждой машины своя, и путь всё равно
/// сойдётся), абсолютный — как есть. Корень не настроен и путь не абсолютный — показывать нечего,
/// null.</summary>
public static class SharedFolderPath
{
    public static string? Resolve(string? diskRoot, string? configured, string defaultSubfolder)
    {
        var value = (configured ?? "").Trim();
        if (value.Length > 0 && Path.IsPathRooted(value))
            return RescueForeignLetter(diskRoot, value) ?? value;
        if (string.IsNullOrWhiteSpace(diskRoot)) return null;
        return Path.Combine(diskRoot, value.Length > 0 ? value : defaultSubfolder);
    }

    /// <summary>Спасение абсолютного пути, записанного НЕ ЭТОЙ машиной. <see cref="ToPortable"/>
    /// чинит настройку в момент выбора папки, но значение, уже уехавшее синхронизацией как
    /// <c>Z:\Конфиг\Наклейки</c>, останется таким до тех пор, пока администратор не переназначит папку
    /// заново, — а у коллеги с той же шарой под <c>Y:</c> она всё это время «пропавшая». Поэтому:
    /// путь, которого на этой машине НЕТ, пробуем найти под своим корнем диска, отбрасывая ведущие
    /// сегменты по одному (<c>Antarus\Конфиг\Наклейки</c> → <c>Конфиг\Наклейки</c> → <c>Наклейки</c>).
    ///
    /// Условия намеренно жёсткие, чтобы это не могло увести не туда: срабатывает только когда
    /// записанной папки на диске нет, а найденная — есть, и совпадать имена должны в точности. Не
    /// нашли — возвращаем null, и путь остаётся ровно тем, что записан: показать «папка недоступна»
    /// честнее, чем подставить чужую.</summary>
    private static string? RescueForeignLetter(string? diskRoot, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(diskRoot)) return null;

        try
        {
            if (Directory.Exists(absolutePath)) return null;

            var root = Path.GetPathRoot(absolutePath) ?? "";
            var tail = absolutePath[root.Length..]
                .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (tail.Length == 0) return null;

            var segments = tail.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                System.StringSplitOptions.RemoveEmptyEntries);
            for (var skip = 0; skip < segments.Length; skip++)
            {
                var candidate = Path.Combine(new[] { diskRoot }.Concat(segments[skip..]).ToArray());
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        catch (System.Exception)
        {
            // Недоступная шара или путь с недопустимыми символами — оставляем как записано.
        }
        return null;
    }

    /// <summary>Приводит выбранную обзором АБСОЛЮТНУЮ папку к виду, который переживёт синхронизацию:
    /// лежит внутри общего диска — остаётся хвост от его корня, лежит снаружи — остаётся как есть.
    ///
    /// Без этого настройка работала ровно у того, кто её задал. Папка наклеек (как и папка типовых
    /// паспортов) — общая, «только буква разная»: администратор выбирал обзором <c>Z:\Конфиг\Наклейки</c>,
    /// значение уезжало синхронизацией дословно, а у коллеги тот же диск подключён под <c>Y:</c> — и
    /// наклейки у него «пропадали». Хвост же сходится на любой машине (см. <see cref="Resolve"/>).
    ///
    /// Совпадение с подпапкой по умолчанию сворачивается в пустую строку — это то же самое место, но
    /// записанное как «настройку не трогали», и она переживёт даже смену правила по умолчанию.</summary>
    public static string ToPortable(string? diskRoot, string? absolutePath, string defaultSubfolder)
    {
        var value = (absolutePath ?? "").Trim();
        if (value.Length == 0 || string.IsNullOrWhiteSpace(diskRoot)) return value;

        string root, full;
        try
        {
            root = Path.GetFullPath(diskRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (System.Exception)
        {
            return value;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) return value;

        var relative = full[prefix.Length..];
        return string.Equals(relative, defaultSubfolder, System.StringComparison.OrdinalIgnoreCase) ? "" : relative;
    }
}
