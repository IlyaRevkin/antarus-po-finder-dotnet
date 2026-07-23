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

    /// <summary>Подтипы, которым эта же прошивка подходит один-в-один. Файлы для них НЕ дублируются:
    /// в папке контроллера каждого такого подтипа создаётся ярлык на папку версии основного подтипа
    /// (Subtype выше), а запись в fw_versions заводится своя — со ссылкой на тот же путь. Так
    /// прошивка находится поиском под каждым типом шкафа, но на диске лежит один раз (по прямому
    /// пожеланию: «чтобы не занимало много памяти — ярлыками раскидывать»).</summary>
    public List<EquipmentSubType> ExtraSubtypes { get; set; } = new();

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
    /// <summary>Id записей, заведённых для дополнительно выбранных подтипов шкафов (см.
    /// FirmwareUploadRequest.ExtraSubtypes) — файлы у них общие с основной версией.</summary>
    public List<int> ExtraFwVersionIds { get; init; } = new();
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
    /// <summary>Публичный, в отличие от остальных фабрик: при пофазной загрузке провал копирования
    /// возвращает FirmwareUploadCopyResult.IoErrorMessage, и превращает его в общий результат уже
    /// вызывающий (см. UploadView.Upload_Click).</summary>
    public static FirmwareUploadResult IoFailure(string message) => Io(message);
}

/// <summary>Всё, что нужно знать копированию файлов, — посчитано заранее по БД (номер версии, пути
/// назначения, автор). Существует ради того, чтобы дисковая фаза загрузки (FirmwareUploadService.
/// CopyFiles) не обращалась к БД и её можно было выполнять в фоновом потоке: соединение SQLite
/// одно на приложение и не потокобезопасно, поэтому обе БД-фазы остаются на потоке UI.</summary>
public class FirmwareUploadPlan
{
    public FirmwareUploadRequest Request { get; init; } = null!;
    public string Root { get; init; } = "";
    public FwVersionNumber Version { get; init; } = null!;
    public int SwVersion { get; init; }
    public int HwVersion { get; init; }
    public bool IsOpc { get; init; }
    public string RequestNum { get; init; } = "";
    public string CabinetSn { get; init; } = "";
    public string Description { get; init; } = "";
    public List<string> LaunchTypes { get; init; } = new();

    /// <summary>Источник — папка (копируем содержимое) или один файл (копируем с новым именем).</summary>
    public bool SourceIsDirectory { get; init; }

    public string DestinationFolder { get; init; } = "";
    public string IoMapFolder { get; init; } = "";
    public string InstructionsFolder { get; init; } = "";
    public string ModbusMapFolder { get; init; } = "";
    public string HmiFolder { get; init; } = "";

    public int? AuthorId { get; init; }
}

/// <summary>Что получилось у дисковой фазы: имя файла, куда легли вложения и какие мелочи не
/// удались. IoErrorMessage не null означает, что не скопировалась сама прошивка — записи в БД в
/// этом случае не будет (как и раньше, см. FirmwareUploadOutcome.IoError).</summary>
public class FirmwareUploadCopyResult
{
    public string DestinationFilename { get; init; } = "";
    public string IoMapStored { get; init; } = "";
    public string InstructionsStored { get; init; } = "";
    public string ModbusMapStored { get; init; } = "";
    public string HmiStored { get; init; } = "";
    public List<string> Warnings { get; init; } = new();
    public string? IoErrorMessage { get; init; }
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
    /// <summary>Полная загрузка одним вызовом: подготовка (БД) → копирование (диск) → запись (БД).
    /// Ровно то, что делал этот метод до разбиения на фазы, и остаётся нормальным способом вызова
    /// там, где не нужно держать интерфейс живым (тесты, любой не-UI код).</summary>
    public static FirmwareUploadResult Upload(Database db, HierarchyService hierarchy, FirmwareUploadRequest request,
        IShortcutCreator? shortcuts = null)
    {
        var (plan, failure) = Prepare(db, hierarchy, request);
        if (plan is null) return failure!;

        var copy = CopyFiles(plan);
        if (copy.IoErrorMessage is not null) return FirmwareUploadResult.IoFailure(copy.IoErrorMessage);

        return Register(db, hierarchy, plan, copy, shortcuts);
    }

