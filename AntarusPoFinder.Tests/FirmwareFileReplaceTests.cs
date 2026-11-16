using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ЗАМЕНА ПЕРЕСОБРАННОГО ВРУЧНУЮ ФАЙЛА ПРОШИВКИ В ВЫПУЩЕННОЙ ВЕРСИИ (тикет Киселёва от
/// 11.11.2026).
///
/// Проверяется не удобство, а четыре вещи, каждая из которых стоит дорого:
/// <list type="number">
/// <item>заменяется ТОТ файл и только похожий на прошивку — промах здесь означает подменённый не
///       тот файл на боевом сервере;</item>
/// <item>прежний файл никуда не девается и лежит так, что его не выберет ни лоадер, ни чистильщик
///       диска, — иначе «сохранили» превращается в «потеряли» или в «залили старое»;</item>
/// <item>кто и когда заменил — записано на диск и читается обратно: без этой записи замена
///       неотличима от чужой правки файлов руками;</item>
/// <item>право есть не у всех.</item>
/// </list></summary>
public class FirmwareFileReplaceTests
{
    private static readonly DateTime When = new(2026, 11, 16, 12, 10, 0);

    private static string Touch(string dir, string name, string content = "старая сборка")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Готовая к замене версия: папка с «Прошивка\», файлом .lfs внутри и журналом.</summary>
    private static string MakeVersion(TempRoot root, string versionRaw = "1.1.0004.0005")
    {
        var versionDir = Path.Combine(root.Path, versionRaw);
        VersionLayout.EnsureFolders(versionDir);
        Touch(VersionLayout.FirmwareFolder(versionDir), versionRaw + ".lfs");
        File.WriteAllText(Path.Combine(versionDir, ChangelogFile.FileName),
            $"# {versionRaw}\nДата: 20261101_1200\nТип пуска: прямой\n\nОписание сборки.");
        return versionDir;
    }

    /// <summary>Заменить можно только то, что мы умеем заменять. Исходник .psl сюда не пускаем
    /// намеренно: правленый проект — это другая прошивка, и ей полагается свой номер.</summary>
    [Fact]
    public void Заменяемым_считается_только_LFS()
    {
        Assert.True(FirmwareFileReplace.LooksReplaceable(@"C:\сборка\испр.lfs"));
        Assert.True(FirmwareFileReplace.LooksReplaceable(@"C:\сборка\ИСПР.LFS"));
        Assert.False(FirmwareFileReplace.LooksReplaceable(@"C:\сборка\проект.psl"));
        Assert.False(FirmwareFileReplace.LooksReplaceable(@"C:\сборка\архив.zip"));
        Assert.False(FirmwareFileReplace.LooksReplaceable(""));
        Assert.False(FirmwareFileReplace.LooksReplaceable(null));
    }

    /// <summary>Подменить файл на боевом сервере может тот, кто модерирует прошивки. Программисту
    /// не даём: у него есть обычный путь — выложить исправленную сборку версией.</summary>
    [Fact]
    public void Право_на_замену_только_у_тех_кто_модерирует()
    {
        Assert.True(FirmwareFileReplace.CanReplace("administrator"));
        Assert.True(FirmwareFileReplace.CanReplace("naladchik"));
        Assert.False(FirmwareFileReplace.CanReplace("programmer"));
        Assert.False(FirmwareFileReplace.CanReplace(""));
        Assert.False(FirmwareFileReplace.CanReplace(null));
    }

    /// <summary>Не тот файл — отказ ДО единой записи на диск: версия обязана остаться нетронутой.</summary>
    [Fact]
    public void Не_LFS_отвергается_и_версию_не_трогает()
    {
        using var root = new TempRoot();
        var versionDir = MakeVersion(root);
        var source = Touch(root.Path, "проект.psl", "исходник");

        var plan = FirmwareFileReplace.Prepare(versionDir, source, When);
        Assert.False(plan.Ok);

        var outcome = FirmwareFileReplace.Replace(versionDir, source, "Киселёв", When);
        Assert.False(outcome.Ok);
        Assert.Equal("старая сборка",
            File.ReadAllText(Path.Combine(VersionLayout.FirmwareFolder(versionDir), "1.1.0004.0005.lfs")));
        Assert.False(Directory.Exists(VersionLayout.ReplacedWriteFolder(versionDir)));
    }

    /// <summary>Заменять нечего — отказ, а не «положим рядом новый файл». Версия без собранного
    /// .lfs это не «версия с пустым местом», а версия, которой сборку никогда не делали, и класть
    /// туда чужой файл в обход загрузки нельзя.</summary>
    [Fact]
    public void Версия_без_LFS_отказывает()
    {
        using var root = new TempRoot();
        var versionDir = Path.Combine(root.Path, "1.1.0004.0005");
        VersionLayout.EnsureFolders(versionDir);
        Touch(VersionLayout.FirmwareFolder(versionDir), "1.1.0004.0005.psl");
        var source = Touch(root.Path, "испр.lfs", "новая сборка");

        var plan = FirmwareFileReplace.Prepare(versionDir, source, When);
        Assert.False(plan.Ok);
        Assert.Contains(".lfs", plan.Error);
    }

    /// <summary>ГЛАВНОЕ. Новый файл занимает место прежнего ПОД ЕГО ИМЕНЕМ (на диске прошивка
    /// названа номером версии), а прежний уезжает в «Предыдущие\» с датой в имени.</summary>
    [Fact]
    public void Новый_файл_встаёт_под_прежним_именем_а_старый_сохраняется_с_датой()
    {
        using var root = new TempRoot();
        var versionDir = MakeVersion(root);
        var source = Touch(root.Path, "ТГР_испр_финал.lfs", "новая сборка");

        var outcome = FirmwareFileReplace.Replace(versionDir, source, "Киселёв", When);
        Assert.True(outcome.Ok, outcome.Error);

        var target = Path.Combine(VersionLayout.FirmwareFolder(versionDir), "1.1.0004.0005.lfs");
        Assert.Equal("новая сборка", File.ReadAllText(target));
        // Имя выбранного файла на диск НЕ едет: иначе сломалось бы правило «файл прошивки назван
        // номером версии», по которому его сверяет чистильщик.
        Assert.False(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(versionDir), "ТГР_испр_финал.lfs")));

        var backup = Path.Combine(VersionLayout.ReplacedWriteFolder(versionDir),
            "1.1.0004.0005_20261116_1210.lfs.old");
        Assert.Equal(backup, outcome.BackupFile);
        Assert.Equal("старая сборка", File.ReadAllText(backup));
        // Мусора от незавершённого копирования не остаётся.
        Assert.Empty(Directory.EnumerateFiles(versionDir, "*" + LfsPublisher.TempSuffix, SearchOption.AllDirectories));
    }

    /// <summary>Сохранённый прежний файл НЕ должен попасться ни лоадеру, ни карточке: оба ищут
    /// сборку по расширению, и второй .lfs в версии рано или поздно уехал бы в контроллер вместо
    /// нового. Отсюда и подпапка, и дописанное «.old».</summary>
    [Fact]
    public void Сохранённый_прежний_не_подменяет_собой_новую_сборку()
    {
        using var root = new TempRoot();
        var versionDir = MakeVersion(root);
        var source = Touch(root.Path, "испр.lfs", "новая сборка");

        var outcome = FirmwareFileReplace.Replace(versionDir, source, "Киселёв", When);
        Assert.True(outcome.Ok, outcome.Error);

        // Лоадер по всей папке версии находит ровно один .lfs — новый.
        var found = LoaderFiles.FindIn(VersionLayout.FirmwareFolders(versionDir), LoaderFiles.LfsExtension);
        Assert.Equal("новая сборка", File.ReadAllText(found!));
        Assert.Single(Directory.EnumerateFiles(versionDir, "*" + LoaderFiles.LfsExtension,
            SearchOption.AllDirectories));

        // И чистильщик диска не предложит сохранённое удалить: «.old» мусором не считается
        // (в отличие от «.bak», см. JunkFiles).
        Assert.False(JunkFiles.IsJunk(outcome.BackupFile));
    }

    /// <summary>Вторая замена в ту же минуту не затирает первую сохранённую сборку — иначе
    /// «сохранили прежний» оказалось бы ложью ровно там, где человек правит второпях.</summary>
    [Fact]
    public void Две_замены_подряд_сохраняют_обе_прежние_сборки()
    {
        using var root = new TempRoot();
        var versionDir = MakeVersion(root);

        var first = FirmwareFileReplace.Replace(versionDir,
            Touch(root.Path, "испр1.lfs", "сборка 2"), "Киселёв", When);
        var second = FirmwareFileReplace.Replace(versionDir,
            Touch(root.Path, "испр2.lfs", "сборка 3"), "Киселёв", When);

        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        Assert.NotEqual(first.BackupFile, second.BackupFile);
        Assert.Equal("старая сборка", File.ReadAllText(first.BackupFile));
        Assert.Equal("сборка 2", File.ReadAllText(second.BackupFile));
        Assert.Equal("сборка 3",
            File.ReadAllText(Path.Combine(VersionLayout.FirmwareFolder(versionDir), "1.1.0004.0005.lfs")));
    }

    /// <summary>Тот же самый файл заменять нечем — это не ошибка диска, а промах выбора, и сказать
    /// про него надо ДО того, как прежняя сборка уедет в «Предыдущие\».</summary>
    [Fact]
    public void Замена_файла_на_самого_себя_отвергается()
    {
        using var root = new TempRoot();
        var versionDir = MakeVersion(root);
        var itself = Path.Combine(VersionLayout.FirmwareFolder(versionDir), "1.1.0004.0005.lfs");

        var plan = FirmwareFileReplace.Prepare(versionDir, itself, When);
        Assert.False(plan.Ok);
    }

    /// <summary>КТО И КОГДА — на диске, в журнале версии, и читается обратно. Запись живёт рядом с
    /// файлами по той же причине, что и отметка модерации: общий диск — единственный канал, который
    /// между машинами точно работает.</summary>
    [Fact]
    public void Кто_и_когда_заменил_записывается_в_журнал_версии()
    {
        using var root = new TempRoot();
        var versionDir = MakeVersion(root);
        var source = Touch(root.Path, "испр.lfs", "новая сборка");

        var outcome = FirmwareFileReplace.Replace(versionDir, source, "Киселёв (Наладчик)", When);
        Assert.True(outcome.Ok, outcome.Error);
        Assert.False(outcome.NoteMissing);

        var history = FirmwareFileReplace.History(versionDir);
        var record = Assert.Single(history);
        Assert.Equal("2026-11-16T12:10:00", record.At);
        Assert.Equal("Киселёв (Наладчик)", record.By);
        Assert.Equal("1.1.0004.0005.lfs", record.File);
        Assert.Contains(VersionLayout.ReplacedFolderName, record.Backup);

        // Остальное содержимое журнала цело: описание и тип пуска — чужая работа, и дописывание
        // строки их трогать не вправе.
        var content = ChangelogFile.TryRead(versionDir)!;
        Assert.Equal("Описание сборки.", content.Description);
        Assert.Equal(new[] { "прямой" }, content.LaunchTypes);

        // Строка для карточки — рядом с автором и исходным названием.
        Assert.Equal("Киселёв (Наладчик), 16.11.2026 12:10",
            FirmwareFileReplace.LastReplacementNote(history));
    }

    /// <summary>Замен было несколько — карточка показывает последнюю и говорит, сколько их всего:
    /// «файл меняли трижды» это уже разговор с автором сборки, а не мелочь.</summary>
    [Fact]
    public void Строка_для_карточки_считает_все_замены()
    {
        using var root = new TempRoot();
        var versionDir = MakeVersion(root);

        FirmwareFileReplace.Replace(versionDir, Touch(root.Path, "a.lfs", "2"), "Киселёв", When);
        FirmwareFileReplace.Replace(versionDir, Touch(root.Path, "b.lfs", "3"), "Иванов",
            When.AddDays(1));

        var history = FirmwareFileReplace.History(versionDir);
        Assert.Equal(2, history.Count);
        Assert.Equal("Иванов, 17.11.2026 12:10 (всего замен: 2)",
            FirmwareFileReplace.LastReplacementNote(history));
        // Замен не было — строки нет вовсе: «Файл заменён: —» не отвечает ни на какой вопрос.
        Assert.Equal("", FirmwareFileReplace.LastReplacementNote(Array.Empty<FwFileReplacement>()));
    }

    /// <summary>Журнала в папке версии не оказалось (легаси-сборка, файл удалили руками) — запись
    /// всё равно должна лечь: восстановить «кто подменил файл на сервере» больше неоткуда.</summary>
    [Fact]
    public void Без_журнала_запись_всё_равно_сохраняется()
    {
        using var root = new TempRoot();
        var versionDir = MakeVersion(root);
        File.Delete(Path.Combine(versionDir, ChangelogFile.FileName));

        var outcome = FirmwareFileReplace.Replace(versionDir,
            Touch(root.Path, "испр.lfs", "новая сборка"), "Киселёв", When);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.False(outcome.NoteMissing);
        Assert.Equal("Киселёв", Assert.Single(FirmwareFileReplace.History(versionDir)).By);
    }

    /// <summary>Неперестроенная версия (файлы прямо в папке версии, без «Прошивка\») — режим
    /// совместимости VersionLayout держится и здесь: сохранённое ложится рядом с тем, что заменили,
    /// а не в чужое место.</summary>
    [Fact]
    public void Старая_раскладка_версии_работает_так_же()
    {
        using var root = new TempRoot();
        var versionDir = Path.Combine(root.Path, "1.1.0004.0005");
        Touch(versionDir, "1.1.0004.0005.lfs");

        var outcome = FirmwareFileReplace.Replace(versionDir,
            Touch(root.Path, "испр.lfs", "новая сборка"), "Киселёв", When);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(Path.Combine(versionDir, VersionLayout.ReplacedFolderName,
            "1.1.0004.0005_20261116_1210.lfs.old"), outcome.BackupFile);
        Assert.Equal("новая сборка", File.ReadAllText(Path.Combine(versionDir, "1.1.0004.0005.lfs")));
    }
}
