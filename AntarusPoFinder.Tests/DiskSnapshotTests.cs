using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Слепок дерева ПО, который уезжает в хранилище.
///
/// Затея Ильи: «может можно через хранилище сделать слепок диска, типа бэкапа, и ты бы мог
/// отслеживать структуру и оркестрировать, не имея доступа к диску». Сетевой диск виден только из
/// конторы, и тот, кто чинит программу, о его структуре может лишь спрашивать.
///
/// Проверяется ровно то, что делает слепок пригодным для СРАВНЕНИЯ: относительные пути, устойчивый
/// порядок, честная отметка об усечении и обе картины мира разом — диск и мнение базы.</summary>
public class DiskSnapshotTests
{
    private static IEnumerable<(string, long, DateTime)> Files(params string[] paths) =>
        paths.Select(p => (p, 10L, new DateTime(2026, 10, 8, 12, 0, 0)));

    private static DiskSnapshot Build(IEnumerable<(string, long, DateTime)> files,
        IEnumerable<FwVersionRecord>? rows = null, int max = DiskSnapshot.MaxFiles) =>
        DiskSnapshot.Build("ILIA-PC", new DateTime(2026, 10, 8, 12, 0, 0), @"Z:\Software",
            files, rows ?? Array.Empty<FwVersionRecord>(), max);

    /// <summary>Главное для сравнения двух слепков: порядок задаётся путём, а не обходом файловой
    /// системы. Иначе два снимка одного диска отличались бы всем подряд.</summary>
    [Fact]
    public void Порядок_файлов_задаётся_путём_а_не_обходом()
    {
        var a = Build(Files(@"ПО\НГР\б.psl", @"ПО\НГР\а.psl"));
        var b = Build(Files(@"ПО\НГР\а.psl", @"ПО\НГР\б.psl"));

        Assert.Equal(a.Files.Select(f => f.Path), b.Files.Select(f => f.Path));
    }

    /// <summary>Пути относительные и через косую черту. Абсолютный путь привязан к тому, как ИМЕННО
    /// эта машина видит шару, и слепки двух машин одного диска иначе не сравнить.</summary>
    [Fact]
    public void Пути_относительные_и_через_косую_черту()
    {
        var s = Build(Files(@"ПО\НГР\2.0\SMH5\1.0\Прошивка\ф.psl"));

        Assert.Equal("ПО/НГР/2.0/SMH5/1.0/Прошивка/ф.psl", s.Files[0].Path);
    }

