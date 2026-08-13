using System;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Служебный мусор файловой системы — то, что оказалось в папке само и документом не
/// является ни при каких обстоятельствах.
///
/// <b>Зачем отдельным местом.</b> Жалоба: «у меня есть прошивка, а инструкции нет, и он почему-то
/// сделал ссылку на Thumbs.db вместо заглушки». В папке «Инструкция» лежал только `Thumbs.db`,
/// созданный проводником при просмотре эскизов, — а «первый файл, который не ярлык и не заглушка»
/// это он и есть. Дальше рушилось всё подряд: карточка загоралась «инструкция ✓», заглушка не
/// клалась (папка же «не пустая»), а под QR уходила ссылка на служебный файл Windows — и наклейку с
/// ней клеили на шкаф.
///
/// Список знал только чистильщик диска, и знал приватно. Теперь он один на всех: и на «что считать
/// документом», и на «что предлагать удалить». Разъехаться этим двум ответам нельзя — иначе
/// программа будет предлагать удалить файл, который сама же считает инструкцией.
///
/// <b>Список закрытый и короткий намеренно.</b> «.old» и «.copy» сюда сознательно не попали: под
/// таким именем на этом диске вполне может лежать нужная предыдущая сборка. Ошибка в сторону
/// «оставить» здесь всегда дешевле.</summary>
public static class JunkFiles
{
    /// <summary>Служебные файлы Windows и офиса — точные имена, без вариантов.</summary>
    public static readonly string[] Names =
    {
        "Thumbs.db", "ehthumbs.db", "desktop.ini", ".DS_Store",
    };

    /// <summary>Расширения незавершённых и временных файлов.</summary>
    public static readonly string[] Extensions =
    {
        ".tmp", ".temp", ".part", ".partial", ".crdownload", ".bak",
    };

    /// <summary>Приставка временных файлов Word/Excel у незакрытого документа.</summary>
    public const string OfficeTempPrefix = "~$";

    /// <summary>Это служебный мусор. Пустой путь — нет: «ничего» мусором не называем.</summary>
    public static bool IsJunk(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string name;
        try { name = Path.GetFileName(path) ?? ""; }
        catch (Exception) { return false; }
        if (name.Length == 0) return false;

        if (Names.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (name.StartsWith(OfficeTempPrefix, StringComparison.Ordinal)) return true;
        return Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Человеческое объяснение, почему это мусор, — или null, если это не мусор. Нужно
    /// чистильщику: он обязан показывать причину, а не просто предлагать удалить.</summary>
    public static string? Reason(string? path)
    {
        if (!IsJunk(path)) return null;

        var name = Path.GetFileName(path!)!;
        if (Names.Contains(name, StringComparer.OrdinalIgnoreCase))
            return $"«{name}» — служебный файл Windows, к структуре диска отношения не имеет.";
        if (name.StartsWith(OfficeTempPrefix, StringComparison.Ordinal))
            return $"«{name}» — временный файл Word/Excel, остаётся от незакрытого документа.";
        return $"«{Path.GetExtension(name)}» — недокачанный или временный файл, рабочей копией не является.";
    }
}
