using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Loader;

/// <summary>
/// Локальная рабочая область одного запуска лоадера. Приложение НЕ клиент-серверное: собирать и
/// заливать нужно на машине оператора, а сетевой диск трогать минимально — поэтому исходники
/// сначала копируются сюда (<see cref="Import"/>), вся работа идёт в <see cref="OutputDir"/>, и
/// только успешный результат уезжает на диск (<see cref="Publish"/>). Это единственный метод здесь,
/// который вообще пишет за пределы локальной папки.
/// </summary>
public sealed class LoaderWorkspace : IDisposable
{
    /// <summary>Корень рабочей области этого запуска.</summary>
    public string Dir { get; }

    /// <summary>Локальная копия исходников (проект .psl, готовый .lfs и всё, что лежало рядом).</summary>
    public string SourceDir => Path.Combine(Dir, "src");

    /// <summary>Всё, что лоадер сделал. Отсюда и только отсюда публикуется результат.</summary>
    public string OutputDir => Path.Combine(Dir, "out");

    /// <summary>Куда диалог сохраняет лог запуска (кнопка «Сохранить лог» пишет туда же по умолчанию).</summary>
    public string LogPath => Path.Combine(Dir, "loader.log");

    private LoaderWorkspace(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(OutputDir);
    }

    /// <summary>Создаёт папку вида <c>rootDir\20260723_141530_2_1_042</c>. Имя версии в названии —
    /// чтобы оператор, открыв рабочую папку руками, понимал, что где лежит.</summary>
    public static LoaderWorkspace Create(string rootDir, string name, DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd_HHmmss");
        var safe = Sanitize(name);
        var baseDir = Path.Combine(rootDir, string.IsNullOrEmpty(safe) ? stamp : $"{stamp}_{safe}");
        var dir = baseDir;
        for (var i = 2; Directory.Exists(dir); i++) dir = $"{baseDir}_{i}";
        return new LoaderWorkspace(dir);
    }

    /// <summary>Копирует файл или папку в <see cref="SourceDir"/> и возвращает локальный путь к
    /// копии. Дальше лоадер работает ТОЛЬКО с этим путём — сетевой источник больше не нужен, и
    /// обрыв сети посреди сборки не превращается в наполовину прочитанный проект.</summary>
    public string Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Не указан файл для загрузки.", nameof(sourcePath));

        if (File.Exists(sourcePath))
            return FileSystemHelpers.CopyFile(sourcePath, SourceDir);

        if (Directory.Exists(sourcePath))
        {
            var dst = Path.Combine(SourceDir, Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar)));
            FileSystemHelpers.CopyTree(sourcePath, dst, overwrite: true);
            return dst;
        }

        throw new FileNotFoundException($"Файл или папка не найдены: {sourcePath}", sourcePath);
    }

    /// <summary>Всё, что лоадер положил в <see cref="OutputDir"/>, включая вложенные папки.</summary>
    public IReadOnlyList<string> CollectArtifacts() =>
        Directory.Exists(OutputDir)
            ? Directory.EnumerateFiles(OutputDir, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList()
            : new List<string>();

    /// <summary>Кладёт результат на диск и возвращает пути уже на диске. Копирование ДОБАВЛЯЮЩЕЕ:
    /// одноимённые файлы перезаписываются, всё остальное в папке версии остаётся нетронутым —
    /// публикация не должна сносить прошивку, рядом с которой кладёт собранный файл (в отличие от
    /// FileSystemHelpers.CopyTree с overwrite, который сначала удаляет папку назначения целиком).</summary>
    public IReadOnlyList<string> Publish(string destDir)
    {
        if (string.IsNullOrWhiteSpace(destDir))
            throw new ArgumentException("Не указана папка публикации.", nameof(destDir));

        var artifacts = CollectArtifacts();
        if (artifacts.Count == 0) return Array.Empty<string>();

        Directory.CreateDirectory(destDir);
        var published = new List<string>();
        foreach (var file in artifacts)
        {
            var rel = Path.GetRelativePath(OutputDir, file);
            var dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
            published.Add(dst);
        }
        return published;
    }

    /// <summary>Удаляет рабочую область. Best-effort: не смогли (файл занят антивирусом/лоадером) —
    /// не повод ронять запуск, папку заберёт <see cref="CleanupOlderThan"/> в следующий раз.</summary>
    public void Delete()
    {
        try { FileSystemHelpers.RmtreeSafe(Dir); }
        catch (Exception) { /* см. комментарий выше */ }
    }

    /// <summary>Подчищает старые рабочие области — они специально не удаляются сразу после запуска
    /// (оператору бывает нужен лог и промежуточные файлы), но и копиться вечно не должны.</summary>
    public static int CleanupOlderThan(string rootDir, TimeSpan age, DateTime? now = null)
    {
        if (!Directory.Exists(rootDir)) return 0;
        var cutoff = (now ?? DateTime.Now) - age;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(rootDir))
        {
            try
            {
                if (Directory.GetLastWriteTime(dir) > cutoff) continue;
                FileSystemHelpers.RmtreeSafe(dir);
                removed++;
            }
            catch (Exception) { /* best effort */ }
        }
        return removed;
    }

    public void Dispose() { /* намеренно НЕ удаляет папку — см. Delete/CleanupOlderThan */ }

    private static string Sanitize(string name) => Regex.Replace(name ?? "", @"[^\w\-\.]", "_").Trim('_');
}
