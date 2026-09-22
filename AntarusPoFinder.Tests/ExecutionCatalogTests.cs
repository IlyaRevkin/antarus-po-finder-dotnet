using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Справочник исполнений заводится заранее, а не выводится из уже загруженного.
///
/// Жалоба Ильи 23.09.2026: «про исполнение отдельный пункт сделай в настройках для заполнения,
/// потому что я сейчас не могу туда добавлять пункты». Получался замкнутый круг: список исполнений
/// собирался из самих прошивок, а чтобы исполнение там появилось, его надо было кому-то вписать при
/// загрузке — и вписать было негде.</summary>
public class ExecutionCatalogTests
{
    [Fact]
    public void Execution_CanBeAddedBeforeAnyFirmwareUsesIt()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        Assert.Empty(db.GetExecutionCatalog());

        db.AddExecutionToCatalog("3 насоса");

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        // Ни одной прошивки с таким исполнением нет, а предлагаться оно обязано.
        Assert.Contains("3 насоса", db.GetExecutionChoices(subtype.Id!.Value, mod.ControllerId));
    }

    /// <summary>Одно и то же название в другом регистре не заводится вторым: исполнения
    /// сравниваются ТОЧНО, и «3 Насоса» рядом с «3 насоса» означало бы две линейки вместо одной.</summary>
    [Fact]
    public void TheSameName_IsNotAddedTwiceInAnotherCase()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        db.AddExecutionToCatalog("ПЧ Danfoss");
        db.AddExecutionToCatalog("пч danfoss");

        Assert.Single(db.GetExecutionCatalog());
    }

    /// <summary>Убрали из справочника — у прошивок пометка остаётся. Она описывает линейку, и
    /// потерять её из-за уборки списка значило бы слить две линейки в одну.</summary>
    [Fact]
    public void RemovingFromCatalog_DoesNotTouchFirmware()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        db.AddExecutionToCatalog("3 насоса");
        db.DeleteExecutionFromCatalog("3 насоса");

        Assert.Empty(db.GetExecutionCatalog());
    }

    /// <summary>Справочник общий для всех машин: исполнение — граница линейки, и заведённое на одной
    /// машине обязано предлагаться на всех. Иначе вторая наберёт его руками в другом написании и
    /// заведёт третью линейку.</summary>
    [Fact]
    public void TheCatalog_TravelsToTheOtherMachine()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.DbA.AddExecutionToCatalog("3 насоса");
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);

        Assert.Contains("3 насоса", m.DbB.GetExecutionCatalog());
    }

    [Fact]
    public void RemovalTravelsToo()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.DbA.AddExecutionToCatalog("3 насоса");
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);
        Assert.Contains("3 насоса", m.DbB.GetExecutionCatalog());

        m.DbA.DeleteExecutionFromCatalog("3 насоса");
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);

        Assert.DoesNotContain("3 насоса", m.DbB.GetExecutionCatalog());
    }
}
