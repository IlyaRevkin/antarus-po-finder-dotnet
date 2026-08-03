using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AntarusPoFinder.Core.Data;

namespace AntarusPoFinder.Core.Services;

/// <summary>Разовая (и потом идемпотентная) чистка на диске файлов-двойников вида
/// «имя (что-то).ext», лежащих рядом с настоящим «имя.ext» в папке параметров.
///
/// ЗАЧЕМ. Жалоба: файл параметров, загруженный сразу под два подтипа (X2 и XL), оказался на диске
/// двумя текстовыми файлами — «параметры X2» и «параметры X2 (XL)» — плюс ярлыки на оба. Текущий код
/// приложения такого сделать не может: копирование ровно одно (ParamsView.Upload_Click →
/// File.Copy под исходным именем), дополнительным подтипам заводится только ЗАПИСЬ и ЯРЛЫК
/// (ParamFileLinkService.LinkToExtraSubtypes), а имя файла нигде не склеивается с подписью подтипа —
/// подпись «Папка (Подтип)» существует только в UI-списках (SubtypeMultiSelect,
/// EditParamSubtypesDialog). Значит источник — снаружи: либо старая версия клиента, либо ручное
/// «сохранить как» коллеги, либо разрешение конфликта облачной синхронизацией диска.
///
/// Поэтому здесь не «исправление бага», а зачистка последствий, устроенная так, чтобы она физически
/// не могла навредить:
///   • удаляется только файл, имя которого = имя настоящего файла + скобочный суффикс;
///   • и только если он ПОБАЙТОВО совпадает с настоящим (одинаковый размер + одинаковый SHA-256);
///   • сохранённые прежние редакции «имя (до ГГГГ-ММ-ДД).ext» (см. ParamFileUploadService) не
///     трогаются НИКОГДА — они для того и оставлены, и они как раз почти всегда отличаются;
///   • всё, что вызвало сомнение (не совпало побайтово, не прочиталось, не удалилось), не удаляется,
///     а попадает в Skipped — чтобы человек увидел это в отчёте и решил сам.</summary>
public static class ParamFileDuplicateCleanup
{
    public record Result(List<string> Removed, List<string> Skipped)
    {
        public bool Any => Removed.Count > 0 || Skipped.Count > 0;
    }

    /// <summary>Ключ разовой чистки в settings — чтобы полный проход по всем записям делался один
    /// раз, а не на каждом открытии страницы (он ходит по сетевому диску).</summary>
    public const string DoneFlagKey = "migration_param_disk_duplicates_cleaned";

    /// <summary>Чистка одной папки: рядом с <paramref name="filename"/> ищутся файлы-двойники с
    /// скобочным суффиксом и удаляются, если побайтово равны оригиналу.</summary>
    public static Result CleanFolder(string folder, string filename)
    {
        var removed = new List<string>();
        var skipped = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(filename)) return new Result(removed, skipped);

        string original;
        try
        {
            if (!Directory.Exists(folder)) return new Result(removed, skipped);
            original = Path.Combine(folder, filename);
            if (!File.Exists(original)) return new Result(removed, skipped);
        }
        catch (Exception ex)
        {
            skipped.Add($"{folder}: {ex.Message}");
            return new Result(removed, skipped);
        }

        byte[] originalHash;
        long originalLength;
        try
        {
            originalLength = new FileInfo(original).Length;
            originalHash = HashFile(original);
        }
        catch (Exception ex)
        {
            skipped.Add($"{original}: {ex.Message}");
            return new Result(removed, skipped);
        }

        foreach (var candidate in FindSuffixedSiblings(folder, filename, skipped))
        {
            try
            {
                if (new FileInfo(candidate).Length != originalLength)
                {
                    skipped.Add($"{candidate}: размер отличается от основного файла — не копия, оставлен");
                    continue;
                }
                if (!originalHash.AsSpan().SequenceEqual(HashFile(candidate)))
                {
                    skipped.Add($"{candidate}: содержимое отличается от основного файла — не копия, оставлен");
                    continue;
                }
                File.Delete(candidate);
                removed.Add(candidate);
            }
            catch (Exception ex)
            {
                skipped.Add($"{candidate}: {ex.Message}");
            }
        }

