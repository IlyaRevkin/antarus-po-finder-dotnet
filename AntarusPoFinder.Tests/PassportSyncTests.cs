using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Синхронизация шаблонов паспортов шкафов между машинами. Правила у passports ровно те же,
/// что у param_files (см. ParamFileSyncTests и разбор в Database.ConfigExchange.ImportPassports), и
/// заведены они сразу такими — чтобы не повторить историю с параметрами, где удаление не уезжало к
/// коллегам, а совпавшая строка никогда не обновлялась.</summary>
public class PassportSyncTests
{
    /// <summary>Рукопожатие: B принимает справочник A, чтобы подтипы соотносились по sync_id.</summary>
    private static void Handshake(TwoMachines m) => m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());

    private static int PickSubtype(Database db)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        return db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name != "—").Id!.Value;
    }

    private static int AddPassport(Database db, int subtypeId, string name, string uploadDate,
        string description = "", string diskPath = @"Z:\Antarus\ПО\ПЖ\ХП\Паспорт") =>
        db.AddPassport(new PassportTemplate
        {
            SubtypeId = subtypeId,
            Name = name,
            Filename = "Паспорт.docx",
            DiskPath = diskPath,
            Description = description,
            UploadDate = uploadDate,
        });

    /// <summary>Загруженный на A паспорт доезжает до B — вместе с тегами, по которым он находится.</summary>
    [Fact]
    public void Passport_TravelsToTheOtherMachine()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var id = AddPassport(m.DbA, PickSubtype(m.DbA), "Паспорт ПЖ ПИ", "2026-08-04 12:00:00", "первый вариант");
        m.DbA.UpdatePassportTags(id, "ЩУН-3");

        var counts = m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        Assert.Equal(1, counts.Passports);

        var arrived = Assert.Single(m.DbB.GetPassports());
        Assert.Equal("Паспорт ПЖ ПИ", arrived.Name);
        Assert.Equal("первый вариант", arrived.Description);
        Assert.Contains("ЩУН-3", arrived.Tags);
        Assert.Single(m.DbB.SearchPassportsByTokens(new[] { "ЩУН-3" }));
    }

    /// <summary>Запись, снятую на A, обязано снять и у B: архивные строки едут в снимке
    /// ПОЛОЖИТЕЛЬНЫМ тумбстоуном, а не молчаливым отсутствием.</summary>
    [Fact]
    public void Deletion_OnOneMachine_ArchivesTheRowOnTheOther()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var id = AddPassport(m.DbA, PickSubtype(m.DbA), "Паспорт ПЖ ПИ", "2026-08-04 12:00:00");
        m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        Assert.Single(m.DbB.GetPassports());

        m.DbA.DeletePassport(id);

        var exported = m.DbA.ExportHierarchyData();
        Assert.Contains(exported.Passports!, p => p.Name == "Паспорт ПЖ ПИ" && p.Archived == 1);

        var counts = m.DbB.ImportHierarchyData(exported);
        Assert.Equal(1, counts.PassportsRemoved);
        Assert.Empty(m.DbB.GetPassports());
    }

    /// <summary>Локальная архивация постоянна: машина, которая об удалении ещё не знает, продолжает
    /// выгружать запись живой — воскресить снятую строку она не должна.</summary>
    [Fact]
    public void LocalArchive_IsNeverRevivedByAStaleLiveCopy()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        AddPassport(m.DbA, PickSubtype(m.DbA), "Паспорт ПЖ ПИ", "2026-08-04 12:00:00");
        var staleSnapshot = m.DbA.ExportHierarchyData(); // снимок, сделанный ДО удаления

        m.DbB.ImportHierarchyData(staleSnapshot);
        m.DbB.DeletePassport(m.DbB.GetPassports().Single().Id!.Value);

        m.DbB.ImportHierarchyData(staleSnapshot);
        Assert.Empty(m.DbB.GetPassports());
    }

    /// <summary>Первый контакт двух независимо заведённых баз: «один и тот же» паспорт совпадает по
    /// натуральному ключу «подтип + название», не задваивается и перенимает чужой sync_id — после
    /// чего следующее удаление доезжает уже по идентификатору. Локальный путь к диску при этом
    /// остаётся своим: у B корень смонтирован другой буквой.</summary>
    [Fact]
    public void FirstContact_MatchesByNaturalKey_AndAdoptsIncomingSyncId()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var idA = AddPassport(m.DbA, PickSubtype(m.DbA), "Паспорт ПЖ ПИ", "2026-08-04 12:00:00", diskPath: @"Z:\A\ПО");
        var idB = AddPassport(m.DbB, PickSubtype(m.DbB), "Паспорт ПЖ ПИ", "2026-08-04 12:00:00", diskPath: @"Y:\B\ПО");

        var syncA = m.DbA.GetPassports().Single(p => p.Id == idA).SyncId;
        Assert.NotEqual(syncA, m.DbB.GetPassports().Single(p => p.Id == idB).SyncId);

        var counts = m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        Assert.Equal(0, counts.Passports); // ничего не задвоилось

        var adopted = Assert.Single(m.DbB.GetPassports());
        Assert.Equal(idB, adopted.Id);
        Assert.Equal(syncA, adopted.SyncId);
        Assert.Equal(@"Y:\B\ПО", adopted.DiskPath);

        m.DbA.DeletePassport(idA);
        m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        Assert.Empty(m.DbB.GetPassports());
    }

    /// <summary>Перезаливка у коллеги доезжает: совпавшая строка берёт свежую дату, дописанный журнал
    /// и новое имя файла (паспорт могли перезалить в другом формате), а теги объединяются.</summary>
    [Fact]
    public void MatchedRow_TakesFresherUpload_AndMergesTags()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var idA = AddPassport(m.DbA, PickSubtype(m.DbA), "Паспорт ПЖ ПИ", "2026-08-01 10:00:00", "первая редакция");
        m.DbA.UpdatePassportTags(idA, "ЩУН-3");
        m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        m.DbB.UpdatePassportTags(m.DbB.GetPassports().Single().Id!.Value, "ЩУН-3 свой-тег");

        m.DbA.UpdatePassportUpload(idA, @"Z:\Antarus\ПО\ПЖ\ХП\Паспорт", "Паспорт.pdf",
            "первая редакция\n[2026-08-04] Перезалит документ.", "2026-08-04 12:00:00");
        m.DbA.UpdatePassportTags(idA, "ЩУН-3 ЩУН-4");

        var counts = m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        Assert.Equal(1, counts.PassportsUpdated);

        var after = Assert.Single(m.DbB.GetPassports());
        Assert.Equal("2026-08-04 12:00:00", after.UploadDate);
        Assert.Equal("Паспорт.pdf", after.Filename);
        Assert.Contains("[2026-08-04]", after.Description);
        Assert.Contains("ЩУН-4", after.Tags);
        Assert.Contains("свой-тег", after.Tags); // свои теги не теряются
    }

    /// <summary>Эталонный снимок вычищает у получателя паспорта, которых в нём нет вовсе (надгробить
    /// нечего — запись завелась только у него). Обычная синхронизация так не делает: чужая загрузка,
    /// которой отправитель ещё не видел, обязана уцелеть.</summary>
    [Fact]
    public void Authoritative_ArchivesPassportsAbsentFromTheSnapshot()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        AddPassport(m.DbA, PickSubtype(m.DbA), "Паспорт ПЖ ПИ", "2026-08-04 12:00:00");
        AddPassport(m.DbB, PickSubtype(m.DbB), "Черновик паспорта", "2026-07-01 10:00:00");

        var exported = m.DbA.ExportHierarchyData();

        // Обычная синхронизация — чужую строку не трогаем.
        m.DbB.ImportHierarchyData(exported);
        Assert.Equal(2, m.DbB.GetPassports().Count);

        var preview = m.DbB.PreviewImportHierarchyData(exported, authoritative: true);
        Assert.Equal(1, preview.PassportsRemoved);
        Assert.Equal(2, m.DbB.GetPassports().Count); // предпросмотр ничего не написал

        var counts = m.DbB.ImportHierarchyData(exported, authoritative: true);
        Assert.Equal(1, counts.PassportsRemoved);
        Assert.Equal("Паспорт ПЖ ПИ", Assert.Single(m.DbB.GetPassports()).Name);
    }

    /// <summary>Предохранитель: снимок приложения, которое о паспортах ещё не знает (ключа в JSON нет
    /// вовсе → Passports == null), не должен читаться как «у отправителя паспортов ноль» — иначе одна
    /// эталонная синхронизация со старого клиента снесла бы таблицу у всех остальных.</summary>
    [Fact]
    public void Authoritative_FromOldClientSnapshot_RemovesNothing()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        AddPassport(m.DbB, PickSubtype(m.DbB), "Паспорт ПЖ ПИ", "2026-08-04 12:00:00");

        var oldStyle = m.DbA.ExportHierarchyData();
        oldStyle.Passports = null; // так выглядит снимок старого клиента

        var counts = m.DbB.ImportHierarchyData(oldStyle, authoritative: true);
        Assert.Equal(0, counts.PassportsRemoved);
        Assert.Single(m.DbB.GetPassports());
    }

    /// <summary>Предпросмотр перед отправкой эталона показывает паспорта, которые у получателей
    /// исчезнут: операция обратимая (запись архивируется, файл на диске цел), но узнавать о ней
    /// постфактум администратор не должен.</summary>
    [Fact]
    public void PreviewAuthoritativeDiff_ListsPassportsThatWillDisappear()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        AddPassport(m.DbA, PickSubtype(m.DbA), "Паспорт ПЖ ПИ", "2026-08-04 12:00:00");
        AddPassport(m.DbB, PickSubtype(m.DbB), "Черновик паспорта", "2026-07-01 10:00:00");

        var diff = Database.PreviewAuthoritativeDiff(m.DbA.ExportHierarchyData(), m.DbB.ExportHierarchyData());
        var category = diff.Categories.Single(c => c.Label == "Паспорта шкафов");

        Assert.Contains(category.Removed, s => s.EndsWith("Черновик паспорта", StringComparison.Ordinal));
        Assert.Contains(category.Added, s => s.EndsWith("Паспорт ПЖ ПИ", StringComparison.Ordinal));
    }
}
