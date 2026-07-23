using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Services;

/// <summary>Everything UploadView.Upload_Click collects from the form before it can build a firmware
/// version: the source file/folder, the chosen group/subtype/controller, and every optional
/// attachment/ОПЦ field. Nullable fields mean "not selected / not filled in" exactly like an empty
/// combo/textbox did in the view. ConfirmUnknownExtension/ConfirmOverwriteExisting are how the caller
/// answers a <see cref="FirmwareUploadOutcome.NeedsConfirmation"/> result — re-submit the same request
/// with the flag the user just agreed to set to true (see FirmwareUploadService.Upload doc).</summary>
public class FirmwareUploadRequest
{
    /// <summary>File or folder path picked/dropped in the main drop zone.</summary>
    public string SourcePath { get; set; } = "";

    public EquipmentGroup? Group { get; set; }
    public EquipmentSubType? Subtype { get; set; }
    public ControllerModification? Modification { get; set; }

    public List<string> LaunchTypes { get; set; } = new();
    public string Description { get; set; } = "";

    /// <summary>"Дата/время в номере версии" checkbox.</summary>
    public bool IncludeDateInVersion { get; set; } = true;

    public bool OpcRequestEnabled { get; set; }
    public string RequestNumRaw { get; set; } = "";
    public bool OpcSnEnabled { get; set; }
    public string CabinetSnRaw { get; set; } = "";

    /// <summary>If set, consumes this reservation's EXACT locked-in version number instead of
    /// computing the next free one — see ReserveVersion_Click's doc for why that matters.</summary>
    public FwVersionReservation? Reservation { get; set; }

    /// <summary>Network drive root (ConfigService.RootPath()) — the caller resolves and validates
    /// this once outside the service so a stale/misconfigured path fails the same way it always has.</summary>
    public string RootPath { get; set; } = "";

    public string IoMapSourcePath { get; set; } = "";
    public string InstructionsSourcePath { get; set; } = "";
    public string ModbusMapSourcePath { get; set; } = "";

    public bool HmiEnabled { get; set; }
    public string HmiSourcePath { get; set; } = "";

    /// <summary>Set by the view via PromptExecutableHint when the main/HMI source is a folder with no
    /// unambiguous recognized-extension file inside — purely a display hint, never used for copying.</summary>
    public string ExecutableHint { get; set; } = "";
    public string HmiExecutableHint { get; set; } = "";

    /// <summary>Manual tags from TagsEditor — the group/subtype/controller auto-tags are added on top
    /// of these by the service itself (see Upload), matching Upload_Click's original behavior.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Used for both fw_versions.author_id (via GetOrCreateUser) and the "reserved_by"-style
    /// audit trail — mirrors AppServices.CurrentUserName being passed as both windowsLogin and name.</summary>
    public string AuthorUserName { get; set; } = "";

    /// <summary>Set to true and re-submit after the user confirms "точно загрузить файл с неизвестным
    /// расширением?" — see FirmwareUploadOutcome.NeedsConfirmation.</summary>
    public bool ConfirmUnknownExtension { get; set; }

    /// <summary>Set to true and re-submit after the user confirms "папка версии уже существует,
    /// перезаписать?" — see FirmwareUploadOutcome.NeedsConfirmation.</summary>
    public bool ConfirmOverwriteExisting { get; set; }
}

public enum FirmwareUploadOutcome
{
    /// <summary>Firmware version created — see Result.FwVersionId/Record/DestinationFolder.</summary>
    Success,
    /// <summary>A required field is missing/invalid — Errors has exactly one message, matching the
    /// single MessageBox Upload_Click used to show for the same problem.</summary>
    ValidationFailed,
    /// <summary>The caller must ask the user a yes/no question and re-submit the SAME request with the
    /// corresponding Confirm* flag set (see ConfirmationKind) — or abandon the upload on "no".</summary>
    NeedsConfirmation,
    /// <summary>Copying the main source file/folder into place failed (disk full, share unreachable,
    /// permissions...) — nothing was recorded in the database. IoErrorMessage is the exception message,
    /// same text the "Ошибка файла" MessageBox used to show.</summary>
    IoError,
}

public enum FirmwareConfirmationKind
{
    /// <summary>The source file's extension isn't in the configured allow-list.</summary>
    UnknownExtension,
    /// <summary>The destination version folder already exists on disk.</summary>
    OverwriteExisting,
}

public class FirmwareUploadResult
{
    public FirmwareUploadOutcome Outcome { get; init; }
    public List<string> Errors { get; init; } = new();

    public FirmwareConfirmationKind? ConfirmationKind { get; init; }
    public string? ConfirmationMessage { get; init; }

    public string? IoErrorMessage { get; init; }

