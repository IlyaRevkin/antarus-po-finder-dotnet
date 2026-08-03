using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Загрузка и — главное — ПЕРЕЗАЛИВКА файла параметров под тем же именем.
///
/// Как было: File.Copy(overwrite: true) физически затирал прежний файл (прежняя редакция параметров
/// исчезала с диска безвозвратно), а в БД заводилась ЕЩЁ ОДНА строка тем же натуральным ключом —
/// таблица обрастала дублями, которые потом схлопывал DedupeParamFiles, теряя при этом свежую дату
/// и описание.
///
/// Как стало (полная история версий, как у прошивок, здесь сознательно НЕ заводится — Илья просил
/// именно «просто дату загрузки, всегда открывать свежую, а кому нужна старая — пусть откроет папку
/// и найдёт»):
///   • прежний файл переименовывается в «имя (до ГГГГ-ММ-ДД).ext» и ОСТАЁТСЯ в той же папке —
///     открыть его можно через «Открыть папку», в программе он не мешается;
///   • новый файл кладётся под исходным именем, поэтому «Открыть» всегда ведёт на свежий;
///   • запись в param_files ОБНОВЛЯЕТСЯ (свежая дата загрузки), а не плодится;
///   • в «Описание» дописывается датированная строка-лог, чтобы было видно, что и когда менялось.</summary>
public static class ParamFileUploadService
{
    /// <summary>Что произошло при загрузке — для сообщения оператору и для тестов.</summary>
    /// <param name="RecordId">Id записи param_files: обновлённой либо только что заведённой.</param>
    /// <param name="Updated">true — существующая запись обновлена (перезаливка), false — заведена новая.</param>
    /// <param name="ArchivedPreviousName">Имя, под которым сохранён прежний файл, либо null, если
    /// прежнего файла в папке не было.</param>
    public record UploadOutcome(int RecordId, bool Updated, string? ArchivedPreviousName);

    /// <summary>Помечает уже лежащий в папке одноимённый файл как прежнюю редакцию: «имя (до
    /// ГГГГ-ММ-ДД).ext». Дата — та, НА которую редакция была актуальна (день перезаливки), поэтому
    /// «до», а не «от». Возвращает новое имя файла либо null, если переименовывать было нечего.
    ///
    /// Вызывается ДО копирования нового файла. Если имя «до …» уже занято (в один день перезалили
    /// дважды), добавляется порядковый номер — прежняя редакция не затирается никогда, в этом весь
    /// смысл операции.</summary>
    public static string? ArchivePreviousOnDisk(string folder, string filename, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(filename)) return null;
        var current = Path.Combine(folder, filename);
        if (!File.Exists(current)) return null;

        var stem = Path.GetFileNameWithoutExtension(filename);
        var ext = Path.GetExtension(filename);
        var stamp = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var candidateName = $"{stem} (до {stamp}){ext}";
        var counter = 1;
        while (File.Exists(Path.Combine(folder, candidateName)))
            candidateName = $"{stem} (до {stamp}, {++counter}){ext}";

        File.Move(current, Path.Combine(folder, candidateName));
        return candidateName;
    }

    /// <summary>Признак «это сохранённая прежняя редакция, а не самостоятельный файл» — по нему
    /// разовая чистка дублей (<see cref="ParamFileDuplicateCleanup"/>) отличает намеренно оставленный
    /// файл от случайной копии и НИКОГДА его не удаляет.</summary>
    public static bool IsArchivedPreviousName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var open = stem.LastIndexOf(" (до ", StringComparison.Ordinal);
        return open >= 0 && stem.EndsWith(")", StringComparison.Ordinal);
    }

    /// <summary>Заводит или обновляет запись о файле параметров. Существующая ищется по натуральному
    /// ключу (подтип + производитель + имя файла) среди живых — это и есть «тот же самый файл,
    /// перезалитый заново». sync_id при обновлении сохраняется: для коллег это ОДНА и та же запись,
    /// у которой поменялись дата и описание, а не новая (см. Database.ConfigExchange.cs).
    ///
    /// Обновляются ВСЕ записи, стоящие за этим файлом на диске — по одной на каждый привязанный
    /// подтип (см. ParamFileLinkService): файл общий, поэтому и дата с описанием у них общие; иначе
    /// в таблице у дополнительных подтипов навсегда осталась бы дата первой загрузки.</summary>
    public static UploadOutcome SaveRecord(Database db, ParamFile record, string? archivedPreviousName, DateTime now)
    {
        record.UploadDate = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var existing = record.SubtypeId is null
            ? null
            : db.FindLiveParamFile(record.SubtypeId.Value, record.Manufacturer, record.Filename);
        if (existing?.Id is null)
        {
            var id = db.AddParamFile(record);
            return new UploadOutcome(id, Updated: false, archivedPreviousName);
        }

        var description = AppendChangeLog(existing.Description, record.Description, now, archivedPreviousName);
        record.Id = existing.Id;
        record.SyncId = existing.SyncId;
        record.Description = description;

        // Все строки этого же файла (основной подтип + дополнительные) — у них общий disk_path и имя.
        var shared = db.GetParamFilesSharingFile(existing.DiskPath, existing.Filename);
        var updatedIds = new HashSet<int> { existing.Id.Value };
        db.UpdateParamFileUpload(existing.Id.Value, record.DiskPath, description, record.UploadDate);
        foreach (var row in shared)
        {
            if (row.Id is null || !updatedIds.Add(row.Id.Value)) continue;
            // disk_path дополнительных строк тот же, что у основной — файл физически один.
            db.UpdateParamFileUpload(row.Id.Value, record.DiskPath, description, record.UploadDate);
        }

        return new UploadOutcome(existing.Id.Value, Updated: true, archivedPreviousName);
    }

    /// <summary>Дописывает в «Описание» датированную строку о том, что изменилось. Описание — это
    /// журнал: старый текст никогда не затирается, новый добавляется снизу. Если оператор при
    /// перезаливке ничего не написал, всё равно фиксируется сам факт и имя сохранённой прежней
    /// редакции — иначе через полгода не понять, почему в папке лежат три файла.</summary>
    public static string AppendChangeLog(string existingDescription, string newComment, DateTime now,
        string? archivedPreviousName)
    {
        var stamp = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var line = new StringBuilder($"[{stamp}] Перезалит файл");
        var comment = (newComment ?? "").Trim();
        if (comment.Length > 0) line.Append(": ").Append(comment);
        if (!string.IsNullOrEmpty(archivedPreviousName))
            line.Append(". Прежняя редакция сохранена как «").Append(archivedPreviousName).Append('»');
        line.Append('.');

        var head = (existingDescription ?? "").TrimEnd();
        return head.Length == 0 ? line.ToString() : head + Environment.NewLine + line;
    }
}
