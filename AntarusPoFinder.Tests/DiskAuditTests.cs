using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>РАЗБОР СЛЕПКА: где база и диск разошлись.
///
/// Повод — жалоба «не найден HMI». Диагностика, приехавшая 06.11.2026, показала не ошибку поиска, а
/// дыру в данных: папка <c>HMI\</c> у версии ЕСТЬ, а файлов в ней НЕТ. Пять случаев, все ПЖ, и у
/// последнего (ХП/SMH5 1.3.0005.0003) на диске нет даже папки версии — только локальная копия.
/// Находить такое по одной жалобе — дорого; слепок должен выдавать такие дыры списком.
///
/// Проверяется именно это: что каждая из пород дыр видна по описи, и что законное состояние за дыру
/// не выдаётся — иначе список станет шумом, который перестанут читать.</summary>
public class DiskAuditTests
{
    private const string Root = @"Z:\Software";

    private static FwVersionRecord Row(string version, string diskPath, string hmi = "",
        string instructions = "", bool archived = false) => new()
    {
        SyncId = "s-" + version,
        GroupName = "ПЖ", SubtypeName = "2.0", CtrlName = "SMH5", VersionRaw = version,
        DiskPath = diskPath, HmiPath = hmi, InstructionsPath = instructions,
        Status = "active", Archived = archived,
    };

    private static DiskSnapshot Snap(IEnumerable<string> folders, IEnumerable<string> files,
        params FwVersionRecord[] rows) =>
        DiskSnapshot.Build("ILIA-PC", new DateTime(2026, 11, 6, 14, 20, 0), Root,
            files.Select(f => (f, 10L, new DateTime(2026, 11, 1))),
            rows, DiskSnapshot.MaxFiles, folders, "2.2.2.0");

    private static IEnumerable<string> Kinds(DiskSnapshot s, string kind) =>
        s.Issues.Where(i => i.Kind == kind).Select(i => i.Path);

    /// <summary>ТОТ САМЫЙ СЛУЧАЙ. Папка HMI у версии есть, файлов в ней нет, у контроллера общей
    /// папки HMI тоже нет — а база обещает проект панели. Без списка папок такую дыру не увидеть
    /// вовсе: пустая папка в описи ФАЙЛОВ следа не оставляет.</summary>
    [Fact]
    public void Пустая_папка_панели_при_обещанном_базой_проекте_это_дыра()
    {
        var version = @"ПО\ПЖ\2.0\SMH5\1.0.0005.0004";
        var s = Snap(
            folders: new[] { @"ПО\ПЖ\2.0\SMH5", version, version + @"\Прошивка", version + @"\HMI" },
            files: new[] { version + @"\Прошивка\1.0.0005.0004.psl" },
            Row("1.0.0005.0004", Root + "\\" + version, hmi: Root + "\\" + version + @"\HMI\панель.fsprj"));

        var issue = Assert.Single(s.Issues, i => i.Kind == DiskAudit.Kind.DocMissing);
        Assert.Equal("ПО/ПЖ/2.0/SMH5/1.0.0005.0004", issue.Path);
        Assert.Contains("HMI", issue.Detail);
    }

    /// <summary>Пустая папка раскладки САМА ПО СЕБЕ дырой не считается: перестройка диска заводит
    /// все пять папок у каждой версии и пустыми — человек должен видеть, куда что класть. Ругайся
    /// слепок на каждую, список утонул бы в тысячах законных строк и его перестали бы читать.</summary>
    [Fact]
    public void Пустая_папка_без_обещания_базы_за_дыру_не_выдаётся()
    {
        var version = @"ПО\ПЖ\2.0\SMH5\1.0";
        var s = Snap(
            folders: new[] { version, version + @"\Прошивка", version + @"\HMI", version + @"\Карта Modbus" },
            files: new[] { version + @"\Прошивка\1.0.psl" },
            Row("1.0", Root + "\\" + version));

        Assert.Empty(s.Issues.Where(i => i.Kind == DiskAudit.Kind.DocMissing));
    }

    /// <summary>...но в СВОДКЕ пустые папки посчитаны. «Пустых HMI 1400 из 1500» — это уже разговор,
    /// и увидеть его надо, не читая список построчно.</summary>
    [Fact]
    public void Пустые_папки_раскладки_посчитаны_в_сводке()
    {
        var a = @"ПО\ПЖ\2.0\SMH5\1.0";
        var b = @"ПО\ПЖ\2.0\SMH5\1.1";
        var s = Snap(
            folders: new[] { a, a + @"\Прошивка", a + @"\HMI", b, b + @"\Прошивка", b + @"\HMI" },
            files: new[] { a + @"\Прошивка\1.0.psl", b + @"\Прошивка\1.1.psl", b + @"\HMI\панель.fsprj" });

        Assert.Equal(1, s.Summary.EmptySlots[HierarchyFolders.Hmi]);
        Assert.Equal(2, s.Summary.VersionsOnDisk);
        Assert.Equal(2, s.Summary.NewLayout);
    }

