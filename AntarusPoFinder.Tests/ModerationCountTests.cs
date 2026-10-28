using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Цифра на вкладке «Модерация» обязана отвечать за строки, которые на ней видно.
///
/// Жалоба наладчика дословно: «в модерации прошивок не реальное значение их количества, у кого-то
/// 2–3, у меня 20+». Причина была в том, что счётчик и список спрашивали РАЗНОЕ: список джойнил
/// подтип, группу и контроллер, а счётчик считал по одной таблице. Прошивка, оставшаяся без
/// подтипа — его удалили, а версия уцелела, — попадала в счётчик и не попадала в список.
///
/// Цифра, за которой нет ни одной строки на экране, хуже отсутствующей: по ней человека посылают
/// разгребать то, чего он не увидит.</summary>
public class ModerationCountTests
{
    private static int Seed(Database db, int subtypeId, int controllerId, int sw)
    {
        var group = db.GetAllEquipmentGroups().First();
        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtypeId,
            ControllerId = controllerId,
            EqPrefix = group.Prefix,
            SubPrefix = 1,
            HwVersion = 1,
            SwVersion = sw,
            DtStr = $"2026010{sw}_0000",
            VersionRaw = $"2.1.001.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl",
            DiskPath = "",
            Description = "на модерации",
            Released = false,
        });
    }

    [Fact]
    public void Счётчик_совпадает_со_списком_когда_всё_на_месте()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = db.GetAllModifications().First();
        Seed(db, subtype.Id!.Value, mod.ControllerId, 1);

        Assert.Equal(db.GetUnreleasedFwVersionsWithNames().Count, db.GetUnreleasedFwVersionsCount());
    }

    [Fact]
    public void Сирот_в_базе_завестись_не_может()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        var mod = db.GetAllModifications().First();

        // Проверка догадки, а не поведения: я предположил, что счётчик раздувают прошивки,
        // оставшиеся без подтипа, и сведение запросов это чинит. Догадка оказалась неверной —
        // внешние ключи включены (PRAGMA foreign_keys=ON), и такую строку просто не записать.
        // Тест остаётся как зафиксированный отрицательный ответ: если кто-то завтра выключит
        // внешние ключи, он упадёт и напомнит, что на них держится в том числе эта цифра.
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() =>
            Seed(db, subtypeId: 999_999, controllerId: mod.ControllerId, sw: 2));
    }
}
