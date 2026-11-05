using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ИСПОЛНЕНИЯ КАК ПАПКИ ВНУТРИ ВЕРСИИ (docs/firmware-executions.md, решение от 04.11.2026).
///
/// Главное, что здесь проверяется, — не удобство, а два правила, нарушение которых стоит выезда на
/// объект:
/// <list type="number">
/// <item>выбрал собранное исполнение — получил ЕГО файл, а не базовый;</item>
/// <item>выбрал несобранное — получил базовую сборку И ПОМЕТКУ. Молчаливой подмены быть не должно:
///       это единственное условие, на котором Илья согласился выдавать базовую сборку вместо поиска
///       старой версии.</item>
/// </list>
/// И третье, незаметное: справочник исполнений ведёт ЧЕЛОВЕК. Папка, имени которой в справочнике
/// нет, исполнением не считается — иначе подпапки проекта KINCO (plc, hmi) стали бы «исполнениями»,
/// а убранное человеком исполнение воскресало бы от оставшейся на диске папки.</summary>
public class FirmwareExecutionFolderTests
{
    private static string Touch(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>Папка исполнения лежит ВНУТРИ «Прошивка\», а не рядом с ней: всё, что в корне
    /// версии подпапкой, перестройка диска считает частью проекта и утащила бы внутрь «Прошивка\».</summary>
    [Fact]
    public void Папка_исполнения_лежит_внутри_папки_прошивки()
    {
        var version = Path.Combine("C:", "ПО", "НГР", "ПЖ", "SMH4", "1.0.0005.0001");
        Assert.Equal(Path.Combine(version, "Прошивка", "3 насоса"),
            VersionLayout.ExecutionFolder(version, "3 насоса"));
        // Пустое исполнение — это и есть базовая папка, отдельной папки у него нет.
        Assert.Equal(VersionLayout.FirmwareFolder(version), VersionLayout.ExecutionFolder(version, ""));
    }

    /// <summary>Имя папки чистится от того, что Windows в имени не допускает, иначе исполнение
    /// «ПЧ Danfoss/ABB» просто не завелось бы на диске.</summary>
    [Fact]
    public void Имя_папки_чистится_от_недопустимых_символов()
    {
        Assert.Equal("ПЧ Danfoss ABB", VersionLayout.ExecutionFolderName("ПЧ Danfoss/ABB"));
        Assert.Equal("3 насоса", VersionLayout.ExecutionFolderName("  3   насоса . "));
        Assert.Equal("", VersionLayout.ExecutionFolderName("   "));
    }

    /// <summary>Собранным считается исполнение, под которое есть ПАПКА С ФАЙЛОМ и имя которого есть
    /// в справочнике. Пустая папка не считается: «папку завели, файл не положили» — ровно то
    /// состояние, в котором наладчик обязан получить базовую сборку с пометкой, а не пустоту.</summary>
    [Fact]
    public void Собранное_исполнение_узнаётся_только_по_справочнику_и_только_с_файлом()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "1.0.0005.0001");
        Touch(VersionLayout.FirmwareFolder(version), "1.0.0005.0001.lfs");
        Touch(VersionLayout.ExecutionFolder(version, "3 насоса"), "1.0.0005.0001.lfs");
        Directory.CreateDirectory(VersionLayout.ExecutionFolder(version, "2 насоса"));   // папка без файла
        Touch(Path.Combine(VersionLayout.FirmwareFolder(version), "plc"), "project.kpr"); // часть проекта

        var catalog = new[] { "2 насоса", "3 насоса" };
        var built = VersionLayout.BuiltExecutions(version, catalog);

