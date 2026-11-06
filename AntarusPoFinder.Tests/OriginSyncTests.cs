using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Исходное название и автор доезжают до коллег — в том числе у прошивок, которые у них
/// уже есть.
///
/// Жалоба Ильи: «исходное название будто бы не синхронизируется». Так и было: обмен обновлял
/// существующую запись узким списком полей, и эти два в него не входили — заполнялись только при
/// первичной вставке. У всех, у кого прошивка уже лежала, имя оставалось пустым навсегда.</summary>
public class OriginSyncTests
{
    private static int Seed(Database db, string sourceName = "")
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = db.GetAllModifications().First();
        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = 1,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "2.1.001.0001.20260101_0000",
            Filename = "fw.psl",
            DiskPath = "",
            Description = "тест",
            SourceName = sourceName,
        });
    }

    [Fact]
    public void Правка_имени_ставит_отметку_времени()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        var id = Seed(db);
        db.UpdateFwVersion(id, sourceName: "НГР_КПЧ_v12.psl");

        var row = db.GetFwVersionById(id);
        Assert.Equal("НГР_КПЧ_v12.psl", row!.SourceName);
        // Без отметки обмен не смог бы решить, чьё значение свежее, и правка осталась бы местной.
        Assert.NotEqual("", row.OriginChangedAt);
    }

    [Fact]
    public void Исходное_название_уезжает_в_общий_конфиг()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        var id = Seed(db);
        db.UpdateFwVersion(id, sourceName: "НГР_КПЧ_v12.psl");

        var exported = db.ExportHierarchyData();
        var fw = exported.FwVersions.Single(v => v.VersionRaw == "2.1.001.0001.20260101_0000");

        Assert.Equal("НГР_КПЧ_v12.psl", fw.SourceName);
        Assert.NotEqual("", fw.OriginChangedAt);
    }

    [Fact]
    public void Приехавшее_имя_заполняет_пустое_у_получателя()
    {
        using var mine = new TempDb();
        using var theirs = new TempDb();
        using var sender = new Database(mine.Path);
        using var receiver = new Database(theirs.Path);

        // У обоих прошивка уже есть — ровно случай, в котором имя раньше не доезжало.
        var sent = Seed(sender);
        Seed(receiver);
        sender.UpdateFwVersion(sent, sourceName: "НГР_КПЧ_v12.psl");

        receiver.ImportHierarchyData(sender.ExportHierarchyData());

        var row = receiver.GetAllFwVersionsWithNames()
            .Single(v => v.VersionRaw == "2.1.001.0001.20260101_0000");
        Assert.Equal("НГР_КПЧ_v12.psl", row.SourceName);
    }
}
