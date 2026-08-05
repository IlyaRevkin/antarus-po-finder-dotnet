using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Снятие тега с ЗАПИСИ должно переживать синхронизацию.
///
/// <b>Жалоба дословно:</b> «после удаления тегов они всё равно возвращаются откуда-то, видимо опять
/// траблы с синхрой».
///
/// <b>Причина.</b> Строки fw_versions/param_files синхронизируются аддитивно, а теги на них просто
/// ОБЪЕДИНЯЛИСЬ множествами. Объединение появилось не зря: тег («точное название шкафа») почти всегда
/// вешают на давно разошедшуюся по машинам прошивку, и без объединения он к коллегам не доезжал. Но у
/// объединения нет обратного хода — снятый тег возвращался с первой же машины, которая о снятии ещё не
/// знала. А так как обмен двусторонний, воскресший тег ехал обратно и к тому, кто его снял: снять его
/// не мог уже никто.
///
/// <b>Как чинится.</b> Снятие тега стало таким же событием с отметкой времени, как удаление в
/// справочнике (flat_list_state, kind = <c>rowtag:&lt;sync_id записи&gt;</c>): выигрывает более
/// поздняя отметка, а не тот, кто позже нажал импорт. См. Database.FlatLists.RowTagMerger.</summary>
public class RowTagDeletionSyncTests
{
    private const string Cabinet = "Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2";
    private const string Extra = "ЩУН-3";

    private static FwVersionRecord SeedFirmware(Database db, HierarchyService hier, string root, string tags)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        const string versionRaw = "1.99.7.1.20260801_1200";

        var folder = hier.FwPath(root, group.Name, subtype.Name, mod.ControllerName, versionRaw);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fw.psl"), "test firmware");

