using System.Collections.Generic;
using System.IO;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что оператор хочет ДОЛОЖИТЬ к версии: файл, вид из справочника и произвольный
/// комментарий. Вид и комментарий здесь равноправны с самим файлом — по ним его потом и находят
/// поиском («краткое руководство для наладчика»), см. Database.Search.cs.</summary>
public record FirmwareExtraFileAdd(string SourcePath, string Kind, string Comment);

/// <summary>Правка уже приложенного: только вид и комментарий (файл не подменяется — для другого
/// файла заводится другое вложение).</summary>
public record FirmwareExtraFileEdit(int AttachmentId, string Kind, string Comment);

/// <summary>Applied — человекочитаемое перечисление того, что реально изменилось (для статуса),
/// Warnings — не фатальные проблемы отдельных файлов: остальные всё равно применяются. Ровно тот же
/// контракт, что у FirmwareAttachmentsResult рядом.</summary>
public record FirmwareExtraFilesResult(List<string> Applied, List<string> Warnings)
{
    public bool AnythingChanged => Applied.Count > 0;
}

/// <summary>Доп. материалы к прошивке: «краткое руководство для наладчика», «специфика работы»,
/// «прошивка ПЛК поставщика» (внутренний алгоритм самого ПЛК, а не наша программа) и всё прочее в
/// таком духе — дословная просьба Ильи. Свободный СПИСОК вложений, а не фиксированные слоты рядом с
/// картами и инструкцией: набор таких файлов заранее не известен, и следующий по счёту вид не должен
/// требовать нового релиза.
///
/// Стоит рядом с <see cref="FirmwareAttachmentsService"/> и работает тем же способом: файл кладётся в
/// папку, которую назовёт <see cref="VersionLayout"/> (своя папка внутри перестроенной версии, общая
/// папка контроллера у прежней — см. VersionLayout.ExtrasWriteFolder), а запись о нём — в БД
/// (Database.FwAttachments.cs). Ничего не собирается из Path.Combine мимо VersionLayout.</summary>
public static class FirmwareExtraFilesService
{
    /// <summary>Куда класть доп. материалы ЭТОЙ версии. Спрашивается в момент операции, а не
    /// запоминается: раскладка версии может измениться между двумя правками одной карточки (диск
    /// перестроили) — тот же приём, что в FirmwareAttachmentsService.Apply.</summary>
    public static string TargetFolder(FwVersionRecord record, string root, string groupName, string subtypeName,
        string controllerName)
    {
        var versionDir = FirmwarePathLocalizer.Localize(record.DiskPath, root);
        var ctrlFolder = Path.Combine(HierarchyService.GroupSubFolder(root, groupName, subtypeName), controllerName);
        return VersionLayout.ExtrasWriteFolder(versionDir, ctrlFolder);
    }

    /// <summary>Применяет разом всё, что набрал оператор в карточке: снятия, правки вида/комментария и
    /// новые файлы. Одним методом, потому что диалог сохраняется целиком, а не по строчке.
    ///
    /// Порядок намеренный: сначала снятия (иначе только что доложенный файл мог бы схлопнуться с
    /// удаляемым одноимённым), потом правки, потом копирование новых.</summary>
    public static FirmwareExtraFilesResult Apply(Database db, FwVersionRecord record, string root,
        string groupName, string subtypeName, string controllerName, string? author,
        IEnumerable<int>? removedIds = null, IEnumerable<FirmwareExtraFileEdit>? edits = null,
        IEnumerable<FirmwareExtraFileAdd>? added = null)
    {
        var applied = new List<string>();
        var warnings = new List<string>();

        foreach (var id in removedIds ?? Array.Empty<int>())
        {
            var attachment = db.GetFwAttachment(id);
            if (attachment is null || !string.IsNullOrEmpty(attachment.DeletedAt)) continue;

            db.TombstoneFwAttachment(id);
            DeleteFileIfUnused(db, attachment, root, warnings);
            applied.Add($"убрано: {attachment.Filename}");
        }

        foreach (var edit in edits ?? Array.Empty<FirmwareExtraFileEdit>())
        {
            var attachment = db.GetFwAttachment(edit.AttachmentId);
            if (attachment is null || !string.IsNullOrEmpty(attachment.DeletedAt)) continue;

            var kind = (edit.Kind ?? "").Trim();
            var comment = (edit.Comment ?? "").Trim();
            if (kind == attachment.Kind && comment == attachment.Comment) continue;

            db.UpdateFwAttachment(edit.AttachmentId, kind, comment);
            if (kind.Length > 0) db.AddFwAttachmentKind(kind);
            applied.Add($"изменено: {attachment.Filename}");
        }

        var toAdd = new List<FirmwareExtraFileAdd>(added ?? Array.Empty<FirmwareExtraFileAdd>());
        if (toAdd.Count == 0) return new FirmwareExtraFilesResult(applied, warnings);

        if (record.Id is null) return new FirmwareExtraFilesResult(applied, warnings);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            warnings.Add("Сетевой диск недоступен — доп. материалы не добавлены.");
            return new FirmwareExtraFilesResult(applied, warnings);
        }