    /// <summary>Фаза 1 (БД + проверки): валидация формы, номер версии, куда всё ляжет. Возвращает
    /// либо план, либо готовый отказ/запрос подтверждения — тогда план null. Копирования здесь нет,
    /// то есть до подтверждений на диск ничего не пишется, как и раньше.</summary>
    public static (FirmwareUploadPlan? Plan, FirmwareUploadResult? Failure) Prepare(
        Database db, HierarchyService hierarchy, FirmwareUploadRequest request)
    {
        if (string.IsNullOrEmpty(request.SourcePath))
            return (null, FirmwareUploadResult.ValidationFailure("Выберите файл прошивки."));

        if (request.Group is null || request.Subtype is null || request.Modification is null)
            return (null, FirmwareUploadResult.ValidationFailure("Укажите тип шкафа, подтип и контроллер."));

        var launchTypes = request.LaunchTypes ?? new List<string>();
        if (launchTypes.Count == 0)
            return (null, FirmwareUploadResult.ValidationFailure("Выберите хотя бы один тип пуска."));

        var desc = (request.Description ?? "").Trim();
        if (string.IsNullOrEmpty(desc))
            return (null, FirmwareUploadResult.ValidationFailure("Укажите описание изменений в этой версии."));

        var root = request.RootPath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return (null, FirmwareUploadResult.ValidationFailure("Сетевой диск недоступен. Проверьте настройки."));

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
                return (null, FirmwareUploadResult.Confirmation(FirmwareConfirmationKind.UnknownExtension,
                    $"Расширение «.{ext}» не входит в список разрешённых (Настройки → Иерархия → Разрешённые расширения).\nТочно загрузить этот файл?"));
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
            return (null, FirmwareUploadResult.Confirmation(FirmwareConfirmationKind.OverwriteExisting,
                $"Папка {fwv.Raw} уже существует.\nПерезаписать?"));
        }

        // Автор заводится здесь, а не после копирования: это последнее обращение к БД перед долгой
        // дисковой фазой, и после него CopyFiles можно спокойно уносить в фоновый поток.
        var user = db.GetOrCreateUser(request.AuthorUserName, request.AuthorUserName);