        var record = new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion, SwVersion = 1, DtStr = "20260801_1200",
            VersionRaw = versionRaw, Filename = "fw.psl", DiskPath = folder,
            Description = "прошивка с тегами", Changelog = "прошивка с тегами",
            Status = "active", Tags = tags,
        };
        record.Id = db.AddFwVersion(record);
        Assert.True(record.Id > 0);
        return record;
    }

    private static void Sync(Database from, Database to) => to.ImportHierarchyData(from.ExportHierarchyData());

    private static string[] TagsOf(Database db, string versionRaw) =>
        TagString.Parse(db.GetAllFwVersionsWithNames(includeArchived: true)
            .Single(v => v.VersionRaw == versionRaw).Tags).ToArray();

    private static int FwId(Database db, string versionRaw) =>
        db.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.VersionRaw == versionRaw).Id!.Value;

    // ── Прошивки ─────────────────────────────────────────────────────────────

    /// <summary>Сама жалоба: тег сняли на своей машине, а коллега его ещё держит — при обмене он не
    /// имеет права вернуться. Раньше возвращался, и снять его было нельзя в принципе.</summary>
    [Fact]
    public void RemovedTag_DoesNotComeBack_FromAColleagueWhoHasNotHeardAboutItYet()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Sync(m.DbA, m.DbB); // рукопожатие справочников

        var fw = SeedFirmware(m.DbA, m.HierA, m.Root.Path, TagString.Join(new[] { Cabinet, Extra }));
        Sync(m.DbA, m.DbB);
        Assert.Contains(Cabinet, TagsOf(m.DbB, fw.VersionRaw));

        // A снимает тег (обычная правка карточки).
        m.DbA.UpdateFwVersion(FwId(m.DbA, fw.VersionRaw), tags: Extra);
        Assert.DoesNotContain(Cabinet, TagsOf(m.DbA, fw.VersionRaw));

        // B о снятии ещё не знает и присылает свой снимок со старым набором.
        Sync(m.DbB, m.DbA);

        Assert.DoesNotContain(Cabinet, TagsOf(m.DbA, fw.VersionRaw));
        Assert.Contains(Extra, TagsOf(m.DbA, fw.VersionRaw));
    }

    /// <summary>Вторая половина того же: снятие обязано ДОЕХАТЬ до коллеги, а не остаться на машине,
    /// где его сделали.</summary>
    [Fact]
    public void RemovedTag_DisappearsOnTheOtherMachineToo()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Sync(m.DbA, m.DbB);

        var fw = SeedFirmware(m.DbA, m.HierA, m.Root.Path, TagString.Join(new[] { Cabinet, Extra }));
        Sync(m.DbA, m.DbB);

        m.DbA.UpdateFwVersion(FwId(m.DbA, fw.VersionRaw), tags: Extra);
        Sync(m.DbA, m.DbB);

        Assert.Equal(new[] { Extra }, TagsOf(m.DbB, fw.VersionRaw));

        // И обратный обмен не воскрешает его у A: B принял снятие как своё собственное решение.
        Sync(m.DbB, m.DbA);
        Assert.Equal(new[] { Extra }, TagsOf(m.DbA, fw.VersionRaw));
    }

    /// <summary>Ради чего объединение и делалось: тег, добавленный на ОДНОЙ машине уже разошедшейся
    /// прошивке, доезжает до всех. Эту половину чинить было нечего — её надо не сломать.</summary>
    [Fact]
    public void AddedTag_StillTravelsToEveryone()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Sync(m.DbA, m.DbB);

        var fw = SeedFirmware(m.DbA, m.HierA, m.Root.Path, Extra);
        Sync(m.DbA, m.DbB);

        // Тег вешает B — у себя, на давно приехавшую прошивку.
        m.DbB.UpdateFwVersion(FwId(m.DbB, fw.VersionRaw), tags: TagString.Join(new[] { Extra, Cabinet }));
        Sync(m.DbB, m.DbA);

        Assert.Contains(Cabinet, TagsOf(m.DbA, fw.VersionRaw));
        // И ни одна машина не потеряла своего.
        Assert.Contains(Extra, TagsOf(m.DbA, fw.VersionRaw));
    }

    /// <summary>Передумали: тег сняли, потом навесили снова. Выигрывает более ПОЗДНЕЕ решение, а не
    /// то, чей импорт случился последним.</summary>
    [Fact]
    public void ReAddingATagAfterRemoval_WinsBecauseItHappenedLater()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Sync(m.DbA, m.DbB);

        var fw = SeedFirmware(m.DbA, m.HierA, m.Root.Path, TagString.Join(new[] { Cabinet, Extra }));
        Sync(m.DbA, m.DbB);

        // A снял, снятие доехало до B.
        m.DbA.UpdateFwVersion(FwId(m.DbA, fw.VersionRaw), tags: Extra);
        Sync(m.DbA, m.DbB);
        Assert.DoesNotContain(Cabinet, TagsOf(m.DbB, fw.VersionRaw));

        // Потом B навесил его обратно — это более позднее событие.
        m.DbB.UpdateFwVersion(FwId(m.DbB, fw.VersionRaw), tags: TagString.Join(new[] { Extra, Cabinet }));
        Sync(m.DbB, m.DbA);

        Assert.Contains(Cabinet, TagsOf(m.DbA, fw.VersionRaw));
        Assert.Contains(Cabinet, TagsOf(m.DbB, fw.VersionRaw));
    }

    /// <summary>Коллега на СТАРОЙ версии программы отметок о снятии не присылает вовсе. Терять из-за
    /// этого теги нельзя: без отметок работает прежнее чистое объединение.</summary>
    [Fact]
    public void AnOldClientWithoutRemovalMarks_StillGetsPlainUnion()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Sync(m.DbA, m.DbB);

        var fw = SeedFirmware(m.DbA, m.HierA, m.Root.Path, Extra);
        Sync(m.DbA, m.DbB);

        // Снимок «старого клиента»: теги есть, секции отметок нет.
        m.DbB.UpdateFwVersion(FwId(m.DbB, fw.VersionRaw), tags: TagString.Join(new[] { Extra, Cabinet }));
        var snapshot = m.DbB.ExportHierarchyData();
        snapshot.FlatListState = null;

        m.DbA.ImportHierarchyData(snapshot);

        Assert.Contains(Cabinet, TagsOf(m.DbA, fw.VersionRaw));
        Assert.Contains(Extra, TagsOf(m.DbA, fw.VersionRaw));
    }

    /// <summary>Строка тегов не должна «меняться» на каждой синхронизации: порядок локальных
    /// сохраняется, новые уходят в конец. Иначе запись вечно выглядела бы изменённой.</summary>
    [Fact]
    public void RepeatedSync_IsStable_AndKeepsTagOrder()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Sync(m.DbA, m.DbB);

        var fw = SeedFirmware(m.DbA, m.HierA, m.Root.Path, TagString.Join(new[] { Cabinet, Extra }));
        Sync(m.DbA, m.DbB);
        m.DbA.UpdateFwVersion(FwId(m.DbA, fw.VersionRaw), tags: Extra);

        for (var i = 0; i < 3; i++)
        {
            Sync(m.DbA, m.DbB);
            Sync(m.DbB, m.DbA);
        }

        Assert.Equal(new[] { Extra }, TagsOf(m.DbA, fw.VersionRaw));
        Assert.Equal(new[] { Extra }, TagsOf(m.DbB, fw.VersionRaw));
    }

    /// <summary>Тег, удалённый В СПРАВОЧНИКЕ, вычищается и из самих записей — это такое же снятие, и
    /// оно тоже обязано пережить обмен. Именно так теги чаще всего и удаляют (Настройки → Теги).</summary>
    [Fact]
    public void DeletingATagFromTheDirectory_AlsoSurvivesSync()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Sync(m.DbA, m.DbB);

        m.DbA.AddTag(Cabinet);
        var fw = SeedFirmware(m.DbA, m.HierA, m.Root.Path, TagString.Join(new[] { Cabinet, Extra }));
        Sync(m.DbA, m.DbB);
        Assert.Contains(Cabinet, TagsOf(m.DbB, fw.VersionRaw));

        m.DbA.DeleteTag(Cabinet);
        Assert.DoesNotContain(Cabinet, TagsOf(m.DbA, fw.VersionRaw));

        // Ни встречный снимок коллеги, ни последующая отдача ему не возвращают тег.
        Sync(m.DbB, m.DbA);
        Assert.DoesNotContain(Cabinet, TagsOf(m.DbA, fw.VersionRaw));

        Sync(m.DbA, m.DbB);
        Assert.DoesNotContain(Cabinet, TagsOf(m.DbB, fw.VersionRaw));
        Assert.DoesNotContain(Cabinet, m.DbB.GetAllTags());
    }

    /// <summary>Переименование тега в справочнике = «старого больше нет, новый появился». Старое имя
    /// не имеет права приехать обратно с чужой машины.</summary>
    [Fact]
    public void RenamingATagInTheDirectory_DoesNotResurrectTheOldNameOnRows()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Sync(m.DbA, m.DbB);

        m.DbA.AddTag(Cabinet);
        var fw = SeedFirmware(m.DbA, m.HierA, m.Root.Path, TagString.Join(new[] { Cabinet, Extra }));
        Sync(m.DbA, m.DbB);

        m.DbA.RenameTag(Cabinet, "ЩУПН-2");
        Sync(m.DbB, m.DbA);

        var tags = TagsOf(m.DbA, fw.VersionRaw);
        Assert.Contains("ЩУПН-2", tags);
        Assert.DoesNotContain(Cabinet, tags);
    }

    // ── Файлы параметров ─────────────────────────────────────────────────────

    /// <summary>У файлов параметров теги живут в той же таблице и синхронизируются той же логикой —
    /// значит и снятие обязано вести себя одинаково.</summary>
    [Fact]
    public void ParamFileTag_RemovedOnOneMachine_DoesNotResurrect()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Sync(m.DbA, m.DbB);

        var group = m.DbA.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = m.DbA.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name != "—");
        var id = m.DbA.AddParamFile(new ParamFile
        {
            SubtypeId = subtype.Id!.Value,
            Manufacturer = "Danfoss",
            Filename = "params.dcfx",
            DiskPath = @"Z:\Antarus\Параметры\ПЖ\ХП\Danfoss",
            Description = "параметры",
            UploadDate = "2026-08-01 10:00:00",
        });
        m.DbA.UpdateParamFileTags(id, TagString.Join(new[] { "ПЖ-100", "ПЖ-200" }));
        Sync(m.DbA, m.DbB);
        Assert.Contains("ПЖ-200", TagString.Parse(m.DbB.GetParamFiles().Single(f => f.Filename == "params.dcfx").Tags));

        // A снимает лишний тег; B о снятии ещё не знает.
        m.DbA.UpdateParamFileTags(id, "ПЖ-100");
        Sync(m.DbB, m.DbA);

        var onA = TagString.Parse(m.DbA.GetParamFiles().Single(f => f.Filename == "params.dcfx").Tags).ToArray();
        Assert.Equal(new[] { "ПЖ-100" }, onA);

        // И снятие доезжает до B.
        Sync(m.DbA, m.DbB);
        var onB = TagString.Parse(m.DbB.GetParamFiles().Single(f => f.Filename == "params.dcfx").Tags).ToArray();
        Assert.Equal(new[] { "ПЖ-100" }, onB);
    }

    // ── Сам механизм отметок ─────────────────────────────────────────────────

    /// <summary>Отметки уезжают в общий конфиг обычной секцией flat_list_state — иначе снятие жило бы
    /// только в локальной базе и никуда не ехало.</summary>
    [Fact]
    public void RemovalMarks_TravelInsideTheOrdinarySnapshot()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        Sync(m.DbA, m.DbB);

        var fw = SeedFirmware(m.DbA, m.HierA, m.Root.Path, TagString.Join(new[] { Cabinet, Extra }));
        m.DbA.UpdateFwVersion(FwId(m.DbA, fw.VersionRaw), tags: Extra);

        var snapshot = m.DbA.ExportHierarchyData();
        var rowTagMarks = (snapshot.FlatListState ?? new())
            .Where(s => s.Kind!.StartsWith(Database.FlatKindRowTagPrefix, StringComparison.Ordinal))
            .ToList();

        var mark = Assert.Single(rowTagMarks.Where(s => s.Name == Cabinet));
        Assert.False(string.IsNullOrEmpty(mark.DeletedAt));

        // Отметка привязана к самой записи (её sync_id), а не к тегу вообще: тот же тег на другой
        // прошивке снятием НЕ считается.
        var syncId = m.DbA.GetAllFwVersionsWithNames(includeArchived: true)
            .Single(v => v.VersionRaw == fw.VersionRaw).SyncId;
        Assert.False(string.IsNullOrEmpty(syncId));
        Assert.Equal(Database.RowTagKind(syncId), mark.Kind);
    }
}
