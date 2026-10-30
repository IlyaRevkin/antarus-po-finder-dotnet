using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Снимок очереди модерации — доказательства, а не догадки.
///
/// Первая попытка объяснить «у кого-то 2–3, у меня 20+» была неверной: я предположил сиротские
/// записи, а тест показал, что их не бывает. Теперь программа присылает цифры сама, и эти тесты
/// проверяют, что присылает она именно то, по чему можно сделать вывод: срезы по каждому условию
/// отбора и номера версий — по ним видно, расходится счёт на одной машине или наборы разные на
/// двух.</summary>
public class ModerationDiagnosticsTests
{
    private static int Seed(Database db, bool released, string? status = null)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = db.GetAllModifications().First();
        var sw = db.GetNextSwVersion(subtype.Id!.Value, mod.ControllerId, 1);

        var id = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = 1,
            SwVersion = sw,
            DtStr = $"2026010{sw}_0000",
            VersionRaw = $"2.1.001.{sw:0000}.2026010{sw}_0000",
            Filename = "fw.psl",
            DiskPath = "",
            Description = "тест",
            Released = released,
        });
        if (status is not null) db.DisableFwVersion(id);
        return id;
    }

    [Fact]
    public void Срезы_сходятся_со_счётчиком_и_показывают_где_сужается_отбор()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        Seed(db, released: false);
        Seed(db, released: false);
        // Выпуск ставится отдельным действием, а не полем при заведении: версия рождается в
        // модерации, и обойти это при заведении нельзя (см. Database.AddFwVersion).
        db.MarkFwVersionReleased(Seed(db, released: true));

        var slices = db.ModerationSlices();

        Assert.Equal(3, slices["всего версий"]);
        Assert.Equal(2, slices["не выпущено"]);
        Assert.Equal(db.GetUnreleasedFwVersionsCount(), slices["итог (как в счётчике и списке)"]);
        // Ради этой строки всё и затевалось: ненулевое значение означало бы, что строки теряются
        // на справочнике — ровно та догадка, которую надо подтвердить или опровергнуть цифрами.
        Assert.Equal(0, slices["потеряно на справочнике"]);
    }

    [Fact]
    public void Выключенная_версия_уходит_из_очереди_и_это_видно_срезом()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        Seed(db, released: false);
        var disabled = Seed(db, released: false);
        db.DisableFwVersion(disabled);

        var slices = db.ModerationSlices();

        Assert.Equal(2, slices["не выпущено"]);
        Assert.Equal(1, slices["+ не откатано и не выключено"]);
        Assert.Equal(1, slices["итог (как в счётчике и списке)"]);
    }

    [Fact]
    public void Номера_версий_совпадают_со_списком_страницы()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        Seed(db, released: false);
        Seed(db, released: false);

        var numbers = db.ModerationVersionNumbers();
        var listed = db.GetUnreleasedFwVersionsWithNames().Select(v => v.VersionRaw).OrderBy(x => x).ToList();

        Assert.Equal(listed, numbers);
    }

    [Fact]
    public void Отправляем_когда_счётчик_разошёлся_со_списком()
    {
        var same = new ModerationDiagnostics.Snapshot { Badge = 5, ListRows = 5 };
        var differ = new ModerationDiagnostics.Snapshot { Badge = 20, ListRows = 3 };

        Assert.False(ModerationDiagnostics.WorthSending(same));
        Assert.True(ModerationDiagnostics.WorthSending(differ));
    }

    [Fact]
    public void В_снимке_нет_ничего_кроме_цифр_и_номеров()
    {
        // Это диагностика счётчика, а не выгрузка чужой базы: описания, имена файлов и пути на
        // диске сюда попадать не должны — их пришлось бы объяснять тому, чью машину читают.
        var json = ModerationDiagnostics.Serialize(new ModerationDiagnostics.Snapshot
        {
            Machine = "PC-KISELEV",
            Versions = { "2.1.001.0001.20260101_0000" },
        });

        Assert.Contains("PC-KISELEV", json);
        Assert.Contains("2.1.001.0001", json);
        Assert.DoesNotContain("diskPath", json);
        Assert.DoesNotContain("description", json);
    }
}
