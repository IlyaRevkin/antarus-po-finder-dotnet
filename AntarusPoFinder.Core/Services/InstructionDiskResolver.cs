using System;
using System.IO;

namespace AntarusPoFinder.Core.Services;

/// <summary>Третий диск — отдельное хранилище ТОЛЬКО под инструкции (docs/hierarchy-rework-plan.md,
/// Этап 3). Смысл затеи: инструкции — самые тяжёлые файлы (сканы, docx с картинками, pdf на десятки
/// мегабайт), и они же нужны с телефона/по ссылке; вынесенные на отдельный диск, они перестают
/// утяжелять обход диска прошивок и могут отдаваться read-only отдельно от него.
///
/// Раскладка на третьем диске — ЗЕРКАЛО первого: тот же путь, только с другим корнем. Ничего не
/// «маппится» таблицей и нигде не хранится: путь считается заменой префикса, поэтому переименование
/// подтипа/контроллера на первом диске автоматически действует и на третьем, а неверно настроенный
/// корень не может увести файл в чужую папку.
///
/// Правила поведения (важно для всех вызывающих):
///   • третий диск не настроен, недоступен или путь лежит ВНЕ первого диска — работаем на первом,
///     ровно как до появления этой возможности;
///   • читаем с третьего диска НАПРЯМУЮ, ярлыки .lnk на первом игнорируются (их кладут только для
///     коллег со старым клиентом, чтобы у них папка инструкции не выглядела пустой);
///   • ничего не удаляем и не переносим сами — переезд уже накопленных инструкций это отдельная
///     разовая операция, а не побочный эффект открытия карточки.</summary>
public static class InstructionDiskResolver
{
    /// <summary>Зеркальный путь на третьем диске для пути на первом. null — зеркала нет и быть не
    /// может: третий диск не задан, первый не задан, либо путь не лежит внутри первого диска
    /// (например, это локальный кэш — его зеркалить бессмысленно и опасно).
    ///
    /// Существование папки НЕ проверяется: этой функцией пользуются и чтение (там существование
    /// проверяет вызывающий), и запись (там папку ещё только предстоит создать).</summary>
    public static string? Mirror(string? firstRoot, string? thirdRoot, string? pathOnFirstDisk)
    {
        if (string.IsNullOrWhiteSpace(firstRoot) || string.IsNullOrWhiteSpace(thirdRoot)) return null;
        if (string.IsNullOrWhiteSpace(pathOnFirstDisk)) return null;

        string first, path, third;
        try
        {
            first = Path.GetFullPath(firstRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            third = Path.GetFullPath(thirdRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            path = Path.GetFullPath(pathOnFirstDisk);
        }
        catch (Exception)
        {
            // Битый путь (недопустимые символы, слишком длинный) — не повод падать: просто нет зеркала.
            return null;
        }

        if (string.Equals(first, third, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(path, first, StringComparison.OrdinalIgnoreCase)) return third;

        var prefix = first + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        return Path.Combine(third, path[prefix.Length..]);
    }

    /// <summary>Папка, из которой НАДО читать инструкцию: зеркало на третьем диске, если оно
    /// существует, иначе исходная папка на первом. Ходит на диск — звать из фонового обхода или по
    /// клику, не из отрисовки.</summary>
    public static string? PreferredReadFolder(string? firstRoot, string? thirdRoot, string? folderOnFirstDisk)
    {
        var mirror = Mirror(firstRoot, thirdRoot, folderOnFirstDisk);
        if (mirror is not null && SafeDirectoryExists(mirror)) return mirror;
        return string.IsNullOrWhiteSpace(folderOnFirstDisk) ? null : folderOnFirstDisk;
    }

    /// <summary>Папка, в которую НАДО писать инструкцию: зеркало на третьем диске, если третий диск
    /// настроен и его корень сейчас доступен, иначе папка на первом. Недоступный третий диск — не
    /// ошибка и не повод отменять загрузку: файл ложится на первый, как раньше.</summary>
    public static string? PreferredWriteFolder(string? firstRoot, string? thirdRoot, string? folderOnFirstDisk)
    {
        var mirror = Mirror(firstRoot, thirdRoot, folderOnFirstDisk);
        if (mirror is not null && SafeDirectoryExists(thirdRoot)) return mirror;
        return string.IsNullOrWhiteSpace(folderOnFirstDisk) ? null : folderOnFirstDisk;
    }

    /// <summary>Пишем ли мы сейчас на третий диск (а значит, надо ли класть ярлык на первом).</summary>
    public static bool WritesToThirdDisk(string? firstRoot, string? thirdRoot, string? folderOnFirstDisk) =>
        PreferredWriteFolder(firstRoot, thirdRoot, folderOnFirstDisk) is { } target &&
        !string.Equals(target, folderOnFirstDisk, StringComparison.OrdinalIgnoreCase);

    private static bool SafeDirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }
}