        var plan = new FirmwareUploadPlan
        {
            Request = request,
            Root = root,
            Version = fwv,
            SwVersion = swInt,
            HwVersion = hwInt,
            IsOpc = isOpc,
            RequestNum = reqNum,
            CabinetSn = cabinetSn,
            Description = desc,
            LaunchTypes = launchTypes,
            SourceIsDirectory = isDir,
            DestinationFolder = dstFolder,
            IoMapFolder = hierarchy.IoMapPath(root, group.Name, subOption.Name, mod.ControllerName),
            InstructionsFolder = hierarchy.InstrPath(root, group.Name, subOption.Name, mod.ControllerName),
            ModbusMapFolder = hierarchy.ModbusMapPath(root, group.Name, subOption.Name, mod.ControllerName),
            HmiFolder = hierarchy.HmiPath(root, group.Name, subOption.Name, mod.ControllerName),
            AuthorId = user.Id,
        };
        return (plan, null);
    }

    /// <summary>Фаза 2 (только диск): копирует прошивку и вложения на сетевой диск, пишет
    /// CHANGELOG.md. В БД не ходит ни разу — вызывающий может выполнить её в фоновом потоке, чтобы
    /// окно не висело всё время копирования (см. UploadView.Upload_Click).</summary>
    public static FirmwareUploadCopyResult CopyFiles(FirmwareUploadPlan plan)
    {
        var request = plan.Request;
        var warnings = new List<string>();
        string dstName;

        try
        {
            Directory.CreateDirectory(plan.DestinationFolder);
            if (plan.SourceIsDirectory)
            {
                CopyDirectoryContents(request.SourcePath, plan.DestinationFolder);
                dstName = Path.GetFileName(request.SourcePath.TrimEnd(Path.DirectorySeparatorChar));
            }
            else
            {
                var ext = Path.GetExtension(request.SourcePath);
                dstName = FirmwareNaming.BuildFirmwareFilename(plan.Version, ext, plan.RequestNum, plan.CabinetSn);
                File.Copy(request.SourcePath, Path.Combine(plan.DestinationFolder, dstName), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            return new FirmwareUploadCopyResult { IoErrorMessage = ex.Message };
        }

        try { ChangelogFile.Write(plan.DestinationFolder, plan.Version, plan.LaunchTypes, plan.Description); }
        catch (Exception ex) { warnings.Add($"CHANGELOG.md: {ex.Message}"); }

        string ioMapStored = "";
        if (!string.IsNullOrEmpty(request.IoMapSourcePath))
        {
            try { ioMapStored = FileSystemHelpers.CopyFileOrFolderShallow(request.IoMapSourcePath, plan.IoMapFolder); }
            catch (Exception ex) { warnings.Add($"Карта ВВ: {ex.Message}"); }
        }
        string instrStored = "";
        if (!string.IsNullOrEmpty(request.InstructionsSourcePath))
        {
            try { instrStored = FileSystemHelpers.CopyFileOrFolderShallow(request.InstructionsSourcePath, plan.InstructionsFolder); }
            catch (Exception ex) { warnings.Add($"Инструкция: {ex.Message}"); }
        }
        string modbusStored = "";
        if (!string.IsNullOrEmpty(request.ModbusMapSourcePath))
        {
            try { modbusStored = FileSystemHelpers.CopyFileOrFolderShallow(request.ModbusMapSourcePath, plan.ModbusMapFolder); }
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
                hmiStored = FirmwareAttachmentsService.CopyHmiProject(plan.HmiFolder, plan.Version.Raw, request.HmiSourcePath);
            }
            catch (Exception ex) { warnings.Add($"HMI-проект: {ex.Message}"); }
        }

        return new FirmwareUploadCopyResult
        {
            DestinationFilename = dstName,
            IoMapStored = ioMapStored,
            InstructionsStored = instrStored,
            ModbusMapStored = modbusStored,
            HmiStored = hmiStored,
            Warnings = warnings,
        };
    }

    /// <summary>Фаза 3 (БД): записывает версию, закрывает резерв, заводит записи дополнительным
    /// подтипам. Файлы к этому моменту уже лежат на диске.</summary>
    public static FirmwareUploadResult Register(Database db, HierarchyService hierarchy, FirmwareUploadPlan plan,
        FirmwareUploadCopyResult copy, IShortcutCreator? shortcuts = null)
    {
        var request = plan.Request;
        var group = request.Group!;
        var subOption = request.Subtype!;
        var mod = request.Modification!;
        var warnings = new List<string>(copy.Warnings);

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
            HwVersion = plan.HwVersion,
            SwVersion = plan.SwVersion,
            DtStr = plan.Version.DtStr,
            VersionRaw = plan.Version.Raw,
            Filename = copy.DestinationFilename,
            DiskPath = plan.DestinationFolder,
            Description = plan.Description,
            Changelog = plan.Description,
            LaunchTypes = plan.LaunchTypes,
            IoMapPath = copy.IoMapStored,
            InstructionsPath = copy.InstructionsStored,
            ModbusMapPath = copy.ModbusMapStored,
            HmiPath = copy.HmiStored,
            ExecutableHint = request.ExecutableHint ?? "",
            HmiExecutableHint = request.HmiEnabled ? request.HmiExecutableHint ?? "" : "",
            IsOpc = plan.IsOpc,
            RequestNum = plan.RequestNum,
            CabinetSn = plan.CabinetSn,
            AuthorId = plan.AuthorId,
            Status = "active",
            Tags = string.Join(' ', tags),
        };

        var newFwId = db.AddFwVersion(record);
        record.Id = newFwId;

        if (request.Reservation is not null && newFwId > 0)
            db.FulfillReservation(request.Reservation.Id!.Value, newFwId);

        var extraIds = LinkToExtraSubtypes(db, hierarchy, request, record, plan.DestinationFolder, plan.IsOpc, warnings, shortcuts);

        return new FirmwareUploadResult
        {
            Outcome = FirmwareUploadOutcome.Success,
            Warnings = warnings,
            ExtraFwVersionIds = extraIds,
            FwVersionId = newFwId,
            Record = record,
            DestinationFolder = plan.DestinationFolder,
            DestinationFilename = copy.DestinationFilename,
        };
    }

    /// <summary>Заводит запись fw_versions для каждого дополнительно выбранного подтипа шкафа и
    /// кладёт в папку его контроллера ярлык на папку версии основного подтипа. Файлы прошивки при
    /// этом НЕ копируются — disk_path у всех записей один и тот же, поэтому и поиск, и «скачать
    /// локально», и «открыть папку» работают с настоящими файлами, а не с ярлыком (ярлык нужен тому,
    /// кто ходит по сетевой папке проводником).
    ///
    /// Номер версии тоже общий (он же — имя файла на диске, внутри самой прошивки вписан именно он):
    /// заводить дополнительным подтипам собственные номера означало бы, что БД показывает один номер,
    /// а файл называется другим. Побочный эффект — следующая загрузка для такого подтипа считает эту
    /// версию своей и берёт следующий sw-номер; это осознанно, подтип действительно её получил.</summary>
    private static List<int> LinkToExtraSubtypes(Database db, HierarchyService hierarchy, FirmwareUploadRequest request,
        FwVersionRecord primary, string primaryFolder, bool isOpc, List<string> warnings, IShortcutCreator? shortcuts)
    {
        var created = new List<int>();
        var extras = (request.ExtraSubtypes ?? new List<EquipmentSubType>())
            .Where(s => s.Id is not null && s.Id != request.Subtype!.Id)
            .GroupBy(s => s.Id!.Value).Select(g => g.First())
            .ToList();
        if (extras.Count == 0) return created;

        var group = request.Group!;
        var mod = request.Modification!;

        foreach (var extra in extras)
        {
            var tags = primary.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (extra.Name != "—" && !tags.Contains(extra.Name, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(extra.Name);
                db.AddTag(extra.Name);
            }

            var copy = new FwVersionRecord
            {
                SubtypeId = extra.Id!.Value,
                ControllerId = primary.ControllerId,
                EqPrefix = primary.EqPrefix,
                SubPrefix = primary.SubPrefix,
                HwVersion = primary.HwVersion,
                SwVersion = primary.SwVersion,
                DtStr = primary.DtStr,
                VersionRaw = primary.VersionRaw,
                Filename = primary.Filename,
                DiskPath = primary.DiskPath,
                Description = primary.Description,
                Changelog = primary.Changelog,
                LaunchTypes = primary.LaunchTypes,
                IoMapPath = primary.IoMapPath,
                InstructionsPath = primary.InstructionsPath,
                ModbusMapPath = primary.ModbusMapPath,
                HmiPath = primary.HmiPath,
                ExecutableHint = primary.ExecutableHint,
                HmiExecutableHint = primary.HmiExecutableHint,
                IsOpc = primary.IsOpc,
                RequestNum = primary.RequestNum,
                CabinetSn = primary.CabinetSn,
                AuthorId = primary.AuthorId,
                Status = primary.Status,
                Tags = string.Join(' ', tags),
            };
            var id = db.AddFwVersion(copy);
            if (id > 0) created.Add(id);

            try
            {
                var ctrlFolder = hierarchy.ControllerFolder(request.RootPath, group.Name, extra.Name, mod.ControllerName, isOpc);
                Directory.CreateDirectory(ctrlFolder);
                shortcuts?.Create(Path.Combine(ctrlFolder, $"{primary.VersionRaw}.lnk"), primaryFolder,
                    $"Прошивка {primary.VersionRaw} — общая с подтипом {request.Subtype!.Name}");
            }
            catch (Exception ex)
            {
                // Ярлык — удобство для того, кто смотрит папку проводником; сама запись уже заведена
                // и в программе прошивка под этим подтипом уже находится.
                warnings.Add($"Ярлык для подтипа {extra.Name}: {ex.Message}");
            }
        }
        return created;
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
