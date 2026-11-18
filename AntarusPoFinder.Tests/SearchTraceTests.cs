using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ПОЧЕМУ СТРОКИ НЕТ В ВЫДАЧЕ — поэтапно и числами (см. Core/Domain/SearchTrace.cs).
///
/// Жалоба, которая повторяется месяцами: залили новую версию прошивки — она не находится; помогает
/// только залить её ЕЩЁ РАЗ, дубликатом. И уточнение Ильи, которое всё меняет: «я пытался отключать
/// только, не помогало, только дубль помогал». Выключение прежних версий случай не лечит — значит
/// новая версия не проигрывает старой при схлопывании (выключенная старая просто ушла бы), а вовсе
/// не доезжает до выдачи.
///
/// Разбирать такое по рассказу нельзя: поиск молчит. Эти тесты держат то, что он теперь говорит.</summary>
public class SearchTraceTests
{
    private static int AddVersion(Database db, int sw, string tags = "", string execution = "",
        string requestNum = "", string cabinetSn = "", string status = FwStatus.Active)
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
            HwVersion = mod.HwVersion,
            SwVersion = sw,
            DtStr = $"2026010{sw}_0000",
            VersionRaw = $"2.1.001.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl",
            DiskPath = $@"X:\ПО\НГР\XL\SMH4\{sw}",
            LaunchTypes = new List<string> { "ПЧ" },
            Tags = tags,
            Execution = execution,
            RequestNum = requestNum,
            CabinetSn = cabinetSn,
            Status = status,
        });
    }

    private static (List<int> Found, SearchTrace Trace) Search(Database db, string query,
        bool ignoreOnDemandWords = false)
    {
        var tokens = SearchService.Normalize(query).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var rows = db.SearchFwVersions(tokens, phrase: query, ignoreOnDemandWords: ignoreOnDemandWords);
        return (rows.Select(r => r.Row.Id ?? 0).ToList(), db.LastSearchTrace!);
    }

    // ── Подсчёт по этапам ────────────────────────────────────────────────────────────────────

    /// <summary>Числа сходятся: что дал индекс, что отсеяли фильтры, что спрятало слово, что не
    /// совпало с запросом, что выбыло при схлопывании и что осталось. Без этой арифметики разбор
    /// превращается в гадание «а точно ли строка вообще была».</summary>
    [Fact]
    public void Разбор_считает_каждый_шаг_отсева()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.AddOnDemandWord("Рх");

        AddVersion(db, 1);                       // обычная, доедет до выдачи
        AddVersion(db, 2, tags: "XL-Рх");        // спрячет слово
        AddVersion(db, 3);                       // схлопнется с первой (та же пара шкаф+контроллер)

        var (found, trace) = Search(db, "XL");

        Assert.Equal(3, trace.IndexRows);
        Assert.Equal(0, trace.DroppedByFilters);
        Assert.Equal(1, trace.HiddenByWordCount);
        Assert.Equal(2, trace.PassedWords);
        Assert.Equal(2, trace.MatchedQuery);
        Assert.Equal(1, trace.CollapsedCount);
        Assert.Equal(trace.Shown, found.Count);
        Assert.Equal(1, trace.Shown);
        Assert.Equal("обычный", trace.Mode);
    }

    /// <summary>Фильтры считаются отдельно от всего остального: «ничего не найдено» из-за слишком
    /// узкого фильтра и из-за спрятавшего слова — две совершенно разные беды.</summary>
    [Fact]
    public void Отсев_фильтрами_считается_отдельно()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        AddVersion(db, 1);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var tokens = new[] { "XL" };
        db.SearchFwVersions(tokens, phrase: "XL",
            filters: new FirmwareSearchFilters { GroupId = group.Id!.Value });

        var trace = db.LastSearchTrace!;
        Assert.Equal(1, trace.IndexRows);
        Assert.Equal(1, trace.DroppedByFilters);
        Assert.Equal(0, trace.HiddenByWordCount);
        Assert.Equal(0, trace.Shown);
        Assert.True(trace.Filtered);
    }

    // ── Слово-виновник ───────────────────────────────────────────────────────────────────────

    /// <summary>Главное, ради чего всё затевалось: видно И слово, И поле, в котором оно нашлось.
    /// «Спрятано» без «кем» не помогает никому.</summary>
    [Fact]
    public void Видно_какое_слово_и_в_каком_поле_спрятало_строку()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.AddOnDemandWord("Рх");
        db.AddOnDemandWord("Опытный");

        var hidden = AddVersion(db, 1, tags: "XL-Рх");

        var (found, trace) = Search(db, "XL");

        Assert.DoesNotContain(hidden, found);
        var row = Assert.Single(trace.HiddenByWord);
        Assert.Equal("Рх", row.Word);
        Assert.Equal("теги", row.Field);
        Assert.Equal(SearchDropReasons.OnDemandWord, row.Reason);
        Assert.Equal(new[] { "Рх" }, trace.HidingWords);
    }

    /// <summary>Виновник называется верно и когда слово досталось не тегу. Исполнение, номер заявки
    /// и серийник шкафа входят в ту же проверку — и именно по ним спрятанную прошивку глазами не
    /// опознать вовсе: в карточке это мелкий текст, а в выдаче её просто нет.</summary>
    [Theory]
    [InlineData("исполнение")]
    [InlineData("номер заявки")]
    [InlineData("серийный номер шкафа")]
    public void Поле_виновника_называется_верно(string field)
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.AddOnDemandWord("Рх");

        AddVersion(db, 1,
            execution: field == "исполнение" ? "Рх" : "",
            requestNum: field == "номер заявки" ? "Рх" : "",
            cabinetSn: field == "серийный номер шкафа" ? "Рх" : "");

        var (_, trace) = Search(db, "XL");

        var row = Assert.Single(trace.HiddenByWord);
        Assert.Equal(field, row.Field);
    }

    /// <summary>В записи — ИМЯ поля, а не его содержимое. Номер заявки и серийник шкафа уезжают
    /// наружу только через труп этой проверки.</summary>
    [Fact]
    public void Значение_поля_в_запись_не_попадает()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.AddOnDemandWord("Рх");
        AddVersion(db, 1, requestNum: "Заявка-4417-Рх", cabinetSn: "SN-900123");

        var (_, trace) = Search(db, "XL");
        var json = trace.ToJson("ILIA-PC", "2.3.8.0", System.DateTime.UtcNow);

        Assert.DoesNotContain("4417", json);
        Assert.DoesNotContain("SN-900123", json);
        Assert.DoesNotContain(@"X:\ПО", json);
        Assert.DoesNotContain("fw.psl", json);
        // А опознавательное — на месте.
        Assert.Contains("Рх", json);
        Assert.Contains("XL", json);
        Assert.Contains("SMH4", json);
    }

    /// <summary>Самого ЗАПРОСА в записи тоже нет: по номеру заявки и по серийнику ищут постоянно,
    /// то есть строка запроса ровно та же тайна, что и поля, которые мы отсюда выбрасываем.</summary>
    [Fact]
    public void Текст_запроса_в_запись_не_попадает()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.AddOnDemandWord("Рх");
        AddVersion(db, 1, tags: "XL-Рх");

        var (_, trace) = Search(db, "XL SN900123");
        var json = trace.ToJson("ILIA-PC", "2.3.8.0", System.DateTime.UtcNow);

        Assert.DoesNotContain("SN900123", json);
        Assert.Equal(2, trace.QueryWords);
    }

    // ── Схлопывание ──────────────────────────────────────────────────────────────────────────

    /// <summary>Причина выбытия при схлопывании называется своим именем: «заменена более свежей» —
    /// это нормальная работа, а «проиграла по рангу» — повод разбираться.</summary>
    [Fact]
    public void Выбывшая_при_схлопывании_получает_причину()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        AddVersion(db, 1);
        AddVersion(db, 2);

        var (_, trace) = Search(db, "XL");

        var row = Assert.Single(trace.Collapsed);
        Assert.Equal(SearchDropReasons.Superseded, row.Reason);
        // Показана свежая — жаловаться не на что, записи такой случай не заслуживает.
        Assert.False(trace.FresherLost);
        Assert.False(trace.AnythingHidden);
    }

    /// <summary>Несколько конфигураций ОДНОЙ версии (разные наборы тегов) — не пропажа, и в разборе
    /// это так и называется. Иначе каждая такая строка выглядела бы бедой.</summary>
    [Fact]
    public void Дубль_конфигурации_называется_дублем_а_не_пропажей()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        var first = AddVersion(db, 1, tags: "XL шкаф A");
        var second = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = db.GetFwVersionById(first)!.SubtypeId,
            ControllerId = db.GetFwVersionById(first)!.ControllerId,
            HwVersion = db.GetFwVersionById(first)!.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "2.1.001.0001.20260101_0000",
            Filename = "fw.psl",
            ConfigName = "шкаф Б",
            Tags = "XL шкаф Б",
            Status = FwStatus.Active,
        });

        var (found, trace) = Search(db, "XL");

        Assert.Single(found);
        Assert.Contains(second, new[] { first, second });
        var row = Assert.Single(trace.Collapsed);
        Assert.Equal(SearchDropReasons.DuplicateConfig, row.Reason);
        Assert.False(trace.FresherLost);
    }

    /// <summary>ГЛАВНЫЙ признак разбираемого случая: показана версия, а выбыла та, что собрана
    /// ПОЗЖЕ. Здесь это сделано разным железом (hw) — линейки друг друга не заменяют, и победителя
    /// выбирает ранг, который счётчик открытий набрал старой версии.</summary>
    [Fact]
    public void Свежая_проиграла_показанной_это_видно_отдельным_признаком()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "XL");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        int Add(int hw, int sw, string dt) => db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            HwVersion = hw,
            SwVersion = sw,
            DtStr = dt,
            VersionRaw = $"2.1.00{hw}.000{sw}.{dt}",
            Filename = "fw.psl",
            Tags = "XL",
            Status = FwStatus.Active,
        });

        var old = Add(mod.HwVersion, 1, "20260101_0000");
        var fresh = Add(mod.HwVersion + 1, 1, "20260505_0000");

        // Старую по этому запросу выбирали десяток раз — ровно так ранг у неё и набирается.
        for (var i = 0; i < 10; i++) db.RecordFwUsage(SearchService.UsageKey("XL"), old);

        var rows = db.SearchFwVersions(new[] { "XL" }, usageQueryKey: SearchService.UsageKey("XL"), phrase: "XL");
        var trace = db.LastSearchTrace!;

        Assert.Equal(old, rows.Single().Row.Id);
        Assert.DoesNotContain(fresh, rows.Select(r => r.Row.Id));
        Assert.True(trace.FresherLost);
        Assert.True(trace.AnythingHidden);
        Assert.Equal(SearchDropReasons.LostByRank, Assert.Single(trace.Collapsed).Reason);
    }

    // ── Показать спрятанное ──────────────────────────────────────────────────────────────────

    /// <summary>Подсказка «скрыто N версий по слову „Рх“ — показать» повторяет тот же поиск с
    /// выключенным правилом. Спрятанное при этом показывается, а схлопывание НЕ отменяется: человек
    /// просил показать спрятанное словом, а не всю историю версий заодно.</summary>
    [Fact]
    public void Показать_спрятанное_словом_не_отменяет_схлопывание()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.AddOnDemandWord("Рх");

        AddVersion(db, 1);
        AddVersion(db, 2);
        var hidden = AddVersion(db, 3, execution: "Рх");

        var (without, _) = Search(db, "XL");
        Assert.DoesNotContain(hidden, without);

        var (with, trace) = Search(db, "XL", ignoreOnDemandWords: true);
        Assert.Contains(hidden, with);
        // Две версии одного исполнения всё так же схлопнулись в одну строку.
        Assert.Equal(2, with.Count);
        // И подсказка по-прежнему знает, что именно было бы спрятано, — иначе, однажды нажатая,
        // она исчезала бы вместе с объяснением, почему была нужна.
        Assert.Equal(1, trace.HiddenByWordCount);
    }

    // ── Прямое доказательство гипотезы ───────────────────────────────────────────────────────

    /// <summary>ТОТ САМЫЙ СЛУЧАЙ, целиком, по рассказу Ильи.
    ///
    /// Новой версии при загрузке достался тег со словом-исключением («Рх»), прежней — нет. Дальше
    /// всё сходится слово в слово:
    ///   • обычным запросом находится СТАРАЯ версия, новой в выдаче нет вовсе;
    ///   • ВЫКЛЮЧЕНИЕ старой не помогает — выдача становится пустой, а новая так и не появляется
    ///     («я пытался отключать только, не помогало»);
    ///   • та же прошивка, залитая ЕЩЁ РАЗ без этого слова, находится сразу («только дубль
    ///     помогал»).
    ///
    /// Это ответ на вопрос, почему починка схлопывания (Database.StillCurrent) жалобу не сняла:
    /// здесь строка до схлопывания просто не доезжает.</summary>
    [Fact]
    public void Доказательство_новая_версия_невидима_из_за_слова_и_выключение_старой_не_помогает()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.AddOnDemandWord("Рх");

        var old = AddVersion(db, 1, tags: "XL");
        var fresh = AddVersion(db, 2, tags: "XL-Рх");

        // 1. Ищем как обычно — находится старая, новой нет.
        var (found, trace) = Search(db, "XL");
        Assert.Equal(new[] { old }, found);
        Assert.Equal(fresh, db.GetFwVersionById(fresh)!.Id);
        Assert.Equal("Рх", Assert.Single(trace.HiddenByWord).Word);

        // 2. Выключаем старую — и выдача становится ПУСТОЙ. Новая не появляется.
        Assert.True(db.DisableFwVersion(old));
        var (afterDisable, traceAfter) = Search(db, "XL");
        Assert.Empty(afterDisable);
        Assert.Equal(1, traceAfter.HiddenByWordCount);
        Assert.True(traceAfter.AnythingHidden);

        // 3. Заливаем ту же прошивку дубликатом, без слова, — находится сразу.
        var duplicate = AddVersion(db, 3, tags: "XL");
        var (afterDuplicate, _) = Search(db, "XL");
        Assert.Equal(new[] { duplicate }, afterDuplicate);
    }
}
