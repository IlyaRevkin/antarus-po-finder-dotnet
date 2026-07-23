using System;
using System.Collections.Generic;
using System.IO;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что оператор хочет доложить/заменить у УЖЕ загруженной версии. null в поле — «не трогать
/// это вложение», пустая строка — «убрать ссылку» (файлы на диске при этом не удаляются: их могут
/// использовать другие версии того же контроллера — все три «карты»/инструкция лежат в общих папках
/// контроллера, а не внутри папки версии).</summary>
public class FirmwareAttachmentsRequest
{
    public string RootPath { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string SubtypeName { get; set; } = "";
    public string ControllerName { get; set; } = "";

    public string? IoMapSourcePath { get; set; }
    public string? InstructionsSourcePath { get; set; }
    public string? ModbusMapSourcePath { get; set; }
    public string? HmiSourcePath { get; set; }
}

/// <summary>Applied — человекочитаемые названия того, что реально изменилось (для статуса/тоста),
/// Warnings — не фатальные проблемы отдельных вложений: остальные всё равно применяются.</summary>
public record FirmwareAttachmentsResult(List<string> Applied, List<string> Warnings)
{
    public bool AnythingChanged => Applied.Count > 0;
}

/// <summary>Доп. файлы (Карта in/out, Карта modbus, Инструкция, HMI-проект) можно было приложить
/// ТОЛЬКО в момент загрузки новой версии прошивки: если карту прислали позже, единственным способом
/// было перезалить версию заново. Этот сервис делает ту же работу, что и соответствующая часть
/// FirmwareUploadService.Upload, но для существующей записи fw_versions — и используется из
/// «Настройки → Прошивки → Изменить» (EditFirmwareDialog).
///
/// Копирование намеренно то же самое, что при загрузке (FileSystemHelpers.CopyFileOrFolderShallow в
/// общие папки контроллера, HMI — в свою папку версии), чтобы «доложенный» файл лежал ровно там же,
/// где лежал бы, если бы его приложили сразу.</summary>
public static class FirmwareAttachmentsService
{
    /// <summary>Копирует HMI-проект в папку HMI контроллера под именем «{версия}_hmi» — общий код
    /// для загрузки новой версии (FirmwareUploadService.Upload) и для догрузки к существующей.
    /// Возвращает путь, который надо записать в fw_versions.hmi_path.
    ///
    /// replaceExisting=false (загрузка) — файлы докладываются поверх, как было исторически;
    /// replaceExisting=true (замена HMI у существующей версии) — старая папка проекта сносится
    /// целиком, иначе от предыдущего проекта остались бы «висящие» файлы, которых в новом нет.</summary>
    public static string CopyHmiProject(string hmiRootFolder, string versionRaw, string sourcePath, bool replaceExisting = false)
    {
        Directory.CreateDirectory(hmiRootFolder);
        if (Directory.Exists(sourcePath))
        {
            var hmiDstFolder = Path.Combine(hmiRootFolder, $"{versionRaw}_hmi");
            if (replaceExisting && Directory.Exists(hmiDstFolder)) FileSystemHelpers.RmtreeSafe(hmiDstFolder);
            Directory.CreateDirectory(hmiDstFolder);
            FileSystemHelpers.CopyTree(sourcePath, hmiDstFolder, overwrite: false);
            return hmiDstFolder;
        }
        var hmiDstName = $"{versionRaw}_hmi{Path.GetExtension(sourcePath)}";
        var dst = Path.Combine(hmiRootFolder, hmiDstName);
        File.Copy(sourcePath, dst, overwrite: true);
        return dst;
    }

    public static FirmwareAttachmentsResult Apply(Database db, HierarchyService hierarchy,
        FwVersionRecord record, FirmwareAttachmentsRequest request)
    {
        var applied = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(request.RootPath) || !Directory.Exists(request.RootPath))
            return new FirmwareAttachmentsResult(applied, new List<string> { "Сетевой диск недоступен — доп. файлы не изменены." });

        string root = request.RootPath, g = request.GroupName, s = request.SubtypeName, c = request.ControllerName;

        string? ioMap = Resolve("Карта in/out", request.IoMapSourcePath, record.IoMapPath,
            () => hierarchy.IoMapPath(root, g, s, c), applied, warnings);
        string? modbus = Resolve("Карта modbus", request.ModbusMapSourcePath, record.ModbusMapPath,
            () => hierarchy.ModbusMapPath(root, g, s, c), applied, warnings);
        string? instr = Resolve("Инструкция", request.InstructionsSourcePath, record.InstructionsPath,
            () => hierarchy.InstrPath(root, g, s, c), applied, warnings);

        string? hmi = null;
        if (request.HmiSourcePath is not null && !PathsEqual(request.HmiSourcePath, record.HmiPath))
        {
            if (request.HmiSourcePath.Length == 0)
            {
                hmi = "";
                applied.Add("HMI-проект (ссылка убрана)");
            }
            else if (!File.Exists(request.HmiSourcePath) && !Directory.Exists(request.HmiSourcePath))
            {
                warnings.Add($"HMI-проект: путь не найден — {request.HmiSourcePath}");
            }
            else
            {
                try
                {
                    hmi = CopyHmiProject(hierarchy.HmiPath(root, g, s, c), record.VersionRaw, request.HmiSourcePath, replaceExisting: true);
                    applied.Add("HMI-проект");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"HMI-проект: {ex.Message}");
                }
            }
        }

        if (ioMap is null && modbus is null && instr is null && hmi is null)
            return new FirmwareAttachmentsResult(applied, warnings);

        db.UpdateFwVersionAttachments(record.Id!.Value, ioMap, instr, modbus, hmi);
        if (ioMap is not null) record.IoMapPath = ioMap;
        if (instr is not null) record.InstructionsPath = instr;
        if (modbus is not null) record.ModbusMapPath = modbus;
        if (hmi is not null) record.HmiPath = hmi;

        return new FirmwareAttachmentsResult(applied, warnings);
    }

    /// <summary>Возвращает новое значение поля для записи в БД, или null — если менять нечего.</summary>
    private static string? Resolve(string label, string? requested, string current, Func<string> destFolder,
        List<string> applied, List<string> warnings)
    {
        if (requested is null || PathsEqual(requested, current)) return null;

        if (requested.Length == 0)
        {
            applied.Add($"{label} (ссылка убрана)");
            return "";
        }
        if (!File.Exists(requested) && !Directory.Exists(requested))
        {
            warnings.Add($"{label}: путь не найден — {requested}");
            return null;
        }
        try
        {
            var stored = FileSystemHelpers.CopyFileOrFolderShallow(requested, destFolder());
            applied.Add(label);
            return stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{label}: {ex.Message}");
            return null;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.Trim().TrimEnd(Path.DirectorySeparatorChar), b.Trim().TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
