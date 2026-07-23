using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Жалоба: «загрузил версию с HMI, потом поправил только ПЛК и залил без панели — старая HMI
/// не подтянулась». Панель и программа ПЛК у одного шкафа живут своей жизнью и обновляются
/// независимо, поэтому загрузка без панели берёт последнюю известную панель этого же шкафа
/// (подтип + контроллер) — см. FirmwareUploadService.BuildPlan и Database.GetLatestHmiForFirmware.
/// Файлы при этом не копируются второй раз: hmi_path указывает на ту же папку проекта.</summary>
public class HmiInheritanceTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public HmiInheritanceTests()
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

    private (EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod) Cabinet(
        string groupName = "ТГР", string controller = "SMH5")
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == groupName);
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == controller && m.DisplayName == controller);
        return (group, subtype, mod);
    }

    private FirmwareUploadRequest Request(string sourcePath, EquipmentGroup group, EquipmentSubType subtype,
        ControllerModification mod) => new()
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

    private string TempPsl()
    {
        var path = Path.Combine(_tempRoot.Path, $"src_{Guid.NewGuid():N}.psl");
        File.WriteAllText(path, "dummy firmware bytes");
        return path;
    }

    private string TempHmiProject(string screenName = "screen1.fsprj")
    {
        var dir = Path.Combine(_tempRoot.Path, $"hmi_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, screenName), "hmi data");
        return dir;
    }

    /// <summary>Загрузка с панелью, затем загрузка того же шкафа без панели.</summary>
    private (FirmwareUploadResult withHmi, FirmwareUploadResult plcOnly) UploadHmiThenPlcOnly(
        EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod, string hint = "screen1.fsprj")
    {
        var first = Request(TempPsl(), group, subtype, mod);
        first.HmiEnabled = true;
        first.HmiSourcePath = TempHmiProject(hint);
        first.HmiExecutableHint = hint;
        var withHmi = FirmwareUploadService.Upload(_db, _hierarchy, first);
        Assert.Equal(FirmwareUploadOutcome.Success, withHmi.Outcome);

        var plcOnly = FirmwareUploadService.Upload(_db, _hierarchy, Request(TempPsl(), group, subtype, mod));
        Assert.Equal(FirmwareUploadOutcome.Success, plcOnly.Outcome);
        return (withHmi, plcOnly);
    }

    [Fact]
    public void PlcOnlyUpload_PicksUpHmiOfThePreviousVersion()
    {
        var (group, subtype, mod) = Cabinet();

        var (withHmi, plcOnly) = UploadHmiThenPlcOnly(group, subtype, mod);

        var stored = _db.GetFwVersionById(plcOnly.FwVersionId)!;
        Assert.Equal(withHmi.Record!.HmiPath, stored.HmiPath);
        Assert.Equal("screen1.fsprj", stored.HmiExecutableHint);
        Assert.True(File.Exists(Path.Combine(stored.HmiPath, "screen1.fsprj")));
    }

    /// <summary>Карточка пишет «Открыть HMI проект (от 3.0.005.0001)» — значит, загрузка обязана
    /// сказать, от какой версии подтянулась панель, а не молча подставить путь.</summary>
    [Fact]
    public void PlcOnlyUpload_ReportsWhichVersionTheHmiCameFrom()
    {
        var (group, subtype, mod) = Cabinet();

        var (withHmi, plcOnly) = UploadHmiThenPlcOnly(group, subtype, mod);

        Assert.Equal(withHmi.Record!.VersionRaw, plcOnly.InheritedHmiFromVersion);
        // Имя папки проекта — «{версия}_hmi»; из него карточка и достаёт номер версии.
        Assert.Equal($"{withHmi.Record.VersionRaw}_hmi", Path.GetFileName(plcOnly.Record!.HmiPath));
    }

    /// <summary>Панель не дублируется на диске: у обеих версий один и тот же путь к проекту, второй
    /// копии папки в HMI-каталоге шкафа не появляется.</summary>
    [Fact]
    public void InheritedHmi_DoesNotCopyTheProjectASecondTime()
    {
        var (group, subtype, mod) = Cabinet();

        UploadHmiThenPlcOnly(group, subtype, mod);

        var hmiRoot = _hierarchy.HmiPath(Root, group.Name, subtype.Name, mod.ControllerName);
        Assert.Single(Directory.GetDirectories(hmiRoot));
    }

    [Fact]
    public void UploadWithItsOwnHmi_DoesNotInheritTheOldOne()
    {
        var (group, subtype, mod) = Cabinet();
        var (withHmi, _) = UploadHmiThenPlcOnly(group, subtype, mod);

        var third = Request(TempPsl(), group, subtype, mod);
        third.HmiEnabled = true;
        third.HmiSourcePath = TempHmiProject("screen2.fsprj");
        third.HmiExecutableHint = "screen2.fsprj";
        var result = FirmwareUploadService.Upload(_db, _hierarchy, third);

        Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
        Assert.Equal("", result.InheritedHmiFromVersion);
        Assert.NotEqual(withHmi.Record!.HmiPath, result.Record!.HmiPath);
        Assert.Equal("screen2.fsprj", result.Record.HmiExecutableHint);
    }

    /// <summary>Обратный порядок: панель обновили, ПЛК — нет. Следующая загрузка ПЛК должна взять
    /// свежую панель, а не первую попавшуюся.</summary>
    [Fact]
    public void PlcOnlyUpload_InheritsTheLatestHmi_NotTheFirstOne()
    {
        var (group, subtype, mod) = Cabinet();
        UploadHmiThenPlcOnly(group, subtype, mod);

        var newer = Request(TempPsl(), group, subtype, mod);
        newer.HmiEnabled = true;
        newer.HmiSourcePath = TempHmiProject("screen2.fsprj");
        newer.HmiExecutableHint = "screen2.fsprj";
        var newerHmi = FirmwareUploadService.Upload(_db, _hierarchy, newer);

        var afterwards = FirmwareUploadService.Upload(_db, _hierarchy, Request(TempPsl(), group, subtype, mod));

        Assert.Equal(newerHmi.Record!.VersionRaw, afterwards.InheritedHmiFromVersion);
        Assert.Equal(newerHmi.Record.HmiPath, afterwards.Record!.HmiPath);
        Assert.Equal("screen2.fsprj", afterwards.Record.HmiExecutableHint);
    }

    [Fact]
    public void NoHmiEverUploaded_LeavesTheVersionWithoutOne()
    {
        var (group, subtype, mod) = Cabinet();

        var result = FirmwareUploadService.Upload(_db, _hierarchy, Request(TempPsl(), group, subtype, mod));

        Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
        Assert.Equal("", result.InheritedHmiFromVersion);
        Assert.True(string.IsNullOrEmpty(result.Record!.HmiPath));
    }

    /// <summary>Панель принадлежит конкретному шкафу: чужая (другой контроллер того же подтипа) не
    /// должна подставляться — на другом контроллере проект панели не тот же самый.</summary>
    [Fact]
    public void HmiOfAnotherController_IsNotInherited()
    {
        var (group, subtype, smh5) = Cabinet();
        UploadHmiThenPlcOnly(group, subtype, smh5);

        var smh4 = _db.GetAllModifications().Single(m => m.ControllerName == "SMH4" && m.DisplayName == "SMH4");
        var result = FirmwareUploadService.Upload(_db, _hierarchy, Request(TempPsl(), group, subtype, smh4));

        Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
        Assert.Equal("", result.InheritedHmiFromVersion);
        Assert.True(string.IsNullOrEmpty(result.Record!.HmiPath));
    }

    /// <summary>Удалённая версия панель не отдаёт — иначе новая прошивка ссылалась бы на папку,
    /// которую пользователь уже убрал (удаление сносит и файлы, см. SettingsView.DeleteFirmware_Click).</summary>
    [Fact]
    public void HmiOfADeletedVersion_IsNotInherited()
    {
        var (group, subtype, mod) = Cabinet();
        var first = Request(TempPsl(), group, subtype, mod);
        first.HmiEnabled = true;
        first.HmiSourcePath = TempHmiProject();
        first.HmiExecutableHint = "screen1.fsprj";
        var withHmi = FirmwareUploadService.Upload(_db, _hierarchy, first);

        _db.TombstoneFwVersion(withHmi.FwVersionId);

        // Номер освободился вместе с версией, поэтому следующая загрузка метит в ту же папку —
        // к делу отношения не имеет, просто подтверждаем перезапись.
        var next = Request(TempPsl(), group, subtype, mod);
        next.ConfirmOverwriteExisting = true;
        var result = FirmwareUploadService.Upload(_db, _hierarchy, next);

        Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
        Assert.Equal("", result.InheritedHmiFromVersion);
        Assert.True(string.IsNullOrEmpty(result.Record!.HmiPath));
    }
}