    /// <summary>Версия без единого файла прошивки. Папка есть, журнал изменений есть, а скачивать
    /// нечего — и поиск такую версию находит, показывает и ничего не выдаёт.</summary>
    [Fact]
    public void Версия_без_файлов_прошивки_видна()
    {
        var version = @"ПО\ПЖ\FD\SMH4\1.1.0004.0001";
        var s = Snap(
            folders: new[] { version, version + @"\Прошивка" },
            files: new[] { version + @"\CHANGELOG.md" },
            Row("1.1.0004.0001", Root + "\\" + version));

        Assert.Contains("ПО/ПЖ/FD/SMH4/1.1.0004.0001", Kinds(s, DiskAudit.Kind.FirmwareMissing));
    }

    /// <summary>У неперестроенной версии файл прошивки лежит прямо в папке версии. Режим
    /// совместимости держится на VersionLayout, и разбор обязан смотреть обе раскладки — иначе
    /// каждая не переехавшая версия попала бы в список как «без прошивки».</summary>
    [Fact]
    public void Старая_раскладка_не_считается_версией_без_прошивки()
    {
        var version = @"ПО\ПЖ\ПИ\SMH4\1.4.0004.0002";
        var s = Snap(
            folders: new[] { version },
            files: new[] { version + @"\1.4.0004.0002.psl", version + @"\CHANGELOG.md" },
            Row("1.4.0004.0002", Root + "\\" + version));

        Assert.Empty(Kinds(s, DiskAudit.Kind.FirmwareMissing));
    }

    /// <summary>Документ у КОНТРОЛЛЕРА закрывает пустую папку версии: перестройка диска не копирует
    /// инструкцию контроллера в каждую версию, поэтому своя папка у переехавшей версии обычно пуста
    /// — а инструкция при этом на месте и открывается. Тот же порядок, что у
    /// VersionLayout.SlotBestReadFolder.</summary>
    [Fact]
    public void Инструкция_контроллера_закрывает_пустую_папку_версии()
    {
        var ctrl = @"ПО\ПЖ\2.0\SMH5";
        var version = ctrl + @"\1.0";
        var s = Snap(
            folders: new[] { ctrl, ctrl + @"\Инструкция", version, version + @"\Прошивка", version + @"\Инструкция" },
            files: new[] { version + @"\Прошивка\1.0.psl", ctrl + @"\Инструкция\инструкция_1.0.pdf" },
            Row("1.0", Root + "\\" + version, instructions: Root + "\\" + ctrl + @"\Инструкция\инструкция_1.0.pdf"));

        Assert.Empty(Kinds(s, DiskAudit.Kind.DocMissing));
    }

    /// <summary>ХП/SMH5 1.3.0005.0003 из той же диагностики: записи в базе есть, папки версии на
    /// диске нет вовсе — живёт только локальная копия на чьей-то машине.</summary>
    [Fact]
    public void В_базе_есть_на_диске_нет()
    {
        var s = Snap(
            folders: new[] { @"ПО\ПЖ\ХП\SMH5" },
            files: Array.Empty<string>(),
            Row("1.3.0005.0003", Root + @"\ПО\ПЖ\ХП\SMH5\1.3.0005.0003"));

        Assert.Contains("ПО/ПЖ/ХП/SMH5/1.3.0005.0003", Kinds(s, DiskAudit.Kind.DbWithoutDisk));
    }

    /// <summary>С архивной версии за отсутствие папки не спрашиваем: её могли унести законно, и
    /// «нет на диске» для неё не новость, а нормальный конец жизни.</summary>
    [Fact]
    public void Архивную_версию_за_отсутствие_папки_не_ругаем()
    {
        var s = Snap(
            folders: new[] { @"ПО\ПЖ\ХП\SMH5" },
            files: Array.Empty<string>(),
            Row("1.0", Root + @"\ПО\ПЖ\ХП\SMH5\1.0", archived: true));

        Assert.Empty(s.Issues);
    }

    /// <summary>Обратная сторона: папка версии на диске есть (её выдаёт собственная «Прошивка\»), а
    /// в базе про неё никто не знает. Такую версию поиском не найти — она существует только в
    /// проводнике.</summary>
    [Fact]
    public void На_диске_есть_в_базе_нет()
    {
        var version = @"ПО\ПЖ\FD\SMH5\1.1.0005.0004";
        var s = Snap(
            folders: new[] { version, version + @"\Прошивка" },
            files: new[] { version + @"\Прошивка\ф.psl" });

        Assert.Contains("ПО/ПЖ/FD/SMH5/1.1.0005.0004", Kinds(s, DiskAudit.Kind.DiskWithoutDb));
    }

