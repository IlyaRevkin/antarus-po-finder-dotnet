using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>КАРТА ВХОДОВ-ВЫХОДОВ НА ТЕЛЕФОНЕ («посмотереть входа выхода» из тикета Ильи).
///
/// Проверяется то, на чём эта половина ломается молча и чего без телефона иначе не увидеть:
/// <list type="number">
/// <item>книга Excel действительно читается в сетку, а не превращается в ссылку на файл — ради
///       этого разбор и заведён (см. IoMapDocs о выборе между страницей и файлом);</item>
/// <item>содержимое ячеек экранировано: карту рисует человек, и угловая скобка в описании сигнала
///       не должна уезжать в браузер разметкой;</item>
/// <item>ссылки на карту нет, когда карты нет, — пункт, ведущий в «ничего не найдено», у шкафа
///       читается как «программа потеряла карту»;</item>
/// <item>адрес файла (<see cref="MobileSite.FileRoute"/>) разбирается ровно обратно тому, как
///       печатается: разойдись эти двое, ссылка на исходный файл вела бы в 404.</item>
/// </list></summary>
public class MobileIoMapTests
{
    private const string Base = "/tok";

    private static int SeedVersion(Database db, string ioMapPath)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "2.1.0001.0001.20260101_0000",
            Filename = "fw.psl",
            DiskPath = "",
            IoMapPath = ioMapPath,
            Status = FwStatus.Active,
        });
    }

    /// <summary>Книга из двух строк: шапка и строка данных. Возвращает путь файла.</summary>
    private static string WriteWorkbook(string folder, string name = "Карта ВВ.xlsx",
        string signal = "Авария насоса", int extraRows = 0)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Входы");
        ws.Cell(1, 1).Value = "Клемма";
        ws.Cell(1, 2).Value = "Сигнал";
        ws.Cell(2, 1).Value = "X1.1";
        ws.Cell(2, 2).Value = signal;
        // Пустая строка-разделитель — в настоящих картах их полно, и на телефоне они съедали бы
        // экран; разбор обязан их выбросить.
        ws.Cell(4, 1).Value = "X1.2";
        ws.Cell(4, 2).Value = "Сухой ход";
        for (var i = 0; i < extraRows; i++) ws.Cell(10 + i, 1).Value = "X9." + i;
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void Workbook_IsReadIntoAGrid_WithoutEmptyRows()
    {
        using var root = new TempRoot();
        var doc = IoMapDocs.Read(WriteWorkbook(root.Path));

        Assert.NotNull(doc);
        Assert.True(doc!.HasGrid);
        var sheet = Assert.Single(doc.Sheets);
        Assert.Equal("Входы", sheet.Name);
        Assert.Equal(3, sheet.Rows.Count);
        Assert.Equal("X1.1", sheet.Rows[1][0]);
        Assert.False(sheet.Truncated);
    }

    /// <summary>Не книга — не беда: карточка файла и ссылка на него, а не пустая страница.</summary>
    [Fact]
    public void NonWorkbook_IsDescribedButNotParsed()
    {
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, "Карта ВВ.pdf");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var doc = IoMapDocs.Read(path);

        Assert.NotNull(doc);
        Assert.False(doc!.HasGrid);
        Assert.Equal("Карта ВВ.pdf", doc.FileName);
    }

    /// <summary>Книга длиннее предела показывается обрезанной, но ОБЯЗАНА сказать об этом: человек,
    /// решивший, что входов больше нет, пойдёт искать несуществующую ошибку в шкафу.</summary>
    [Fact]
    public void LongSheet_IsTruncated_AndSaysSo()
    {
        using var root = new TempRoot();
        var doc = IoMapDocs.Read(WriteWorkbook(root.Path, extraRows: IoMapDocs.MaxRows + 20));

        var sheet = Assert.Single(doc!.Sheets);
        Assert.Equal(IoMapDocs.MaxRows, sheet.Rows.Count);
        Assert.True(sheet.Truncated);

        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db, "");
        var page = MobileSite.Render(db, $"io/{id}", "", Base, _ => doc);

        Assert.Contains("не целиком", page.Html);
    }

    [Fact]
    public void IoMapPage_ShowsCells_AndLinksToTheOriginalFile()
    {
        using var root = new TempRoot();
        var doc = IoMapDocs.Read(WriteWorkbook(root.Path));
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db, "");

        var page = MobileSite.Render(db, $"io/{id}", "", Base, _ => doc);

        Assert.Equal(200, page.Status);
        Assert.Contains("X1.1", page.Html);
        Assert.Contains("Авария насоса", page.Html);
        Assert.Contains($"{Base}/{MobileSite.FileRoute(id)}", page.Html);
        // Путь к конторской шаре телефону знать незачем — в разметку он не попадает.
        Assert.DoesNotContain(root.Path, page.Html);
    }

    /// <summary>Карту рисует человек, и текст из ячейки идёт в разметку напрямую.</summary>
    [Fact]
    public void IoMapPage_EscapesCellText()
    {
        using var root = new TempRoot();
        var doc = IoMapDocs.Read(WriteWorkbook(root.Path, signal: "<script>alert(1)</script>"));
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db, "");

        var page = MobileSite.Render(db, $"io/{id}", "", Base, _ => doc);

        Assert.DoesNotContain("<script>", page.Html);
        Assert.Contains("&lt;script&gt;", page.Html);
    }

    [Fact]
    public void IoMapPage_WithoutMap_Is404()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db, "");

        Assert.Equal(404, MobileSite.Render(db, $"io/{id}", "", Base, _ => null).Status);
        // И без самого поиска карты (делегат не передан) страница обязана отвечать, а не падать.
        Assert.Equal(404, MobileSite.Render(db, $"io/{id}", "", Base).Status);
    }

    [Fact]
    public void Card_LinksToTheMap_OnlyWhenThereIsOne()
    {
        using var root = new TempRoot();
        var doc = IoMapDocs.Read(WriteWorkbook(root.Path));
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db, "");

        Assert.Contains($"{Base}/io/{id}", MobileSite.Render(db, $"v/{id}", "", Base, _ => doc).Html);
        Assert.DoesNotContain($"{Base}/io/{id}", MobileSite.Render(db, $"v/{id}", "", Base, _ => null).Html);
    }

    [Fact]
    public void FileRoute_IsParsedBackExactly()
    {
        Assert.True(MobileSite.TryParseFileRoute(MobileSite.FileRoute(42), out var id));
        Assert.Equal(42, id);
        Assert.True(MobileSite.TryParseFileRoute("/" + MobileSite.FileRoute(7) + "/", out _));

        // Страница карты — не файл карты, и спутать их нельзя: по одному адресу HTML, по другому байты.
        Assert.False(MobileSite.TryParseFileRoute("io/42", out _));
        Assert.False(MobileSite.TryParseFileRoute("io/нет/f", out _));
        Assert.False(MobileSite.TryParseFileRoute("v/42", out _));
    }

    /// <summary>Поиск файла карты берёт ТОТ ЖЕ путь, что и карточка на компьютере: путь, записанный
    /// у версии. Разойдись они — телефон и компьютер показывали бы разные карты одной версии.</summary>
    [Fact]
    public void FileFor_UsesTheSamePathAsTheDesktopCard()
    {
        using var root = new TempRoot();
        var path = WriteWorkbook(root.Path);
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db, path);

        Assert.Equal(path, IoMapDocs.FileFor(db, id));
        Assert.Null(IoMapDocs.FileFor(db, 999999));
    }
}