    /// <summary>Non-fatal problems from optional-attachment copies (Карта ВВ/Инструкция/Карта modbus/
    /// HMI) or the CHANGELOG.md write — the main upload still succeeded despite these.</summary>
    public List<string> Warnings { get; init; } = new();

    public int FwVersionId { get; init; }
    public FwVersionRecord? Record { get; init; }
    public string? DestinationFolder { get; init; }
    public string? DestinationFilename { get; init; }

    public bool IsSuccess => Outcome == FirmwareUploadOutcome.Success;

    private static FirmwareUploadResult Fail(string message) =>
        new() { Outcome = FirmwareUploadOutcome.ValidationFailed, Errors = new List<string> { message } };

    private static FirmwareUploadResult Confirm(FirmwareConfirmationKind kind, string message) =>
        new() { Outcome = FirmwareUploadOutcome.NeedsConfirmation, ConfirmationKind = kind, ConfirmationMessage = message };

    private static FirmwareUploadResult Io(string message) =>
        new() { Outcome = FirmwareUploadOutcome.IoError, IoErrorMessage = message };

    internal static FirmwareUploadResult ValidationFailure(string message) => Fail(message);
    internal static FirmwareUploadResult Confirmation(FirmwareConfirmationKind kind, string message) => Confirm(kind, message);
    internal static FirmwareUploadResult IoFailure(string message) => Io(message);
}

/// <summary>Result of PSL/KINCO controller autodetection — see FirmwareUploadService.AutodetectFromPsl/
/// AutodetectKinco. DeviceKey is "" when nothing recognizable was found in the file at all (silent —
/// the view shows a status message only when a key WAS read but didn't match anything in the
/// catalogue).</summary>
public record FirmwareAutodetectResult(ControllerModification? Modification, string DeviceKey);

