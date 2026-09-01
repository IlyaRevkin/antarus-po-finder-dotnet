using System.Linq;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Каким способом прошивка попадает в контроллер. Оба семейства Segnetics (SMH и Pixel)
/// программируются в SMLogix и имеют исходник .psl, но заливаются они по-разному:
/// <list type="bullet">
/// <item><description><b>SMH</b> (SMH2Gi/SMH4/SMH5…) — через Segnetics Loader: из .psl собирается
/// загрузочный .lfs и заливается загрузчиком.</description></item>
/// <item><description><b>Pixel/Pixel2</b> — Loader НЕ поддерживают вовсе: прошивка это сам проект
/// SMLogix (.psl), его открывают в SMLogix, а не собирают в .lfs и не льют загрузчиком.</description></item>
/// </list>
///
/// До появления этого признака решение «Loader или SMLogix» принималось вслепую — по наличию .psl: у
/// Pixel-версии с исходником программа предлагала «Собрать LFS» и вела «Загрузить в ПЛК» в Segnetics
/// Loader, которого для Pixel не существует и который на машине наладчика не установлен. Отсюда жалоба
/// «на Pixel нет лоадера, а он LFS создал и через лоадер грузит — должен открывать .psl в SMLogix».
///
/// Отличается от <see cref="SegneticsProject"/> намеренно: тот отвечает «бывают ли у версии .psl/.lfs
/// вообще» (Pixel тоже Segnetics и .psl у него есть), а этот — «заливается ли контроллер загрузчиком».
///
/// Признак выводится из СЕМЕЙСТВА контроллера по справочнику, а не по наличию файла на диске: даже
/// если рядом с Pixel-версией кто-то по ошибке уже собрал .lfs, лить его загрузчиком всё равно нельзя.
/// Справочник контроллеров администратор пополняет сам, поэтому Loader-семейством считается только
/// явно известное (SMH); незнакомое имя и любой другой вендор грузятся НЕ через Segnetics Loader — что
/// для них и верно.</summary>
public static class ControllerLoadMethod
{
    /// <summary>Семейства контроллеров, которые заливаются Segnetics Loader'ом (.lfs). Всё остальное —
    /// открытием проекта в родной среде (для Segnetics-Pixel это .psl в SMLogix).</summary>
    private static readonly string[] LoaderFamilies = ["SMH"];

    /// <summary>Поддерживает ли контроллер заливку через Segnetics Loader (сборку и заливку .lfs).
    /// false — прошивку открывают проектом в родной среде: у Pixel это .psl в SMLogix, у остальных
    /// вендоров — свой проект.</summary>
    public static bool SupportsLoader(string? controllerName)
    {
        if (string.IsNullOrWhiteSpace(controllerName)) return false;
        var name = controllerName.Trim().ToUpperInvariant();
        return LoaderFamilies.Any(f => name.StartsWith(f, System.StringComparison.Ordinal));
    }
}