        var folder = TargetFolder(record, root, groupName, subtypeName, controllerName);
        foreach (var add in toAdd)
        {
            var src = (add.SourcePath ?? "").Trim();
            if (src.Length == 0) continue;
            if (!File.Exists(src))
            {
                warnings.Add($"Доп. материал: файл не найден — {src}");
                continue;
            }

            string stored;
            try
            {
                stored = CopyWithoutOverwriting(src, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Доп. материал ({Path.GetFileName(src)}): {ex.Message}");
                continue;
            }

            var kind = (add.Kind ?? "").Trim();
            db.AddFwAttachment(new FwAttachment
            {
                FwVersionId = record.Id.Value,
                Filename = Path.GetFileName(stored),
                DiskPath = stored,
                Kind = kind,
                Comment = (add.Comment ?? "").Trim(),
                AddedBy = author ?? "",
            });
            if (kind.Length > 0) db.AddFwAttachmentKind(kind);
            applied.Add($"добавлено: {Path.GetFileName(stored)}");
        }

        return new FirmwareExtraFilesResult(applied, warnings);
    }

    /// <summary>Копирует файл в папку доп. материалов, СОХРАНЯЯ его имя, и никогда не затирая
    /// одноимённый молча: у уже занятого имени к нему приписывается « (2)», « (3)»… Имя осмысленное —
    /// файл открывают и глазами ищут в проводнике, поэтому переименовывать его в «версия_что-то»
    /// нельзя; но и подменять чужой файл с тем же именем тоже нельзя, а у неперестроенной версии
    /// папка доп. материалов ОБЩАЯ на весь контроллер, так что совпадение имён там — обычное дело.
    ///
    /// Выбранный файл уже лежит на своём месте (оператор ткнул в него же через диалог, который
    /// открывается в этой самой папке) — ничего не копируем: File.Copy сам в себя Windows отвергает
    /// как «файл занят другим процессом» (на этих же граблях уже стояла модерация, см.
    /// FirmwareAttachmentsService.CopyFirmwareFileIntoVersionFolder).</summary>
    internal static string CopyWithoutOverwriting(string src, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(src);
        var direct = Path.Combine(folder, name);
        if (SamePath(src, direct)) return direct;

        var dst = direct;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; File.Exists(dst); n++)
            dst = Path.Combine(folder, $"{stem} ({n}){ext}");

        File.Copy(src, dst, overwrite: false);
        return dst;
    }

    /// <summary>Убирает файл с диска вслед за снятым вложением — но только если на него не
    /// ссылается другое живое вложение (у неперестроенной версии папка общая на весь контроллер, и
    /// один файл вполне может быть приложен к нескольким версиям). Не получилось — не беда: запись
    /// уже снята, а оставшийся файл это занятое место, а не потеря данных.
    ///
    /// Делает это ТОЛЬКО оператор своей рукой. Приехавшее по синхронизации снятие файлы не трогает
    /// (см. Database.ConfigExchange, секция fw_attachments): у коллеги тот же файл может быть
    /// единственной копией.</summary>
    private static void DeleteFileIfUnused(Database db, FwAttachment attachment, string root, List<string> warnings)
    {
        if (attachment.Id is null || string.IsNullOrWhiteSpace(attachment.DiskPath)) return;
        if (db.IsAttachmentFileSharedByOthers(attachment.DiskPath, attachment.Id.Value)) return;

        var path = FirmwarePathLocalizer.Localize(attachment.DiskPath, root);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Файл {attachment.Filename} остался на диске: {ex.Message}");
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