/// <summary>The upload transaction previously buried in UploadView.Upload_Click: resolves the version
/// number, copies the source file/folder and every optional attachment into the on-disk hierarchy,
/// writes CHANGELOG.md, records the fw_versions row, fulfils a reservation if one was used, and tags
/// the result — all pure/testable now that it doesn't touch a single WPF control. UploadView's job
/// after this refactor is just: collect a FirmwareUploadRequest from its form fields, call Upload,
/// and turn the FirmwareUploadResult into a MessageBox/status-bar update (see UploadView.Upload_Click).
///
/// 1:1 behavioral port — every check, its order, and every message string matches what Upload_Click
/// did before this refactor (Спринт 2, Задача 1). Two checks (unknown extension, destination folder
/// already exists) used to pop a Yes/No MessageBox mid-method and either continue or return; since the
/// service can't show UI, they instead return NeedsConfirmation and expect the caller to re-submit the
/// identical request with the matching Confirm* flag set once the user agrees — see
/// FirmwareUploadRequest.ConfirmUnknownExtension/ConfirmOverwriteExisting.</summary>
public static class FirmwareUploadService
{
    public static FirmwareUploadResult Upload(Database db, HierarchyService hierarchy, FirmwareUploadRequest request)
    {
        if (string.IsNullOrEmpty(request.SourcePath))
            return FirmwareUploadResult.ValidationFailure("Выберите файл прошивки.");

        if (request.Group is null || request.Subtype is null || request.Modification is null)
            return FirmwareUploadResult.ValidationFailure("Укажите тип шкафа, подтип и контроллер.");

        var launchTypes = request.LaunchTypes ?? new List<string>();
        if (launchTypes.Count == 0)
            return FirmwareUploadResult.ValidationFailure("Выберите хотя бы один тип пуска.");

        var desc = (request.Description ?? "").Trim();
        if (string.IsNullOrEmpty(desc))
            return FirmwareUploadResult.ValidationFailure("Укажите описание изменений в этой версии.");

        var root = request.RootPath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return FirmwareUploadResult.ValidationFailure("Сетевой диск недоступен. Проверьте настройки.");

        var group = request.Group;
        var subOption = request.Subtype;
        var mod = request.Modification;

        bool isDir = Directory.Exists(request.SourcePath);
        if (!isDir)
        {
            var ext = Path.GetExtension(request.SourcePath).TrimStart('.').ToLowerInvariant();
            var allowed = new HashSet<string>(db.GetAllowedExtensions().Select(x => x.ToLowerInvariant()));
            if (allowed.Count > 0 && !allowed.Contains(ext) && !request.ConfirmUnknownExtension)
            {
                return FirmwareUploadResult.Confirmation(FirmwareConfirmationKind.UnknownExtension,
                    $"Расширение «.{ext}» не входит в список разрешённых (Настройки → Иерархия → Разрешённые расширения).\nТочно загрузить этот файл?");
            }
        }

        bool isOpc = request.OpcRequestEnabled || request.OpcSnEnabled;
        var reqNumRaw = request.OpcRequestEnabled ? (request.RequestNumRaw ?? "").Trim() : "";
        var cabinetSnRaw = request.OpcSnEnabled ? (request.CabinetSnRaw ?? "").Trim() : "";
        var reqNum = Format5Digits(reqNumRaw);
        var cabinetSn = Format5Digits(cabinetSnRaw);
        int hwInt = mod.HwVersion;

        // If a reservation is picked, consume its EXACT locked-in number (Parse, never recompute) —
        // that's the whole point: the number inside the compiled firmware must match what gets saved.
        var reservation = request.Reservation;
        FwVersionNumber fwv;
        int swInt;
        if (reservation is not null)
        {
            fwv = FwVersionNumber.Parse(reservation.VersionRaw)!;
            swInt = fwv.SwVersion;
        }
        else
        {
            swInt = db.GetNextSwVersion(subOption.Id!.Value, mod.ControllerId, hwInt);
            fwv = FwVersionNumber.Build(group.Prefix, subOption.Prefix, hwInt, swInt, includeDate: request.IncludeDateInVersion);
        }

        var dstFolder = hierarchy.FwPath(root, group.Name, subOption.Name, mod.ControllerName, fwv.Raw, isOpc);

        if (Directory.Exists(dstFolder) && !request.ConfirmOverwriteExisting)
        {
            return FirmwareUploadResult.Confirmation(FirmwareConfirmationKind.OverwriteExisting,
                $"Папка {fwv.Raw} уже существует.\nПерезаписать?");
        }

        string dstName;
        try
        {
            Directory.CreateDirectory(dstFolder);
            if (isDir)
            {
                CopyDirectoryContents(request.SourcePath, dstFolder);
                dstName = Path.GetFileName(request.SourcePath.TrimEnd(Path.DirectorySeparatorChar));
            }
            else
            {
                var ext = Path.GetExtension(request.SourcePath);
                dstName = FirmwareNaming.BuildFirmwareFilename(fwv, ext, reqNum, cabinetSn);
                File.Copy(request.SourcePath, Path.Combine(dstFolder, dstName), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            return FirmwareUploadResult.IoFailure(ex.Message);
        }

        var user = db.GetOrCreateUser(request.AuthorUserName, request.AuthorUserName);

        var warnings = new List<string>();
        try { ChangelogFile.Write(dstFolder, fwv, launchTypes, desc); }
        catch (Exception ex) { warnings.Add($"CHANGELOG.md: {ex.Message}"); }

        string ioMapStored = "";
        if (!string.IsNullOrEmpty(request.IoMapSourcePath))
        {
            try { ioMapStored = FileSystemHelpers.CopyFileOrFolderShallow(request.IoMapSourcePath, hierarchy.IoMapPath(root, group.Name, subOption.Name, mod.ControllerName)); }
            catch (Exception ex) { warnings.Add($"Карта ВВ: {ex.Message}"); }
        }
        string instrStored = "";
        if (!string.IsNullOrEmpty(request.InstructionsSourcePath))
        {
            try { instrStored = FileSystemHelpers.CopyFileOrFolderShallow(request.InstructionsSourcePath, hierarchy.InstrPath(root, group.Name, subOption.Name, mod.ControllerName)); }
            catch (Exception ex) { warnings.Add($"Инструкция: {ex.Message}"); }
        }
        string modbusStored = "";
        if (!string.IsNullOrEmpty(request.ModbusMapSourcePath))
        {
            try { modbusStored = FileSystemHelpers.CopyFileOrFolderShallow(request.ModbusMapSourcePath, hierarchy.ModbusMapPath(root, group.Name, subOption.Name, mod.ControllerName)); }
            catch (Exception ex) { warnings.Add($"Карта modbus: {ex.Message}"); }
        }
        string hmiStored = "";
        if (request.HmiEnabled && !string.IsNullOrEmpty(request.HmiSourcePath))
        {
            try
            {
                // Named after the PLC version + "_hmi" — see UploadView's original comment (round 43ish)
                // on why the HMI folder is versioned per-upload instead of flat/overwritten. Сам
                // копир вынесен в FirmwareAttachmentsService, чтобы «доложить HMI к уже загруженной
                // версии» клало проект ровно туда же и так же, что и загрузка новой версии.
                var hmiRootFolder = hierarchy.HmiPath(root, group.Name, subOption.Name, mod.ControllerName);
                hmiStored = FirmwareAttachmentsService.CopyHmiProject(hmiRootFolder, fwv.Raw, request.HmiSourcePath);
            }
            catch (Exception ex) { warnings.Add($"HMI-проект: {ex.Message}"); }
        }

        // Group/subtype/controller no longer go into the filename itself (see FirmwareNaming.
        // BuildFirmwareFilename) — added here as ordinary tags instead, so a search for "НГР" or
        // "SMH5" still finds the file by the same words it used to carry in its name.
        var tags = (request.Tags ?? new List<string>()).ToList();
        foreach (var autoTag in new[] { group.Name, subOption.Name == "—" ? null : subOption.Name, mod.ControllerName })
            if (!string.IsNullOrWhiteSpace(autoTag) && !tags.Contains(autoTag, StringComparer.OrdinalIgnoreCase))
                tags.Add(autoTag);
        foreach (var tag in tags) db.AddTag(tag);

        var record = new FwVersionRecord
        {
            SubtypeId = subOption.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subOption.Prefix,
            HwVersion = hwInt,
            SwVersion = swInt,
            DtStr = fwv.DtStr,
            VersionRaw = fwv.Raw,
            Filename = dstName,
            DiskPath = dstFolder,
            Description = desc,
            Changelog = desc,
            LaunchTypes = launchTypes,
            IoMapPath = ioMapStored,
            InstructionsPath = instrStored,
            ModbusMapPath = modbusStored,
            HmiPath = hmiStored,
            ExecutableHint = request.ExecutableHint ?? "",
            HmiExecutableHint = request.HmiEnabled ? request.HmiExecutableHint ?? "" : "",
            IsOpc = isOpc,
            RequestNum = reqNum,
            CabinetSn = cabinetSn,
            AuthorId = user.Id,
            Status = "active",
            Tags = string.Join(' ', tags),
        };

        var newFwId = db.AddFwVersion(record);
        record.Id = newFwId;

        if (reservation is not null && newFwId > 0)
            db.FulfillReservation(reservation.Id!.Value, newFwId);

        return new FirmwareUploadResult
        {
            Outcome = FirmwareUploadOutcome.Success,
            Warnings = warnings,
            FwVersionId = newFwId,
            Record = record,
            DestinationFolder = dstFolder,
            DestinationFilename = dstName,
        };
    }

    // ── PSL / KINCO auto-detection ────────────────────────────────────────────

    /// <summary>Unlike PSL (which has an inspectable internal format — see PslInspector), .dpj/.kpr/
    /// .kpj don't have a documented structure to parse a model out of, and KINCO has exactly one
    /// modification in the hierarchy (see HierarchyDefaultsData) — so just recognizing the extension is
    /// already an unambiguous match, no file content needs reading.</summary>
    public static readonly HashSet<string> KincoProjectExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".dpj", ".kpr", ".kpj" };

    public static ControllerModification? AutodetectKinco(IEnumerable<ControllerModification> mods) =>
        mods.FirstOrDefault(m => string.Equals(m.ControllerName, "KINCO", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns null only when the .psl itself couldn't be read at all (mirrors the original
    /// try/catch-and-give-up in UploadView.TryPslAutodetect). Otherwise DeviceKey is "" when nothing
    /// recognizable was embedded in the file (silent case — caller shows nothing) and non-empty
    /// with Modification null when a key WAS read but doesn't match any modification in the catalogue
    /// (caller shows a "не найдено в справочнике" status message).</summary>
    public static FirmwareAutodetectResult? AutodetectFromPsl(string path, IEnumerable<ControllerModification> mods)
    {
        PslInfo info;
        try { info = PslInspector.Inspect(path); }
        catch { return null; }

        var deviceKey = info.Plc.DeviceKey;
        if (string.IsNullOrEmpty(deviceKey)) return new FirmwareAutodetectResult(null, "");

        var match = FindModificationByPslKey(info.Plc.Model, info.Plc.Modification, mods);
        return new FirmwareAutodetectResult(match, deviceKey);
    }

    public static ControllerModification? FindModificationByPslKey(string model, string modification, IEnumerable<ControllerModification> mods)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var expected = string.IsNullOrEmpty(modification) ? model : $"{model}-{modification}";
        return mods.FirstOrDefault(m =>
            string.Equals(m.ControllerName, model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.DisplayName, expected, StringComparison.OrdinalIgnoreCase));
    }

    // ── helpers ported unchanged from UploadView ────────────────────────────────

    /// <summary>Формат для имени файла — "(01312)"/"SN00042": число дополняется нулями до 5 цифр. Не
    /// блокирует нечисловой ввод (возвращает как есть) — это косметика имени файла, а не строгая
    /// валидация поля. Public — also used by UploadView's live path preview (UpdatePreview), which
    /// needs the exact same formatting outside of a full Upload() call.</summary>
    public static string Format5Digits(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return "";
        return int.TryParse(trimmed, out var n) && n is >= 0 and <= 99999 ? n.ToString("D5") : trimmed;
    }

    private static void CopyDirectoryContents(string srcDir, string dstDir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(srcDir))
        {
            var name = Path.GetFileName(entry);
            var dest = Path.Combine(dstDir, name);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(dest);
                CopyDirectoryContents(entry, dest);
            }
            else
            {
                File.Copy(entry, dest, overwrite: true);
            }
        }
    }

}
