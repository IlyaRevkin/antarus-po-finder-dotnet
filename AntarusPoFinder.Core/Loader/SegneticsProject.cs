using System;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Относятся ли .psl/.lfs к этой версии вообще.
///
/// .psl (исходник SMLogix) и .lfs (собранный файл для заливки) — формат Segnetics и только его. У
/// шкафа на KINCO их нет и быть не может, поэтому строка «LFS — · PSL —» на такой карточке читается
/// как «файлы потерялись», хотя терять нечего. Показывать их состояние имеет смысл ровно там, где
/// эти файлы вообще бывают.
///
/// Порядок проверки: сначала то, что реально нашлось на диске (файл рядом с версией — самый честный
/// признак, он же покрывает случай, когда контроллер назван как-то по-своему), потом подсказка
/// исполняемого файла, и только потом имя контроллера. Список семейств — по каталогу Segnetics
/// (SMH, Pixel/PXL, Trim); справочник контроллеров администратор пополняет сам, поэтому незнакомое
/// имя считается НЕ Segnetics: лишняя строка про LFS хуже, чем её отсутствие — файлы всё равно
/// определятся по диску, как только там что-то появится.</summary>
public static class SegneticsProject
{
    private static readonly string[] Families = ["SMH", "PIXEL", "PXL", "TRIM"];

    public static bool IsRelevant(string? controllerName, string? executableHint, bool foundLfs = false, bool foundPsl = false)
    {
        if (foundLfs || foundPsl) return true;

        if (!string.IsNullOrWhiteSpace(executableHint))
        {
            var ext = Path.GetExtension(executableHint.Trim());
            if (string.Equals(ext, LoaderFiles.PslExtension, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, LoaderFiles.LfsExtension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (string.IsNullOrWhiteSpace(controllerName)) return false;
        var name = controllerName.Trim().ToUpperInvariant();
        return Families.Any(f => name.StartsWith(f, StringComparison.Ordinal));
    }
}