    /// <summary>Мусор проводника и ярлык папку непустой не делают. Это ровно та ошибка, на которой
    /// уже обожглись однажды: Thumbs.db в своей папке версии превращал пустую «Инструкция» в
    /// «непустую», и документ контроллера переставал показываться.</summary>
    [Fact]
    public void Ярлык_и_мусор_проводника_папку_не_наполняют()
    {
        var version = @"ПО\ПЖ\2.0\SMH5\1.0";
        var s = Snap(
            folders: new[] { version, version + @"\Прошивка", version + @"\HMI" },
            files: new[]
            {
                version + @"\Прошивка\ф.psl",
                version + @"\HMI\Thumbs.db",
                version + @"\HMI\панель.lnk",
            },
            Row("1.0", Root + "\\" + version, hmi: Root + "\\" + version + @"\HMI\панель.fsprj"));

        Assert.Contains("ПО/ПЖ/2.0/SMH5/1.0", Kinds(s, DiskAudit.Kind.DocMissing));
    }

    /// <summary>Версия без журнала изменений — не беда, а наблюдение: по CHANGELOG досмотр диска
    /// восстанавливает описание и теги, и без него версия приезжает к коллегам немой.</summary>
    [Fact]
    public void Отсутствие_журнала_изменений_отмечается_отдельной_породой()
    {
        var version = @"ПО\ПЖ\2.0\SMH5\1.0";
        var s = Snap(
            folders: new[] { version, version + @"\Прошивка" },
            files: new[] { version + @"\Прошивка\ф.psl" },
            Row("1.0", Root + "\\" + version));

        Assert.Contains("ПО/ПЖ/2.0/SMH5/1.0", Kinds(s, DiskAudit.Kind.ChangelogMissing));
        Assert.Empty(Kinds(s, DiskAudit.Kind.FirmwareMissing));
    }

    /// <summary>Список дыр не бесконечен: если их тысячи, дело не в отдельных версиях, и это видно
    /// по первой тысяче. Усечение называется вслух — молча обрезанный список читался бы как «больше
    /// ничего нет».</summary>
    [Fact]
    public void Список_дыр_усекается_и_признаётся_в_этом()
    {
        var rows = Enumerable.Range(0, DiskAudit.MaxIssues + 10)
            .Select(i => Row($"1.{i}", Root + $@"\ПО\ПЖ\2.0\SMH5\1.{i}"))
            .ToArray();

        var s = Snap(new[] { @"ПО\ПЖ\2.0\SMH5" }, Array.Empty<string>(), rows);

        Assert.Equal(DiskAudit.MaxIssues + 1, s.Issues.Count);
        Assert.Equal("truncated", s.Issues[^1].Kind);
    }

    /// <summary>Порядок разбора задаётся путями, а не порядком строк базы: два слепка подряд должны
    /// отличаться ровно тем, чем отличается диск.</summary>
    [Fact]
    public void Порядок_дыр_устойчив()
    {
        var a = Row("1.1", Root + @"\ПО\ПЖ\2.0\SMH5\1.1");
        var b = Row("1.0", Root + @"\ПО\ПЖ\2.0\SMH5\1.0");

        var first = Snap(new[] { @"ПО\ПЖ" }, Array.Empty<string>(), a, b);
        var second = Snap(new[] { @"ПО\ПЖ" }, Array.Empty<string>(), b, a);

        Assert.Equal(first.Issues.Select(i => i.Path), second.Issues.Select(i => i.Path));
    }

    /// <summary>Слепок со схемой 2 переживает json вместе с папками, разбором и сводкой: его читают
    /// на той стороне, и потерянное при сериализации поле равносильно его отсутствию.</summary>
    [Fact]
    public void Папки_разбор_и_сводка_переживают_json()
    {
        var version = @"ПО\ПЖ\2.0\SMH5\1.0";
        var s = Snap(new[] { version, version + @"\HMI" }, new[] { version + @"\ф.psl" },
            Row("1.0", Root + "\\" + version, hmi: Root + "\\" + version + @"\HMI\панель.fsprj"));

        var back = DiskSnapshot.FromJson(s.ToJson());

        Assert.NotNull(back);
        Assert.Equal(2, back!.Schema);
        Assert.Equal("2.2.2.0", back.AppVersion);
        Assert.Contains("ПО/ПЖ/2.0/SMH5/1.0/HMI", back.Folders);
        Assert.Equal(s.Issues.Count, back.Issues.Count);
        Assert.Equal(s.Summary.VersionsInDb, back.Summary.VersionsInDb);
    }
}
