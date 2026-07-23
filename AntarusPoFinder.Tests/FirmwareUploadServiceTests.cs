using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Covers FirmwareUploadService — the upload transaction extracted out of UploadView.
/// Upload_Click (Спринт 2, Задача 1). A fresh Database seeds the default catalogue (see
/// HierarchyDefaultsData), so every test just picks a real group/subtype/controller/modification out
/// of it instead of hand-rolling hierarchy rows.</summary>
public class FirmwareUploadServiceTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public FirmwareUploadServiceTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
        _hierarchy.EnsureStructure(Root);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
        _tempRoot.Dispose();
    }

    // ── seeding helpers ──────────────────────────────────────────────────────

    /// <summary>ТГР has exactly one ("—" placeholder) subtype — the simplest group/subtype combo,
    /// no extra subfolder segment in the resulting path.</summary>
    private (EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod) SeedTgrSmh5()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");
        return (group, subtype, mod);
    }

    private static string WriteTempFile(string extension, string content = "dummy firmware bytes")
    {
        var path = Path.Combine(Path.GetTempPath(), $"antarus_upload_test_{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }

    private FirmwareUploadRequest BaseRequest(string sourcePath, EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod) => new()
    {
        SourcePath = sourcePath,
        Group = group,
        Subtype = subtype,
        Modification = mod,
        LaunchTypes = new() { "УПП" },
        Description = "тестовая загрузка",
        IncludeDateInVersion = false,
        RootPath = Root,
        AuthorUserName = "tester",
    };

    // ── success paths ────────────────────────────────────────────────────────

    [Fact]
    public void Upload_PlainPslFile_Success()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Record);
            Assert.Equal("3.0.005.0001", result.Record!.VersionRaw); // ТГР prefix=3, "—" prefix=0, SMH5 hw=5, first sw
            Assert.True(Directory.Exists(result.DestinationFolder));
            Assert.True(File.Exists(Path.Combine(result.DestinationFolder!, result.DestinationFilename!)));
            Assert.True(File.Exists(Path.Combine(result.DestinationFolder!, "CHANGELOG.md")));

            var changelog = File.ReadAllText(Path.Combine(result.DestinationFolder!, "CHANGELOG.md"));
            Assert.Contains("тестовая загрузка", changelog);
            Assert.Contains("УПП", changelog);

            var stored = _db.GetFwVersionById(result.FwVersionId);
            Assert.NotNull(stored);
            Assert.Equal("active", stored!.Status);
            Assert.Contains("ТГР", stored.Tags.Split(' '));
            Assert.Contains("SMH5", stored.Tags.Split(' '));
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Upload_WithHmiFolder_CopiesHmiIntoVersionedSubfolder()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        var hmiDir = Path.Combine(Path.GetTempPath(), $"antarus_hmi_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(hmiDir);
        File.WriteAllText(Path.Combine(hmiDir, "screen1.fsprj"), "hmi data");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.HmiEnabled = true;
            request.HmiSourcePath = hmiDir;
            request.HmiExecutableHint = "screen1.fsprj";

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.Empty(result.Warnings);
            var expectedHmiFolder = Path.Combine(_hierarchy.HmiPath(Root, group.Name, subtype.Name, mod.ControllerName), $"{result.Record!.VersionRaw}_hmi");
            Assert.Equal(expectedHmiFolder, result.Record.HmiPath);
            Assert.True(File.Exists(Path.Combine(expectedHmiFolder, "screen1.fsprj")));
            Assert.Equal("screen1.fsprj", result.Record.HmiExecutableHint);
        }
        finally
        {
            File.Delete(src);
            Directory.Delete(hmiDir, recursive: true);
        }
    }

    /// <summary>Пятый тип пуска «Отсутствует» — это обычное значение launch_types, а не отдельный
    /// флаг: валидация «выберите хотя бы один тип пуска» должна его принимать, а сам он должен
    /// доезжать в БД и в CHANGELOG.md ровно как остальные четыре.</summary>
    [Fact]
    public void Upload_LaunchTypeNone_IsAcceptedAndStored()
    {
        Assert.Contains(ConfigService.LaunchTypeNone, ConfigService.LaunchTypes);

        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.LaunchTypes = new() { ConfigService.LaunchTypeNone };

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            var stored = _db.GetFwVersionById(result.FwVersionId);
            Assert.Equal(new[] { ConfigService.LaunchTypeNone }, stored!.LaunchTypes);
            Assert.Contains(ConfigService.LaunchTypeNone,
                File.ReadAllText(Path.Combine(result.DestinationFolder!, "CHANGELOG.md")));
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Upload_OpcRequestAndSnEnabled_FilenameAndFolderReflectBoth()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.OpcRequestEnabled = true;
            request.RequestNumRaw = "1312";
            request.OpcSnEnabled = true;
            request.CabinetSnRaw = "42";

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.Contains("_(01312)", result.DestinationFilename);
            Assert.Contains("_SN00042", result.DestinationFilename);
            Assert.Contains("ОПЦ", result.DestinationFolder!.Split(Path.DirectorySeparatorChar));
            Assert.True(result.Record!.IsOpc);
            Assert.Equal("01312", result.Record.RequestNum);
            Assert.Equal("00042", result.Record.CabinetSn);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Upload_FolderSource_MergeCopiesContentsAndUsesFolderName()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var srcDir = Path.Combine(Path.GetTempPath(), $"antarus_folder_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "main.lfs"), "plc project");
        File.WriteAllText(Path.Combine(srcDir, "driver.dll"), "support file");
        try
        {
            var request = BaseRequest(srcDir, group, subtype, mod);

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.Equal(Path.GetFileName(srcDir), result.DestinationFilename);
            Assert.True(File.Exists(Path.Combine(result.DestinationFolder!, "main.lfs")));
            Assert.True(File.Exists(Path.Combine(result.DestinationFolder!, "driver.dll")));
        }
        finally { Directory.Delete(srcDir, recursive: true); }
    }

    [Fact]
    public void Upload_WithReservation_UsesReservedNumberAndFulfillsIt()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var reservation = _db.ReserveNextVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion,
            group.Prefix, subtype.Prefix, "programmer1", includeDate: false);

        var src = WriteTempFile(".psl");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.Reservation = reservation;

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.Equal(reservation.VersionRaw, result.Record!.VersionRaw);
            Assert.Empty(_db.GetActiveReservations(subtype.Id!.Value, mod.ControllerId, mod.HwVersion));
        }
        finally { File.Delete(src); }
    }

    // ── PSL / KINCO autodetection ────────────────────────────────────────────

    [Fact]
    public void AutodetectFromPsl_SyntheticSample_MatchesSeededModification()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "TestData", "sample_smh4.psl");
        Assert.True(File.Exists(samplePath), $"test resource missing: {samplePath}");

        var smh4 = _db.GetAllControllerModels().Single(c => c.Name == "SMH4");
        _db.AddControllerModification(smh4.Id!.Value, "SMH4-1234", 99, "test modification matching sample psl device key");
        var mods = _db.GetAllModifications();

        var result = FirmwareUploadService.AutodetectFromPsl(samplePath, mods);

        Assert.NotNull(result);
        Assert.Equal("SMH4-1234-01-2", result!.DeviceKey);
        Assert.NotNull(result.Modification);
        Assert.Equal("SMH4-1234", result.Modification!.DisplayName);
    }

    [Fact]
    public void AutodetectFromPsl_SyntheticSample_NoMatchingModification_ReturnsDeviceKeyWithNullModification()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "TestData", "sample_smh4.psl");
        var mods = _db.GetAllModifications(); // default catalogue has "SMH4" but not "SMH4-1234"

        var result = FirmwareUploadService.AutodetectFromPsl(samplePath, mods);

        Assert.NotNull(result);
        Assert.Equal("SMH4-1234-01-2", result!.DeviceKey);
        Assert.Null(result.Modification);
    }

    [Fact]
    public void AutodetectFromPsl_MissingFile_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"antarus_missing_{Guid.NewGuid():N}.psl");

        var result = FirmwareUploadService.AutodetectFromPsl(missing, _db.GetAllModifications());

        Assert.Null(result);
    }

    [Fact]
    public void AutodetectKinco_ReturnsSeededKincoModification()
    {
        var match = FirmwareUploadService.AutodetectKinco(_db.GetAllModifications());

        Assert.NotNull(match);
        Assert.Equal("KINCO", match!.ControllerName);
    }

    [Fact]
    public void FindModificationByPslKey_EmptyModel_ReturnsNull()
    {
        Assert.Null(FirmwareUploadService.FindModificationByPslKey("", "1234", _db.GetAllModifications()));
    }

    // ── confirmation flow (unknown extension / overwrite) ────────────────────

    [Fact]
    public void Upload_UnknownExtension_NeedsConfirmationThenSucceedsWhenConfirmed()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".xyz"); // not in the default allowed-extensions seed (psl/lfs/kpr/kpj/dpj)
        try
        {
            var request = BaseRequest(src, group, subtype, mod);

            var first = FirmwareUploadService.Upload(_db, _hierarchy, request);
            Assert.Equal(FirmwareUploadOutcome.NeedsConfirmation, first.Outcome);
            Assert.Equal(FirmwareConfirmationKind.UnknownExtension, first.ConfirmationKind);

            request.ConfirmUnknownExtension = true;
            var second = FirmwareUploadService.Upload(_db, _hierarchy, request);
            Assert.Equal(FirmwareUploadOutcome.Success, second.Outcome);
        }
        finally { File.Delete(src); }
    }

    /// <summary>Полный аналог Upload_UnknownExtension_NeedsConfirmationThenSucceedsWhenConfirmed выше,
    /// но для HMI-вложения и отдельного списка allowed_extensions_hmi (fsprj/emt/emtp/emsln по
    /// умолчанию) — проверяет, что расширение основной прошивки (.psl, разрешено) не мешает отдельной
    /// проверке HMI-файла с неразрешённым расширением.</summary>
    [Fact]
    public void Upload_UnknownHmiExtension_NeedsConfirmationThenSucceedsWhenConfirmed()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        var hmiSrc = WriteTempFile(".xyz", "hmi data"); // not in the default HMI allowed-extensions seed
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.HmiEnabled = true;
            request.HmiSourcePath = hmiSrc;

            var first = FirmwareUploadService.Upload(_db, _hierarchy, request);
            Assert.Equal(FirmwareUploadOutcome.NeedsConfirmation, first.Outcome);
            Assert.Equal(FirmwareConfirmationKind.UnknownHmiExtension, first.ConfirmationKind);

            request.ConfirmUnknownHmiExtension = true;
            var second = FirmwareUploadService.Upload(_db, _hierarchy, request);
            Assert.Equal(FirmwareUploadOutcome.Success, second.Outcome);
        }
        finally { File.Delete(src); File.Delete(hmiSrc); }
    }

    [Fact]
    public void Upload_DestinationFolderAlreadyExists_NeedsConfirmationThenOverwrites()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        // sw_version 1 is what a fresh (no active versions yet) request will compute — pre-create its
        // destination folder on disk (without a DB row) to simulate a stray/leftover folder.
        var fwv = FwVersionNumber.Build(group.Prefix, subtype.Prefix, mod.HwVersion, 1, includeDate: false);
        var dstFolder = _hierarchy.FwPath(Root, group.Name, subtype.Name, mod.ControllerName, fwv.Raw, isOpc: false);
        Directory.CreateDirectory(dstFolder);
        File.WriteAllText(Path.Combine(dstFolder, "stale.txt"), "leftover");

        var src = WriteTempFile(".psl");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);

            var first = FirmwareUploadService.Upload(_db, _hierarchy, request);
            Assert.Equal(FirmwareUploadOutcome.NeedsConfirmation, first.Outcome);
            Assert.Equal(FirmwareConfirmationKind.OverwriteExisting, first.ConfirmationKind);

            request.ConfirmOverwriteExisting = true;
            var second = FirmwareUploadService.Upload(_db, _hierarchy, request);
            Assert.Equal(FirmwareUploadOutcome.Success, second.Outcome);
            Assert.Equal(dstFolder, second.DestinationFolder);
        }
        finally { File.Delete(src); }
    }

    // ── validation ────────────────────────────────────────────────────────────

    [Fact]
    public void Upload_MissingSourcePath_ReturnsValidationFailed()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var request = BaseRequest("", group, subtype, mod);

        var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

        Assert.Equal(FirmwareUploadOutcome.ValidationFailed, result.Outcome);
        Assert.Equal("Выберите файл прошивки.", result.Errors.Single());
    }

    [Fact]
    public void Upload_MissingController_ReturnsValidationFailed()
    {
        var (group, subtype, _) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        try
        {
            var request = BaseRequest(src, group, subtype, null!);
            request.Modification = null;

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.ValidationFailed, result.Outcome);
            Assert.Equal("Укажите тип шкафа, подтип и контроллер.", result.Errors.Single());
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Upload_NoLaunchTypes_ReturnsValidationFailed()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.LaunchTypes.Clear();

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.ValidationFailed, result.Outcome);
            Assert.Equal("Выберите хотя бы один тип пуска.", result.Errors.Single());
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Upload_EmptyDescription_ReturnsValidationFailed()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.Description = "   ";

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.ValidationFailed, result.Outcome);
            Assert.Equal("Укажите описание изменений в этой версии.", result.Errors.Single());
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Upload_RootPathMissing_ReturnsValidationFailed()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.RootPath = Path.Combine(Root, "does-not-exist-at-all");

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.ValidationFailed, result.Outcome);
            Assert.Equal("Сетевой диск недоступен. Проверьте настройки.", result.Errors.Single());
        }
        finally { File.Delete(src); }
    }
}
