using AntarusPoFinder.Core.Loader;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ЧТО УХОДИТ В КОНТРОЛЛЕР — ПРОЕКТ, А НЕ ПОЛОВИНА ПРОЕКТА.
///
/// Настоящая причина окирпичивания SMH5 (подтверждена Ильёй 21.10.2026: «через лоджик и через
/// лоадер всё ок и без форматирования»). Finder выбирал файл правилом «LFS, если есть, иначе PSL» —
/// то есть предпочитал уже собранный LFS. Loader по типу файла строит разный план: из .psl он
/// соберёт и зальёт проект, из .zop зальёт обе части (LFS и ELF), а из .lfs — только LFS. У SMH5
/// проект двухчастный, и от одного LFS в контроллере остаётся чужая ELF-часть: он доработает до
/// снятия питания и уйдёт в бесконечную перезагрузку.
///
/// Тесты закрепляют не «SMH5 особенный», а общее правило: заливаем то, что несёт проект целиком.</summary>
public class DeployArtifactTests
{
    [Fact]
    public void Проект_целиком_предпочтительнее_собранного_файла()
    {
        var choice = DeployArtifact.Pick(zopPath: null, pslPath: @"C:\v\p.psl", lfsPath: @"C:\v\p.lfs", controllerName: "SMH4");

        Assert.Equal(@"C:\v\p.psl", choice.Path);
    }

    /// <summary>.zop главнее .psl: в нём обе части уже собраны, пересборка изолированным SMLogix
    /// не нужна — а значит и не может разойтись с тем, что проверял программист.</summary>
    [Fact]
    public void Zop_главнее_psl()
    {
        var choice = DeployArtifact.Pick(@"C:\v\p.zop", @"C:\v\p.psl", @"C:\v\p.lfs", "SMH5");

        Assert.Equal(@"C:\v\p.zop", choice.Path);
    }

    /// <summary>Старым семействам привычный путь оставлен: у них ELF-части нет, одним LFS заливали
    /// годами, и запрещать это из-за беды SMH5 значило бы сломать работу там, где всё исправно.</summary>
    [Theory]
    [InlineData("SMH4")]
    [InlineData("PIXEL2-1321")]
    [InlineData("")]
    [InlineData(null)]
    public void Один_LFS_остальным_разрешён(string? controller)
    {
        var choice = DeployArtifact.Pick(null, null, @"C:\v\p.lfs", controller);

        Assert.True(choice.Ok);
        Assert.Equal(@"C:\v\p.lfs", choice.Path);
    }

    /// <summary>А вот SMH5 с одним только LFS заливать нечем — и программа обязана сказать это ДО
    /// подключения к контроллеру, а не оставить человека с кирпичом и догадками.</summary>
    [Theory]
    [InlineData("SMH5")]
    [InlineData("smh5")]
    [InlineData("SMH5-0000")]
    public void Для_SMH5_один_LFS_запрещён_и_объяснён(string controller)
    {
        var choice = DeployArtifact.Pick(null, null, @"C:\v\p.lfs", controller);

        Assert.False(choice.Ok);
        Assert.Contains("ELF", choice.Problem);
        Assert.Contains(".psl", choice.Problem);
    }

    [Fact]
    public void Когда_нет_ничего_это_тоже_внятная_причина()
    {
        var choice = DeployArtifact.Pick(null, null, null, "SMH4");

        Assert.False(choice.Ok);
        Assert.Contains("нет", choice.Problem);
    }

    /// <summary>В журнале операции должно быть видно, ЧЕМ заливали: это первый вопрос, когда
    /// контроллер повёл себя странно.</summary>
    [Fact]
    public void Журнал_называет_файл_и_что_в_нём()
    {
        Assert.Contains("обе части", DeployArtifact.LogLine(@"C:\v\p.zop"));
        Assert.Contains("соберёт", DeployArtifact.LogLine(@"C:\v\p.psl"));
        Assert.Contains("только LFS", DeployArtifact.LogLine(@"C:\v\p.lfs"));
    }
}
