using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Куда ложится шаблон паспорта шкафа и как заводится/обновляется его запись.
///
/// Раскладка на диске: ПО\&lt;тип&gt;[\&lt;подтип&gt;]\Паспорт\&lt;название&gt;\&lt;файл&gt; — общая папка «Паспорт»
/// принадлежит подтипу (см. HierarchyFolders.Passports), внутри по подпапке на каждый паспорт. Своя
/// подпапка нужна ровно затем, чтобы у одного подтипа могло быть несколько паспортов с одинаковым
/// именем файла («Паспорт.docx» под разные исполнения шкафа) и чтобы рядом с документом лежал его
/// PDF для печати, не мешаясь с чужими.
///
/// Перезаливка ведёт себя ровно как у файлов параметров (Илья просил именно так: «без истории как у
/// прошивок, просто дата загрузки, всегда открывать свежую, а кому нужна старая — пусть откроет
/// папку»): прежний файл уезжает в подпапку «Прежние редакции» под именем «имя (до ГГГГ-ММ-ДД).ext»,
/// новый ложится под своим именем, запись ОБНОВЛЯЕТСЯ (свежая дата + датированная строка в описании),
/// а не плодится второй строкой. Обе операции переиспользованы из ParamFileUploadService — там они
/// написаны без единой отсылки к параметрам, и вторая копия того же кода разошлась бы с оригиналом.</summary>
public static class PassportService
{
    /// <summary>Имя подпапки паспорта: название, как его ввёл оператор, с заменой символов, которые
    /// файловая система не примет. Пустое/полностью «неудобное» название — «Паспорт», чтобы папка
    /// всё равно получилась (валидацию непустого названия делает форма загрузки).</summary>
    public static string FolderName(string name)
    {
        var cleaned = new string((name ?? "").Trim()
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
        // Точка в конце имени папки Windows молча отбрасывает — уберём сами, чтобы записанный в БД
        // путь совпадал с тем, что реально появилось на диске.
        cleaned = cleaned.TrimEnd('.', ' ');
        return cleaned.Length == 0 ? HierarchyFolders.Passports : cleaned;
    }

    /// <summary>Папка конкретного паспорта на диске.</summary>
    public static string Folder(HierarchyService hierarchy, string root, string groupName, string subName, string name) =>
        Path.Combine(hierarchy.PassportsPath(root, groupName, subName), FolderName(name));

    /// <summary>Заводит или обновляет запись паспорта. Существующая ищется по натуральному ключу
    /// «подтип + название» среди живых — это и есть «тот же самый паспорт, перезалитый заново»;
    /// sync_id при обновлении сохраняется, чтобы для коллег это осталась ОДНА строка (см.
    /// Database.ConfigExchange.cs).</summary>
    /// <param name="archivedPreviousName">Имя, под которым сохранена прежняя редакция, либо null.</param>
    public static PassportSaveOutcome SaveRecord(Database db, PassportTemplate record,
        string? archivedPreviousName, DateTime now)
    {
        record.UploadDate = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var existing = record.SubtypeId is null ? null : db.FindLivePassport(record.SubtypeId.Value, record.Name);
        if (existing?.Id is null)
        {
            var id = db.AddPassport(record);
            return new PassportSaveOutcome(id, Updated: false, archivedPreviousName);
        }

        var description = ParamFileUploadService.AppendChangeLog(existing.Description, record.Description, now, archivedPreviousName);
        record.Id = existing.Id;
        record.SyncId = existing.SyncId;
        record.Description = description;
        db.UpdatePassportUpload(existing.Id.Value, record.DiskPath, record.Filename, description, record.UploadDate);
        return new PassportSaveOutcome(existing.Id.Value, Updated: true, archivedPreviousName);
    }

    /// <summary>Что реально лежит в папке паспорта: исходный docx (правка) и PDF (печать), плюс
    /// признак «PDF устарел относительно docx». Тот же резолвер, что у инструкции — задача
    /// дословно та же (docx рядом с собранным из него pdf в одной папке), и разводить два
    /// одинаковых резолвера было бы копией ради имени типа.
    ///
    /// Подпапка с прежними редакциями из рассмотрения исключена: файл копируется на диск со СВОЕЙ
    /// датой изменения, а не с датой загрузки, поэтому убранная в архив редакция вполне может
    /// оказаться «свежее» актуальной — и открывалась бы вместо неё.
    ///
    /// <paramref name="localRoot"/> — корень ЭТОЙ машины: путь в записи абсолютный и записан тем,
    /// кто грузил (у него шара могла быть смонтирована другой буквой).</summary>
    public static InstructionDoc ResolveDoc(PassportTemplate passport, string localRoot)
    {
        var folder = FirmwarePathLocalizer.Localize(passport.DiskPath, localRoot);
        var stored = string.IsNullOrEmpty(passport.Filename) ? null : Path.Combine(folder, passport.Filename);
        return InstructionDocResolver.Resolve(stored, folder, ParamFileUploadService.ArchiveFolderName);
    }
}

/// <param name="RecordId">Id записи passports: обновлённой либо только что заведённой.</param>
/// <param name="Updated">true — существующая запись обновлена (перезаливка), false — заведена новая.</param>
/// <param name="ArchivedPreviousName">Имя, под которым сохранена прежняя редакция, либо null.</param>
public record PassportSaveOutcome(int RecordId, bool Updated, string? ArchivedPreviousName);
