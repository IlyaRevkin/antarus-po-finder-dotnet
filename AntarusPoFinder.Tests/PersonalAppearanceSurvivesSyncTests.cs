using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Оформление — дело личное и за пределы машины не выходит.
///
/// Жалоба Ильи 16.09.2026: «настройка цвета же у каждого своя, а у коллег иногда или не
/// сохраняется, или переносится с другого компа».
///
/// Тема («светлая/тёмная») в списке личных настроек была с самого начала, а вот акцентный цвет —
/// нет. Получалось несогласованно и ровно так, как описано: свой цвет уезжал ко всем, а чужой
/// приезжал и затирал подобранный. Со стороны это выглядит как «цвет не сохраняется»: он
/// сохранялся, просто следующая же синхронизация возвращала чужой.
///
/// Проверяем оба направления — и что своё не утекает, и что чужое не прилетает.</summary>
public class PersonalAppearanceSurvivesSyncTests
{
    [Fact]
    public void IncomingConfig_DoesNotOverwriteLocallyChosenAccent()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.CfgA.SetAccent("#ac0c6f");
        m.CfgA.SetTheme("dark");
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");

        m.CfgB.SetAccent("#1f6feb");
        m.CfgB.SetTheme("light");

        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        if (update is not null) ConfigSyncService.Apply(m.SvcB, update.ConfigPath, m.Root.Path);

        Assert.Equal("#1f6feb", m.CfgB.Accent());
        Assert.Equal("light", m.CfgB.Theme());
    }

    /// <summary>И обратная сторона: свой цвет не должен даже попадать в общий файл. Иначе он
    /// приедет к тому, кто ещё ни разу цвет не трогал, и у человека ни с того ни с сего поменяется
    /// оформление — а он и не поймёт, откуда.</summary>
    [Fact]
    public void ChosenAccent_DoesNotTravelIntoTheSharedConfig()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.CfgA.SetAccent("#ac0c6f");
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");

        var plain = AntarusPoFinder.Core.Infrastructure.ConfigFileCrypto.TryDecrypt(
            System.IO.File.ReadAllBytes(ConfigSyncService.ConfigPathFor(m.Root.Path)))!;
        Assert.DoesNotContain("#ac0c6f", plain);
    }
}