        Assert.Equal(new[] { "3 насоса" }, built);
        // «plc» — подпапка проекта KINCO, и на диске она от исполнения ничем не отличается. Не
        // попадает она в исполнения ровно потому, что её нет в справочнике: признак — человек, а
        // не содержимое папки.
        Assert.DoesNotContain("plc", VersionLayout.BuiltExecutions(version, catalog));
        Assert.Contains("plc", VersionLayout.BuiltExecutions(version, new[] { "plc" }));
    }

    /// <summary>Выбрал собранное — получаешь ЕГО файл.</summary>
    [Fact]
    public void Выбранное_собранное_исполнение_даёт_свой_файл()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "1.0.0005.0001");
        var baseFile = Touch(VersionLayout.FirmwareFolder(version), "base.lfs");
        var ownFile = Touch(VersionLayout.ExecutionFolder(version, "3 насоса"), "pumps3.lfs");

        var target = FirmwareExecutionResolver.Resolve(new[] { version }, "3 насоса", new[] { "3 насоса" });

        Assert.True(target.Configured);
        Assert.Equal("", target.Warning);
        var found = LoaderFiles.FindDeploymentFiles(target.Folders, null, target.SkipFolders);
        Assert.Equal(ownFile, found.LfsPath);
        Assert.NotEqual(baseFile, found.LfsPath);
    }

    /// <summary>Выбрал то, чего в версии нет, — базовая сборка И ПОМЕТКА. В пометке названы оба
    /// выхода: создать конфигурацию или убрать исполнение у шкафа.</summary>
    [Fact]
    public void Несобранное_исполнение_даёт_базовую_сборку_с_пометкой()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "1.0.0005.0001");
        var baseFile = Touch(VersionLayout.FirmwareFolder(version), "base.lfs");
        Touch(VersionLayout.ExecutionFolder(version, "2 насоса"), "pumps2.lfs");

        var target = FirmwareExecutionResolver.Resolve(
            new[] { version }, "3 насоса", new[] { "2 насоса", "3 насоса" });

        Assert.False(target.Configured);
        Assert.True(target.Pick.FallsBackToBase);
        Assert.Contains("3 насоса", target.Warning);
        Assert.Contains("исходная", target.Warning);
        Assert.Contains("создайте конфигурацию", target.Warning);
        Assert.Contains("уберите исполнение", target.Warning);

        var found = LoaderFiles.FindDeploymentFiles(target.Folders, null, target.SkipFolders);
        Assert.Equal(baseFile, found.LfsPath);
    }

    /// <summary>Базовая сборка НИКОГДА не подменяется файлом чужого исполнения — даже когда базового
    /// файла нужного расширения в папке не оказалось. Поиск спускается во вложенные папки (ради
    /// проектов, разложенных подпапками), и без исключения папок исполнений в контроллер уехала бы
    /// чужая конфигурация молча.</summary>
    [Fact]
    public void Базовая_сборка_не_подменяется_файлом_исполнения()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "1.0.0005.0001");
        Touch(VersionLayout.FirmwareFolder(version), "base.psl");            // базовый только .psl
        Touch(VersionLayout.ExecutionFolder(version, "3 насоса"), "x.lfs");  // .lfs есть только у исполнения

        var target = FirmwareExecutionResolver.Resolve(new[] { version }, "", new[] { "3 насоса" });
        var found = LoaderFiles.FindDeploymentFiles(target.Folders, null, target.SkipFolders);

        Assert.Null(found.LfsPath);
        Assert.EndsWith("base.psl", found.PslPath);
    }

    /// <summary>Неперестроенная версия (файлы прямо в папке версии) продолжает работать ровно как
    /// раньше: исполнений нет — ничего не меняется.</summary>
    [Fact]
    public void Режим_совместимости_не_ломается()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "1.0.0005.0001");
        var legacy = Touch(version, "старая.lfs");

        var target = FirmwareExecutionResolver.Resolve(new[] { version }, "", new[] { "3 насоса" });
        var found = LoaderFiles.FindDeploymentFiles(target.Folders, null, target.SkipFolders);

        Assert.Equal(legacy, found.LfsPath);
        Assert.Empty(VersionLayout.BuiltExecutions(version, new[] { "3 насоса" }));
    }

    /// <summary>Положить конфигурацию исполнения в СУЩЕСТВУЮЩУЮ версию: новой версии и нового
    /// номера не появляется, базовая сборка не трогается — ради этого всё и затевалось.</summary>
    [Fact]
    public void Конфигурация_кладётся_в_ту_же_версию_и_не_трогает_базовую()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "1.0.0005.0001");
        var baseFile = Touch(VersionLayout.FirmwareFolder(version), "base.lfs");
        var source = Touch(Path.Combine(root.Path, "src"), "pumps3.lfs");

        var placed = FirmwareExecutionBuild.Place(version, "3 насоса", new[] { source });

        Assert.True(placed.Ok);
        Assert.Equal(VersionLayout.ExecutionFolder(version, "3 насоса"), placed.Folder);
        Assert.True(File.Exists(Path.Combine(placed.Folder, "pumps3.lfs")));
        Assert.True(File.Exists(baseFile));
        Assert.Equal(new[] { "3 насоса" }, VersionLayout.BuiltExecutions(version, new[] { "3 насоса" }));
        // Папка версии осталась одна: исполнение версию не создаёт.
        Assert.Single(Directory.GetDirectories(root.Path).Where(d => Path.GetFileName(d) == "1.0.0005.0001"));
    }

    /// <summary>Предпросмотр формы загрузки («куда ляжет») и сама укладка считают путь ОДНИМ и тем
    /// же кодом — FirmwareExecutionBuild.Prepare. Разойдись они, человек увидел бы один путь, а файл
    /// уехал бы по другому, причём в папку, которая уедет в шкаф.</summary>
    [Fact]
    public void Предпросмотр_показывает_ту_же_папку_куда_реально_ляжет()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "1.0.0005.0001");
        Touch(VersionLayout.FirmwareFolder(version), "base.lfs");
        var source = Touch(Path.Combine(root.Path, "src"), "pumps3.lfs");

        // Файлы ещё не выбраны — путь уже известен и показывается как есть.
        var preview = FirmwareExecutionBuild.Prepare(version, "3 насоса", null);
        Assert.True(preview.Ok);

        var placed = FirmwareExecutionBuild.Place(version, "3 насоса", new[] { source });
        Assert.True(placed.Ok);
        Assert.Equal(preview.Folder, placed.Folder);
        Assert.True(File.Exists(Path.Combine(preview.Folder, "pumps3.lfs")));
    }

    /// <summary>Предпросмотр не врёт и когда класть НЕКУДА: версия без папки на диске и невыбранное
    /// исполнение названы ошибкой, а не показаны правдоподобным путём.</summary>
    [Fact]
    public void Предпросмотр_называет_причину_вместо_выдуманного_пути()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "1.0.0005.0001");
        Directory.CreateDirectory(version);

        Assert.False(FirmwareExecutionBuild.Prepare(Path.Combine(root.Path, "нет такой"), "3 насоса", null).Ok);
        Assert.False(FirmwareExecutionBuild.Prepare(version, "", null).Ok);
        Assert.True(FirmwareExecutionBuild.Prepare(version, "3 насоса", null).Ok);
    }

    /// <summary>Непустая папка исполнения не перезаписывается молча: это то, что уедет в шкаф.</summary>
    [Fact]
    public void Существующая_конфигурация_не_затирается_без_согласия()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "1.0.0005.0001");
        Touch(VersionLayout.ExecutionFolder(version, "3 насоса"), "старая.lfs");
        var source = Touch(Path.Combine(root.Path, "src"), "новая.lfs");

        var first = FirmwareExecutionBuild.Place(version, "3 насоса", new[] { source });
        Assert.False(first.Ok);
        Assert.True(first.NeedsReplaceConfirmation);
        Assert.True(File.Exists(Path.Combine(first.Folder, "старая.lfs")));

        var second = FirmwareExecutionBuild.Place(version, "3 насоса", new[] { source }, replaceExisting: true);
        Assert.True(second.Ok);
        Assert.False(File.Exists(Path.Combine(second.Folder, "старая.lfs")));
        Assert.True(File.Exists(Path.Combine(second.Folder, "новая.lfs")));
    }

    /// <summary>В выборе на карточке — ВЕСЬ справочник шкафа, а не только собранное. Показывать
    /// лишь собранное значило бы, что наладчик не находит своего исполнения и идёт за прошлой
    /// версией — то есть получает устаревшую прошивку, ровно то, чего избегаем.</summary>
    [Fact]
    public void В_выборе_весь_справочник_шкафа()
    {
        var choices = FirmwareExecutionPick.Choices(new[] { "3 насоса", "3 Насоса", "", "2 насоса" });
        Assert.Equal(new[] { "", "3 насоса", "2 насоса" }, choices);
    }

    /// <summary>Справочник привязан к ШКАФУ: убрали «3 насоса» у КПЧ — у ПЖ оно осталось. Это и есть
    /// вопрос Ильи про КПЧ, и ответ на него — ручное удаление, а не вывод из содержимого папок.</summary>
    [Fact]
    public void Справочник_исполнений_у_подтипа_и_убирается_руками()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtypes = db.GetSubtypesForGroup(group.Id!.Value);
        var first = subtypes[0].Id!.Value;
        var second = subtypes.Count > 1 ? subtypes[1].Id!.Value : first;

        db.AddSubtypeExecution(first, "3 насоса");
        db.AddSubtypeExecution(first, "2 насоса");
        db.AddSubtypeExecution(first, "3 НАСОСА");   // то же самое, второй строкой не ложится
        if (second != first) db.AddSubtypeExecution(second, "3 насоса");

        Assert.Equal(new[] { "3 насоса", "2 насоса" }, db.GetSubtypeExecutions(first));

        db.DeleteSubtypeExecution(first, "3 насоса");
        Assert.Equal(new[] { "2 насоса" }, db.GetSubtypeExecutions(first));
        if (second != first) Assert.Equal(new[] { "3 насоса" }, db.GetSubtypeExecutions(second));
    }

    /// <summary>Исполнения шкафов ездят между машинами по ключу «группа/подтип|исполнение»: id
    /// подтипа у каждой машины свой, и отдать его значило бы раздать исполнения чужим шкафам.</summary>
    [Fact]
    public void Ключ_синхронизации_разбирается_обратно()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
        db.AddSubtypeExecution(subtype.Id!.Value, "3 насоса");

        var key = db.SubtypeExecutionKey(subtype.Id!.Value, "3 насоса");
        Assert.Equal($"{group.Name}/{subtype.Name}|3 насоса", key);
        Assert.Equal((subtype.Id!.Value, "3 насоса"), db.ParseSubtypeExecutionKey(key));
        // Шкафа с таким именем здесь нет — исполнение не заводим молча.
        Assert.Null(db.ParseSubtypeExecutionKey("НГР/нет такого|3 насоса"));
        Assert.Contains(key, db.GetSubtypeExecutionKeys());
    }
}
