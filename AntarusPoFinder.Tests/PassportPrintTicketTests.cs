using System.Linq;
using System.Xml.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Настройки печати паспорта: с двух сторон, разворот относительно КОРОТКОГО края.
///
/// Просьба Ильи дословно: «важно, чтобы при печати настройки не сбивались: печатать с двух сторон,
/// разворачивать относительно короткого края». Отправка файла ассоциацией Windows никаких настроек
/// не несёт — печатается тем, что стоит у принтера сейчас; единственное место, где эти два параметра
/// живут, это PrintTicket очереди печати.
///
/// Сама печать здесь, разумеется, не запускается: проверяется правка тикета — то, что в задании
/// оказывается именно «двусторонняя с переворотом по короткому краю», что остальные настройки
/// принтера при этом не теряются и что «книжный» разворот по длинному краю заменяется, а не
/// остаётся рядом вторым вариантом.</summary>
public class PassportPrintTicketTests
{
    private static readonly XNamespace Psf = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";

    /// <summary>Тикет от принтера, у которого про двустороннюю печать не сказано ничего, зато задан
    /// размер бумаги — его мы обязаны сохранить.</summary>
    private const string TicketWithPaperSize = """
        <psf:PrintTicket xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                         xmlns:psk="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords" version="1">
          <psf:Feature name="psk:PageMediaSize"><psf:Option name="psk:ISOA4"/></psf:Feature>
        </psf:PrintTicket>
        """;

    [Fact]
    public void ApplyTwoSidedShortEdge_SetsExactlyThatOption()
    {
        var ticket = PrintTicketXml.ApplyTwoSidedShortEdge(TicketWithPaperSize);

        Assert.Equal(PrintTicketXml.TwoSidedShortEdge, PrintTicketXml.DuplexOption(ticket));
    }

    /// <summary>Всё остальное в тикете — бумага, лоток, качество — остаётся как было: мы правим один
    /// параметр, а не переписываем настройки принтера за человека.</summary>
    [Fact]
    public void ApplyTwoSidedShortEdge_KeepsTheRestOfTheTicket()
    {
        var ticket = PrintTicketXml.ApplyTwoSidedShortEdge(TicketWithPaperSize);

        var root = XDocument.Parse(ticket).Root!;
        var paper = root.Elements(Psf + "Feature")
            .Single(f => f.Attribute("name")!.Value.EndsWith("PageMediaSize"));
        Assert.Equal("psk:ISOA4", paper.Element(Psf + "Option")!.Attribute("name")!.Value);
    }

    /// <summary>«Книжный» разворот по ДЛИННОМУ краю — ровно то, чего просили избежать: он должен
    /// замениться, а не остаться рядом вторым вариантом (задание с двумя вариантами противоречиво).</summary>
    [Fact]
    public void ApplyTwoSidedShortEdge_ReplacesLongEdge_RatherThanAddingASecondOption()
    {
        var longEdge = PrintTicketXml.ApplyDuplex(null, PrintTicketXml.TwoSidedLongEdge);
        Assert.Equal(PrintTicketXml.TwoSidedLongEdge, PrintTicketXml.DuplexOption(longEdge));

        var shortEdge = PrintTicketXml.ApplyTwoSidedShortEdge(longEdge);

        Assert.Equal(PrintTicketXml.TwoSidedShortEdge, PrintTicketXml.DuplexOption(shortEdge));
        var feature = XDocument.Parse(shortEdge).Root!.Elements(Psf + "Feature")
            .Single(f => f.Attribute("name")!.Value.EndsWith(PrintTicketXml.DuplexFeature));
        Assert.Single(feature.Elements(Psf + "Option"));
    }

    /// <summary>Префикс, которым записаны имена из словаря схемы, у каждого драйвера свой — «psk»
    /// наугад искать нельзя. Тикет с чужим префиксом обязан читаться и правиться как свой.</summary>
    [Fact]
    public void AForeignNamespacePrefix_IsUnderstoodJustTheSame()
    {
        const string foreign = """
            <a:PrintTicket xmlns:a="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                           xmlns:b="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords" version="1">
              <a:Feature name="b:JobDuplexAllDocumentsContiguously"><a:Option name="b:OneSided"/></a:Feature>
            </a:PrintTicket>
            """;

        Assert.Equal(PrintTicketXml.OneSided, PrintTicketXml.DuplexOption(foreign));

        var applied = PrintTicketXml.ApplyTwoSidedShortEdge(foreign);

        Assert.Equal(PrintTicketXml.TwoSidedShortEdge, PrintTicketXml.DuplexOption(applied));
        // Признак остался ОДИН: не разобрав чужой префикс, мы завели бы второй такой же рядом.
        Assert.Single(XDocument.Parse(applied).Root!.Elements(Psf + "Feature"));
    }

    /// <summary>Тикета нет или он не разобрался (обрезанный, чужого формата) — собираем свой
    /// минимальный. Задание с одним нужным параметром лучше, чем отказ печатать.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не xml вовсе")]
    [InlineData("<Другое>совсем не тикет</Другое>")]
    public void AMissingOrBrokenTicket_IsRebuiltFromScratch(string? broken)
    {
        var ticket = PrintTicketXml.ApplyTwoSidedShortEdge(broken);

        Assert.Equal(PrintTicketXml.TwoSidedShortEdge, PrintTicketXml.DuplexOption(ticket));
        Assert.Equal(Psf + "PrintTicket", XDocument.Parse(ticket).Root!.Name);
    }

    /// <summary>Про двустороннюю печать в тикете не сказано ничего — это не «односторонняя», а
    /// «неизвестно»: различать их важно, иначе прежние настройки принтера вернулись бы не теми.</summary>
    [Fact]
    public void DuplexOption_IsNull_WhenTheTicketSaysNothingAboutIt()
    {
        Assert.Null(PrintTicketXml.DuplexOption(TicketWithPaperSize));
    }

    /// <summary>Двусторонняя печать паспорта включена по умолчанию — так просил Илья — и едет ко
    /// всем машинам: как оформляется паспорт, идущий заказчику, это политика предприятия, а не
    /// привычка отдельного компьютера.</summary>
    [Fact]
    public void DuplexIsOnByDefault_AndIsASharedPolicy()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.True(cfg.PassportDuplexShortEdge());

        cfg.SetPassportDuplexShortEdge(false);
        Assert.False(cfg.PassportDuplexShortEdge());

        var skipped = ConfigSyncSkipKeys.Read();
        Assert.DoesNotContain("passport_duplex_short_edge", skipped);
    }
}
