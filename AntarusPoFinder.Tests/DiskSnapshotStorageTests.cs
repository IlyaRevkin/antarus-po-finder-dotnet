using System;
using System.Linq;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Раскладка слепков диска в бакете и правило «сколько держать».</summary>
public class DiskSnapshotStorageTests
{
    /// <summary>Самый свежий слепок лежит под заранее известным адресом: читающей стороне не нужно
    /// сперва листать бакет.</summary>
    [Fact]
    public void У_последнего_слепка_постоянный_адрес()
    {
        Assert.Equal("disk/ilia-pc/latest.json.gz", DiskSnapshotStorage.LatestKey("ILIA-PC"));
    }

    /// <summary>Имя машины человек задаёт как хочет — в ключе бакета остаётся только то, что не
    /// развалит адрес. Кириллица и пробелы в именах объектов уже стоили нам разъехавшихся ссылок.</summary>
    [Theory]
    [InlineData("ILIA-PC", "ilia-pc")]
    [InlineData("ant_srv.local", "ant_srv-local")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    public void Имя_машины_приводится_к_безопасному(string machine, string expected)
    {
        Assert.Equal(expected, DiskSnapshotStorage.SanitizeMachine(machine));
    }

    /// <summary>Имя целиком из непригодных символов (кириллица) не должно давать пустую папку —
    /// иначе слепки разных машин легли бы в одну кучу.</summary>
    [Fact]
    public void Имя_без_латиницы_не_превращается_в_пустоту()
    {
        var folder = DiskSnapshotStorage.SanitizeMachine("Комп");

        Assert.NotEqual("", folder);
        Assert.DoesNotContain("/", folder);
    }

    [Fact]
    public void Датированный_слепок_назван_временем_снятия()
    {
        var key = DiskSnapshotStorage.DatedKey("ILIA-PC", new DateTime(2026, 10, 8, 14, 5, 9, DateTimeKind.Utc));

        Assert.Equal("disk/ilia-pc/20261008_140509.json.gz", key);
    }

    /// <summary>Главное правило уборки: «последний» не трогается никогда. Иначе уборка однажды унесла
    /// бы единственный свежий слепок.</summary>
    [Fact]
    public void Последний_слепок_не_удаляется_никогда()
    {
        var keys = Enumerable.Range(1, 30)
            .Select(i => $"disk/ilia-pc/202610{i:00}_120000.json.gz")
            .Append("disk/ilia-pc/latest.json.gz");

        Assert.DoesNotContain("disk/ilia-pc/latest.json.gz", DiskSnapshotStorage.ToPrune(keys, keep: 5));

        // keep: 0 — «не держать ничего». Именно здесь и ловится ошибка: при сортировке по имени
        // «latest» оказывается ВПЕРЕДИ дат (латиница старше цифр), поэтому при щедром лимите он
        // уцелел бы и по случайности, даже если его забыли исключить из очереди.
        Assert.Empty(DiskSnapshotStorage.ToPrune(keys, keep: 0)
            .Where(k => k.EndsWith("latest.json.gz", StringComparison.Ordinal)));
    }

    /// <summary>Остаётся ровно столько датированных, сколько просили, и удаляются САМЫЕ СТАРЫЕ.</summary>
    [Fact]
    public void Удаляются_самые_старые_сверх_лимита()
    {
        var keys = new[]
        {
            "disk/ilia-pc/20261001_120000.json.gz",
            "disk/ilia-pc/20261005_120000.json.gz",
            "disk/ilia-pc/20261008_120000.json.gz",
        };

        var prune = DiskSnapshotStorage.ToPrune(keys, keep: 2);

        Assert.Equal(new[] { "disk/ilia-pc/20261001_120000.json.gz" }, prune);
    }

    [Fact]
    public void Пока_слепков_меньше_лимита_уборки_нет()
    {
        var keys = new[] { "disk/ilia-pc/20261008_120000.json.gz", "disk/ilia-pc/latest.json.gz" };

        Assert.Empty(DiskSnapshotStorage.ToPrune(keys, keep: 14));
    }

    /// <summary>Посторонние объекты в папке машины уборка не трогает: удалять то, чего не клала, —
    /// не её дело.</summary>
    [Fact]
    public void Чужие_файлы_уборка_не_трогает()
    {
        var keys = new[]
        {
            "disk/ilia-pc/заметка.txt",
            "disk/ilia-pc/20260101_120000.json.gz",
            "disk/ilia-pc/20260102_120000.json.gz",
        };

        var prune = DiskSnapshotStorage.ToPrune(keys, keep: 1);

        Assert.Equal(new[] { "disk/ilia-pc/20260101_120000.json.gz" }, prune);
    }
}
