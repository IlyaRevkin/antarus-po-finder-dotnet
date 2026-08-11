using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Куда класть собранный .lfs. Сетевая папка версии — единственное место, откуда его
/// увидят ОСТАЛЬНЫЕ машины (флаг «есть LFS» на карточке считается обходом папки версии, см.
/// SearchView.ScanVersionFolder); локальная копия — зеркало, чтобы своя же карточка показала файл
/// сразу, не дожидаясь следующей синхронизации. Пустое поле = такой папки сейчас нет.</summary>
public sealed record LfsPublishPlan(string? NetworkFolder, string? LocalFolder)
{
    public bool HasAny => NetworkFolder is not null || LocalFolder is not null;

    /// <summary>Сетевая папка первой: если шара отвалится посреди публикации, до неё уже дойдёт.</summary>
    public IReadOnlyList<string> Folders =>
        new[] { NetworkFolder, LocalFolder }.Where(f => f is not null).Select(f => f!).ToList();
}

/// <summary>Что реально удалось положить. Warnings не фатальны: собранный файл уже существует
/// локально, и «не смогли положить в одну из папок» не должно ронять всю операцию.</summary>
public sealed record LfsPublishResult(IReadOnlyList<string> Published, IReadOnlyList<string> Warnings)
{
    public bool AnyPublished => Published.Count > 0;
}

/// <summary>Публикация собранного .lfs в папки версии.
///
/// Сборка всегда идёт ЛОКАЛЬНО (LoaderWorkspace), на диск уезжает только готовый файл — сетевая
/// шара компании регулярно отвечает через раз, и «наполовину записанный .lfs» рядом с исходником
/// хуже, чем его отсутствие: карточка покажет «LFS ✓», а в контроллер уедет мусор. Поэтому файл
/// сначала копируется под временным именем и только потом одним переименованием занимает место
/// целевого: обрыв на середине оставляет мусорный <c>*.lfs.part</c>, но не битый <c>*.lfs</c>.</summary>
public static class LfsPublisher
{
    public const string TempSuffix = ".part";

    /// <summary>Куда класть результат по папкам ВЕРСИИ, которые нам дали. Несуществующая (сеть
    /// отвалилась, локальной копии ещё нет) — не ошибка, просто не цель публикации.
    ///
    /// Внутри перестроенной версии файлы прошивки живут в подпапке «Прошивка\», и собранный .lfs —
    /// такой же файл прошивки, как лежащий рядом с ним .psl. Раньше здесь этого никто не знал, и
    /// .lfs ложился в КОРЕНЬ папки версии — вперемешку с CHANGELOG.md и папками документов, отдельно
    /// от собственного исходника. У неперестроенной версии подпапки нет, и всё остаётся как было:
    /// решение принимает <see cref="VersionLayout.FirmwareWriteFolder"/>, единственное место, которое
    /// вообще знает раскладку.</summary>
    public static LfsPublishPlan Plan(string? networkFolder, string? localFolder) =>
        new(FirmwareTarget(networkFolder), FirmwareTarget(localFolder));

    private static string? FirmwareTarget(string? versionFolder) =>
        Existing(versionFolder) is { } dir ? VersionLayout.FirmwareWriteFolder(dir) : null;

    /// <summary>Кладёт файл в папку через временное имя. Возвращает итоговый путь.</summary>
    public static string Publish(string builtFile, string destDir)
    {
        if (string.IsNullOrWhiteSpace(builtFile))
            throw new ArgumentException("Не указан собранный файл.", nameof(builtFile));
        if (!File.Exists(builtFile))
            throw new FileNotFoundException($"Собранный файл не найден: {builtFile}", builtFile);
        if (string.IsNullOrWhiteSpace(destDir))
            throw new ArgumentException("Не указана папка публикации.", nameof(destDir));

        Directory.CreateDirectory(destDir);
        var final = Path.Combine(destDir, Path.GetFileName(builtFile));
        var temp = final + TempSuffix;
        try
        {
            File.Copy(builtFile, temp, overwrite: true);
            File.Move(temp, final, overwrite: true);
            return final;
        }
        catch (Exception)
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Публикует во все папки плана. Не бросает: недоступная папка превращается в
    /// предупреждение, остальные всё равно получают файл.</summary>
    public static LfsPublishResult PublishAll(string builtFile, LfsPublishPlan plan)
    {
        var published = new List<string>();
        var warnings = new List<string>();

        if (plan.NetworkFolder is null)
            warnings.Add("Папка версии на сетевом диске недоступна — собранный LFS туда не попал, коллеги его пока не увидят.");

        foreach (var dir in plan.Folders)
        {
            try { published.Add(Publish(builtFile, dir)); }
            catch (Exception ex) { warnings.Add($"Не удалось сохранить LFS в {dir}: {ex.Message}"); }
        }

        if (published.Count == 0 && plan.Folders.Count == 0)
            warnings.Add("Не нашлось ни одной папки версии, куда положить собранный LFS.");

        return new LfsPublishResult(published, warnings);
    }

    private static string? Existing(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* мусорный .part заберёт следующая публикация или оператор */ }
    }
}
