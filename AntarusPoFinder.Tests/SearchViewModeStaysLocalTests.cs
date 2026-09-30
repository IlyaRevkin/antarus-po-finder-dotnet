using AntarusPoFinder.App.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Галочка «Показывать все версии» — режим просмотра, и за пределы машины он не выходит.
///
/// Жалоба Ильи 30.09.2026: «после загрузки ОПЦ в поисковой выдаче вываливаются все прошивки, точнее
/// все версии что были, даже заменённые и откатанные». И следом, на вопрос про галочку: «я никогда
/// эту галочку не ставил».
///
/// И это чистая правда — её поставил кто-то другой. Ключ не был в списке личных настроек, поэтому
/// включённая у одного человека галочка уезжала в общий конфиг и приезжала ко всем: у остальных
/// поиск начинал показывать каждую версию отдельной строкой, включая заменённые более свежими.
/// Со стороны — необъяснимо сломавшаяся выдача, причём «сама», без единого действия.
///
/// ОПЦ к этому отношения не имела вовсе: загрузка просто была поводом посмотреть в выдачу
/// внимательно. Ровно тот же случай, что был с акцентным цветом (см. PersonalAppearanceSurvivesSyncTests).
///
/// Проверяются оба направления — и что своё не утекает, и что чужое не прилетает.</summary>
public class SearchViewModeStaysLocalTests
{
    [Fact]
    public void Чужая_галочка_не_меняет_мою_выдачу()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        // Коллега включил себе «показывать все версии» и отправил изменения.
        m.CfgA.SetSearchShowAllVersions(true);
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");

        // У меня она никогда не стояла.
        Assert.False(m.CfgB.SearchShowAllVersions());

        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        if (update is not null) ConfigSyncService.Apply(m.SvcB, update.ConfigPath, m.Root.Path);

        Assert.False(m.CfgB.SearchShowAllVersions(), "чужой режим просмотра не должен приезжать");
    }

    /// <summary>И обратная сторона: мой режим не должен даже попадать в общий файл. Иначе он приедет
    /// к тому, кто про эту галочку и не слышал, — а разбираться тот пойдёт с поломанной выдачей.</summary>
    [Fact]
    public void Мой_режим_не_попадает_в_общий_конфиг()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.CfgA.SetSearchShowAllVersions(true);
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");

        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        if (update is not null) ConfigSyncService.Apply(m.SvcB, update.ConfigPath, m.Root.Path);

        // У второй машины режим остаётся выключенным даже после приёма — значит в файл он не попал.
        Assert.False(m.CfgB.SearchShowAllVersions());
    }
}
