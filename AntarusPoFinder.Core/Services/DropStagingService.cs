using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Services;

/// <summary>Сборка нескольких перетащенных (или выбранных в диалоге) файлов в одну временную папку.
///
/// Зачем: и загрузка прошивки, и HMI-проект умеют принимать либо ОДИН файл, либо папку целиком —
/// «набор файлов» третьим вариантом нигде не поддерживался, и перетаскивание пяти файлов молча
/// брало первый, остальные исчезали без единого сообщения. Проект, у которого исполняемый файл и
/// драйверы просто лежат рядом (не в общей папке), из-за этого нельзя было загрузить, не создав
/// папку руками в проводнике.
///
/// Вместо отдельной ветки «много файлов» через весь конвейер загрузки — файлы копируются в одну
/// временную папку, и дальше по коду это обычный сценарий «перетащили папку»: те же проверки, тот же
/// выбор исполняемого файла (PickFileDialog умеет заходить в подпапки), то же копирование целиком.
/// Временная папка удаляется после успешной загрузки и при очистке формы (Cleanup).</summary>
public static class DropStagingService
{
    /// <summary>Собирает paths во временную папку и возвращает путь к ней. Один путь на входе
    /// возвращается как есть — временная копия ради одного файла не нужна.</summary>
    /// <param name="tempRoot">Корень для временной папки; по умолчанию системный %TEMP%. Параметр
    /// существует ради тестов — они не должны мусорить в реальном %TEMP% и зависеть от него.</param>
    public static string Stage(IReadOnlyList<string> paths, string? tempRoot = null)
    {
        if (paths is null || paths.Count == 0)
            throw new ArgumentException("Нечего собирать: список путей пуст.", nameof(paths));
        if (paths.Count == 1) return paths[0];

        // Имя временной папки видно оператору и попадает в поле filename записи (для папок туда
        // пишется имя исходной папки) — поэтому не GUID, а имя первого файла без расширения: набор
        // «Project.psl + driver.dll» даёт «Project», как если бы оператор сложил их в папку сам.
        var hint = Path.GetFileNameWithoutExtension(paths[0].TrimEnd(Path.DirectorySeparatorChar));
        var folderName = Sanitize(string.IsNullOrWhiteSpace(hint) ? "Файлы" : hint);

        var stageRoot = Path.Combine(tempRoot ?? Path.GetTempPath(), $"antarus_drop_{Guid.NewGuid():N}");
        var staged = Path.Combine(stageRoot, folderName);
        Directory.CreateDirectory(staged);

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) continue;

            if (Directory.Exists(path))
                FileSystemHelpers.CopyTree(path, EnsureDir(Path.Combine(staged, name)), overwrite: true);
            else if (File.Exists(path))
                File.Copy(path, UniqueName(Path.Combine(staged, name)), overwrite: false);
        }
        return staged;
    }

    /// <summary>Удаляет временную папку, созданную Stage (вместе с родительским antarus_drop_*).
    /// Best-effort: незакрытый хэндл или уже удалённая папка не должны ничего ронять — это уборка
    /// мусора, а не часть загрузки. Пути, не созданные Stage, игнорируются — так вызывающий код может
    /// звать Cleanup на любом _srcPath, не выясняя, временный он или оригинальный.</summary>
    public static void Cleanup(string? stagedPath)
    {
        if (string.IsNullOrEmpty(stagedPath)) return;
        var parent = Path.GetDirectoryName(stagedPath.TrimEnd(Path.DirectorySeparatorChar));
        if (parent is null) return;
        if (!Path.GetFileName(parent).StartsWith("antarus_drop_", StringComparison.Ordinal)) return;

        try
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Останется в %TEMP% до следующей уборки системы — не повод показывать ошибку оператору.
        }
    }

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Два одноимённых файла из разных папок в одном перетаскивании — редкость, но File.Copy
    /// с overwrite молча потерял бы один из них.</summary>
    private static string UniqueName(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "Файлы" : cleaned;
    }
}
