using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Кнопка «Открыть HMI проект»: что откроется и какое расширение написано на кнопке — одно
/// решение, как и у ПЛК (см. PlcOpenResolverTests). Раньше расширение на кнопке панели не писалось
/// вовсе, а порядок вариантов открытия жил прямо в SearchView.OpenHmi.</summary>
public class HmiOpenResolverTests
{
    private static string Touch(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void SeparateProjectFolder_WithoutHint_OpensFolderItself_NoExtensionOnButton()
    {
        using var root = new TempRoot();
        var hmi = Path.Combine(root.Path, "2.1.041_hmi");
        Touch(hmi, "panel.dpj");

        var src = new HmiOpenSources { HmiPath = hmi };
        Assert.Equal(hmi, HmiOpenResolver.Resolve(src));
        // Открывается папка — расширения на кнопке быть не должно, пустые скобки хуже отсутствия.
        Assert.Null(HmiOpenResolver.ResolveExtension(src));
    }

    [Fact]
    public void SeparateProjectFolder_WithHint_OpensHintedFile()
    {
        using var root = new TempRoot();
        var hmi = Path.Combine(root.Path, "2.1.041_hmi");
        Touch(hmi, "panel.dpj");
        var nested = Touch(hmi, @"Project\panel.emt");

        var src = new HmiOpenSources { HmiPath = hmi, ExecutableHint = @"Project\panel.emt" };
        Assert.Equal(nested, HmiOpenResolver.Resolve(src));
        Assert.Equal(".emt", HmiOpenResolver.ResolveExtension(src));
    }

    [Fact]
    public void StoredFolderGone_FallsBackToSiblingHmiFolder()
    {
        using var root = new TempRoot();
        var sibling = Path.Combine(root.Path, "HMI");
        Touch(sibling, "panel.dpj");

        Assert.Equal(sibling, HmiOpenResolver.Resolve(new HmiOpenSources
        {
            HmiPath = Path.Combine(root.Path, "нет такой папки_hmi"),
            SiblingHmiFolder = sibling,
        }));
    }

    [Fact]
    public void StoredPathGone_AndNoSibling_ResolvesToNull_NoSubstitution()
    {
        using var root = new TempRoot();
        // Рядом с версией лежит панель другого проекта — подменять ею записанный, но исчезнувший путь
        // нельзя: оператор открыл бы чужой проект, думая, что открывает свой.
        var version = Path.Combine(root.Path, "2.1.041");
        Touch(version, "other-panel.dpj");

        Assert.Null(HmiOpenResolver.Resolve(new HmiOpenSources
        {
            HmiPath = Path.Combine(root.Path, "нет такой папки_hmi"),
            FilteredFolders = new[] { version },
        }));
    }

    [Fact]
    public void NoSeparateProject_HintInsideVersionFolder_Wins()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "2.1.041");
        Touch(version, "plc.kpr");
        var panel = Touch(version, "panel.dpj");

        var src = new HmiOpenSources
        {
            ExecutableHint = "panel.dpj",
            CandidateFolders = new[] { version },
            FilteredFolders = new[] { version },
        };
        Assert.Equal(panel, HmiOpenResolver.Resolve(src));
        Assert.Equal(".dpj", HmiOpenResolver.ResolveExtension(src));
    }

    [Fact]
    public void NoSeparateProject_NoHint_DetectsByExtension()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "2.1.041");
        Touch(version, "plc.kpr");
        var panel = Touch(version, @"Panel\hmi.emtp");

        var src = new HmiOpenSources { FilteredFolders = new[] { version } };
        Assert.Equal(panel, HmiOpenResolver.Resolve(src));
        Assert.Equal(".emtp", HmiOpenResolver.ResolveExtension(src));
    }

    [Fact]
    public void NothingAnywhere_ResolvesToNull()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "2.1.041");
        Touch(version, "plc.kpr");

        Assert.Null(HmiOpenResolver.Resolve(new HmiOpenSources { FilteredFolders = new[] { version } }));
        Assert.Null(HmiOpenResolver.Resolve(new HmiOpenSources()));
    }
}
