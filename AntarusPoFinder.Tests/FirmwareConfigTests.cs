using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>КОНФИГУРАЦИИ шкафов — заранее заготовленные варианты одной и той же прошивки (см.
/// FirmwareConfigService). Формулировка: «одна прошивка с разными настройками может быть: типа 1
/// или 2, или вообще нет задвижек, а прошивка та же… всё отличие будет только в тегах, а тегом будет
/// название шкафа».
///
/// Главное, что здесь проверяется: файлы на диске ОДНИ И ТЕ ЖЕ (вариантов бывает десяток, шара WebDAV
/// и медленная — копировать нельзя), выдача поиска отдаёт ОДНУ строку на прошивку (ту конфигурацию,
/// чьи теги совпали), а модерация и история версий вариантов не показывают вовсе.</summary>
public class FirmwareConfigTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    private const string Cabinet2Pumps = "Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(9-14А)-АВР-FD-Ст";
    private const string CabinetJockey = "Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(24-32А)/Пд-(6А)/Зд1(4А)-АВР-FD-Ст";

    public FirmwareConfigTests()
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

    private FirmwareUploadResult Upload(EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod,
        params string[] tags)
    {
        var src = Path.Combine(_tempRoot.Path, $"src_{Guid.NewGuid():N}.psl");
        File.WriteAllText(src, "dummy firmware bytes");
        var result = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
        {
            SourcePath = src,
            Group = group,
            Subtype = subtype,
            Modification = mod,
            LaunchTypes = new() { "УПП" },
            Description = "прошивка с конфигурациями",
            IncludeDateInVersion = false,
            RootPath = Root,
            AuthorUserName = "tester",
            Tags = tags.ToList(),
        });
        Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
        return result;
    }

    private FirmwareConfigService.ApplyResult ApplyBulk(FwVersionRecord primary, string bulkText) =>
        FirmwareConfigService.Apply(_db, primary, FirmwareConfigService.ParseBulk(bulkText));

    // ── Разбор массового ввода ────────────────────────────────────────────────

    /// <summary>Обычная строка — просто название шкафа: оно и имя конфигурации, и её единственный тег.
    /// Это и есть «завести пачкой»: список названий вставляется из таблицы как есть.</summary>
    [Fact]
    public void ParseBulk_PlainLines_BecomeNameAndTag()
    {
        var specs = FirmwareConfigService.ParseBulk($"{Cabinet2Pumps}\n{CabinetJockey}");

        Assert.Equal(2, specs.Count);
        Assert.Equal(Cabinet2Pumps, specs[0].Name);
        Assert.Equal(new[] { Cabinet2Pumps }, specs[0].Tags);
        Assert.Equal(CabinetJockey, specs[1].Name);
    }

    [Fact]
    public void ParseBulk_NamedForm_SplitsNameAndTags()
    {
        var specs = FirmwareConfigService.ParseBulk($"2 насоса | {Cabinet2Pumps}; ПЖ-ПП-2");

        var spec = Assert.Single(specs);
        Assert.Equal("2 насоса", spec.Name);
        Assert.Equal(new[] { Cabinet2Pumps, "ПЖ-ПП-2" }, spec.Tags);
    }

    /// <summary>Пустые строки и повторы — обычное содержимое вставленного из таблицы списка.</summary>
    [Fact]
    public void ParseBulk_SkipsBlanksAndDuplicateNames()
    {
        var specs = FirmwareConfigService.ParseBulk($"\n  \n{Cabinet2Pumps}\n{Cabinet2Pumps.ToUpperInvariant()}\n");

        Assert.Single(specs);
    }

    /// <summary>«Имя |» без тегов — имя работает и тегом: вариант без единого тега поиском не найти,
    /// а значит он бессмыслен.</summary>
    [Fact]
    public void ParseBulk_NamedFormWithoutTags_UsesNameAsTag()
    {
        var spec = Assert.Single(FirmwareConfigService.ParseBulk("2 насоса |"));
        Assert.Equal(new[] { "2 насоса" }, spec.Tags);
    }

    /// <summary>Показ и разбор — обратные операции: редактор показывает уже заведённое тем же текстом,
    /// которым его вводят, иначе повторное сохранение меняло бы конфигурации на ровном месте.</summary>
    [Fact]
    public void FormatBulk_RoundTripsThroughParseBulk()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;
        ApplyBulk(primary, $"{Cabinet2Pumps}\n2 насоса + жокей | {CabinetJockey}; жокей");

        var text = FirmwareConfigService.FormatBulk(FirmwareConfigService.Current(_db, primary));
        var reparsed = FirmwareConfigService.ParseBulk(text);

        Assert.Equal(2, reparsed.Count);
        Assert.Equal(Cabinet2Pumps, reparsed[0].Name);
        Assert.Equal(new[] { Cabinet2Pumps }, reparsed[0].Tags);
        Assert.Equal("2 насоса + жокей", reparsed[1].Name);
        Assert.Equal(new[] { CabinetJockey, "жокей" }, reparsed[1].Tags);

        // И повторное применение того же текста — пустая операция, а не «всё заново».
        var again = ApplyBulk(primary, text);
        Assert.False(again.Changed);
    }

    // ── Заведение / правка / удаление ─────────────────────────────────────────

    /// <summary>Пачкой за один заход — и ни одного лишнего байта на диске: у всех конфигураций та же
    /// папка и тот же номер версии, что у самой прошивки.</summary>
    [Fact]
    public void Apply_CreatesConfigsSharingTheSameFilesOnDisk()
    {
        var (group, subtype, mod) = Cabinet();
        var uploaded = Upload(group, subtype, mod);
        var primary = uploaded.Record!;
        var filesBefore = Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Length;

        var result = ApplyBulk(primary, $"{Cabinet2Pumps}\n{CabinetJockey}");

        Assert.Equal(2, result.Added.Count);
        var configs = _db.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw);
        Assert.Equal(2, configs.Count);
        Assert.All(configs, c =>
        {
            Assert.Equal(primary.DiskPath, c.DiskPath);
            Assert.Equal(primary.VersionRaw, c.VersionRaw);
            Assert.Equal(primary.Filename, c.Filename);
        });
        Assert.Equal(filesBefore, Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Length);
    }

    /// <summary>Теги конфигурации = базовые теги прошивки + её собственное название шкафа. Базовые
    /// нужны, чтобы вариант находился и по общим словам, а не только по точному названию.</summary>
    [Fact]
    public void Apply_ConfigTags_IncludeBaseTagsOfFirmware()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod, "пожарные насосы").Record!;

        ApplyBulk(primary, Cabinet2Pumps);

        var config = Assert.Single(_db.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw));
        var tags = TagString.Parse(config.Tags);
        Assert.Contains(Cabinet2Pumps, tags);
        Assert.Contains("пожарные насосы", tags);
        Assert.Contains(group.Name, tags);
        // А редактору отдаются только СОБСТВЕННЫЕ теги варианта — базовые он не показывает.
        var shown = Assert.Single(FirmwareConfigService.Current(_db, primary));
        Assert.Equal(new[] { Cabinet2Pumps }, shown.Tags);
    }

    [Fact]
    public void Apply_ChangedTags_UpdateExistingConfigInPlace()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;
        ApplyBulk(primary, $"2 насоса | {Cabinet2Pumps}");
        var idBefore = _db.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw).Single().Id;

        var result = ApplyBulk(primary, $"2 насоса | {Cabinet2Pumps}; ещё тег");

        Assert.Equal(new[] { "2 насоса" }, result.Updated);
        Assert.Empty(result.Added);
        var config = Assert.Single(_db.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw));
        Assert.Equal(idBefore, config.Id); // та же строка, а не новая
        Assert.Contains("ещё тег", TagString.Parse(config.Tags));
    }

    /// <summary>Убрали строку — конфигурация удалена НАДГРОБИЕМ (доедет до коллег), а файлы прошивки
    /// не тронуты: они общие и принадлежат самой прошивке, а не варианту.</summary>
    [Fact]
    public void Apply_RemovedConfig_IsTombstoned_FilesUntouched()
    {
        var (group, subtype, mod) = Cabinet();
        var uploaded = Upload(group, subtype, mod);
        var primary = uploaded.Record!;
        ApplyBulk(primary, $"{Cabinet2Pumps}\n{CabinetJockey}");

        var result = ApplyBulk(primary, Cabinet2Pumps);

        Assert.Equal(new[] { CabinetJockey }, result.Removed);
        Assert.Single(_db.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw));
        Assert.True(Directory.Exists(uploaded.DestinationFolder!));
        Assert.NotEmpty(Directory.GetFiles(uploaded.DestinationFolder!));
        // Сама прошивка на месте и не помечена удалённой.
        Assert.NotNull(_db.GetFwVersionById(primary.Id!.Value));
    }

    /// <summary>Конфигурация уже выпущенной прошивки не всплывает в модерации: это та же прошивка,
    /// проверять в ней нечего.</summary>
    [Fact]
    public void Apply_InheritsReleasedStateFromFirmware()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;
        _db.MarkFwVersionReleased(primary.Id!.Value);
        primary.Released = true;

        ApplyBulk(primary, Cabinet2Pumps);

        var config = Assert.Single(_db.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw));
        Assert.True(config.Released);
    }

    // ── Модерация и история их не показывают ──────────────────────────────────

    /// <summary>Десять вариантов одного шкафа не должны превращать очередь модерации в десять записей:
    /// проверять в них нечего, выпускаются они вместе с самой прошивкой.</summary>
    [Fact]
    public void Configs_DoNotShowUpInModerationQueue()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;
        var queueBefore = _db.GetUnreleasedFwVersionsCount();

        ApplyBulk(primary, $"{Cabinet2Pumps}\n{CabinetJockey}");

        Assert.Equal(queueBefore, _db.GetUnreleasedFwVersionsCount());
        Assert.DoesNotContain(_db.GetUnreleasedFwVersionsWithNames(), v => v.ConfigName.Length > 0);
    }

    /// <summary>И в историю версий тоже: конфигурация — не отдельная версия, номер и файлы у неё те же.</summary>
    [Fact]
    public void Configs_DoNotShowUpInVersionHistory()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;
        ApplyBulk(primary, $"{Cabinet2Pumps}\n{CabinetJockey}");

        var history = _db.GetFwVersionsHistory(subtype.Id!.Value, mod.ControllerId);

        Assert.Single(history);
        Assert.Equal(primary.VersionRaw, history[0].VersionRaw);
    }

    /// <summary>Выпуск прошивки из модерации снимает её и со всех конфигураций разом — они делят файлы
    /// (MarkFwVersionReleasedWithLinked).</summary>
    [Fact]
    public void ReleasingFirmware_AlsoReleasesItsConfigs()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;
        ApplyBulk(primary, $"{Cabinet2Pumps}\n{CabinetJockey}");

        _db.MarkFwVersionReleasedWithLinked(primary.Id!.Value);

        Assert.All(_db.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw), c => Assert.True(c.Released));
        Assert.Equal(0, _db.GetUnreleasedFwVersionsCount());
    }

    // ── Поиск ─────────────────────────────────────────────────────────────────

    /// <summary>Ровно то, что требовалось: наладчик вводит название своего шкафа и получает ОДНУ
    /// строку выдачи — ту конфигурацию, которая ему подходит, а не десяток одинаковых прошивок.</summary>
    [Fact]
    public void Search_ByCabinetName_ReturnsSingleResult_WithMatchingConfig()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;
        ApplyBulk(primary, $"{Cabinet2Pumps}\n{CabinetJockey}");

        var hits = SearchService.Search(_db, CabinetJockey);

        var hit = Assert.Single(hits);
        Assert.Equal(CabinetJockey, hit.ConfigName);
        Assert.Equal(primary.VersionRaw, hit.VersionRaw);
        // Та же самая прошивка: папка на диске у конфигурации общая с основной записью.
        Assert.Equal(primary.DiskPath, hit.FirmwareDir);
    }

    /// <summary>Второй шкаф того же ряда — та же прошивка, но другая конфигурация.</summary>
    [Fact]
    public void Search_ByOtherCabinetName_ReturnsTheOtherConfig()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;
        ApplyBulk(primary, $"{Cabinet2Pumps}\n{CabinetJockey}");

        var hit = Assert.Single(SearchService.Search(_db, Cabinet2Pumps));

        Assert.Equal(Cabinet2Pumps, hit.ConfigName);
    }

    /// <summary>Шаблонная конфигурация (звёздочка) закрывает весь ряд амперажей одной строкой — то,
    /// ради чего шаблонные теги и делались.</summary>
    [Fact]
    public void Search_TemplateConfig_CoversWholeAmperageRange()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;
        ApplyBulk(primary, "Пожарные насосы 2 шт | Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(*-*А)-АВР-FD-Ст");

        foreach (var amps in new[] { "9-14", "20-25", "100-125" })
        {
            var hit = Assert.Single(SearchService.Search(_db,
                $"Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-({amps}А)-АВР-FD-Ст"));
            Assert.Equal("Пожарные насосы 2 шт", hit.ConfigName);
        }
    }

    /// <summary>Общий запрос («покажи прошивки этого шкафа вообще») — конкретная комплектация в нём не
    /// спрашивалась, поэтому наверх выходит САМА прошивка, а не произвольный её вариант.</summary>
    [Fact]
    public void Search_GenericQuery_PrefersFirmwareItselfOverConfigs()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;
        ApplyBulk(primary, $"{Cabinet2Pumps}\n{CabinetJockey}");

        var hit = Assert.Single(SearchService.Search(_db, $"{group.Name} {mod.ControllerName}"));

        Assert.Equal("", hit.ConfigName);
        Assert.Equal(primary.Id!.Value, hit.FwVersionId);
    }

    // ── Обновление прошивки ───────────────────────────────────────────────────

    /// <summary>Конфигурации переживают обновление прошивки: залили новую версию — весь заготовленный
    /// ряд комплектаций переехал на неё. Без этого «запараметрировать заранее» работало бы ровно до
    /// первого обновления.</summary>
    [Fact]
    public void NewVersion_CarriesOverConfigsOfPreviousVersion()
    {
        var (group, subtype, mod) = Cabinet();
        var first = Upload(group, subtype, mod).Record!;
        ApplyBulk(first, $"2 насоса | {Cabinet2Pumps}\nЖокей и задвижка | {CabinetJockey}");

        var second = Upload(group, subtype, mod);

        Assert.Equal(2, second.CarriedOverConfigs.Count);
        var carried = FirmwareConfigService.Current(_db, second.Record!);
        Assert.Equal(new[] { "2 насоса", "Жокей и задвижка" }, carried.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { Cabinet2Pumps }, carried[0].Tags);
        // Файлы новой версии свои (это настоящая новая версия), а конфигурации ссылаются на них.
        Assert.All(_db.GetFwVersionConfigs(second.Record!.DiskPath, second.Record!.VersionRaw),
            c => Assert.Equal(second.Record!.DiskPath, c.DiskPath));
        // И поиск по названию шкафа находит уже НОВУЮ версию.
        var hit = Assert.Single(SearchService.Search(_db, CabinetJockey));
        Assert.Equal(second.Record!.VersionRaw, hit.VersionRaw);
    }

    /// <summary>Первая версия шкафа переносить не от чего — и молчаливо ничего не заводит.</summary>
    [Fact]
    public void FirstVersion_CarriesOverNothing()
    {
        var (group, subtype, mod) = Cabinet();
        var first = Upload(group, subtype, mod);

        Assert.Empty(first.CarriedOverConfigs);
        Assert.Empty(FirmwareConfigService.Current(_db, first.Record!));
    }

    // ── «Дублировать» ─────────────────────────────────────────────────────────

    /// <summary>Кнопка «Дублировать» (Настройки → Прошивки) теперь заводит именно КОНФИГУРАЦИЮ: копия
    /// получает непустое имя варианта. Без него две записи с одинаковым натуральным ключом были бы для
    /// синхронизации одной и той же строкой и схлопывались бы у коллег в одну.</summary>
    [Fact]
    public void DuplicateFwVersion_CreatesNamedConfig_SharingTheSameFiles()
    {
        var (group, subtype, mod) = Cabinet();
        var primary = Upload(group, subtype, mod).Record!;

        var firstCopy = _db.DuplicateFwVersion(primary.Id!.Value);
        var secondCopy = _db.DuplicateFwVersion(primary.Id!.Value);

        var a = _db.GetFwVersionById(firstCopy)!;
        var b = _db.GetFwVersionById(secondCopy)!;
        Assert.Equal("Конфигурация 2", a.ConfigName);
        Assert.Equal("Конфигурация 3", b.ConfigName);
        Assert.NotEqual(a.SyncId, b.SyncId);
        Assert.Equal(primary.DiskPath, a.DiskPath);
        Assert.Equal(primary.VersionRaw, b.VersionRaw);
        // И очередь модерации от дублирования не растёт.
        Assert.DoesNotContain(_db.GetUnreleasedFwVersionsWithNames(), v => v.ConfigName.Length > 0);
    }
}
