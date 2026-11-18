using System;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>КОГДА РАЗБОР ПОИСКА УЕЗЖАЕТ, А КОГДА МОЛЧИТ.
///
/// Поиск делают сотни раз в день. Отправлять разбор каждого — значит за неделю завалить папку
/// <c>diag/</c> так, что читать перестанут всё, включая журналы заливок, ради которых она и
/// заводилась. Поэтому отбор тройной: случай интересен (что-то исчезло незаметно для человека),
/// такой случай ещё не присылали, и за сутки не набралось слишком много. Здесь проверяется каждая
/// ступень — и то, что отправка, как и у слепка, не выпустит наружу секрет.</summary>
public class SearchTraceDropTests
{
    private static SearchTrace Trace(bool hiddenByWord = false, bool fresherLost = false,
        string word = "Рх", string version = "2.1.001.0002.20260102_0000")
    {
        var trace = new SearchTrace { Mode = "обычный", QueryWords = 1, IndexRows = 5, PassedFilters = 5 };
        if (hiddenByWord)
            trace.NoteHiddenByWord(new SearchTraceRow(version, "XL", "SMH4",
                SearchDropReasons.OnDemandWord, word, "теги"));
        if (fresherLost)
        {
            trace.NoteCollapsed(new SearchTraceRow(version, "XL", "SMH4", SearchDropReasons.LostByRank));
            trace.FresherLost = true;
        }
        return trace;
    }

    // ── Ступень 1: интересен ли случай ───────────────────────────────────────────────────────

    /// <summary>Обычный поиск — ничего не спрятано, записи нет. Это главная из трёх ступеней:
    /// без неё канал получал бы сотни записей в день с каждой машины.</summary>
    [Fact]
    public void Когда_ничего_не_спрятано_запись_не_уходит()
    {
        var ordinary = Trace();
        // Схлопывание само по себе поводом не является: свежая версия вытесняет прежние на каждом
        // втором поиске, и объяснять в этом нечего.
        ordinary.NoteCollapsed(new SearchTraceRow("2.1.001.0001.20260101_0000", "XL", "SMH4",
            SearchDropReasons.Superseded));

        Assert.False(SearchTraceDrop.Interesting(ordinary));
        Assert.False(new SearchTraceLedger().Allow(ordinary, DateTime.UtcNow));
    }

    [Fact]
    public void Спрятанное_словом_и_проигравшая_свежая_запись_заслуживают()
    {
        Assert.True(SearchTraceDrop.Interesting(Trace(hiddenByWord: true)));
        Assert.True(SearchTraceDrop.Interesting(Trace(fresherLost: true)));
    }

    [Fact]
    public void Разбора_нет_вовсе_значит_и_отправлять_нечего()
    {
        Assert.False(SearchTraceDrop.Interesting(null));
        Assert.False(new SearchTraceLedger().Allow(null, DateTime.UtcNow));
    }

    // ── Ступень 2: такое уже присылали ───────────────────────────────────────────────────────

    /// <summary>Человек, у которого прошивка «пропала», повторит один и тот же запрос раз десять,
    /// прежде чем поверит. Все десять раз это одна и та же запись.</summary>
    [Fact]
    public void Один_и_тот_же_случай_уезжает_только_раз()
    {
        var ledger = new SearchTraceLedger();
        var now = DateTime.UtcNow;

        Assert.True(ledger.Allow(Trace(hiddenByWord: true), now));
        for (var i = 0; i < 9; i++)
            Assert.False(ledger.Allow(Trace(hiddenByWord: true), now.AddSeconds(i)));
    }

    /// <summary>А вот ДРУГАЯ прошивка или другое слово — другой случай, и его надо прислать.
    /// Иначе первая же запись затыкала бы все остальные до конца дня.</summary>
    [Fact]
    public void Другой_случай_уезжает_своим_чередом()
    {
        var ledger = new SearchTraceLedger();
        var now = DateTime.UtcNow;

        Assert.True(ledger.Allow(Trace(hiddenByWord: true), now));
        Assert.True(ledger.Allow(Trace(hiddenByWord: true, word: "Опытный"), now));
        Assert.True(ledger.Allow(Trace(hiddenByWord: true, version: "2.1.001.0009.20260909_0000"), now));
    }

