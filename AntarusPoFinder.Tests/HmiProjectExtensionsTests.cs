using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Расширения панели — ОДИН список на всю программу.
///
/// Жалоба Киселёва 05.11.2026: «при обновлении новой версии ПО Finder не обнаружил файл HMI».
/// Воспроизвести было нечем, но при разборе нашлось расхождение, которое к такому исходу приводит
/// гарантированно: у карточки поиска был СВОЙ список расширений панели («KincoHmiExts» —
/// .dpj/.emt/.emtp/.emsln), а у того, кто проект реально открывает
/// (<see cref="PlcOpenResolver.HmiExtensions"/>), в списке есть ещё и .fsprj. Версия с проектом
/// FStudio в папке версии и пустым hmi_path получалась «без HMI» на карточке.
///
/// Доказательство, что .fsprj — полноправный проект панели, а не экзотика: именно он лежит в
/// дефолтном списке разрешённых расширений HMI, по которому загрузка пропускает вложение
/// (Database.SeedAllowedExtensionsHmiDefaults), и именно им подписан выбор файла панели в форме
/// загрузки (UploadView.HmiExecutableExts).</summary>
public class HmiProjectExtensionsTests
{
    [Theory]
    [InlineData("панель.fsprj")]
    [InlineData("панель.dpj")]
    [InlineData("панель.emt")]
    [InlineData("панель.emtp")]
    [InlineData("панель.emsln")]
    [InlineData(@"C:\ПО\НГР\XL\SMH5\2.4.1321.0005\HMI\2.4.1321.0005_hmi\ПАНЕЛЬ.FSPRJ")]
    public void ProjectFilesAreRecognised(string path) =>
        Assert.True(PlcOpenResolver.IsHmiProjectFile(path));

    [Theory]
    [InlineData("прошивка.lfs")]
    [InlineData("проект.psl")]
    [InlineData("CHANGELOG.md")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherFilesAreNot(string? path) =>
        Assert.False(PlcOpenResolver.IsHmiProjectFile(path));

    /// <summary>Сторожевой тест ровно на ту поломку: набор, который загрузка РАЗРЕШАЕТ класть как
    /// проект панели, обязан целиком опознаваться тем, кто решает «панель у версии есть».
    /// Разойдись они снова — файл можно будет загрузить, а на карточке его не будет видно.</summary>
    [Fact]
    public void EverythingUploadAcceptsIsRecognised()
    {
        foreach (var ext in new[] { "fsprj", "emt", "emtp", "emsln" })
            Assert.True(PlcOpenResolver.IsHmiProjectFile("проект." + ext), ext);
    }
}
