using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Доп. материалы прошивки между двумя машинами (см. секцию fw_attachments в
/// Database.ConfigExchange.cs). Секция аддитивная и с тумбстоуном, как fw_versions/param_files, и
/// проверяется здесь ровно то, на чём этот класс задач уже обжигался: удаление доезжает, снятое не
/// воскресает, а СТАРЫЙ клиент (снимок без секции вовсе) ничего не ломает и ничего не стирает.</summary>
public class FwAttachmentSyncTests : IDisposable
{
    private readonly TempDb _fileA = new();
    private readonly TempDb _fileB = new();
    private readonly Database _a;
    private readonly Database _b;

    public FwAttachmentSyncTests()
    {
        _a = new Database(_fileA.Path);
        _b = new Database(_fileB.Path);
        // Рукопожатие, пока базы ещё совпадают по именам: без него у справочников разные sync_id и
        // соотносить строки нечем (см. док ConfigSyncTests).
        _b.ImportHierarchyData(_a.ExportHierarchyData());
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _fileA.Dispose();
        _fileB.Dispose();
    }

    /// <summary>Прошивка на машине A — минимальная строка: для синхронизации вложений диск не нужен,
    /// важна только сама запись и её sync_id.</summary>
    private static int AddFirmware(Database db, string versionRaw)
    {
        var group = db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");

        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = versionRaw,
            Filename = "fw.psl",
            DiskPath = @"Z:\Software\ПО\ТГР\SMH5\" + versionRaw,
            Description = "тестовая версия",
            UploadDate = "2026-01-01 00:00:00",
            LaunchTypes = new() { "УПП" },
        });
    }

    private static int AddAttachment(Database db, int fwId, string kind, string comment, string filename = "руководство.pdf") =>
        db.AddFwAttachment(new FwAttachment
        {
            FwVersionId = fwId,
            Filename = filename,
            DiskPath = @"Z:\Software\ПО\ТГР\SMH5\Доп. материалы\" + filename,
            Kind = kind,
            Comment = comment,
            AddedBy = "tester",
        });

    private static FwAttachment SingleAttachmentOf(Database db, string versionRaw)
    {
        var fw = db.GetAllFwVersionsWithNames().Single(v => v.VersionRaw == versionRaw);
        return Assert.Single(db.GetFwAttachments(fw.Id!.Value));
    }

    [Fact]
    public void Attachment_TravelsToOtherMachine_WithKindAndComment()
    {
        var fwA = AddFirmware(_a, "3.0.0005.0001.20260101_0000");
        AddAttachment(_a, fwA, FwAttachmentKinds.SetupGuide, "краткое руководство для наладчика");

        var counts = _b.ImportHierarchyData(_a.ExportHierarchyData());

        Assert.Equal(1, counts.FwAttachmentsAdded);
        var onB = SingleAttachmentOf(_b, "3.0.0005.0001.20260101_0000");
        Assert.Equal(FwAttachmentKinds.SetupGuide, onB.Kind);
        Assert.Equal("краткое руководство для наладчика", onB.Comment);
        // Тот же sync_id — дальше стороны говорят об одной и той же строке, а не о двух похожих.
        Assert.Equal(_a.GetFwAttachments(fwA).Single().SyncId, onB.SyncId);

        // Повторный импорт того же снимка ничего не добавляет — иначе каждая синхронизация плодила бы
        // копии (ровно то, что случалось с param_files до sync_id).
        var again = _b.ImportHierarchyData(_a.ExportHierarchyData());
        Assert.Equal(0, again.FwAttachmentsAdded);
        Assert.Single(_b.GetFwAttachments(_b.GetAllFwVersionsWithNames().Single().Id!.Value));
    }

    [Fact]
    public void EditedKindAndComment_TravelByTimestamp()
    {
        var fwA = AddFirmware(_a, "3.0.0005.0002.20260101_0000");
        var attachmentA = AddAttachment(_a, fwA, FwAttachmentKinds.Other, "пока непонятно что");
        _b.ImportHierarchyData(_a.ExportHierarchyData());

        _a.UpdateFwAttachment(attachmentA, FwAttachmentKinds.WorkSpecifics, "специфика работы на объекте");
        var counts = _b.ImportHierarchyData(_a.ExportHierarchyData());

        Assert.Equal(1, counts.FwAttachmentsUpdated);
        var onB = SingleAttachmentOf(_b, "3.0.0005.0002.20260101_0000");
        Assert.Equal(FwAttachmentKinds.WorkSpecifics, onB.Kind);
        Assert.Equal("специфика работы на объекте", onB.Comment);
    }

    [Fact]
    public void Removal_TravelsAsTombstone()
    {
        var fwA = AddFirmware(_a, "3.0.0005.0003.20260101_0000");
        var attachmentA = AddAttachment(_a, fwA, FwAttachmentKinds.SetupGuide, "лишний файл");
        _b.ImportHierarchyData(_a.ExportHierarchyData());
        Assert.Single(_b.GetFwAttachments(_b.GetAllFwVersionsWithNames().Single().Id!.Value));

        _a.TombstoneFwAttachment(attachmentA);
        var counts = _b.ImportHierarchyData(_a.ExportHierarchyData());

        Assert.Equal(1, counts.FwAttachmentsRemoved);
        Assert.Empty(_b.GetFwAttachments(_b.GetAllFwVersionsWithNames().Single().Id!.Value));
    }

    /// <summary>Снятое ЗДЕСЬ вложение не воскрешает входящая «живая» копия с машины, которая об
    /// удалении ещё не знает. Без этого правила снятие жило бы ровно до следующей синхронизации.</summary>
    [Fact]
    public void LocalRemoval_IsPermanent_AgainstStaleIncomingCopy()
    {
        var fwA = AddFirmware(_a, "3.0.0005.0004.20260101_0000");
        AddAttachment(_a, fwA, FwAttachmentKinds.SetupGuide, "руководство");
        _b.ImportHierarchyData(_a.ExportHierarchyData());

        var onB = SingleAttachmentOf(_b, "3.0.0005.0004.20260101_0000");
        _b.TombstoneFwAttachment(onB.Id!.Value);

        // A о снятии ещё не знает и присылает своё живое вложение.
        var counts = _b.ImportHierarchyData(_a.ExportHierarchyData());

        Assert.Equal(0, counts.FwAttachmentsAdded);
        Assert.Empty(_b.GetFwAttachments(_b.GetAllFwVersionsWithNames().Single().Id!.Value));
    }

    /// <summary>Снимок со СТАРОГО клиента секции fw_attachments не содержит вовсе (null). Это «я о них
    /// не знаю», а не «у меня их ноль»: чужие вложения он трогать не имеет права, и импорт такого
    /// снимка обязан пройти без единой ошибки.</summary>
    [Fact]
    public void OldClientSnapshot_WithoutSection_KeepsAttachmentsIntact()
    {
        var fwB = AddFirmware(_b, "3.0.0005.0005.20260101_0000");
        AddAttachment(_b, fwB, FwAttachmentKinds.VendorPlcFirmware, "прошивка ПЛК поставщика");

        var oldClient = _a.ExportHierarchyData();
        oldClient.FwAttachments = null;       // старый клиент этих ключей просто не пишет
        oldClient.FwAttachmentKinds = null;

        var counts = _b.ImportHierarchyData(oldClient);

        Assert.Equal(0, counts.FwAttachmentsRemoved);
        Assert.Single(_b.GetFwAttachments(fwB));
        Assert.Equal(FwAttachmentKinds.Defaults.Length, _b.GetFwAttachmentKinds().Count);
    }

    /// <summary>И наоборот: снимок с машины, которая про доп. материалы ЗНАЕТ, но своих не имеет
    /// (пустой список), тоже не должен ничего сносить — секция аддитивная, «у меня их нет» никогда не
    /// означает «удали свои».</summary>
    [Fact]
    public void EmptySection_DoesNotRemoveForeignAttachments()
    {
        var fwB = AddFirmware(_b, "3.0.0005.0006.20260101_0000");
        AddAttachment(_b, fwB, FwAttachmentKinds.SetupGuide, "только у B");

        var snapshot = _a.ExportHierarchyData();
        Assert.NotNull(snapshot.FwAttachments);
        Assert.Empty(snapshot.FwAttachments!);

        _b.ImportHierarchyData(snapshot);

        Assert.Single(_b.GetFwAttachments(fwB));
    }

    /// <summary>Справочник видов — обычный плоский список: удаление и возврат разъезжаются отметками
    /// времени, а не «выигрывает тот, кто последним нажал импорт» (см. Database.FlatLists.cs).</summary>
    [Fact]
    public void AttachmentKinds_DeletionAndRevival_Travel()
    {
        _a.AddFwAttachmentKind("Схема объекта");
        _b.ImportHierarchyData(_a.ExportHierarchyData());
        Assert.Contains("Схема объекта", _b.GetFwAttachmentKinds());

        _a.DeleteFwAttachmentKind("Схема объекта");
        var afterDelete = _b.ImportHierarchyData(_a.ExportHierarchyData());
        Assert.Equal(1, afterDelete.AttachmentKindsRemoved);
        Assert.DoesNotContain("Схема объекта", _b.GetFwAttachmentKinds());

        _a.AddFwAttachmentKind("Схема объекта");
        var afterRevival = _b.ImportHierarchyData(_a.ExportHierarchyData());
        Assert.Equal(1, afterRevival.AttachmentKindsAdded);
        Assert.Contains("Схема объекта", _b.GetFwAttachmentKinds());
    }

    /// <summary>Удалённый вид не возвращается с машины, которая об удалении ещё не знает, — та самая
    /// жалоба «удаляю, а оно снова появляется», ради которой заведён flat_list_state.</summary>
    [Fact]
    public void DeletedKind_IsNotResurrectedByStaleSnapshot()
    {
        _a.AddFwAttachmentKind("Схема объекта");
        _b.ImportHierarchyData(_a.ExportHierarchyData());

        // B удаляет вид у себя; у A он всё ещё живой, и A присылает свой снимок.
        _b.DeleteFwAttachmentKind("Схема объекта");
        _b.ImportHierarchyData(_a.ExportHierarchyData());

        Assert.DoesNotContain("Схема объекта", _b.GetFwAttachmentKinds());
    }

    /// <summary>Прошивки, к которой относится вложение, у получателя ещё нет — вложение просто
    /// пропускается и приедет следующей синхронизацией. Ронять импорт из-за этого нельзя: снимок
    /// собирается на одной машине, а строки прошивок доезжают не мгновенно.</summary>
    [Fact]
    public void AttachmentWithoutItsFirmware_IsSkippedSilently()
    {
        var fwA = AddFirmware(_a, "3.0.0005.0007.20260101_0000");
        AddAttachment(_a, fwA, FwAttachmentKinds.SetupGuide, "руководство");

        var snapshot = _a.ExportHierarchyData();
        snapshot.FwVersions.Clear();          // прошивка до этой машины ещё не доехала

        var counts = _b.ImportHierarchyData(snapshot);

        Assert.Equal(0, counts.FwAttachmentsAdded);
        Assert.Empty(_b.GetAllFwVersionsWithNames());
    }
}
