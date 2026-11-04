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

    // ── Параметры ПЧ/УПП ─────────────────────────────────────────────────────
    //
    // Жалоба Ильи: «я моб версию посмотрел, там нет поиска параметров упп ПЧ, а это важно».
    // Проверяется то, на чём это ломается молча: поиск по коду настройки, состояния значения
    // («снимается по месту» вместо «onsite»), правило отбора «неразмеченная строка видна всегда»
    // и экранирование текста, который пишут руками.

    private static ParamTableRow Row(string code, string title, string value = "",
        string purpose = "", string description = "", string applicability = "") => new()
    {
        Kind = ParamRowKind.Param,
        Code = code,
        Title = title,
        Value = value,
        ValueState = value.Length > 0 ? ParamValueState.Set : ParamValueState.OnSite,
        Purpose = purpose,
        Description = description,
        Applicability = applicability,
        GroupName = ParamGroupCatalog.Main,
    };

    private static int SeedTable(Database db, string name, params ParamTableRow[] rows)
    {
        var id = db.AddParamTable(new ParamTable
        {
            DiskPath = @"Z:\ПО\Параметры\ESQ",
            Filename = "ESQ-230.par",
            Name = name,
            Manufacturer = "ESQ",
        });
        ParamTableEditing.SaveRevision(db, id, rows.ToList(), "первая", "Ilia");
        return id;
    }

    /// <summary>Главный случай: наладчик помнит код настройки, а не название документа. И ответ
    /// без строки, из-за которой документ нашёлся, заставляет открывать его и искать глазами.</summary>
    [Fact]
    public void ParamSearch_FindsTableByParameterCode_AndShowsTheMatchingRow()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedTable(db, "Задание Modbus",
            Row("P0-02", "Выбор канала команды запуска", "2"),
            Row("P0-10", "Максимальная частота", "55"));

        var page = MobileSite.Render(db, "p", "?q=" + System.Uri.EscapeDataString("P0-10"), Base);

        Assert.Equal(200, page.Status);
        Assert.Contains($"{Base}/t/{id}", page.Html);
        Assert.Contains("P0-10", page.Html);
        // Весь блок P0-xx в выдачу попадать не должен — иначе поиск по коду теряет смысл.
        Assert.DoesNotContain("P0-02", page.Html);
    }

    /// <summary>Ищут и по названию параметра — на объекте код помнят не всегда.</summary>
    [Fact]
    public void ParamSearch_FindsTableByParameterTitle()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedTable(db, "Задание Modbus", Row("P0-10", "Максимальная частота", "55"));

        var page = MobileSite.Render(db, "p", "?q=" + System.Uri.EscapeDataString("максимальная частота"), Base);

        Assert.Contains($"{Base}/t/{id}", page.Html);
    }

    /// <summary>Строка таблицы на телефоне обязана читаться целиком: код, название, значение.</summary>
    [Fact]
    public void TablePage_ShowsCodeTitleAndValue()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedTable(db, "Задание Modbus", Row("P0-10", "Максимальная частота", "55"));

        var page = MobileSite.Render(db, $"t/{id}", "", Base);

        Assert.Equal(200, page.Status);
        Assert.Contains("P0-10", page.Html);
        Assert.Contains("Максимальная частота", page.Html);
        Assert.Contains("55", page.Html);
    }

    /// <summary>«Снимается по месту» обязано читаться словами. Пустая ячейка здесь читалась бы как
    /// «ноль», а код состояния («onsite») — вообще никак.</summary>
    [Fact]
    public void TablePage_SpellsOutValueStates()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedTable(db, "Двигатель", Row("P1-02", "Номинальный ток двигателя"));

        var page = MobileSite.Render(db, $"t/{id}", "", Base);

        Assert.DoesNotContain(ParamValueState.OnSite, page.Html);
        Assert.Contains("по месту", page.Html);
    }

    /// <summary>Применимость — подзаголовок над строками, а не поле строки. Потерять её на телефоне
    /// значит выставить параметр не тому аппарату.</summary>
    [Fact]
    public void TablePage_KeepsApplicabilityAsAHeading()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedTable(db, "Задание Modbus",
            Row("P0-10", "Максимальная частота", "55", applicability: "Только для ПЧ №1"));

        var page = MobileSite.Render(db, $"t/{id}", "", Base);

        Assert.Contains("Только для ПЧ №1", page.Html);
    }

    /// <summary>Отбор «для наладки» показывает размеченное наладкой и прячет размеченное только
    /// сервисом.</summary>
    [Fact]
    public void TablePage_PurposeFilter_HidesTheOtherPurpose()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedTable(db, "Задание Modbus",
            Row("P0-02", "Выбор канала команды запуска", "2", ParamRowPurpose.Commissioning),
            Row("P1-02", "Номинальный ток двигателя", "12", ParamRowPurpose.Service));

        var page = MobileSite.Render(db, $"t/{id}", "?f=" + ParamRowPurpose.Commissioning, Base);

        Assert.Contains("P0-02", page.Html);
        Assert.DoesNotContain("P1-02", page.Html);
    }

    /// <summary>ГЛАВНОЕ ПРАВИЛО ОТБОРА: строка без разметки видна при любом наборе. Спрятать её
    /// значит оставить параметр невыставленным — а разметка у накопленных таблиц пустая.</summary>
    [Fact]
    public void TablePage_PurposeFilter_KeepsUnmarkedRowsVisible()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedTable(db, "Задание Modbus",
            Row("P0-02", "Выбор канала команды запуска", "2", ParamRowPurpose.Commissioning),
            Row("P0-07", "Источник задания частоты", "9"));

        var page = MobileSite.Render(db, $"t/{id}", "?f=" + ParamRowPurpose.Service, Base);

        Assert.Contains("P0-07", page.Html);
        Assert.DoesNotContain("P0-02", page.Html);
    }

    /// <summary>Опечатка в адресе отбора — это «все строки», а не пустая страница: ссылку правят
    /// руками, и терять из-за этого таблицу нельзя.</summary>
    [Fact]
    public void TablePage_UnknownPurposeShowsEverything()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedTable(db, "Задание Modbus",
            Row("P0-02", "Выбор канала команды запуска", "2", ParamRowPurpose.Commissioning),
            Row("P1-02", "Номинальный ток двигателя", "12", ParamRowPurpose.Service));

        var page = MobileSite.Render(db, $"t/{id}", "?f=чепуха", Base);

        Assert.Contains("P0-02", page.Html);
        Assert.Contains("P1-02", page.Html);
    }

    /// <summary>Название параметра и описание пишут руками, и угловая скобка в них не должна
    /// уезжать в браузер разметкой.</summary>
    [Fact]
    public void TablePage_EscapesRowText()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedTable(db, "Задание Modbus",
            Row("P0-10", "<script>alert(1)</script>", "55", description: "<b>x</b>"));

        var page = MobileSite.Render(db, $"t/{id}", "", Base);

        Assert.DoesNotContain("<script>", page.Html);
        Assert.Contains("&lt;script&gt;", page.Html);
    }

    [Fact]
    public void ParamSearch_EscapesTheQuery_AndUnknownTableGives404()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var page = MobileSite.Render(db, "p", "?q=" + System.Uri.EscapeDataString("\"><script>x</script>"), Base);
        Assert.DoesNotContain("<script>", page.Html);

        Assert.Equal(404, MobileSite.Render(db, "t/999999", "", Base).Status);
    }

    /// <summary>Набранный запрос переносится между разделами: заставлять человека набирать то же
    /// самое на экранной клавиатуре — худшее, что можно сделать с поиском на телефоне.</summary>
    [Fact]
    public void Nav_CarriesTheQueryBetweenSections()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var page = MobileSite.Render(db, "", "?q=" + System.Uri.EscapeDataString("P0-10"), Base);

        Assert.Contains($"{Base}/p?q=P0-10", page.Html);
    }
}
