using AntarusPoFinder.App.Views;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Раздел «Вложения» в окне тикета.
///
/// Жалоба Ильи: «в тикетах зачем отображать „открыть папку вложений" или „открыть вложение", если
/// их нет». Незачем — кнопка, умеющая только сообщить, что нажимать было не на что, бесполезна.
///
/// Случаев, однако, три, и в этом вся соль: вложения лежат на сетевом диске, и когда он не
/// подключён, «вложений нет» — неправда. Промолчать в этом случае тоже нельзя: человек решит, что
/// файлы потерялись.</summary>
public class TicketAttachmentsPanelTests
{
    [Fact]
    public void Вложения_есть_показываем_всё()
    {
        var s = TicketAttachmentsPanelState.For(shareAvailable: true, fileCount: 2);

        Assert.True(s.ShowSection);
        Assert.True(s.ShowList);
        Assert.True(s.ShowButtons);
        Assert.Equal("", s.Note);
    }

    /// <summary>Главное. Вложений нет — нет и раздела: ни заголовка, ни надписи о пустоте, ни кнопок.</summary>
    [Fact]
    public void Вложений_нет_раздела_тоже_нет()
    {
        var s = TicketAttachmentsPanelState.For(shareAvailable: true, fileCount: 0);

        Assert.False(s.ShowSection);
        Assert.False(s.ShowButtons);
        Assert.False(s.ShowList);
        Assert.Equal("", s.Note);
    }

    /// <summary>Сетевого диска нет — об этом говорим вслух, но кнопки всё равно убираем: открывать
    /// нечего, папки на этой машине не существует.</summary>
    [Fact]
    public void Без_сетевого_диска_объясняем_но_кнопок_не_даём()
    {
        var s = TicketAttachmentsPanelState.For(shareAvailable: false, fileCount: 0);

        Assert.True(s.ShowSection);
        Assert.False(s.ShowButtons);
        Assert.False(s.ShowList);
        Assert.Contains("диск", s.Note);
    }

    /// <summary>«Нет вложений» и «не видно вложений» не должны выглядеть одинаково — иначе отключённый
    /// диск читался бы как потерянные файлы.</summary>
    [Fact]
    public void Нет_и_не_видно_это_разные_состояния()
    {
        var empty = TicketAttachmentsPanelState.For(shareAvailable: true, fileCount: 0);
        var unknown = TicketAttachmentsPanelState.For(shareAvailable: false, fileCount: 0);

        Assert.NotEqual(empty, unknown);
    }
}