    [Fact]
    public void Корень_отрезается_независимо_от_регистра_и_хвостовой_черты()
    {
        Assert.Equal(@"ПО\а.psl", DiskSnapshot.Relative(@"Z:\Software", @"z:\SOFTWARE\ПО\а.psl"));
        Assert.Equal(@"ПО\а.psl", DiskSnapshot.Relative(@"Z:\Software\", @"Z:\Software\ПО\а.psl"));
    }

    /// <summary>Путь не из-под корня остаётся как есть: соврать, что он внутри, хуже, чем показать
    /// чужой путь. Такое приезжает с машины, где шара подключена иначе.</summary>
    [Fact]
    public void Чужой_путь_не_выдаётся_за_внутренний()
    {
        Assert.Equal(@"D:\прочее\а.psl", DiskSnapshot.Relative(@"Z:\Software", @"D:\прочее\а.psl"));
    }

    /// <summary>Упёрлись в потолок — говорим прямо и показываем настоящее число файлов. Молча
    /// обрезанный слепок читался бы как «на диске больше ничего нет».</summary>
    [Fact]
    public void Усечение_признаётся_а_не_скрывается()
    {
        var s = Build(Files(@"ПО\1", @"ПО\2", @"ПО\3"), max: 2);

        Assert.True(s.Truncated);
        Assert.Equal(3, s.FileCount);
        Assert.Equal(2, s.Files.Count);
    }

    [Fact]
    public void Пока_файлов_меньше_потолка_усечения_нет()
    {
        var s = Build(Files(@"ПО\1", @"ПО\2"), max: 2);

        Assert.False(s.Truncated);
        Assert.Equal(2, s.Files.Count);
    }

    /// <summary>В слепке лежат ОБЕ картины мира: что реально на диске и что об этом думает база.
    /// Ради сравнения он и заводился — расхождение базы с диском и порождало прошлые беды.</summary>
    [Fact]
    public void В_слепке_есть_и_диск_и_мнение_базы()
    {
        var rows = new List<FwVersionRecord>
        {
            new()
            {
                SyncId = "s1", GroupName = "НГР", SubtypeName = "2.0", CtrlName = "SMH5",
                VersionRaw = "1.1.4.32", DiskPath = @"Z:\Software\ПО\НГР\2.0\SMH5\1.1.4.32",
                IsOpc = true, RequestNum = "01312", CabinetSn = "778899", Status = "active",
                Released = false, Archived = false, SwVersion = 32, HwVersion = 4, Execution = "3 насоса",
            },
        };

        var s = Build(Files(@"ПО\НГР\2.0\SMH5\1.1.4.32\Прошивка\ф.psl"), rows);

        Assert.Single(s.Db);
        Assert.Equal("ПО/НГР/2.0/SMH5/1.1.4.32", s.Db[0].DiskPath);
        Assert.True(s.Db[0].IsOpc);
        Assert.Equal("01312", s.Db[0].RequestNum);
    }

    /// <summary>Состояние модерации едет в слепке. Вопрос, ради которого это заведено: «у коллеги 20
    /// на модерации, а у меня 3». Очередь модерации — это строки с released = 0, не архивные и не
    /// заменённые более свежей версией той же линейки; чтобы понять, какая из трёх причин расхождения
    /// сработала, нужны именно эти поля — и номер версии ПО, по которому считается «заменена».</summary>
    [Fact]
    public void В_слепке_видно_состояние_модерации()
    {
        var rows = new List<FwVersionRecord>
        {
            new()
            {
                SyncId = "s1", GroupName = "НГР", SubtypeName = "2.0", CtrlName = "SMH5",
                VersionRaw = "1.1.4.32", DiskPath = @"Z:\Software\ПО\НГР",
                Status = "active", Released = false, Archived = false,
                SwVersion = 32, HwVersion = 4, Execution = "3 насоса",
            },
        };

        var s = Build(Files(@"ПО\НГР\ф.psl"), rows);

        Assert.False(s.Db[0].Released);
        Assert.False(s.Db[0].Archived);
        Assert.Equal(32, s.Db[0].SwVersion);
        Assert.Equal(4, s.Db[0].HwVersion);
        Assert.Equal("3 насоса", s.Db[0].Execution);
    }

    /// <summary>Слепок переживает поездку в JSON и обратно, а кириллица остаётся читаемой: его
    /// смотрят глазами не реже, чем кодом.</summary>
    [Fact]
    public void Слепок_переживает_json()
    {
        var s = Build(Files(@"ПО\НГР\прошивка.psl"));

        var json = s.ToJson();
        var back = DiskSnapshot.FromJson(json);

        Assert.NotNull(back);
        Assert.Contains("ПО/НГР/прошивка.psl", json);
        Assert.Equal(s.Files[0].Path, back!.Files[0].Path);
        Assert.Equal(s.Machine, back.Machine);
    }

    /// <summary>Машина и время — обязательная часть слепка. Рассуждать о диске по снимку недельной
    /// давности, не зная об этом, хуже, чем не рассуждать вовсе.</summary>
    [Fact]
    public void Слепок_подписан_машиной_и_временем()
    {
        var s = Build(Files(@"ПО\а.psl"));

        Assert.Equal("ILIA-PC", s.Machine);
        Assert.Equal("2026-10-08T12:00:00", s.TakenAt);
    }
}
