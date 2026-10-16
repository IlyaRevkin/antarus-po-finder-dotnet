using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ЛИЧНАЯ РАССЫЛКА: наладчик настраивает себе сам.
///
/// Жалоба Ильи 16.10.2026: «наладчик который заведен не может настроить себе рассылку и опять весь
/// спам получает».
///
/// Почему не просто «дать ему править общий список»: общий список — настройка конторы, он едет в
/// общем конфиге на все машины, и правя его, наладчик менял бы рассылку коллегам. Поэтому личная
/// подписка живёт локально и действует ТОЛЬКО на свой адрес, и только в сторону «меньше» —
/// расширить себе рассылку в обход администратора нельзя.</summary>
public class PersonalEmailTests
{
    private static ConfigService Cfg(Database db) => new(db);

    [Fact]
    public void Личная_настройка_пустая_значит_всё_как_было()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        var cfg = Cfg(db);

        Assert.Equal("", cfg.MyEmail());
        Assert.Empty(cfg.MyEmailCategories());
    }

    [Fact]
    public void Свой_адрес_и_категории_сохраняются()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        var cfg = Cfg(db);

        cfg.SetMyEmail(" naladchik@company.ru ");
        cfg.SetMyEmailCategories(new[] { "FirmwareAndParams", "Sync" });

        Assert.Equal("naladchik@company.ru", cfg.MyEmail());
        Assert.Equal(new[] { "FirmwareAndParams", "Sync" }, cfg.MyEmailCategories());
    }

    /// <summary>Личная настройка не должна уехать в общий конфиг: это настройка КОНКРЕТНОГО человека
    /// на КОНКРЕТНОЙ машине. Уехав, она применилась бы к коллегам — то есть ровно наоборот тому,
    /// ради чего заводилась.</summary>
    [Fact]
    public void Личная_настройка_не_уезжает_коллегам()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        m.SvcA.Cfg.SetMyEmail("ilia@company.ru");
        m.SvcA.Cfg.SetMyEmailCategories(new[] { "Sync" });

        AntarusPoFinder.App.Services.ConfigSyncService.Export(m.SvcA, root, "profileA");
        AntarusPoFinder.App.Services.ConfigSyncService.Apply(m.SvcB,
            AntarusPoFinder.App.Services.ConfigSyncService.ConfigPathFor(root), root);

        Assert.Equal("", m.SvcB.Cfg.MyEmail());
        Assert.Empty(m.SvcB.Cfg.MyEmailCategories());
    }

    /// <summary>Общий список при этом никуда не девается: что кому положено, по-прежнему решает он.
    /// Личная настройка умеет только убавить, и проверяется это там, где письмо собирается
    /// (NotificationCenter.PersonalFilterAllows) — здесь же закреплено само правило: адрес, которого
    /// нет в общем списке, писем не получает, сколько ни отмечай категорий себе.</summary>
    [Fact]
    public void Адрес_вне_общего_списка_писем_не_получает()
    {
        var rules = new[]
        {
            new EmailRule { Id = "r1", Category = "", Recipient = "ilia@company.ru" },
        };

        var recipients = EmailRouting.RecipientsFor("Sync", rules);

        Assert.Contains("ilia@company.ru", recipients);
        Assert.DoesNotContain("naladchik@company.ru", recipients);
    }
}
