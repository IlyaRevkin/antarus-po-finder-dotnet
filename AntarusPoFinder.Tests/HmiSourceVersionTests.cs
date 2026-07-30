using AntarusPoFinder.App.Views;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Tests;

/// <summary>Карточка прошивки помечает HMI-проект «от версии X», когда панель унаследована от другой
/// сборки (имя папки «{X}_hmi» ≠ текущей версии). Но после hw-переписывания (напр. 2.4.044.0005 →
/// 2.4.1321.0005) папку панели переименовывают не всегда — правка hw могла не доиграть, диск быть
/// офлайн, либо старую версию удалили руками до того, как переименование доехало синхроном. Тогда у
/// живой прошивки нового hw панель «{старый hw}_hmi» остаётся, и карточка навсегда пишет «HMI от
/// {старый hw}», хотя программа та же самая — сменилась только аппаратная цифра. Это ровно повторная
/// жалоба Ильи. FirmwareCard.HmiSourceVersion гасит такой случай (различие ТОЛЬКО в hw → null →
/// «HMI ✓»), но настоящее наследование от другой сборки (иной sw/дата) по-прежнему помечает.</summary>
public class HmiSourceVersionTests
{
    private static HierarchyResult Result(string versionRaw, string hmiFolderName) => new()
    {
        VersionRaw = versionRaw,
        HmiPath = hmiFolderName.Length == 0 ? "" : $@"\\srv\Software\ПО\PIXEL2\HMI\{hmiFolderName}",
    };

    [Fact]
    public void HmiFromSameVersion_ReturnsNull()
    {
        Assert.Null(FirmwareCard.HmiSourceVersion(Result("2.4.1321.0005", "2.4.1321.0005_hmi")));
    }

    [Fact]
    public void HmiDiffersOnlyInHw_ReturnsNull_TreatedAsSameFirmware()
    {
        // Панель осталась со старым hw (044), прошивка уже 1321 — различие только в аппаратной цифре.
        Assert.Null(FirmwareCard.HmiSourceVersion(Result("2.4.1321.0005", "2.4.044.0005_hmi")));
    }

    [Fact]
    public void HmiDiffersOnlyInHw_WithDateSuffix_ReturnsNull()
    {
        Assert.Null(FirmwareCard.HmiSourceVersion(
            Result("2.4.1321.0005.20260710_1200", "2.4.044.0005.20260710_1200_hmi")));
    }

    [Fact]
    public void HmiFromEarlierSwBuild_StillLabelled()
    {
        // Настоящее наследование: панель от прошлой sw-сборки (0004 против 0005) — «от версии» уместно.
        Assert.Equal("2.4.1321.0004", FirmwareCard.HmiSourceVersion(Result("2.4.1321.0005", "2.4.1321.0004_hmi")));
    }

    [Fact]
    public void HmiFromDifferentDateSuffix_StillLabelled()
    {
        Assert.Equal("2.4.1321.0005.20260101_0900", FirmwareCard.HmiSourceVersion(
            Result("2.4.1321.0005.20260710_1200", "2.4.1321.0005.20260101_0900_hmi")));
    }

    [Fact]
    public void EmptyHmiPath_ReturnsNull()
    {
        Assert.Null(FirmwareCard.HmiSourceVersion(Result("2.4.1321.0005", "")));
    }

    [Fact]
    public void UnparseableFolderVersion_KeepsOldBehaviour_Labelled()
    {
        // Не разбирается как номер версии — гасить нечем, честно показываем как есть (как раньше).
        Assert.Equal("legacy-panel", FirmwareCard.HmiSourceVersion(Result("2.4.1321.0005", "legacy-panel_hmi")));
    }
}