        return new Result(removed, skipped);
    }

    /// <summary>Файлы «{имя без расширения} (…){расширение}» в той же папке. Сохранённые прежние
    /// редакции отсеиваются здесь же — см. ParamFileUploadService.IsArchivedPreviousName.</summary>
    private static List<string> FindSuffixedSiblings(string folder, string filename, List<string> skipped)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        var ext = Path.GetExtension(filename);
        var result = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(path);
                if (string.Equals(name, filename, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(Path.GetExtension(name), ext, StringComparison.OrdinalIgnoreCase)) continue;

                var candidateStem = Path.GetFileNameWithoutExtension(name);
                if (!candidateStem.StartsWith(stem + " (", StringComparison.OrdinalIgnoreCase)) continue;
                if (!candidateStem.EndsWith(")", StringComparison.Ordinal)) continue;
                if (ParamFileUploadService.IsArchivedPreviousName(name)) continue;

                result.Add(path);
            }
        }
        catch (Exception ex)
        {
            skipped.Add($"{folder}: {ex.Message}");
        }
        return result;
    }

    /// <summary>Убирает ПОЛНУЮ копию файла, случайно оказавшуюся в папке другого подтипа вместо
    /// ярлыка на общий файл. Удаляет только при доказанном побайтовом совпадении с оригиналом; в
    /// любом другом случае (отличается, не читается, не удаляется) возвращает false и не трогает
    /// ничего — вызывающий код показывает это оператору как предупреждение.</summary>
    public static bool TryRemoveIdenticalCopy(string originalFullPath, string copyFullPath, out string? reason)
    {
        reason = null;
        try
        {
            if (string.Equals(Path.GetFullPath(originalFullPath), Path.GetFullPath(copyFullPath), StringComparison.OrdinalIgnoreCase))
                return false;
            if (!File.Exists(originalFullPath) || !File.Exists(copyFullPath)) return false;
            if (new FileInfo(originalFullPath).Length != new FileInfo(copyFullPath).Length)
            {
                reason = $"{copyFullPath}: отличается от общего файла — оставлен, разберитесь вручную";
                return false;
            }
            if (!HashFile(originalFullPath).AsSpan().SequenceEqual(HashFile(copyFullPath)))
            {
                reason = $"{copyFullPath}: отличается от общего файла — оставлен, разберитесь вручную";
                return false;
            }
            File.Delete(copyFullPath);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{copyFullPath}: {ex.Message}";
            return false;
        }
    }

    private static byte[] HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return sha.ComputeHash(stream);
    }

    /// <summary>Полный проход по всем живым записям параметров: каждая уникальная пара
    /// (папка на диске, имя файла) чистится один раз.
    ///
    /// Записи БД, оставшиеся от удалённых двойников, архивируются — иначе в таблице висела бы строка
    /// на файл, которого больше нет, а с новой синхронизацией она уехала бы к коллегам как «живая».
    /// Сверка идёт по ПОЛНОМУ пути (папка + имя), а не по одному имени: одноимённый файл в папке
    /// другого подтипа/производителя — самостоятельная запись, её трогать нельзя.
    ///
    /// <paramref name="paramsRoot"/> (необязательный, «…\Параметры») — если передан, дополнительно
    /// убираются ярлыки «{имя двойника}.lnk», указывавшие на только что удалённый двойник: их клали
    /// в папки дополнительных подтипов, и без этого там остались бы битые ярлыки. Удаляется строго
    /// файл с точным именем удалённого двойника плюс «.lnk» — ничего другого этот проход не трогает.</summary>
    public static Result CleanAll(Database db, string? paramsRoot = null)
    {
        var result = CleanFolders(Targets(db), paramsRoot);
        ArchiveRemovedRows(db, result.Removed);
        return result;
    }

    /// <summary>Пары «папка + имя файла», которые имеет смысл чистить — по одной на каждый реальный
    /// файл. Отдельно от <see cref="CleanFolders"/>, чтобы UI мог собрать их в потоке БД, а сам
    /// обход сетевого диска увести в фон (Database — одно соединение SQLite, ходить в него из двух
    /// потоков сразу нельзя).</summary>
    public static List<(string Folder, string Filename)> Targets(Database db) =>
        db.GetParamFiles()
            .Where(r => !string.IsNullOrWhiteSpace(r.DiskPath) && !string.IsNullOrWhiteSpace(r.Filename))
            .Select(r => (Folder: r.DiskPath, r.Filename))
            .Distinct()
            .ToList();

    /// <summary>Чисто дисковая часть: никакой БД, можно спокойно звать из фонового потока.</summary>
    public static Result CleanFolders(IEnumerable<(string Folder, string Filename)> targets, string? paramsRoot = null)
    {
        var removed = new List<string>();
        var skipped = new List<string>();
        foreach (var (folder, filename) in targets)
        {
            var result = CleanFolder(folder, filename);
            removed.AddRange(result.Removed);
            skipped.AddRange(result.Skipped);
        }
        if (removed.Count > 0) RemoveDanglingShortcuts(paramsRoot, removed.Select(Path.GetFileName)!, skipped);
        return new Result(removed, skipped);
    }

    /// <summary>Архивирует записи, стоявшие за удалёнными двойниками. Сверка по ПОЛНОМУ пути
    /// (папка + имя): одноимённый файл в папке другого подтипа/производителя — самостоятельная
    /// запись, её трогать нельзя.</summary>
    public static void ArchiveRemovedRows(Database db, IReadOnlyCollection<string> removedFullPaths)
    {
        if (removedFullPaths.Count == 0) return;
        var removed = new HashSet<string>(removedFullPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var row in db.GetParamFiles())
        {
            if (row.Id is null || string.IsNullOrWhiteSpace(row.DiskPath) || string.IsNullOrWhiteSpace(row.Filename)) continue;
            if (removed.Contains(Path.Combine(row.DiskPath, row.Filename)))
                db.DeleteParamFile(row.Id.Value);
        }
    }

    private static void RemoveDanglingShortcuts(string? paramsRoot, IEnumerable<string> removedNames, List<string> skipped)
    {
        if (string.IsNullOrWhiteSpace(paramsRoot)) return;
        var wanted = new HashSet<string>(removedNames.Select(n => n + ".lnk"), StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(paramsRoot)) return;
            foreach (var link in Directory.EnumerateFiles(paramsRoot, "*.lnk", SearchOption.AllDirectories))
            {
                if (!wanted.Contains(Path.GetFileName(link))) continue;
                try { File.Delete(link); }
                catch (Exception ex) { skipped.Add($"{link}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            skipped.Add($"{paramsRoot}: {ex.Message}");
        }
    }
}
