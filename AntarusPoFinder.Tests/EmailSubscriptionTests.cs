using System;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ПОДПИСКИ: ОДИН АДРЕС — ОДНА СТРОКА, НЕСКОЛЬКО ГАЛОЧЕК.
///
/// Жалобы Ильи 15.10.2026: «надо сделать чтобы выбор что получать на почту не выпадающим списком
/// было где можно что-то одно выбрать, а что-то конкретное» и «нужно добавить для почт что заведены
/// редакт рассылки что получать и почты в целом».
///
/// В хранилище правило по-прежнему «одна категория → один адрес»: формат ездит в общем конфиге, и
/// менять его ради удобства экрана нельзя — старые машины читают его как раньше. Поэтому здесь
/// проверяется ровно стык: свернуть правила в подписки и развернуть обратно, ничего не потеряв и
/// ничего не выдумав.</summary>
public class EmailSubscriptionTests
{
    private static EmailRule Rule(string category, string to, bool enabled = true) =>
        new() { Id = Guid.NewGuid().ToString(), Category = category, Recipient = to, Enabled = enabled };

    private static string NewId() => Guid.NewGuid().ToString();

    [Fact]
    public void Несколько_правил_одного_адреса_сворачиваются_в_одну_строку()
    {
        var subs = EmailRouting.ToSubscriptions(new[]
        {
            Rule("Sync", "ivanov@company.ru"),
            Rule("FirmwareAndParams", "ivanov@company.ru"),
        });

        var one = Assert.Single(subs);
        Assert.Equal("ivanov@company.ru", one.Recipient);
        Assert.Equal(new[] { "Sync", "FirmwareAndParams" }, one.Categories);
        Assert.False(one.Everything);
    }

    /// <summary>«Всё подряд» перебивает перечисление: если человеку заведены и «всё», и отдельные
    /// категории, он всё равно получает всё. Показать это как «только прошивки» значило бы соврать
    /// про то, что реально уходит ему в ящик.</summary>
    [Fact]
    public void Подписка_на_всё_перебивает_перечисление()
    {
        var subs = EmailRouting.ToSubscriptions(new[]
        {
            Rule("FirmwareAndParams", "ivanov@company.ru"),
            Rule("", "ivanov@company.ru"),
        });

        Assert.True(subs.Single().Everything);
        Assert.Empty(subs.Single().Categories);
    }

    /// <summary>Круг «правила → подписки → правила» не должен терять адресатов и категории: иначе
    /// одно открытие окна настроек молча обрезало бы кому-то рассылку.</summary>
    [Fact]
    public void Круг_туда_и_обратно_ничего_не_теряет()
    {
        var original = new[]
        {
            Rule("Sync", "ivanov@company.ru"),
            Rule("FirmwareAndParams", "ivanov@company.ru"),
            Rule("", "Программисты"),
        };

        var back = EmailRouting.ToRules(EmailRouting.ToSubscriptions(original), NewId);

        Assert.Equal(3, back.Count);
        Assert.Equal(
            original.Select(r => (r.Category, r.Recipient)).OrderBy(x => x.Category).ThenBy(x => x.Recipient),
            back.Select(r => (r.Category, r.Recipient)).OrderBy(x => x.Category).ThenBy(x => x.Recipient));
    }

    /// <summary>Главная проверка этого стыка: после круга КОМУ ЧТО уходит — не меняется. Правила
    /// нужны ровно для этого, остальное в них служебное.</summary>
    [Fact]
    public void После_круга_адресаты_событий_те_же()
    {
        var original = new[]
        {
            Rule("Sync", "ivanov@company.ru"),
            Rule("", "Программисты"),
            Rule("FirmwareAndParams", "petrov@company.ru", enabled: false),
        };
        var back = EmailRouting.ToRules(EmailRouting.ToSubscriptions(original), NewId);

        foreach (var (category, _) in NotificationCategoryInfo.All)
        {
            Assert.Equal(
                EmailRouting.RecipientsFor(category.ToString(), original),
                EmailRouting.RecipientsFor(category.ToString(), back));
        }
    }

    /// <summary>Выключенный адрес остаётся в списке выключенным, а не исчезает: его завели осознанно,
    /// и «пропал» вместо «выключен» означает, что человек заведёт его заново и получит дубль.</summary>
    [Fact]
    public void Выключенная_подписка_не_теряется()
    {
        var subs = EmailRouting.ToSubscriptions(new[] { Rule("Sync", "ivanov@company.ru", enabled: false) });

        var one = Assert.Single(subs);
        Assert.False(one.Enabled);

        var back = EmailRouting.ToRules(subs, NewId);
        Assert.False(back.Single().Enabled);
    }

    /// <summary>Порядок строк сохраняется по первому появлению адреса: список правят глазами, и
    /// перетасовка при каждом сохранении читалась бы как «что-то поменялось само».</summary>
    [Fact]
    public void Порядок_адресов_сохраняется()
    {
        var subs = EmailRouting.ToSubscriptions(new[]
        {
            Rule("Sync", "b@company.ru"),
            Rule("Sync", "a@company.ru"),
            Rule("Hierarchy", "b@company.ru"),
        });

        Assert.Equal(new[] { "b@company.ru", "a@company.ru" }, subs.Select(s => s.Recipient));
    }

    /// <summary>Пустой адрес — это не подписка. Такие строки появляются от случайного Enter в поле и
    /// молча превращали бы список в набор пустышек.</summary>
    [Fact]
    public void Пустой_адрес_не_становится_подпиской()
    {
        Assert.Empty(EmailRouting.ToSubscriptions(new[] { Rule("Sync", "   ") }));
        Assert.Empty(EmailRouting.ToRules(
            new[] { new EmailRouting.EmailSubscription("  ", Array.Empty<string>(), true) }, NewId));
    }
}
