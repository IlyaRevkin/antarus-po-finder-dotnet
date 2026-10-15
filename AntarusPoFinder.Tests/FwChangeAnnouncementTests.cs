using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ЧТО НАПИСАНО В УВЕДОМЛЕНИИ О ЗАГРУЖЕННОЙ ПРОШИВКЕ.
///
/// Жалоба Ильи 15.10.2026: «вот коллега выложил прошивку, в уведомлении написано только номер
/// прошивки, надо ещё отображать что изменилась какой тип прошивки и в целом больше инфы».
///
/// Текст рождается один раз, при загрузке, и дальше едет как есть — в накопитель правок, в журнал
/// ревизии общего конфига и на экран коллеги. Переспросить по нему нельзя: это всё, что коллега
/// увидит. Поэтому проверяется не «строка не пустая», а наличие каждого куска, из-за отсутствия
/// которого и жаловались.</summary>
public class FwChangeAnnouncementTests
{
    [Fact]
    public void В_уведомлении_есть_шкаф_контроллер_и_что_изменилось()
    {
        var text = FwChangeAnnouncement.Uploaded("НГР", "КНС", "SMH4", "2.1.0004.0002",
            description: "добавлен контроль сухого хода");

        Assert.Contains("НГР/КНС/SMH4", text);
        Assert.Contains("2.1.0004.0002", text);
        Assert.Contains("добавлен контроль сухого хода", text);
    }

    /// <summary>Исполнение — своя линейка прошивок (см. FwExecution): «3 насоса» и «ПЧ Danfoss»
    /// актуальны ОДНОВРЕМЕННО. Без него два уведомления об одном шкафе различаются только номером
    /// версии, и понять, какую из линеек тронули, нельзя.</summary>
    [Fact]
    public void Исполнение_названо()
    {
        var text = FwChangeAnnouncement.Uploaded("НГР", "КНС", "SMH4", "2.1.0004.0002", execution: "3 насоса");

        Assert.Contains("3 насоса", text);
    }

    /// <summary>ОПЦ — разовая сборка под конкретный шкаф. Коллеге важно не перепутать её с обычной
    /// версией линейки, поэтому в уведомлении она названа вместе с номером заявки и SN.</summary>
    [Fact]
    public void ОПЦ_видно_вместе_с_заявкой_и_серийником()
    {
        var text = FwChangeAnnouncement.Uploaded("НГР", "КНС", "SMH4", "2.1.0004.0002",
            isOpc: true, requestNum: "47137", cabinetSn: "778899");

        Assert.Contains(FwCardMeta.Marker, text);
        Assert.Contains("47137", text);
        Assert.Contains("778899", text);
    }

    [Fact]
    public void Обычная_прошивка_не_помечается_как_ОПЦ()
    {
        var text = FwChangeAnnouncement.Uploaded("НГР", "КНС", "SMH4", "2.1.0004.0002");

        Assert.DoesNotContain(FwCardMeta.Marker, text);
    }

    /// <summary>Описание бывает на абзац, а строка едет в плашку и в журнал ревизии, где длинный
    /// текст вытесняет собой всё остальное. Обрезаем — но по границе слова: обрыв посреди слова
    /// читается как сбой программы, а не как сокращение.</summary>
    [Fact]
    public void Длинное_описание_обрезается_по_границе_слова()
    {
        var longText = string.Join(" ", System.Linq.Enumerable.Repeat("исправление", 40));

        var text = FwChangeAnnouncement.Shorten(longText);

        Assert.True(text.Length <= FwChangeAnnouncement.MaxDescription + 1, $"длина {text.Length}");
        Assert.EndsWith("…", text);
        Assert.DoesNotContain("исправлен…", text);
    }

    /// <summary>Многострочное описание схлопывается в одну строку: плашка всё равно показывает одну,
    /// а в журнале ревизии перевод строки ломает формат записи.</summary>
    [Fact]
    public void Перевод_строки_в_описании_не_ломает_строку_уведомления()
    {
        var text = FwChangeAnnouncement.Uploaded("НГР", "КНС", "SMH4", "2.1.0004.0002",
            description: "первая строка\nвторая строка");

        Assert.DoesNotContain("\n", text);
        Assert.Contains("первая строка вторая строка", text);
    }

    /// <summary>Короткое описание не трогаем вовсе — многоточие на полной фразе выглядит как потеря
    /// текста.</summary>
    [Fact]
    public void Короткое_описание_остаётся_целым()
    {
        Assert.Equal("мелкая правка", FwChangeAnnouncement.Shorten("мелкая правка"));
    }

    /// <summary>Имён может не быть вовсе (старый вызов, пустая форма) — уведомление обязано остаться
    /// осмысленным, а не превратиться в «Загружена прошивка //  2.1».</summary>
    [Fact]
    public void Без_имён_уведомление_не_разваливается()
    {
        var text = FwChangeAnnouncement.Uploaded("", "", "", "2.1.0004.0002");

        Assert.Equal("Загружена прошивка 2.1.0004.0002", text);
    }
}