    /// <summary>Случай, который длится второй день, стоит прислать ещё раз: это уже не «десятый
    /// повтор того же запроса», а беда, которая не прошла.</summary>
    [Fact]
    public void Через_сутки_случай_можно_прислать_снова()
    {
        var ledger = new SearchTraceLedger();
        var now = DateTime.UtcNow;

        Assert.True(ledger.Allow(Trace(hiddenByWord: true), now));
        Assert.False(ledger.Allow(Trace(hiddenByWord: true), now.AddHours(23)));
        Assert.True(ledger.Allow(Trace(hiddenByWord: true), now.AddHours(25)));
    }

    // ── Ступень 3: потолок в сутки ───────────────────────────────────────────────────────────

    /// <summary>Страховка от того, чего мы не предусмотрели: даже если завтра найдётся способ
    /// породить тысячу РАЗНЫХ случаев за день, канал не захлебнётся.</summary>
    [Fact]
    public void За_сутки_уезжает_не_больше_потолка()
    {
        var ledger = new SearchTraceLedger { MaxPerDay = 3 };
        var now = DateTime.UtcNow;

        var allowed = Enumerable.Range(0, 20)
            .Count(i => ledger.Allow(Trace(hiddenByWord: true, word: "слово" + i), now.AddMinutes(i)));

        Assert.Equal(3, allowed);
    }

    // ── Упаковка, имя, секреты ───────────────────────────────────────────────────────────────

    /// <summary>Архив, который не разбирается тем же кодом, бесполезен.</summary>
    [Fact]
    public void Запись_упаковывается_и_разбирается_обратно()
    {
        var json = Trace(hiddenByWord: true).ToJson("ILIA-PC", "2.3.8.0", DateTime.UtcNow);

        Assert.Equal(json, SearchTraceDrop.Unpack(SearchTraceDrop.Pack(json)));
    }

    /// <summary>Имя файла — тем же правилом, что у слепка и журнала: время первым, только
    /// латиница, своя приставка. В diag/ всё лежит вперемешку от всех машин.</summary>
    [Fact]
    public void Имя_архива_начинается_со_времени_и_не_содержит_кириллицы()
    {
        var name = SearchTraceDrop.FileName("Наладка-3", new DateTime(2026, 11, 6, 14, 20, 5));

        Assert.StartsWith("20261106_142005_search_", name);
        Assert.EndsWith(".zip", name);
        Assert.DoesNotContain(name, "абвгдеёжзийклмнопрстуфхцчшщъыьэюя".ToCharArray().Select(c => c.ToString()));
    }

    /// <summary>Проверка на секреты — ТА ЖЕ, что у слепка диска, и у самой отправки. Запись
    /// собирается из перечисленных полей СЕГОДНЯ; через год туда допишут «ещё одно полезное поле»,
    /// и единственное, что отделит разбор поиска от утечки, — эта проверка.</summary>
    [Fact]
    public async System.Threading.Tasks.Task Запись_с_секретом_наружу_не_уходит()
    {
        var json = """{"machine":"ILIA-PC","search":{"mode":"обычный"},"s3Secret":"enc:AAAA"}""";

        var result = await SearchTraceDrop.SendAsync(json, "ILIA-PC", DateTime.UtcNow,
            "https://files.rev-il.ru/drop/upload", "drop-код");

        Assert.False(result.Ok);
        Assert.Contains("не отправлен", result.Error!);
        Assert.Equal(0, result.Bytes);
    }

    /// <summary>Обратная половина: настоящая запись проверку проходит. Иначе это была бы не защита,
    /// а выключатель, который однажды выключат.</summary>
    [Fact]
    public void Настоящая_запись_проверку_на_секреты_проходит()
    {
        var json = Trace(hiddenByWord: true, fresherLost: true).ToJson("ILIA-PC", "2.3.8.0", DateTime.UtcNow);

        Assert.Null(DiskSnapshotSafety.Refusal(json));
    }

    /// <summary>Канал не настроен — это штатное состояние, а не ошибка: молчим без попытки.</summary>
    [Fact]
    public async System.Threading.Tasks.Task Без_настроенного_канала_отправка_просто_молчит()
    {
        var json = Trace(hiddenByWord: true).ToJson("ILIA-PC", "2.3.8.0", DateTime.UtcNow);

        var result = await SearchTraceDrop.SendAsync(json, "ILIA-PC", DateTime.UtcNow, "", "");

        Assert.True(result.Ok);
        Assert.Equal(0, result.Bytes);
    }
}
