using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Копия папки версии под ДРУГОЙ подтип шкафа: настоящие файлы, а не ярлык.
///
/// <b>Почему копия, а не ссылка.</b> Одна и та же прошивка часто подходит нескольким подтипам, и
/// раньше это делалось «экономно»: файлы лежали один раз, в папке основного подтипа, а остальным
/// подтипам заводилась запись с тем же <c>disk_path</c> плюс ярлык в папке контроллера. Дальше
/// выяснилось, что экономия не стоит своей цены: у копии тот же номер версии (то есть по номеру
/// нельзя понять, какому шкафу принадлежит прошивка), в папке подтипа лежит ярлык вместо прошивки, а
/// документы шкафа приходится разводить отдельным механизмом (см. VersionDocFolders). Решение Ильи:
/// «уходим от ярлыков, кладём всегда саму прошивку, даже если подходит нескольким».
///
/// <b>Что делает переименование.</b> Имя версии зашито в имена файлов: сама прошивка
/// (<see cref="FirmwareNaming.BuildFirmwareFilename"/>), инструкция и её заглушка
/// (<see cref="InstructionNaming"/>), папка HMI-проекта («&lt;версия&gt;_hmi»). У копии номер СВОЙ — с
/// префиксом её подтипа, — поэтому всё, что содержит старую строку версии в имени, переименовывается
/// на новую. Правило одно на файлы и на папки: иначе копия ссылалась бы именами на версию соседнего
/// подтипа, ровно от чего и уходим.</summary>
public static class VersionFolderCopy
{
    public sealed record Result(string TargetFolder, string FirmwareFileName, List<string> Warnings)
    {
        public bool Ok => Warnings.Count == 0;
    }

    /// <summary>Копирует папку версии целиком. Существующая цель не очищается, файлы в ней
    /// перезаписываются — повторный вызов доводит прерванную копию до конца и ничего не ломает.</summary>
    /// <param name="oldFirmwareName">Имя файла прошивки у исходной версии (<c>fw_versions.filename</c>) —
    /// именно оно переименовывается в каноническое имя новой версии, даже если каноническим не было.
    /// Пусто — переименование идёт только по общему правилу «строка версии в имени».</param>
    /// <param name="newFirmwareName">Каноническое имя файла прошивки новой версии.</param>
    public static Result Copy(string sourceFolder, string targetFolder, string oldVersionRaw, string newVersionRaw,
        string oldFirmwareName, string newFirmwareName)
    {
        var warnings = new List<string>();
        var firmwareName = "";

        if (!Directory.Exists(sourceFolder))
            return new Result(targetFolder, "", new List<string> { $"Папки версии нет на диске: {sourceFolder}" });

        void CopyInto(string src, string dst)
        {
            Directory.CreateDirectory(dst);

            foreach (var file in Directory.EnumerateFiles(src))
            {
                var name = Path.GetFileName(file);
                var target = string.Equals(name, oldFirmwareName, StringComparison.OrdinalIgnoreCase)
                             && newFirmwareName.Length > 0
                    ? newFirmwareName
                    : RenameForVersion(name, oldVersionRaw, newVersionRaw);
                try
                {
                    File.Copy(file, Path.Combine(dst, target), overwrite: true);
                    if (string.Equals(name, oldFirmwareName, StringComparison.OrdinalIgnoreCase))
                        firmwareName = target;
                }
                catch (Exception ex)
                {
                    warnings.Add($"{name}: {ex.Message}");
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(src))
            {
                var name = Path.GetFileName(dir);
                CopyInto(dir, Path.Combine(dst, RenameForVersion(name, oldVersionRaw, newVersionRaw)));
            }
        }

        try { CopyInto(sourceFolder, targetFolder); }
        catch (Exception ex) { warnings.Add(ex.Message); }

        // Файл прошивки на диске не нашёлся под записанным именем (переименовали руками, запись
        // устарела) — имя копии всё равно должно быть каноническим, иначе в базе будет одно, а на
        // диске другое. Ищем единственный файл в «Прошивка» и переименовываем его.
        if (firmwareName.Length == 0 && newFirmwareName.Length > 0)
            firmwareName = RenameLoneFirmwareFile(targetFolder, newFirmwareName, warnings);

        return new Result(targetFolder, firmwareName, warnings);
    }

    /// <summary>Имя файла или папки, в котором строка версии заменена на новую. Не содержит её —
    /// возвращается как есть: имена, не завязанные на версию (CHANGELOG.md, драйверы проекта),
    /// трогать нельзя.</summary>
    public static string RenameForVersion(string name, string oldVersionRaw, string newVersionRaw)
    {
        if (string.IsNullOrEmpty(oldVersionRaw) || string.IsNullOrEmpty(newVersionRaw)) return name;
        return name.Replace(oldVersionRaw, newVersionRaw, StringComparison.OrdinalIgnoreCase);
    }

    private static string RenameLoneFirmwareFile(string versionFolder, string newFirmwareName, List<string> warnings)
    {
        try
        {
            var folder = VersionLayout.FirmwareFolders(versionFolder).FirstOrDefault();
            if (folder is null) return "";

            var files = Directory.EnumerateFiles(folder)
                .Where(f => !VersionLayout.IsServiceFile(f) && !JunkFiles.IsJunk(f))
                .Take(2).ToList();
            if (files.Count != 1) return "";

            var target = Path.Combine(folder, newFirmwareName);
            if (!string.Equals(files[0], target, StringComparison.OrdinalIgnoreCase))
                File.Move(files[0], target, overwrite: true);
            return newFirmwareName;
        }
        catch (Exception ex)
        {
            warnings.Add($"Имя файла прошивки в копии: {ex.Message}");
            return "";
        }
    }

    /// <summary>Пути документов копии: то, что лежало ВНУТРИ исходной папки версии, переезжает в
    /// копию (с учётом переименования по версии), всё остальное остаётся как было. Общая папка
    /// документов контроллера принадлежит не версии, а шкафу, и подменять её на несуществующий путь
    /// внутри копии значит потерять документ.
    ///
    /// Пустой путь остаётся пустым — «документа нет» копируется без изменений.</summary>
    public static string RepointPath(string? path, string sourceFolder, string targetFolder,
        string oldVersionRaw, string newVersionRaw)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (!IsInside(path, sourceFolder)) return path;

        var relative = path.Substring(sourceFolder.TrimEnd(Path.DirectorySeparatorChar).Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var renamed = string.Join(Path.DirectorySeparatorChar,
            relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Select(part => RenameForVersion(part, oldVersionRaw, newVersionRaw)));
        return Path.Combine(targetFolder, renamed);
    }

    private static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var normalized = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(normalized + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
