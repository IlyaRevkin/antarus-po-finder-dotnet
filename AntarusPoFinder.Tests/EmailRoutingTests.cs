using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Кому уходит письмо. Просьба владельца: дублировать уведомления на почту, «какие действия
/// дублируются для каких групп ad или конкретных почтовых ящиков».
///
/// Проверяется здесь, а не на живой почте, по простой причине: на живой почте видно только «пришло»
/// и «не пришло». Что письмо ушло дважды или что выключенное правило продолжает слать, замечают
/// через неделю — и то по жалобе.</summary>
public class EmailRoutingTests
{
    private static EmailRule Rule(string cat, string to, bool on = true) =>
        new() { Id = to + cat, Category = cat, Recipient = to, Enabled = on };

    [Fact]
    public void Письмо_уходит_только_по_своей_категории()
    {
        var rules = new List<EmailRule> { Rule("FirmwareAndParams", "prog@company.ru"), Rule("Sync", "admin@company.ru") };

        Assert.Equal(new[] { "prog@company.ru" }, EmailRouting.RecipientsFor("FirmwareAndParams", rules));
    }

    /// <summary>Пустая категория — «дублировать всё». Самый частый случай при первой настройке, и
    /// если бы он означал «ничего», человек решил бы, что почта просто не работает.</summary>
    [Fact]
    public void Правило_без_категории_ловит_всё()
    {
        var rules = new List<EmailRule> { Rule("", "boss@company.ru") };

        Assert.Single(EmailRouting.RecipientsFor("Sync", rules));
        Assert.Single(EmailRouting.RecipientsFor("FirmwareAndParams", rules));
    }

    [Fact]
    public void Выключенное_правило_молчит()
    {
        var rules = new List<EmailRule> { Rule("Sync", "admin@company.ru", on: false) };

        Assert.Empty(EmailRouting.RecipientsFor("Sync", rules));
    }

    /// <summary>Адрес, попавший под два правила, получает одно письмо. Дубль не безобиден: тому,
    /// кому всё приходит по два раза, перестают быть нужны и первые экземпляры.</summary>
    [Fact]
    public void Два_правила_на_один_адрес_дают_одно_письмо()
    {
        var rules = new List<EmailRule> { Rule("", "boss@company.ru"), Rule("Sync", "BOSS@company.ru") };

        Assert.Single(EmailRouting.RecipientsFor("Sync", rules));
    }

    [Fact]
    public void Пустой_адрес_пропускается_а_не_шлётся_в_никуда()
    {
        var rules = new List<EmailRule> { Rule("Sync", "   ") };

        Assert.Empty(EmailRouting.RecipientsFor("Sync", rules));
    }

    /// <summary>Группа AD — не ошибка ввода. Просили про группы прямым текстом, и ругаться на
    /// строку без собачки значило бы запретить половину того, о чём просили.</summary>
    [Fact]
    public void Группа_ad_не_считается_ошибкой()
    {
        Assert.True(EmailRouting.LooksLikeMailbox("ivanov@company.ru"));
        Assert.False(EmailRouting.LooksLikeMailbox("Программисты"));
        Assert.False(EmailRouting.LooksLikeMailbox("ivanov@company"));
    }
}
