using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Тикет от 09.11.2026 (Киселёв, NALADKA1): «поисковый запрос XL выводит в поиске
/// устаревшую версию прошивки — требуется полностью вывести из релиза».
///
/// «XL» — это ИМЯ ПОДТИПА (НГР-XL), а не тег конкретной прошивки. Значит запросу одинаково
/// подходят ВСЕ версии этого шкафа: слово совпадает с именем подтипа и с именем папки у каждой из
/// них, очки релевантности у всех равны. Схлопывание выдачи (Database.Deduplicate) оставляло из
/// таких одинаковых строк ту, у которой БОЛЬШЕ РАНГ, а ранг — это очки плюс счётчик открытий плюс
/// ручной вес. Устаревшую версию по такому запросу выбирали месяцами, счётчик у неё набрался, и
/// она навсегда выигрывала у свежей, которую никто ещё не открывал. Свежая при этом не просто
/// стояла ниже — её не было в выдаче вовсе: схлопывание оставляет по шкафу одну строку.
///
/// Правило, которое чинит это: внутри одного шкафа сначала отбирается то, что ещё актуально (то же
/// самое «эту версию уже сменила более свежая», что в SQL у Database.NotSuperseded и в метках
/// FwHistoryStatus.Labels), и только потом ранг решает, какая из актуальных строк покажется.
/// Популярность и вес двигают выдачу между шкафами и между конфигурациями — но не умеют воскресить
/// заменённую версию.</summary>
public class SearchStaleVersionTests
{
    private static int AddVersion(Database db, int sw, string tags = "", string execution = "",
        int? hw = null)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "XL");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = hw ?? mod.HwVersion,
            SwVersion = sw,
            DtStr = $"2026010{sw}_0000",
            VersionRaw = $"2.1.001.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl",
            DiskPath = $@"X:\ПО\НГР\XL\SMH4\{sw}",
            LaunchTypes = new List<string> { "ПЧ" },
            Tags = tags,
            Execution = execution,
            Status = FwStatus.Active,
        });
    }

    private static List<int> Search(Database db, string query = "XL")
    {
        var tokens = SearchService.Normalize(query).Split(' ');
        return db.SearchFwVersions(tokens, usageQueryKey: SearchService.UsageKey(query), phrase: query)
            .Select(r => r.Row.Id ?? 0).ToList();
    }

    /// <summary>Сам тикет: по «XL» наверху стояла версия, которую уже сменила более свежая, просто
    /// потому что её чаще открывали.</summary>
    [Fact]
    public void Поиск_по_имени_подтипа_не_выдаёт_версию_которую_сменила_более_свежая()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var older = AddVersion(db, sw: 1);
        var newer = AddVersion(db, sw: 2);

        // Устаревшую выбирали по этому же запросу десяток раз — ровно так счётчик и набирается.
        for (var i = 0; i < 10; i++) db.RecordFwUsage(SearchService.UsageKey("XL"), older);

        var found = Search(db);

        Assert.Equal(new[] { newer }, found);
        Assert.DoesNotContain(older, found);
    }

    /// <summary>Случай с живого диска (18.11.2026, НГР-КНС SMH4, слепок с DESKTOP-LQE4J68).
    ///
    /// В базе лежали ЧЕТЫРЕ записи одного шкафа: 2.1.0004.0001 откатана, 2.1.0004.0002 активна и
    /// ПРОДУБЛИРОВАНА (две строки с разными sync_id), 2.1.0004.0003 активна. На диске при этом
    /// осталась папка только у .0003 — у остальных папки исчезли, и карточка честно писала «на
    /// диске по сохранённому пути не найдена».
    ///
    /// Выдача показывала .0002 — живую в базе, но мёртвую на диске. Человек получал прошивку,
    /// которую нельзя ни открыть, ни залить, и шёл вручную выключать все прежние версии, чтобы
    /// добраться до свежей.
    ///
    /// Проверяем ровно эту расстановку: дубликаты и откатанная соседка не должны мешать показать
    /// самую свежую.</summary>
    [Fact]
    public void Дубликаты_и_откатанная_версия_не_мешают_показать_самую_свежую()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var rolledBack = AddVersion(db, sw: 1);
        var duplicateA = AddVersion(db, sw: 2);
        var duplicateB = AddVersion(db, sw: 2);
        var newest = AddVersion(db, sw: 3);

        db.RollbackFwVersion(rolledBack);

        // Обе копии .0002 успели набрать популярность — именно так старая версия и выигрывала.
        for (var i = 0; i < 10; i++)
        {
            db.RecordFwUsage(SearchService.UsageKey("XL"), duplicateA);
            db.RecordFwUsage(SearchService.UsageKey("XL"), duplicateB);
        }

        var found = Search(db);

        Assert.Contains(newest, found);
        Assert.DoesNotContain(duplicateA, found);
        Assert.DoesNotContain(duplicateB, found);
        Assert.DoesNotContain(rolledBack, found);
    }

    /// <summary>То же самое ручным весом: оператор, поставивший вес старой версии под этот запрос,
    /// не должен возвращать её в выдачу — для «пусть выдаётся именно эта» есть отметка «текущая».</summary>
    [Fact]
    public void Ручной_вес_не_поднимает_заменённую_версию()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var older = AddVersion(db, sw: 1);
        var newer = AddVersion(db, sw: 2);

        db.SetLocalFwWeight(SearchService.UsageKey("XL"), older, 500);

        Assert.Equal(new[] { newer }, Search(db));
    }

    /// <summary>Оверрайд оператора работает: отмеченная вручную «текущая» версия выдаётся, даже
    /// если по номеру она старше. Это единственный законный способ показать не самую свежую
    /// (см. Database.SetFwVersionManualCurrent и FwHistoryStatus.Labels).</summary>
    [Fact]
    public void Отмеченная_вручную_текущая_версия_выдаётся_вместо_более_свежей()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var older = AddVersion(db, sw: 1);
        AddVersion(db, sw: 2);
        db.SetFwVersionManualCurrent(older);

        Assert.Equal(new[] { older }, Search(db));
    }

    /// <summary>Конфигурации шкафа (config_name — варианты ОДНОЙ версии с разными тегами) эта
    /// правка не трогает: они все одного номера, и между ними по-прежнему решают теги и ранг.
    /// Иначе наладчик, вбивший название своей комплектации, получал бы чужую.</summary>
    [Fact]
    public void Между_конфигурациями_свежей_версии_по_прежнему_решают_теги()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1, tags: "2 насоса");
        var newer = AddVersion(db, sw: 2);
        var newerConfig = db.GetFwVersionById(newer)!;
        newerConfig.Id = null;
        newerConfig.ConfigName = "2 насоса";
        newerConfig.Tags = "2 насоса";
        var configId = db.AddFwVersion(newerConfig);

        var found = Search(db, "XL 2 насоса");

        Assert.Equal(configId, found.First());
    }

    /// <summary>Разные ИСПОЛНЕНИЯ и разное ЖЕЛЕЗО остаются каждый со своей актуальной версией:
    /// свежая версия одной линейки не заменяет соседнюю (то же правило, что в NotSuperseded).</summary>
    [Fact]
    public void Соседние_линейки_не_заменяют_друг_друга()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var ordinaryOld = AddVersion(db, sw: 1);
        var ordinaryNew = AddVersion(db, sw: 2);
        var withExecution = AddVersion(db, sw: 1, execution: "3 насоса");

        var found = Search(db, "XL");

        Assert.Contains(ordinaryNew, found);
        Assert.Contains(withExecution, found);
        Assert.DoesNotContain(ordinaryOld, found);
    }
}
