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

/// <summary>ОПЦ и выдача поиска — через НАСТОЯЩУЮ загрузку, а не вставку строк в базу.
///
/// Жалоба Ильи, повторённая трижды: «при загрузке ОПЦ ОПЦ всё ещё заменяет стандартную прошивку.
/// Плюс после загрузки ОПЦ в поисковой выдаче вываливаются все прошивки, точнее все версии что были,
/// даже заменённые и откатанные».
///
/// Прошлый набор тестов (OpcSearchTests) эти беды НЕ поймал, и причина в том, как он был написан:
/// он клал строки прямо в базу и проверял выдачу. Так проверяется схлопывание, но не загрузка —
/// а ломается, судя по жалобе, именно она. Здесь сценарий повторён целиком: сначала обычные версии
/// линейки загружаются как положено, потом поверх грузится ОПЦ, и только после этого смотрим выдачу.</summary>
public class OpcUploadSearchTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public OpcUploadSearchTests()
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

    private (EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod) Seed()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");
        return (group, subtype, mod);
    }

    private static string TempFile(string ext = ".psl")
    {
        var p = Path.Combine(Path.GetTempPath(), $"opc_search_{Guid.NewGuid():N}{ext}");
        File.WriteAllText(p, "dummy");
        return p;
    }

    private FirmwareUploadRequest Request(string src, EquipmentGroup g, EquipmentSubType s, ControllerModification m) => new()
    {
        SourcePath = src,
        Group = g,
        Subtype = s,
        Modification = m,
        LaunchTypes = new() { "УПП" },
        Description = "тест",
        IncludeDateInVersion = false,
        RootPath = Root,
        AuthorUserName = "tester",
    };

    /// <summary>Сколько строк отдаёт поиск по имени шкафа — то, что наладчик видит на экране.</summary>
    private List<FwVersionRecord> Search() => _db.SearchFwVersionsByTokens(new[] { "ТГР" });

    [Fact]
    public void После_загрузки_опц_выдача_не_разваливается_на_все_версии()
    {
        var (g, s, m) = Seed();
        var files = new List<string>();
        try
        {
            // Три обычные версии линейки: свежая заменяет прежние, в выдаче должна остаться ОДНА.
            for (var i = 0; i < 3; i++)
            {
                var f = TempFile(); files.Add(f);
                var r = FirmwareUploadService.Upload(_db, _hierarchy, Request(f, g, s, m));
                Assert.Equal(FirmwareUploadOutcome.Success, r.Outcome);
            }

            var beforeOpc = Search();
            Assert.Single(beforeOpc);

            // Теперь ОПЦ поверх — разовая сборка под конкретную заявку.
            var opc = TempFile(); files.Add(opc);
            var req = Request(opc, g, s, m);
            req.OpcEnabled = true;
            req.CabinetSnRaw = "42";
            req.RequestNumRaw = "01312";
            req.OpcBaseSwVersion = beforeOpc[0].SwVersion;
            var opcResult = FirmwareUploadService.Upload(_db, _hierarchy, req);
            Assert.Equal(FirmwareUploadOutcome.Success, opcResult.Outcome);

            var afterOpc = Search();

            // Ожидается ровно две строки: текущая обычная и ОПЦ рядом с ней. Заменённые версии
            // линейки в выдачу возвращаться не должны — они и до ОПЦ были схлопнуты.
            Assert.Equal(2, afterOpc.Count);
            Assert.Single(afterOpc.Where(r => r.IsOpc));
            Assert.Single(afterOpc.Where(r => !r.IsOpc));
        }
        finally { foreach (var f in files) File.Delete(f); }
    }

    /// <summary>ОПЦ не вытесняет обычную прошивку: обе остаются в выдаче.</summary>
    [Fact]
    public void Опц_не_заменяет_стандартную_в_выдаче()
    {
        var (g, s, m) = Seed();
        var files = new List<string>();
        try
        {
            var f = TempFile(); files.Add(f);
            var baseResult = FirmwareUploadService.Upload(_db, _hierarchy, Request(f, g, s, m));
            Assert.Equal(FirmwareUploadOutcome.Success, baseResult.Outcome);

            var opc = TempFile(); files.Add(opc);
            var req = Request(opc, g, s, m);
            req.OpcEnabled = true;
            req.CabinetSnRaw = "42";
            req.RequestNumRaw = "01312";
            req.OpcBaseSwVersion = baseResult.Record!.SwVersion;
            Assert.Equal(FirmwareUploadOutcome.Success,
                FirmwareUploadService.Upload(_db, _hierarchy, req).Outcome);

            var found = Search();

            Assert.Contains(found, r => !r.IsOpc);
            Assert.Contains(found, r => r.IsOpc);
        }
        finally { foreach (var f in files) File.Delete(f); }
    }

    /// <summary>То же самое, но на базе, ПОХОЖЕЙ НА БОЕВУЮ: есть откатанная версия, есть два
    /// исполнения. Ровно этим она и отличается от чистой, а жалоба приходит именно с боевой.
    ///
    /// Откатанная версия в выдаче не должна появляться никогда — ни до ОПЦ, ни после: поиск берёт
    /// только активные. Исполнения схлопываются каждое в свою строку, это их смысл.</summary>
    [Fact]
    public void На_базе_с_исполнениями_и_откатом_выдача_после_опц_тоже_не_разваливается()
    {
        var (g, s, m) = Seed();
        var files = new List<string>();
        try
        {
            // Две версии обычной линейки.
            var last = 0;
            for (var i = 0; i < 2; i++)
            {
                var f = TempFile(); files.Add(f);
                var r = FirmwareUploadService.Upload(_db, _hierarchy, Request(f, g, s, m));
                last = r.Record!.SwVersion;
            }

            // Откатанная версия — как в боевой базе после «вернуть прежнюю».
            _db.AddFwVersion(new FwVersionRecord
            {
                SubtypeId = s.Id!.Value, ControllerId = m.ControllerId,
                EqPrefix = g.Prefix, SubPrefix = s.Prefix, HwVersion = m.HwVersion,
                SwVersion = last + 5, VersionRaw = "rolled-back", Status = "rolled_back",
            });

            // Два исполнения — каждое своя линейка, обе актуальны одновременно.
            foreach (var exec in new[] { "2 насоса", "3 и более насосов" })
                _db.AddFwVersion(new FwVersionRecord
                {
                    SubtypeId = s.Id!.Value, ControllerId = m.ControllerId,
                    EqPrefix = g.Prefix, SubPrefix = s.Prefix, HwVersion = m.HwVersion,
                    SwVersion = last + 1, VersionRaw = "exec-" + exec, Status = "active",
                    Execution = exec,
                });

            var before = Search();

            var opc = TempFile(); files.Add(opc);
            var req = Request(opc, g, s, m);
            req.OpcEnabled = true;
            req.CabinetSnRaw = "42";
            req.RequestNumRaw = "01312";
            req.OpcBaseSwVersion = last;
            Assert.Equal(FirmwareUploadOutcome.Success,
                FirmwareUploadService.Upload(_db, _hierarchy, req).Outcome);

            var after = Search();

            // Прибавилась РОВНО одна строка — сама ОПЦ. Всё прочее осталось как было.
            Assert.Equal(before.Count + 1, after.Count);
            Assert.DoesNotContain(after, r => r.Status == "rolled_back");
            Assert.Equal(1, after.Count(r => r.IsOpc));
        }
        finally { foreach (var f in files) File.Delete(f); }
    }

    /// <summary>Признак ОПЦ достаётся ТОЛЬКО самой ОПЦ. Проставься он и обычным записям — у каждой
    /// стал бы свой ключ схлопывания, выдача перестала бы сворачиваться вовсе, и наружу вывалилась бы
    /// вся история версий. Ровно то, на что жалуются.</summary>
    [Fact]
    public void Признак_опц_не_расползается_на_обычные_версии()
    {
        var (g, s, m) = Seed();
        var files = new List<string>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var f = TempFile(); files.Add(f);
                FirmwareUploadService.Upload(_db, _hierarchy, Request(f, g, s, m));
            }

            var opc = TempFile(); files.Add(opc);
            var req = Request(opc, g, s, m);
            req.OpcEnabled = true;
            req.CabinetSnRaw = "42";
            req.RequestNumRaw = "01312";
            req.OpcBaseSwVersion = _db.GetAllFwVersionsWithNames(includeArchived: true)
                .Where(v => !v.IsOpc).Max(v => v.SwVersion);
            FirmwareUploadService.Upload(_db, _hierarchy, req);

            var all = _db.GetAllFwVersionsWithNames(includeArchived: true);

            Assert.Equal(1, all.Count(v => v.IsOpc));
        }
        finally { foreach (var f in files) File.Delete(f); }
    }
}
