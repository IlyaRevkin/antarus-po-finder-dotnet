using AntarusPoFinder.App.Views;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Доп. материалы: какое действие у файла главное — «открыть» или «показать в папке».
///
/// Просьба Ильи: «доп. материалы нужно чтобы можно было открыть как файл в папке, потому что прошивку
/// нужно выбрать как файл в основной программе контроллера перетаскиванием, а не открыть как файл».
/// Прошивку ПЛК поставщика открывать нечем и незачем — её кладут в среду разработки контроллера,
/// перетащив файл мышью, поэтому у неё главное действие «Показать в папке» (а руководство наладчика
/// по-прежнему открывается).
///
/// Вид определяется по ВХОЖДЕНИЮ слова, а не точным равенством: справочник видов живёт в БД и люди
/// его правят («Прошивка ПЛК поставщика» легко станет «Прошивка ПЛК (Kinco)»), а расширение — второй
/// признак для случая, когда вид указали как «Прочее».</summary>
public class ExtraFilesRevealTests
{
    private static FwAttachment Attachment(string kind, string filename) =>
        new() { Kind = kind, Filename = filename };

    [Fact]
    public void VendorPlcFirmware_PrefersRevealInFolder()
    {
        Assert.True(ExtraFilesDialog.PrefersReveal(
            Attachment(FwAttachmentKinds.VendorPlcFirmware, "kinco_algo.pkg")));
    }

    [Fact]
    public void RenamedFirmwareKind_StillPrefersReveal()
    {
        Assert.True(ExtraFilesDialog.PrefersReveal(Attachment("Прошивка ПЛК (Kinco)", "algo.pkg")));
        Assert.True(ExtraFilesDialog.PrefersReveal(Attachment("прошивка от поставщика", "algo.pkg")));
    }

    [Fact]
    public void FirmwareLikeExtension_PrefersReveal_EvenWhenTheKindSaysNothing()
    {
        Assert.True(ExtraFilesDialog.PrefersReveal(Attachment(FwAttachmentKinds.Other, "project.lfs")));
        Assert.True(ExtraFilesDialog.PrefersReveal(Attachment("", "dump.bin")));
    }

    [Fact]
    public void Documents_StillOpenAsBefore()
    {
        Assert.False(ExtraFilesDialog.PrefersReveal(
            Attachment(FwAttachmentKinds.SetupGuide, "руководство.docx")));
        Assert.False(ExtraFilesDialog.PrefersReveal(
            Attachment(FwAttachmentKinds.WorkSpecifics, "специфика.pdf")));
        Assert.False(ExtraFilesDialog.PrefersReveal(Attachment(FwAttachmentKinds.Other, "схема.png")));
    }

    /// <summary>Подпись строки — вид, имя файла и комментарий: именно они объясняют, какой из
    /// приложенных файлов сейчас нужен.</summary>
    [Fact]
    public void Label_ShowsKindFilenameAndComment_SkippingEmptyParts()
    {
        Assert.Equal("Руководство наладчика — руководство.docx — по этому шкафу",
            ExtraFilesDialog.Label(new FwAttachment
            {
                Kind = FwAttachmentKinds.SetupGuide,
                Filename = "руководство.docx",
                Comment = "по этому шкафу",
            }));

        Assert.Equal("algo.pkg", ExtraFilesDialog.Label(new FwAttachment { Filename = "algo.pkg" }));
    }
}
