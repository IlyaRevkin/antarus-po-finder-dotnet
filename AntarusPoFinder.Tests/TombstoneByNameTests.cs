using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Удалённый подтип не возвращается, даже когда идентификаторы машин не сошлись.
///
/// Жалоба Ильи, повторенная в пятый раз: «пять раз удалял из иерархии НГР 2.0, он всё равно
/// возвращается». Надгробия по sync_id эту беду закрывали только наполовину: у строки, заведённой
/// до появления обмена, идентификатора нет вовсе, а у коллеги на тот же по смыслу подтип свой —
/// и чужой снимок каждый раз заводил подтип заново.</summary>
public class TombstoneByNameTests
{
    private static (int groupId, int subtypeId) Seed(Database db, string group, string subtype)
    {
        var g = db.GetAllEquipmentGroups().First(x => x.Name == group);
        var s = db.UpsertEquipmentSubtype(new EquipmentSubType { GroupId = g.Id!.Value, Name = subtype, Prefix = 9 });
        return (g.Id!.Value, s);
    }

    [Fact]
    public void Надгробие_пишется_по_имени_даже_без_идентификатора()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        // Пустой sync_id — ровно случай строки, заведённой до появления обмена.
        db.MarkHierarchyDeleted(Database.TombstoneSubtype, syncId: "", label: "НГР / 2.0");

        var key = Database.TombstoneNameKey("НГР / 2.0");
        Assert.True(db.DeletedLaterThan(Database.TombstoneSubtype, key, null));
    }

    [Fact]
    public void Ключ_имени_не_зависит_от_регистра_и_лишних_пробелов()
    {
        // Снимок с чужой машины приходит с тем же именем, но набранным иначе: «НГР / 2.0» против
        // «нгр /  2.0». Для человека это одно и то же, и для надгробия обязано быть тоже.
        Assert.Equal(Database.TombstoneNameKey("НГР / 2.0"), Database.TombstoneNameKey("  нгр / 2.0  "));
    }

    [Fact]
    public void Заново_заведённый_подтип_надгробие_не_отбивает()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        db.MarkHierarchyDeleted(Database.TombstoneSubtype, syncId: "", label: "НГР / 2.0",
            deletedAt: "2026-01-01 10:00:00");

        var key = Database.TombstoneNameKey("НГР / 2.0");

        // Правка СВЕЖЕЕ удаления — значит подтип завели заново, и он обязан приехать.
        Assert.False(db.DeletedLaterThan(Database.TombstoneSubtype, key, "2026-02-01 10:00:00"));
        // Правка старше удаления — это тот самый возврат, его отбиваем.
        Assert.True(db.DeletedLaterThan(Database.TombstoneSubtype, key, "2025-12-01 10:00:00"));
    }

    [Fact]
    public void Удаление_подтипа_кладёт_оба_надгробия()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        var (_, subtypeId) = Seed(db, "НГР", "2.0-тест");
        db.DeleteEquipmentSubtype(subtypeId);

        var byName = Database.TombstoneNameKey("НГР / 2.0-тест");
        Assert.True(db.DeletedLaterThan(Database.TombstoneSubtype, byName, null));
    }
}
