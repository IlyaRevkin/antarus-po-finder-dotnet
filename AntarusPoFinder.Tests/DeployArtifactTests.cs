using AntarusPoFinder.Core.Loader;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ЧТО УХОДИТ В КОНТРОЛЛЕР.
///
/// Здесь записана история двух неверных догадок про окирпичивание SMH5 — чтобы третья не пошла по
/// тому же кругу. Сначала думали, что не хватает подготовки ПЛК (оказалось нет: «через лоджик и
/// через лоадер всё ок и без форматирования»), потом — что Finder заливает голый .lfs вместо
/// проекта (тоже нет: «в лоадере я заливаю только лфс», тот же файл руками работает).
///
/// Поэтому правило простое и совпадает с тем, как человек работает руками: заливаем собранный файл,
/// если он есть. Единственное, что добавилось от разбора, — программа научилась видеть .zop: это
/// проект целиком, и если он лежит в папке, выбирать между ним и половиной проекта не о чем.</summary>
public class DeployArtifactTests
{
    [Fact]
    public void Собранный_файл_предпочтительнее_исходного_проекта()
    {
        var choice = DeployArtifact.Pick(zopPath: null, pslPath: @"C:\v\p.psl", lfsPath: @"C:\v\p.lfs", controllerName: "SMH4");

        // Собранный .lfs — первый: именно его заливает человек руками, а пересборка изолированным
        // SMLogix дала бы в контроллер ДРУГОЙ файл, не тот, что проверил программист.
        Assert.Equal(@"C:\v\p.lfs", choice.Path);
    }

    /// <summary>.zop главнее всех: в нём проект целиком — и LFS, и ELF.</summary>
    [Fact]
    public void Zop_главнее_остальных()
    {
        var choice = DeployArtifact.Pick(@"C:\v\p.zop", @"C:\v\p.psl", @"C:\v\p.lfs", "SMH5");

        Assert.Equal(@"C:\v\p.zop", choice.Path);
    }

    /// <summary>Голый .lfs разрешён ВСЕМ, включая SMH5. Запрет, введённый 21.10.2026 по второй
    /// догадке, ломал ровно тот путь, которым и работают руками, — и был снят в тот же день, как
    /// только Илья сказал «в лоадере я заливаю только лфс».</summary>
    [Theory]
    [InlineData("SMH4")]
    [InlineData("SMH5")]
    [InlineData("PIXEL2-1321")]
    [InlineData("")]
    [InlineData(null)]
    public void Один_LFS_разрешён_всем(string? controller)
    {
        var choice = DeployArtifact.Pick(null, null, @"C:\p.lfs", controller);

        Assert.True(choice.Ok);
        Assert.Equal(@"C:\p.lfs", choice.Path);
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
