using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ОТПРАВКА СЛЕПКА ЧЕРЕЗ ТИХИЙ КАНАЛ — упаковка, имя файла и отсечение секретов.
///
/// Канал выбран частный (страница приёма, silent=1), а не бакет на хостинге: бакет публичен на
/// чтение, а слепок — это опись внутренней сетевой папки конторы. Проверяется здесь то, что можно
/// проверить без сети: что архив разбирается обратно, что имя файла годится для папки, куда
/// складывают все машины, и что слепок с секретом наружу не уйдёт.</summary>
public class DiskSnapshotDropTests
{
    private static DiskSnapshot Snapshot(params FwVersionRecord[] rows) =>
        DiskSnapshot.Build("ILIA-PC", new DateTime(2026, 11, 6, 14, 20, 0), @"Z:\Software",
            new[] { (@"ПО\ПЖ\2.0\SMH5\1.0\Прошивка\ф.psl", 10L, new DateTime(2026, 11, 1)) },
            rows, DiskSnapshot.MaxFiles, new[] { @"ПО\ПЖ\2.0\SMH5\1.0" }, "2.2.2.0");

    /// <summary>Сжатие обязательно — опись это десятки тысяч строк. Но сжатый слепок, который не
    /// разжимается тем же кодом, бесполезен: проверяем поездку туда и обратно.</summary>
    [Fact]
    public void Слепок_упаковывается_и_разбирается_обратно()
    {
        var json = Snapshot().ToJson();

        var archive = DiskSnapshotDrop.Pack(json);

        Assert.Equal(json, DiskSnapshotDrop.Unpack(archive));
    }

    /// <summary>Сжатие должно действительно сжимать: на повторяющихся путях опись ужимается в разы,
    /// и ровно ради этого архив и заведён.</summary>
    [Fact]
    public void Упакованный_слепок_заметно_меньше_исходного()
    {
        var rows = Enumerable.Range(0, 300)
            .Select(i => new FwVersionRecord
            {
                SyncId = "s" + i, GroupName = "ПЖ", SubtypeName = "2.0", CtrlName = "SMH5",
                VersionRaw = "1." + i, DiskPath = $@"Z:\Software\ПО\ПЖ\2.0\SMH5\1.{i}", Status = "active",
            })
            .ToArray();
        var json = Snapshot(rows).ToJson();

        var archive = DiskSnapshotDrop.Pack(json);

        Assert.True(archive.Length * 3 < json.Length,
            $"архив {archive.Length} байт против {json.Length} — сжатия почти нет");
    }

    /// <summary>Имя файла: время ПЕРВЫМ и только латиница. Файлы в diag/ лежат вперемешку от всех
    /// машин, сортировка по имени должна совпадать с сортировкой по времени, а кириллица в именах,
    /// которые забирают по SSH и разбирают скриптом, — лишний источник приключений.</summary>
    [Fact]
    public void Имя_архива_начинается_со_времени_и_не_содержит_кириллицы()
    {
        var name = DiskSnapshotDrop.FileName("Наладка-3", new DateTime(2026, 11, 6, 14, 20, 5));

        Assert.StartsWith("20261106_142005_disk_", name);
        Assert.EndsWith(".zip", name);
        Assert.DoesNotContain(name, "абвгдеёжзийклмнопрстуфхцчшщъыьэюя".ToCharArray().Select(c => c.ToString()));
    }

    /// <summary>Слепок уезжает с рабочей машины наружу сам, по расписанию. Единственное, что
    /// отделяет опись диска от утечки реквизитов, — состав полей, а он со временем меняется.
    /// Поэтому отправка проверяет слепок на то, чему в нём не место, и отказывается молча не
    /// отправлять, а сказать об этом.</summary>
    [Fact]
    public async System.Threading.Tasks.Task Слепок_с_паролем_наружу_не_уходит()
    {
        var json = """{"schema":2,"machine":"ILIA-PC","s3Secret":"enc:AAAA"}""";

        var result = await DiskSnapshotDrop.SendAsync(json, "ILIA-PC", DateTime.UtcNow,
            "https://files.rev-il.ru/drop/upload", "drop-код");

        Assert.False(result.Ok);
        Assert.Contains("не отправлен", result.Error!);
        Assert.Equal(0, result.Bytes);
    }

    /// <summary>Настоящий слепок проверку проходит — иначе она была бы не защитой, а выключателем,
    /// который однажды выключат.</summary>
    [Fact]
    public void Настоящий_слепок_проверку_на_секреты_проходит()
    {
        var rows = new[]
        {
            new FwVersionRecord
            {
                SyncId = "s1", GroupName = "ПЖ", SubtypeName = "2.0", CtrlName = "SMH5",
                VersionRaw = "1.0", DiskPath = @"Z:\Software\ПО\ПЖ\2.0\SMH5\1.0",
                Status = "active", AuthorName = "revkin.i", RequestNum = "01312",
            },
        };

        Assert.Null(DiskSnapshotSafety.Refusal(Snapshot(rows).ToJson()));
    }
}

/// <summary>ПРОВЕРКА СЛЕПКА НА СЕКРЕТЫ. Ищем ИМЕНА полей, а не значения: папка «Секретный проект»
/// ничего не нарушает, а поле <c>password</c> означает ровно одно — в слепок положили не ту часть
/// базы.</summary>
public class DiskSnapshotSafetyTests
{
    [Theory]
    [InlineData("""{"password":"123"}""")]
    [InlineData("""{"s3SecretEnc":"что угодно"}""")]
    [InlineData("""{"adminToken":"x"}""")]
    [InlineData("""{"apiKey":"x"}""")]
    [InlineData("""{"пароль":"x"}""")]
    public void Поле_с_секретным_именем_ловится(string json)
    {
        Assert.NotEmpty(DiskSnapshotSafety.Scan(json));
    }

    /// <summary>Шифрованное значение общего конфига начинается на «enc:» и не бывает ничем другим —
    /// это ловится по значению, даже если поле назвали невинно.</summary>
    [Fact]
    public void Шифрованное_значение_ловится_даже_при_невинном_имени()
    {
        Assert.NotEmpty(DiskSnapshotSafety.Scan("""{"note":"enc:AAAABBBB"}"""));
    }

    /// <summary>А вот путь со словом «секрет» внутри — это просто папка, и отказываться из-за неё
    /// отправлять слепок нельзя: так проверку и приучают отключать.</summary>
    [Fact]
    public void Путь_со_словом_секрет_поводом_для_отказа_не_является()
    {
        Assert.Empty(DiskSnapshotSafety.Scan("""{"path":"ПО/ПЖ/секретный проект/1.0/ф.psl"}"""));
    }

    [Fact]
    public void Нечитаемый_json_проверку_не_проходит()
    {
        Assert.NotEmpty(DiskSnapshotSafety.Scan("{это не json"));
    }
}
