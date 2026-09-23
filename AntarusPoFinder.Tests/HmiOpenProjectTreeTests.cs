using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Кнопка «Открыть HMI проект» открывает ФАЙЛ проекта, а не папку.
///
/// Жалоба Ильи 23.09.2026: «у Kinco пожарок куда-то стал деваться файл .dpj для открытия. Указал его
/// и в качестве файла открытия, и в качестве файла HMI, но горит просто кнопка „открыть HMI" и
/// открывается папка, а не файл».
///
/// ⚠️ Причина найдена на НАСТОЯЩЕМ проекте с диска, а не выведена из головы: среда Kinco держит
/// вторую копию проекта в служебной папке temp —
///     УПД_MK070_v8-26\УПД_MK070_v8-26.dpj        ← настоящий
///     УПД_MK070_v8-26\temp\УПД_mk070_v8-26.dpj   ← копия среды
/// Автовыбор ищет файлы панели по всему дереву, находил ДВА и отвечал «выбрать не могу» — тогда
/// открывалась папка. Поэтому в образцах ниже папка temp обязательна: без неё тест зеленеет и
/// без исправления, что я и проверил (первая попытка этих тестов не ловила ничего).</summary>
public class HmiOpenProjectTreeTests
{
    /// <summary>Складывает дерево проекта Kinco так, как оно лежит на диске, вместе с копией в temp.</summary>
    private static string MakeKincoProject(string parent, string name)
    {
        var dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".dpj"), "проект");
        File.WriteAllText(Path.Combine(dir, name + ".pkgx"), "пакет");
        File.WriteAllText(Path.Combine(dir, name + ".bak"), "копия");
        Directory.CreateDirectory(Path.Combine(dir, "image"));
        Directory.CreateDirectory(Path.Combine(dir, "vg"));
        var temp = Path.Combine(dir, "temp");
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(temp, name.ToLowerInvariant() + ".dpj"), "копия среды");
        return dir;
    }

    [Fact]
    public void ProjectFolderStored_OpensTheEntryFile_NotTheFolder()
    {
        using var root = new TempRoot();
        var project = MakeKincoProject(root.Path, "УПД_MK070_v8-26");

        var target = HmiOpenResolver.Resolve(new HmiOpenSources { HmiPath = project });

        Assert.Equal(Path.Combine(project, "УПД_MK070_v8-26.dpj"), target);
    }

    /// <summary>У версии записана папка НАД проектом — общая «HMI». Тоже должен открыться файл.</summary>
    [Fact]
    public void FolderAboveStored_OpensTheEntryFile()
    {
        using var root = new TempRoot();
        var hmi = Path.Combine(root.Path, "HMI");
        Directory.CreateDirectory(hmi);
        var project = MakeKincoProject(hmi, "УПД_MK070_v8-26");

        var target = HmiOpenResolver.Resolve(new HmiOpenSources { HmiPath = hmi });

        Assert.Equal(Path.Combine(project, "УПД_MK070_v8-26.dpj"), target);
    }

    /// <summary>Проектов внутри два — выбрать за человека нельзя, открываем папку.</summary>
    [Fact]
    public void TwoProjectsInside_TheFolderOpens()
    {
        using var root = new TempRoot();
        var hmi = Path.Combine(root.Path, "HMI");
        Directory.CreateDirectory(hmi);
        MakeKincoProject(hmi, "первый");
        MakeKincoProject(hmi, "второй");

        Assert.Equal(hmi, HmiOpenResolver.Resolve(new HmiOpenSources { HmiPath = hmi }));
    }

    /// <summary>Указанный оператором файл важнее любых догадок: он выбирал его руками.</summary>
    [Fact]
    public void OperatorsChoice_WinsOverTheGuess()
    {
        using var root = new TempRoot();
        var project = MakeKincoProject(root.Path, "УПД_MK070_v8-26");
        File.WriteAllText(Path.Combine(project, "другой.dpj"), "второй проект");

        var target = HmiOpenResolver.Resolve(new HmiOpenSources
        {
            HmiPath = project,
            ExecutableHint = "другой.dpj",
        });

        Assert.Equal(Path.Combine(project, "другой.dpj"), target);
    }

    /// <summary>Обратная половина: одинокий файл панели, без всякого дерева, открывается как и
    /// раньше — иначе правка сломала бы всё, что лежит по-старому.</summary>
    [Fact]
    public void LoneProjectFile_StillOpens()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(root.Path, "HMI");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "панель.emt"), "проект");

        Assert.Equal(Path.Combine(folder, "панель.emt"),
            HmiOpenResolver.Resolve(new HmiOpenSources { HmiPath = folder }));
    }
}
