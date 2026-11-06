using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ОДНА ПРОШИВКА — ОДНА СТРОКА в очереди модерации.
///
/// Жалоба звучала как «в модерации не реальное количество: у кого-то 2–3, а у меня 20+», и сперва
/// её приняли за расхождение счётчика со списком. Снимки с пяти машин (ModerationDiagnostics)
/// показали другое: счётчик и список совпадают ВСЕГДА, а расходятся машины — и в очереди машины
/// NALADKA1 номера версий прямо повторяются («1.1.0005.0004» дважды). Повтор — не ошибка счёта: у
/// прошивки заводится своя запись на каждый подтип шкафа, которому она подходит. Поиск такие строки
/// схлопывает (Database.Deduplicate), очередь модерации — нет.
///
/// Здесь проверяется схлопывание по тому же признаку, по которому записи и есть одна прошивка
/// (Core/Services/ModerationQueue), выпуск ВСЕЙ группы одним действием и равенство счётчика числу
/// строк.</summary>
public class ModerationGroupingTests
{
    private sealed class Fixture : System.IDisposable
    {
        public readonly TempDb File = new();
        public readonly Database Db;
        public readonly List<EquipmentSubType> Subtypes;
        public readonly int ControllerId;
        public readonly int EqPrefix;

        public Fixture()
        {
            Db = new Database(File.Path);
            var group = Db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
            EqPrefix = group.Prefix;
            Subtypes = Db.GetSubtypesForGroup(group.Id!.Value).Take(4).ToList();
            Assert.True(Subtypes.Count >= 3, "сидовая иерархия обязана давать минимум три подтипа НГР");
            ControllerId = Db.GetAllModifications().First().ControllerId;
        }

        public void Dispose() { Db.Dispose(); File.Dispose(); }
    }

