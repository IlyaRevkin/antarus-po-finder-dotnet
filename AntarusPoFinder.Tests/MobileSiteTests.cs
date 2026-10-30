using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>СТРАНИЦА, КОТОРУЮ ВИДИТ ТЕЛЕФОН СЕРВИСНИКА.
///
/// Проверяется не вёрстка, а те три вещи, ради которых страница заведена и на которых её легче
/// всего незаметно сломать:
/// <list type="number">
/// <item>поиск находит прошивку тем же движком, что и программа на компьютере;</item>
/// <item>карточка называет АКТУАЛЬНУЮ версию, а не ту, которую открыли, — это главный вопрос, с
///       которым лезут в телефон, стоя у шкафа;</item>
/// <item>всё, что пришло из базы и из строки запроса, экранировано: описания прошивок пишут руками,
///       а строка запроса возвращается в поле ввода.</item>
/// </list>
/// Тесты работают с настоящей базой (TempDb), а не с заглушкой: половина ценности MobileSite именно
/// в том, что он ходит в те же выборки, что и остальная программа.</summary>
public class MobileSiteTests
{
    private const string Base = "/tok";

    /// <summary>Заводит в базе шкаф с двумя версиями одного железа: 0001 и более свежую 0002.
    /// Возвращает их id в порядке загрузки.</summary>
    private static (int Older, int Newer) SeedTwoVersions(Database db, string description = "")
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        int Add(int sw, string desc) => db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = sw,
            DtStr = $"2026010{sw}_0000",
            VersionRaw = $"2.1.0001.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl",
            DiskPath = "",
            Description = desc,
            Status = FwStatus.Active,
        });

        return (Add(1, description), Add(2, ""));
    }

    [Fact]
    public void SearchPage_FindsFirmware_AndLinksToItsCard()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var (_, newer) = SeedTwoVersions(db);

        var page = MobileSite.Render(db, "", "?q=" + System.Uri.EscapeDataString("КНС"), Base);

        Assert.Equal(200, page.Status);
        Assert.Contains($"{Base}/v/{newer}", page.Html);
    }

    /// <summary>Пустой запрос — не ошибка: это первый экран, который видит человек, открыв закладку.</summary>
    [Fact]
    public void SearchPage_WithoutQuery_IsStillAPage()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var page = MobileSite.Render(db, "", "", Base);

        Assert.Equal(200, page.Status);
        Assert.Contains("name=\"q\"", page.Html);
    }

    /// <summary>Главное на карточке. Открыли старую версию — страница обязана сказать, что текущая
    /// другая, и дать на неё ссылку. Без этого телефон отвечает на вопрос «актуальная ли эта
    /// прошивка» молчанием, то есть хуже, чем никак.</summary>
    [Fact]
    public void Card_OfSupersededVersion_NamesTheCurrentOne()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var (older, newer) = SeedTwoVersions(db);

        var page = MobileSite.Render(db, $"v/{older}", "", Base);

        Assert.Equal(200, page.Status);
        Assert.Contains(FwHistoryStatus.Superseded, page.Html);
        Assert.Contains($"{Base}/v/{newer}", page.Html);
    }

    /// <summary>А на самой текущей версии ссылки «актуальная — вон та» быть не должно: она и есть
    /// актуальная, и ссылка на саму себя сбивала бы с толку ровно в том месте, где должна успокаивать.</summary>
    [Fact]
    public void Card_OfCurrentVersion_SaysSo_AndOffersNoAlternative()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var (_, newer) = SeedTwoVersions(db);

        var page = MobileSite.Render(db, $"v/{newer}", "", Base);

        Assert.Contains(FwHistoryStatus.Current, page.Html);
        Assert.DoesNotContain("Актуальная сейчас", page.Html);
    }

    /// <summary>Описание прошивки пишет человек, и угловая скобка в нём не должна ни ломать
    /// страницу, ни уезжать в браузер разметкой.</summary>
    [Fact]
    public void Card_EscapesTextFromTheDatabase()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var (older, _) = SeedTwoVersions(db, "<script>alert(1)</script>");

        var page = MobileSite.Render(db, $"v/{older}", "", Base);

        Assert.DoesNotContain("<script>", page.Html);
        Assert.Contains("&lt;script&gt;", page.Html);
    }

    /// <summary>То же самое для строки запроса — она возвращается обратно в поле ввода, то есть
    /// попадает в разметку напрямую.</summary>
    [Fact]
    public void SearchPage_EscapesTheQuery()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var page = MobileSite.Render(db, "", "?q=" + System.Uri.EscapeDataString("\"><script>x</script>"), Base);

        Assert.DoesNotContain("<script>", page.Html);
    }

    [Fact]
    public void UnknownPath_And_UnknownVersion_Give404()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        Assert.Equal(404, MobileSite.Render(db, "нет-такого", "", Base).Status);
        Assert.Equal(404, MobileSite.Render(db, "v/999999", "", Base).Status);
    }

    /// <summary>Кириллица в запросе приходит процентами, а «+» обычная GET-форма шлёт вместо
    /// пробела — разбор обязан понимать оба.</summary>
    [Fact]
    public void ParseQuery_DecodesCyrillicAndPlusSigns()
    {
        var parsed = MobileSite.ParseQuery("?q=" + System.Uri.EscapeDataString("НГР КНС").Replace("%20", "+"));
        Assert.Equal("НГР КНС", parsed["q"]);
    }

    /// <summary>Постучаться может кто угодно, в том числе с битой процентной последовательностью, —
    /// это не повод ронять страницу.</summary>
    [Fact]
    public void ParseQuery_SurvivesBrokenEscaping()
    {
        var parsed = MobileSite.ParseQuery("?q=%&ok=1");
        Assert.Equal("1", parsed["ok"]);
    }
}
