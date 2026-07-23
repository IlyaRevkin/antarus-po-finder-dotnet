using AntarusPoFinder.Core.Loader;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>«Не понятно, есть LFS или нет, то же самое с PSL» — карточка теперь пишет это явно, но
/// только там, где эти файлы бывают: .psl/.lfs — формат Segnetics, у шкафа на KINCO прочерк
/// означал бы потерянный файл. См. SegneticsProject.</summary>
public class SegneticsProjectTests
{
    [Theory]
    [InlineData("SMH4")]
    [InlineData("SMH5")]
    [InlineData("PIXEL")]
    [InlineData("PIXEL2-1320")]
    [InlineData("pxl2")]
    [InlineData("TRIM5")]
    public void SegneticsControllers_AreRelevant(string controller) =>
        Assert.True(SegneticsProject.IsRelevant(controller, ""));

    [Theory]
    [InlineData("KINCO")]
    [InlineData("")]
    [InlineData(null)]
    // Незнакомый контроллер (справочник пополняет администратор) — считаем «не Segnetics»:
    // если .psl/.lfs там всё-таки окажутся, признак включится по факту находки на диске.
    [InlineData("OWEN ПЛК110")]
    public void NonSegneticsControllers_AreNotRelevant(string? controller) =>
        Assert.False(SegneticsProject.IsRelevant(controller, ""));

    [Fact]
    public void FoundFilesWin_EvenOnAnUnknownController()
    {
        Assert.True(SegneticsProject.IsRelevant("OWEN", "", foundLfs: true));
        Assert.True(SegneticsProject.IsRelevant("OWEN", "", foundPsl: true));
    }

    [Fact]
    public void ExecutableHintWithPslExtension_IsEnough() =>
        Assert.True(SegneticsProject.IsRelevant("самодельный", "ШУЗ-1.psl"));

    [Fact]
    public void ExecutableHintOfAnotherKind_DoesNotMakeItSegnetics() =>
        Assert.False(SegneticsProject.IsRelevant("KINCO", "project.kpj"));
}
