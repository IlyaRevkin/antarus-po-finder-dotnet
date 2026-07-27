using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.App.Views;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Фильтр «Подтип» в поиске (SearchView, панель «Фильтры») раньше терял точность у
/// одноимённых, но разных подтипов из разных типов шкафа — например, подтип «2.0» есть и у ПЖ, и у
/// НГР (см. HierarchyDefaultsData), с разными Id. Список вариантов фильтра дедуплировался по ПОДПИСИ
/// (Label), а не по Id: второй одноимённый вариант тихо пропадал из выпадающего списка, а оставшийся
/// оказывался привязан к произвольному (первому попавшемуся при чтении из БД) Id — выбор подтипа в
/// фильтре превращался в лотерею между двумя разными типами шкафа. Сама фильтрация в
/// Database.PassesFilters всегда сравнивала по Id (это никогда не было сломано) — ломался именно
/// список вариантов, из которого этот Id брался.
///
/// SearchView.DedupeFilterOptions/SubtypeFilterLabel — internal static, чистые функции, вынесенные
/// из FillFilter/ReloadSubtypeFilter специально, чтобы проверить это тестом без поднятия самого
/// WPF-контрола (см. AssemblyInfo.cs — InternalsVisibleTo("AntarusPoFinder.Tests")).</summary>
public class SearchFilterLogicTests
{
    // ── DedupeFilterOptions ──────────────────────────────────────────────────

    [Fact]
    public void DedupeFilterOptions_KeepsBothOptions_WhenSameLabelButDifferentId()
    {
        var options = new[]
        {
            new SearchView.FilterOption("2.0", Id: 101),
            new SearchView.FilterOption("2.0", Id: 202),
        };

        var result = SearchView.DedupeFilterOptions(options);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, o => o.Id == 101);
        Assert.Contains(result, o => o.Id == 202);
    }

    [Fact]
    public void DedupeFilterOptions_CollapsesTrueDuplicates_SameId()
    {
        var options = new[]
        {
            new SearchView.FilterOption("2.0", Id: 101),
            new SearchView.FilterOption("2.0", Id: 101),
        };

        var result = SearchView.DedupeFilterOptions(options);

        Assert.Single(result);
    }

    /// <summary>Тип пуска (FilterLaunchCombo) не имеет Id — дедуп по Id для него бессмысленен, у
    /// таких вариантов дедуп по-прежнему идёт по тексту.</summary>
    [Fact]
    public void DedupeFilterOptions_OptionsWithoutId_DedupeByText()
    {
        var options = new[]
        {
            new SearchView.FilterOption("ПЧ", Text: "ПЧ"),
            new SearchView.FilterOption("ПЧ", Text: "ПЧ"),
        };

        var result = SearchView.DedupeFilterOptions(options);

        Assert.Single(result);
    }

    // ── SubtypeFilterLabel ───────────────────────────────────────────────────

    [Fact]
    public void SubtypeFilterLabel_BareName_WhenGroupNamesNotProvided()
    {
        var subtype = new EquipmentSubType { Id = 1, GroupId = 5, Name = "2.0" };

        Assert.Equal("2.0", SearchView.SubtypeFilterLabel(subtype, null));
    }

    [Fact]
    public void SubtypeFilterLabel_AddsGroupSuffix_WhenGroupNamesProvided()
    {
        var pzh = new EquipmentSubType { Id = 1, GroupId = 5, Name = "2.0" };
        var ngr = new EquipmentSubType { Id = 2, GroupId = 6, Name = "2.0" };
        var groupNames = new Dictionary<int, string> { [5] = "ПЖ", [6] = "НГР" };

        Assert.Equal("2.0 (ПЖ)", SearchView.SubtypeFilterLabel(pzh, groupNames));
        Assert.Equal("2.0 (НГР)", SearchView.SubtypeFilterLabel(ngr, groupNames));
    }

    // ── End-to-end: фильтрация по конкретному Id подтипа точна ──────────────

    /// <summary>Настоящая проверка "по id, а не по имени": в реальном справочнике подтип «2.0»
    /// существует у ПЖ и у НГР одновременно (разные Id) — фильтр по SubtypeId одного из них не должен
    /// задевать записи другого, даже при полностью совпадающем имени подтипа.</summary>
    [Fact]
    public void FilterBySubtypeId_DistinguishesSameNamedSubtypesOfDifferentGroups()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var pzhGroup = db.GetAllEquipmentGroups().Single(g => g.Name == "ПЖ");
        var ngrGroup = db.GetAllEquipmentGroups().Single(g => g.Name == "НГР");
        var pzhSubtype = db.GetSubtypesForGroup(pzhGroup.Id!.Value).Single(s => s.Name == "2.0");
        var ngrSubtype = db.GetSubtypesForGroup(ngrGroup.Id!.Value).Single(s => s.Name == "2.0");
        Assert.NotEqual(pzhSubtype.Id, ngrSubtype.Id); // предпосылка теста: правда разные записи

        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        int AddVersion(EquipmentGroup group, EquipmentSubType subtype, int sw) => db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = sw,
            DtStr = $"2026010{sw}_0000",
            VersionRaw = $"2.1.001.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl",
            LaunchTypes = new() { "ПЧ" },
            Status = "active",
        });

        var pzhVersionId = AddVersion(pzhGroup, pzhSubtype, 1);
        var ngrVersionId = AddVersion(ngrGroup, ngrSubtype, 2);

        var pzhHits = SearchService.Search(db, "", filters: new FirmwareSearchFilters { SubtypeId = pzhSubtype.Id });
        var ngrHits = SearchService.Search(db, "", filters: new FirmwareSearchFilters { SubtypeId = ngrSubtype.Id });

        var pzhHit = Assert.Single(pzhHits);
        Assert.Equal(pzhVersionId, pzhHit.FwVersionId);

        var ngrHit = Assert.Single(ngrHits);
        Assert.Equal(ngrVersionId, ngrHit.FwVersionId);
    }
}
