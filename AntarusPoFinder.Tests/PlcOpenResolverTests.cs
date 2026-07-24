using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>«Какой файл откроет кнопка "Открыть прошивку ПЛК"». Проверяется именно порядок выбора:
/// на этом же ответе строится подпись кнопки (расширение в скобках), поэтому разъехаться они не
/// должны — раньше карточка угадывала расширение отдельно и умела только .psl/.lfs.</summary>
public class PlcOpenResolverTests
{
    [Fact]
    public void ExtensionOf_Folder_IsNull_EvenWhenNameHasDots()
    {
        using var root = new TempRoot();
        var versionFolder = Path.Combine(root.Path, "2.1.041");
        Directory.CreateDirectory(versionFolder);

        // Иначе кнопка получила бы подпись «(.041)» — имя папки версии целиком из точек.
        Assert.Null(PlcOpenResolver.ExtensionOf(versionFolder));
    }

    private static string Touch(string root, params string[] parts)
    {
        var path = Path.Combine(root, Path.Combine(parts));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private static PlcOpenSources Sources(string dir, string? hint = null) => new()
    {
        CandidateFolders = new[] { dir },
        VersionFolders = new[] { dir },
        FilteredFolders = new[] { dir },
        ExecutableHint = hint,
        NetworkFolder = dir,
    };

    [Fact]
    public void Hint_WinsOverEverythingElse()
    {
        using var root = new TempRoot();
        Touch(root.Path, "project.psl");
        var expected = Touch(root.Path, "Driver", "App.exe");

        var target = PlcOpenResolver.Resolve(Sources(root.Path, @"Driver\App.exe"));

        Assert.Equal(expected, target);
        Assert.Equal(".exe", PlcOpenResolver.ExtensionOf(target));
    }

    [Fact]
    public void Psl_PreferredOverLfs()
    {
        using var root = new TempRoot();
        var psl = Touch(root.Path, "project.psl");
        Touch(root.Path, "project.lfs");

        // .lfs не открывается ничем, кроме лоадера, — открывают исходник.
        Assert.Equal(psl, PlcOpenResolver.Resolve(Sources(root.Path)));
        Assert.Equal(".psl", PlcOpenResolver.ResolveExtension(Sources(root.Path)));
    }

    [Fact]
    public void PlcFile_PickedWhenPanelFileLiesNextToIt()
    {
        using var root = new TempRoot();
        Touch(root.Path, "panel.dpj");
        var plc = Touch(root.Path, "program.kpr");

        // Без этого шага общий детект «первый непонятный файл» мог открыть файл панели вместо ПЛК.
        Assert.Equal(plc, PlcOpenResolver.Resolve(Sources(root.Path)));
        Assert.Equal(".kpr", PlcOpenResolver.ResolveExtension(Sources(root.Path)));
    }

    [Fact]
    public void UnknownProject_StillReportsRealExtension()
    {
        using var root = new TempRoot();
        Touch(root.Path, "CHANGELOG.md");
        var project = Touch(root.Path, "cabinet.zip");

        // Главное в этом раунде: расширение пишется на кнопке для ЛЮБОГО проекта, а не только для
        // .psl/.lfs — сопроводительные файлы при этом за проект не принимаются.
        Assert.Equal(project, PlcOpenResolver.Resolve(Sources(root.Path)));
        Assert.Equal(".zip", PlcOpenResolver.ResolveExtension(Sources(root.Path)));
    }

    [Fact]
    public void EmptyFolder_FallsBackToFolderItself_NoExtension()
    {
        using var root = new TempRoot();

        // Показать содержимое папки полезнее, чем сказать «не найдено», но скобок на кнопке быть не
        // должно: открывается папка, а не файл.
        Assert.Equal(root.Path, PlcOpenResolver.Resolve(Sources(root.Path)));
        Assert.Null(PlcOpenResolver.ResolveExtension(Sources(root.Path)));
    }

    [Fact]
    public void NothingOnDisk_ResolvesToNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), "antarus-nope-" + Path.GetRandomFileName());

        Assert.Null(PlcOpenResolver.Resolve(Sources(missing)));
        Assert.Null(PlcOpenResolver.ResolveExtension(Sources(missing)));
    }

    [Fact]
    public void VersionFolders_NotSubstitutedByNeighbourVersion_ForPsl()
    {
        using var root = new TempRoot();
        var thisVersion = Path.Combine(root.Path, "1.0.001");
        var neighbour = Path.Combine(root.Path, "1.0.002");
        Directory.CreateDirectory(thisVersion);
        var neighbourPsl = Touch(neighbour, "project.psl");
        var ownFile = Touch(thisVersion, "program.kpr");

        // Соседняя версия годится как фоллбэк «чем открыть» (CandidateFolders), но её .psl не должен
        // выдаваться за файл этой версии — иначе откроется чужой проект.
        var target = PlcOpenResolver.Resolve(new PlcOpenSources
        {
            CandidateFolders = new[] { thisVersion, neighbour },
            VersionFolders = new[] { thisVersion },
            FilteredFolders = new[] { thisVersion },
            NetworkFolder = thisVersion,
        });

        Assert.Equal(ownFile, target);
        Assert.NotEqual(neighbourPsl, target);
    }

    [Theory]
    [InlineData("C:\\ПО\\project.PSL", ".psl")]
    [InlineData("C:\\ПО\\version", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtensionOf_LowercasesAndIgnoresExtensionless(string? path, string? expected) =>
        Assert.Equal(expected, PlcOpenResolver.ExtensionOf(path));
}
