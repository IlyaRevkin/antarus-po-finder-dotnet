using System;
using System.IO;

namespace AntarusPoFinder.Core.Services;

/// <summary>Каноническое имя файла инструкции — <c>инструкция_&lt;версия&gt;.&lt;расширение&gt;</c>.
///
/// Тот же приём, что и у файла прошивки (<see cref="AntarusPoFinder.Core.Domain.FirmwareNaming"/>):
/// имя файла привязано к версии, поэтому по файлу, вырванному из контекста (переслали в почте,
/// положили на флешку), видно, к какой версии он относится. Разница только в приставке: у прошивки
/// именем служит сама строка версии, а инструкций у версии может быть две (исходный .docx и
/// собранный .pdf) — они и различаются расширением, а «инструкция_» отделяет их от файла прошивки,
/// когда оба оказались в одной папке.
///
/// <b>Что НЕ переименовывается никогда:</b>
/// <list type="bullet">
/// <item><description>ярлыки .lnk — это указатель на уехавший на третий диск файл, а не документ
/// (см. <see cref="InstructionDiskResolver"/>);</description></item>
/// <item><description>заглушка «инструкция в разработке» — у неё своё имя и свой смысл
/// (см. <see cref="InstructionStub"/>);</description></item>
/// <item><description>папки — инструкция иногда лежит папкой со сканами, и переименование папки
/// осиротило бы путь, записанный в <c>fw_versions.instructions_path</c> у коллег.</description></item>
/// </list>
///
/// Расширение приводится к нижнему регистру по той же причине, что и у прошивки: «.PDF» и «.pdf» —
/// одно и то же, и каноническое имя не должно зависеть от того, как файл назвали в Word.</summary>
public static class InstructionNaming
{
    /// <summary>Приставка канонического имени. Строчными: имя папки версии тоже пишется как есть, и
    /// заглавная «И» в начале выглядела бы как отдельная сущность рядом с «2.1.0042.0001…».</summary>
    public const string Prefix = "инструкция_";

    /// <summary>«инструкция_2.1.0042.0001.20260422_1348.pdf». Пустая версия — пустая строка:
    /// строить имя не из чего, вызывающий обязан это проверить.</summary>
    public static string BuildFileName(string? versionRaw, string? ext)
    {
        if (string.IsNullOrWhiteSpace(versionRaw)) return "";
        var e = ext ?? "";
        if (e.Length > 0 && !e.StartsWith('.')) e = "." + e;
        return Prefix + versionRaw.Trim() + e.ToLowerInvariant();
    }

    /// <summary>Имя файла уже каноническое (с точностью до регистра расширения — «.PDF» считается
    /// НЕ каноническим, его тоже надо привести к нижнему).</summary>
    public static bool IsCanonical(string? path, string? versionRaw)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(versionRaw)) return false;
        var expected = BuildFileName(versionRaw, Path.GetExtension(path));
        return expected.Length > 0 &&
               string.Equals(Path.GetFileName(path), expected, StringComparison.Ordinal);
    }

    /// <summary>Этот файл трогать нельзя ни при каких обстоятельствах — см. доку класса.</summary>
    public static bool IsUntouchable(string path) =>
        DocFileResolver.IsShortcut(path) || InstructionStub.IsStub(path);

    /// <summary>Каноническое ИМЯ, к которому надо привести этот файл, или null — переименовывать не
    /// надо (уже каноническое) либо нельзя (ярлык, заглушка, версия неизвестна).</summary>
    public static string? CanonicalNameFor(string? path, string? versionRaw)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(versionRaw)) return null;
        if (IsUntouchable(path)) return null;
        if (IsCanonical(path, versionRaw)) return null;

        var name = BuildFileName(versionRaw, Path.GetExtension(path));
        return name.Length > 0 ? name : null;
    }

    /// <summary>Проверить имя файла на диске и переименовать, если оно не каноническое. Возвращает
    /// АКТУАЛЬНЫЙ путь файла: новый — если переименовали, прежний — если не понадобилось или не
    /// вышло.
    ///
    /// Ничего не затирает: если каноническое имя уже занято ДРУГИМ файлом (в папке лежат два .pdf от
    /// разных источников), файл остаётся под своим именем — потерять чужой документ ради красивого
    /// имени нельзя. Ошибка ввода-вывода (файл открыт в Acrobat, шара отвалилась) тоже не
    /// фатальна: инструкция уже лежит там, где надо, а имя поправит следующая перестройка диска.</summary>
    public static string EnsureCanonicalName(string path, string? versionRaw)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return path;
        if (CanonicalNameFor(path, versionRaw) is not { } canonical) return path;

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return path;
        var target = Path.Combine(dir, canonical);

        try
        {
            return RenameKeepingCase(path, target) ? target : path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return path;
        }
    }

    /// <summary>Переименование внутри одной папки с отдельно разобранным случаем «различие только в
    /// регистре» («….PDF» → «….pdf»): Windows считает такие имена одним и тем же файлом, и обычный
    /// File.Move на них падает «файл уже существует» — идём через временное имя. Тот же приём, что и
    /// у переименования прошивки в DiskLayoutMigrator.</summary>
    private static bool RenameKeepingCase(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.Ordinal)) return false;

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            var tmp = target + ".antarus-rename";
            File.Move(source, tmp);
            try
            {
                File.Move(tmp, target);
            }
            catch (Exception)
            {
                // Второй шаг не удался — возвращаем исходное имя: оставить инструкцию лежать под
                // «….antarus-rename» нельзя, её перестанут находить резолверы документов.
                try { File.Move(tmp, source); } catch (Exception) { /* путь останется во временном имени */ }
                throw;
            }
            return true;
        }

        if (File.Exists(target)) return false;
        File.Move(source, target);
        return true;
    }
}
