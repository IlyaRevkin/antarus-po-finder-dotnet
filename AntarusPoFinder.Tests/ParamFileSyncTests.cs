using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Синхронизация файлов параметров между двумя машинами — то, чего у param_files не было
/// вовсе до появления sync_id: удаление не разъезжалось, уже совпавшая строка никогда не
/// обновлялась, а эталонная синхронизация не умела вычистить чужой мусор. Жалоба владельца дословно:
/// «у меня в таблице 2 записи, а у коллеги 4, и все не те, что я залил».
///
/// Машины строятся через TwoMachines: у каждой своя локальная БД, общий корень на диске — ровно как
/// два реальных ПК с одной сетевой шарой.</summary>
public class ParamFileSyncTests
{
    private const string Manufacturer = "Danfoss";

    /// <summary>Рукопожатие: B принимает справочник A, чтобы подтипы соотносились по sync_id, а не по
    /// именам (то же требование, что у остальных тестов синхронизации).</summary>
    private static void Handshake(TwoMachines m) => m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());

    private static (int SubtypeId, string GroupName, string SubtypeName) PickSubtype(Database db)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name != "—");
        return (subtype.Id!.Value, group.Name, subtype.Name);
    }

    private static int AddParams(Database db, int subtypeId, string filename, string uploadDate,
        string description = "", string diskPath = @"Z:\Antarus\Параметры\ПЖ\ХП\Danfoss") =>
        db.AddParamFile(new ParamFile
        {
            SubtypeId = subtypeId,
            Manufacturer = Manufacturer,
            Filename = filename,
            DiskPath = diskPath,
            Description = description,
            UploadDate = uploadDate,
        });

    // ── Жалоба 2: удаление между машинами ────────────────────────────────────────────────────

    /// <summary>Запись, снятую на машине A, обязано снять и у B. Раньше архивные строки вообще не
    /// попадали в снимок (WHERE archived = 0 в экспорте), а импорт был «только добавлять» — снятая
    /// запись оставалась у коллеги навсегда, и таблицы расходились.</summary>
    [Fact]
    public void Deletion_OnOneMachine_ArchivesTheRowOnTheOther()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var (subtypeA, _, _) = PickSubtype(m.DbA);
        var id = AddParams(m.DbA, subtypeA, "params_v1.dcfx", "2026-08-01 10:00:00");

        m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        Assert.Single(m.DbB.GetParamFiles().Where(f => f.Filename == "params_v1.dcfx"));

        // A снимает запись (файл на диске при этом не трогается).
        m.DbA.DeleteParamFile(id);

        var exported = m.DbA.ExportHierarchyData();
        // Тумбстоун обязан ехать в снимке — именно ПОЛОЖИТЕЛЬНЫМ сигналом, а не молчаливым отсутствием.
        Assert.Contains(exported.ParamFiles, p => p.Filename == "params_v1.dcfx" && p.Archived == 1);

        var counts = m.DbB.ImportHierarchyData(exported);
        Assert.Equal(1, counts.ParamFilesRemoved);
        Assert.Empty(m.DbB.GetParamFiles().Where(f => f.Filename == "params_v1.dcfx"));
    }

    /// <summary>Обратное правило: локальная архивация постоянна. Машина, которая ещё не знает об
    /// удалении, продолжает выгружать запись живой — воскресить снятую строку она не должна.</summary>
    [Fact]
    public void LocalArchive_IsNeverRevivedByAStaleLiveCopy()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var (subtypeA, _, _) = PickSubtype(m.DbA);
        AddParams(m.DbA, subtypeA, "params_v1.dcfx", "2026-08-01 10:00:00");
        var staleSnapshot = m.DbA.ExportHierarchyData(); // снимок, сделанный ДО удаления

        m.DbB.ImportHierarchyData(staleSnapshot);
        var idB = m.DbB.GetParamFiles().Single(f => f.Filename == "params_v1.dcfx").Id!.Value;
        m.DbB.DeleteParamFile(idB);

        m.DbB.ImportHierarchyData(staleSnapshot);
        Assert.Empty(m.DbB.GetParamFiles().Where(f => f.Filename == "params_v1.dcfx"));
    }

    // ── «Усыновление» sync_id при первом контакте ────────────────────────────────────────────

    /// <summary>Две независимо заведённые базы уже содержат «один и тот же» файл, но с разными
    /// GUID-ами. При первом контакте строка обязана совпасть по натуральному ключу (подтип +
    /// производитель + имя файла), НЕ задвоиться и перенять чужой sync_id — после чего стороны
    /// говорят об одной строке уже по идентификатору, и следующее удаление/обновление доезжает.</summary>
    [Fact]
    public void FirstContact_MatchesByNaturalKey_AndAdoptsIncomingSyncId()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var (subtypeA, _, _) = PickSubtype(m.DbA);
        var (subtypeB, _, _) = PickSubtype(m.DbB);

        var idA = AddParams(m.DbA, subtypeA, "shared.dcfx", "2026-08-01 10:00:00", diskPath: @"Z:\A\Параметры");
        var idB = AddParams(m.DbB, subtypeB, "shared.dcfx", "2026-08-01 10:00:00", diskPath: @"Y:\B\Параметры");

        var syncA = m.DbA.GetParamFiles().Single(f => f.Id == idA).SyncId;
        var syncBefore = m.DbB.GetParamFiles().Single(f => f.Id == idB).SyncId;
        Assert.NotEqual("", syncA);
        Assert.NotEqual(syncA, syncBefore);

        var counts = m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        Assert.Equal(0, counts.ParamFiles); // ничего не задвоилось

        var rowsB = m.DbB.GetParamFiles().Where(f => f.Filename == "shared.dcfx").ToList();
        var adopted = Assert.Single(rowsB);
        Assert.Equal(idB, adopted.Id);          // строка та же самая, не заменённая
        Assert.Equal(syncA, adopted.SyncId);    // …но идентификатор теперь общий
        // disk_path остался локальным: у машины B свой корень, чужой абсолютный путь ей не подходит.
        Assert.Equal(@"Y:\B\Параметры", adopted.DiskPath);

        // Раз идентификатор общий — следующее удаление у A доезжает до B.
        m.DbA.DeleteParamFile(idA);
        m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        Assert.Empty(m.DbB.GetParamFiles().Where(f => f.Filename == "shared.dcfx"));
    }

    /// <summary>Уже совпавшая строка обновляется свежей датой/описанием/тегами: раньше импорт её
    /// просто пропускал («уже есть»), и перезаливка у коллеги никогда не доезжала.</summary>
    [Fact]
    public void MatchedRow_TakesFresherUploadDateDescriptionAndTags()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var (subtypeA, _, _) = PickSubtype(m.DbA);
        var idA = AddParams(m.DbA, subtypeA, "params.dcfx", "2026-08-01 10:00:00", "первая редакция");
        m.DbA.UpdateParamFileTags(idA, "ПЖ-100");
        m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());

        var before = m.DbB.GetParamFiles().Single(f => f.Filename == "params.dcfx");
        Assert.Equal("2026-08-01 10:00:00", before.UploadDate);

        // A перезалил файл: свежая дата + дописанный журнал в описании + ещё один тег.
        m.DbA.UpdateParamFileUpload(idA, @"Z:\Antarus\Параметры\ПЖ\ХП\Danfoss",
            "первая редакция\n[2026-08-02] Перезалит файл.", "2026-08-02 09:30:00");
        m.DbA.UpdateParamFileTags(idA, "ПЖ-100 ПЖ-200");

        var counts = m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        Assert.Equal(1, counts.ParamFilesUpdated);

        var after = m.DbB.GetParamFiles().Single(f => f.Filename == "params.dcfx");
        Assert.Equal("2026-08-02 09:30:00", after.UploadDate);
        Assert.Contains("[2026-08-02]", after.Description);
        Assert.Contains("ПЖ-200", after.Tags);
        Assert.Contains("ПЖ-100", after.Tags); // теги объединяются, свои не теряются
    }

    // ── Жалоба 2, вторая половина: эталонная синхронизация ───────────────────────────────────

    /// <summary>Эталонный снимок вычищает у получателя записи параметров, которых в нём нет вовсе —
    /// в том числе те, которых у отправителя никогда и не было (надгробить нечего, поэтому обычная
    /// синхронизация тут бессильна). Именно случай «у меня 2 записи, а у коллеги 4, и все не те».</summary>
    [Fact]
    public void Authoritative_ArchivesParamRowsAbsentFromTheSnapshot()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var (subtypeA, _, _) = PickSubtype(m.DbA);
        var (subtypeB, _, _) = PickSubtype(m.DbB);

        AddParams(m.DbA, subtypeA, "правильный_1.dcfx", "2026-08-01 10:00:00");
        AddParams(m.DbA, subtypeA, "правильный_2.dcfx", "2026-08-01 10:05:00");

        AddParams(m.DbB, subtypeB, "мусор_1.dcfx", "2026-07-01 10:00:00");
        AddParams(m.DbB, subtypeB, "мусор_2.dcfx", "2026-07-01 10:01:00");
        AddParams(m.DbB, subtypeB, "мусор_3.dcfx", "2026-07-01 10:02:00");
        AddParams(m.DbB, subtypeB, "мусор_4.dcfx", "2026-07-01 10:03:00");

        var exported = m.DbA.ExportHierarchyData();
        Assert.True(exported.ParamFilesHaveSync);

        var preview = m.DbB.PreviewImportHierarchyData(exported, authoritative: true);
        Assert.Equal(4, preview.ParamFilesRemoved);
        Assert.Equal(2, preview.ParamFiles);
        Assert.Equal(4, m.DbB.GetParamFiles().Count); // предпросмотр ничего не написал

        var counts = m.DbB.ImportHierarchyData(exported, authoritative: true);
        Assert.Equal(4, counts.ParamFilesRemoved);

        var live = m.DbB.GetParamFiles().Select(f => f.Filename).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "правильный_1.dcfx", "правильный_2.dcfx" }, live);
    }

    /// <summary>Обычная (не эталонная) синхронизация ничего у получателя не вычищает — своя загрузка,
    /// которой отправитель ещё не видел, обязана уцелеть. Поведение ровно как до появления sync_id.</summary>
    [Fact]
    public void NonAuthoritative_KeepsRowsTheSenderNeverHad()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var (subtypeA, _, _) = PickSubtype(m.DbA);
        var (subtypeB, _, _) = PickSubtype(m.DbB);
        AddParams(m.DbA, subtypeA, "у_ильи.dcfx", "2026-08-01 10:00:00");
        AddParams(m.DbB, subtypeB, "только_у_коллеги.dcfx", "2026-08-01 11:00:00");

        m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());

        Assert.Equal(2, m.DbB.GetParamFiles().Count);
        Assert.Contains(m.DbB.GetParamFiles(), f => f.Filename == "только_у_коллеги.dcfx");
    }

    /// <summary>Предпросмотр перед отправкой эталона показывает файлы параметров, которые исчезнут у
    /// получателей — операция обратимая (запись архивируется, файл на диске цел), но узнавать о ней
    /// постфактум администратор не должен.</summary>
    [Fact]
    public void PreviewAuthoritativeDiff_ListsParamFilesThatWillDisappear()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var (subtypeA, _, _) = PickSubtype(m.DbA);
        var (subtypeB, _, _) = PickSubtype(m.DbB);
        AddParams(m.DbA, subtypeA, "мой.dcfx", "2026-08-01 10:00:00");
        AddParams(m.DbB, subtypeB, "чужой_мусор.dcfx", "2026-07-01 10:00:00");

        var diff = Database.PreviewAuthoritativeDiff(m.DbA.ExportHierarchyData(), m.DbB.ExportHierarchyData());
        var category = diff.Categories.Single(c => c.Label == "Файлы параметров");

        Assert.Contains(category.Removed, s => s.EndsWith("чужой_мусор.dcfx", StringComparison.Ordinal));
        Assert.Contains(category.Added, s => s.EndsWith("мой.dcfx", StringComparison.Ordinal));
    }

    /// <summary>Предохранитель: снимок со старой версии приложения (без sync_id у параметров и без
    /// архивных строк) не должен быть принят за полный — иначе одна эталонная синхронизация со
    /// старого клиента вычистила бы у всех остальных всю таблицу параметров.</summary>
    [Fact]
    public void Authoritative_FromOldClientSnapshot_RemovesNothing()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Handshake(m);

        var (subtypeB, _, _) = PickSubtype(m.DbB);
        AddParams(m.DbB, subtypeB, "локальный.dcfx", "2026-08-01 10:00:00");

        var oldStyle = m.DbA.ExportHierarchyData();
        oldStyle.ParamFilesHaveSync = false;      // так выглядит снимок старого клиента
        oldStyle.ParamFiles.Clear();

        var counts = m.DbB.ImportHierarchyData(oldStyle, authoritative: true);
        Assert.Equal(0, counts.ParamFilesRemoved);
        Assert.Single(m.DbB.GetParamFiles());
    }
}