    /// <summary>Запись прошивки. Подтип, папка на диске, номер версии и родство задаются явно —
    /// именно из них и складывается признак «это одна и та же прошивка».</summary>
    private static FwVersionRecord Add(Fixture f, EquipmentSubType subtype, string diskPath, string versionRaw,
        int sw = 1, string syncId = "", string copyOf = "", int hw = 1)
    {
        var rec = new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = f.ControllerId,
            EqPrefix = f.EqPrefix,
            SubPrefix = subtype.Prefix,
            HwVersion = hw,
            SwVersion = sw,
            DtStr = "20261102_1200",
            VersionRaw = versionRaw,
            Filename = "fw.psl",
            DiskPath = diskPath,
            Description = "ждёт модерации",
            Status = "active",
            SyncId = syncId,
            CopyOf = copyOf,
            Released = false,
        };
        rec.Id = f.Db.AddFwVersion(rec);
        rec.SyncId = f.Db.GetFwVersionSyncId(rec.Id.Value);
        return rec;
    }

    /// <summary>Прежний способ «та же прошивка под другим подтипом»: общая папка на диске, общий
    /// номер, в чужом подтипе лежит только ярлык. Таких записей на дисках накоплено больше всего —
    /// именно они и давали «20+».</summary>
    [Fact]
    public void Записи_делящие_папку_схлопываются_в_одну_строку()
    {
        using var f = new Fixture();
        const string disk = @"Z:\ПО\НГР\КНС\SMH4\1.1.0005.0004";
        Add(f, f.Subtypes[0], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[1], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[2], disk, "1.1.0005.0004");

        var queue = f.Db.GetModerationQueue();

        Assert.Single(queue);
        Assert.Equal(3, queue[0].RecordCount);
        Assert.Equal(3, queue[0].SubtypeNames.Count);
        Assert.Equal(3, f.Db.GetUnreleasedFwVersionRowCount());
    }

    /// <summary>Нынешний способ: копия получает свою папку и свой номер, родство хранится явно
    /// (copy_of = sync_id оригинала). Общей папки у них нет вовсе, и без второго признака такие
    /// копии остались бы отдельными строками.</summary>
    [Fact]
    public void Копии_со_своей_папкой_схлопываются_по_родству()
    {
        using var f = new Fixture();
        var primary = Add(f, f.Subtypes[0], @"Z:\ПО\НГР\КНС\SMH4\1.1.0005.0004", "1.1.0005.0004");
        Add(f, f.Subtypes[1], @"Z:\ПО\НГР\ПЖ\SMH4\1.2.0005.0004", "1.2.0005.0004", copyOf: primary.SyncId);

        var queue = f.Db.GetModerationQueue();

        Assert.Single(queue);
        Assert.Equal(2, queue[0].RecordCount);
        // Главная строка — оригинал, а не копия: его подтип человек и считает «тем самым шкафом».
        Assert.Equal(primary.Id, queue[0].Primary.Id);
    }

    /// <summary>Родство транзитивно: A и B делят папку, C — копия A. Все трое одна прошивка, и
    /// выпускаться должны вместе. Эта проверка и есть причина, по которой выпуск идёт по всем
    /// членам группы, а не одним вызовом по главной записи.</summary>
    [Fact]
    public void Родство_транзитивно_папка_плюс_копия()
    {
        using var f = new Fixture();
        const string disk = @"Z:\ПО\НГР\КНС\SMH4\1.1.0005.0004";
        var a = Add(f, f.Subtypes[0], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[1], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[2], @"Z:\ПО\НГР\ПЖ\SMH4\1.3.0005.0004", "1.3.0005.0004", copyOf: a.SyncId);

        var queue = f.Db.GetModerationQueue();

        Assert.Single(queue);
        Assert.Equal(3, queue[0].Ids.Count);
    }

    /// <summary>Обратная сторона: лишнего схлопывания быть не должно. Две РАЗНЫЕ прошивки разных
    /// шкафов вполне могут носить один номер версии — склеив их по номеру, человек одним нажатием
    /// выпустил бы чужую.</summary>
    [Fact]
    public void Разные_прошивки_с_одинаковым_номером_остаются_разными_строками()
    {
        using var f = new Fixture();
        Add(f, f.Subtypes[0], @"Z:\ПО\НГР\КНС\SMH4\1.1.0005.0004", "1.1.0005.0004");
        Add(f, f.Subtypes[1], @"Z:\ПО\НГР\ПЖ\SMH4\1.1.0005.0004", "1.1.0005.0004");

        Assert.Equal(2, f.Db.GetModerationQueue().Count);
        Assert.Equal(2, f.Db.GetUnreleasedFwVersionsCount());
    }

    /// <summary>Записи без файлов на диске (пустой disk_path) не родня друг другу: иначе «одной
    /// прошивкой» стали бы все такие записи разом — и человек выпустил бы половину базы.</summary>
    [Fact]
    public void Записи_без_папки_не_склеиваются_между_собой()
    {
        using var f = new Fixture();
        Add(f, f.Subtypes[0], "", "1.1.0005.0004");
        Add(f, f.Subtypes[1], "", "1.1.0005.0004");

        Assert.Equal(2, f.Db.GetModerationQueue().Count);
    }

    /// <summary>ГЛАВНОЕ требование безопасности: вывод из модерации выпускает ВСЮ группу. Выпустить
    /// одну запись из трёх — значит оставить прошивку наполовину выпущенной: в поиске она уже видна,
    /// а в очереди висят её же записи, и следующий человек не понимает, что с ними делать.</summary>
    [Fact]
    public void Выпуск_группы_выводит_из_модерации_все_её_записи()
    {
        using var f = new Fixture();
        const string disk = @"Z:\ПО\НГР\КНС\SMH4\1.1.0005.0004";
        var a = Add(f, f.Subtypes[0], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[1], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[2], @"Z:\ПО\НГР\ПЖ\SMH4\1.3.0005.0004", "1.3.0005.0004", copyOf: a.SyncId);

        var item = Assert.Single(f.Db.GetModerationQueue());
        f.Db.ReleaseModerationGroup(item.Ids);

        Assert.Empty(f.Db.GetModerationQueue());
        Assert.Equal(0, f.Db.GetUnreleasedFwVersionsCount());
        Assert.Equal(0, f.Db.GetUnreleasedFwVersionRowCount());
        Assert.All(f.Db.GetAllFwVersionsWithNames(), v => Assert.True(v.Released));
    }

    /// <summary>Доставка решения коллегам касается всех записей группы: выпустили одну — выпущены
    /// все, значит и у коллег должны стать выпущенными все, а не одна.</summary>
    [Fact]
    public void В_доставку_попадают_все_записи_группы()
    {
        using var f = new Fixture();
        const string disk = @"Z:\ПО\НГР\КНС\SMH4\1.1.0005.0004";
        var a = Add(f, f.Subtypes[0], disk, "1.1.0005.0004");
        var b = Add(f, f.Subtypes[1], disk, "1.1.0005.0004");
        var c = Add(f, f.Subtypes[2], @"Z:\ПО\НГР\ПЖ\SMH4\1.3.0005.0004", "1.3.0005.0004", copyOf: a.SyncId);

        var item = Assert.Single(f.Db.GetModerationQueue());
        var ids = f.Db.GetModerationGroupDeliveryIds(item.Ids);

        Assert.Equal(new[] { a.Id!.Value, b.Id!.Value, c.Id!.Value }.OrderBy(x => x), ids);
    }

    /// <summary>Выпуск не должен ломать обмен между машинами: отметка идёт только в одну сторону
    /// (0 → 1), и повторный выпуск уже выпущенной группы ничего не меняет и не воскрешает.</summary>
    [Fact]
    public void Повторный_выпуск_группы_ничего_не_портит()
    {
        using var f = new Fixture();
        const string disk = @"Z:\ПО\НГР\КНС\SMH4\1.1.0005.0004";
        Add(f, f.Subtypes[0], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[1], disk, "1.1.0005.0004");

        var item = Assert.Single(f.Db.GetModerationQueue());
        f.Db.ReleaseModerationGroup(item.Ids);
        f.Db.ReleaseModerationGroup(item.Ids);

        Assert.Empty(f.Db.GetModerationQueue());
        Assert.All(f.Db.GetAllFwVersionsWithNames(), v => Assert.True(v.Released));
    }

    /// <summary>Счётчик на бейдже и число строк на экране считаются ОДНИМ И ТЕМ ЖЕ: они и раньше
    /// совпадали, и схлопывание не имело права развести их обратно. Набор нарочно смешанный —
    /// группа из трёх, группа из двух и одиночка.</summary>
    [Fact]
    public void Счётчик_равен_числу_строк_списка()
    {
        using var f = new Fixture();
        const string disk1 = @"Z:\ПО\НГР\КНС\SMH4\1.1.0005.0004";
        Add(f, f.Subtypes[0], disk1, "1.1.0005.0004");
        Add(f, f.Subtypes[1], disk1, "1.1.0005.0004");
        Add(f, f.Subtypes[2], disk1, "1.1.0005.0004");

        // Разные hw — чтобы прошивки не вытесняли друг друга как «заменённые более свежей»
        // (NotSuperseded): проверяется счёт, а не правило вытеснения.
        const string disk2 = @"Z:\ПО\НГР\КНС\SMH4\2.4.1321.0009";
        Add(f, f.Subtypes[0], disk2, "2.4.1321.0009", sw: 2, hw: 2);
        Add(f, f.Subtypes[1], disk2, "2.4.1321.0009", sw: 2, hw: 2);

        Add(f, f.Subtypes[2], @"Z:\ПО\НГР\ПЖ\SMH4\3.0.0001.0001", "3.0.0001.0001", sw: 3, hw: 3);

        var queue = f.Db.GetModerationQueue();

        Assert.Equal(3, queue.Count);
        Assert.Equal(queue.Count, f.Db.GetUnreleasedFwVersionsCount());
        // А записей в базе по-прежнему шесть — ровно эта разница и выглядела как «20+».
        Assert.Equal(6, f.Db.GetUnreleasedFwVersionRowCount());
    }

    /// <summary>Диагностика обязана прямо называть разницу: сколько записей схлопнулось. По этому
    /// срезу и видно, почему у одной машины очередь длиннее, чем у соседней.</summary>
    [Fact]
    public void Диагностика_показывает_сколько_копий_схлопнуто()
    {
        using var f = new Fixture();
        const string disk = @"Z:\ПО\НГР\КНС\SMH4\1.1.0005.0004";
        Add(f, f.Subtypes[0], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[1], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[2], disk, "1.1.0005.0004");

        var slices = f.Db.ModerationSlices();

        Assert.Equal(0, slices["потеряно на справочнике"]);
        Assert.Equal(2, slices["схлопнуто копий по подтипам"]);
        Assert.Equal(f.Db.GetModerationQueue().Count, slices["итог (как в счётчике и списке)"]);
    }

    /// <summary>В строке видно, скольких подтипов касается прошивка: до трёх — перечислением,
    /// дальше первые два и «и ещё N». Человек должен ПОНИМАТЬ, что одним действием выпускает
    /// несколько записей, а не узнавать об этом потом.</summary>
    [Fact]
    public void В_строке_перечислены_подтипы()
    {
        using var f = new Fixture();
        const string disk = @"Z:\ПО\НГР\КНС\SMH4\1.1.0005.0004";
        Add(f, f.Subtypes[0], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[1], disk, "1.1.0005.0004");
        Add(f, f.Subtypes[2], disk, "1.1.0005.0004");

        var item = Assert.Single(f.Db.GetModerationQueue());

        Assert.True(item.IsMulti);
        Assert.Equal(string.Join(", ", item.SubtypeNames), item.SubtypesDisplay);
        foreach (var name in item.SubtypeNames)
            Assert.Contains(name, item.SubtypesFull);
    }

    [Fact]
    public void Длинный_список_подтипов_сокращается_с_числом_остальных()
    {
        var records = new List<FwVersionRecord>();
        foreach (var name in new[] { "КНС", "ПЖ", "НГР", "ВНС", "ТП" })
            records.Add(new FwVersionRecord { Id = records.Count + 1, SubtypeName = name, VersionRaw = "1.1.0001.0001" });

        var item = new ModerationQueueItem(records);

        Assert.Equal("КНС, ПЖ и ещё 3 подтипа", item.SubtypesDisplay);
        Assert.Equal("КНС, ПЖ, НГР, ВНС, ТП", item.SubtypesFull);
    }

    [Theory]
    [InlineData(1, "подтип")]
    [InlineData(2, "подтипа")]
    [InlineData(5, "подтипов")]
    [InlineData(11, "подтипов")]
    [InlineData(21, "подтип")]
    [InlineData(22, "подтипа")]
    public void Склонение_слова_подтип(int n, string expected) =>
        Assert.Equal(expected, ModerationQueueItem.Plural(n));

    /// <summary>Один и тот же подтип, записанный в разном регистре, — один подтип, а не два:
    /// иначе строка рисовала бы «+1 подтип» на пустом месте. Про кириллицу и COLLATE NOCASE в
    /// SQLite в этом проекте уже обжигались, поэтому сравнение здесь именно человеческое.</summary>
    [Fact]
    public void Подтипы_в_разном_регистре_считаются_одним()
    {
        var item = new ModerationQueueItem(new List<FwVersionRecord>
        {
            new() { Id = 1, SubtypeName = "ПИ" },
            new() { Id = 2, SubtypeName = "пи" },
        });

        Assert.Single(item.SubtypeNames);
        // Записей при этом всё равно две, и выпускаются обе.
        Assert.Equal(2, item.RecordCount);
    }
}
