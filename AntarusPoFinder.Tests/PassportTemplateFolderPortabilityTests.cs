using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Папка бланков паспортов задаётся один раз в Настройки → Печать и должна работать у всех,
/// а не только у того, кто её выбрал: настройка синхронизируемая, а сетевой диск у каждой машины
/// подключён своей буквой. Правило то же, что у наклеек (SharedFolderPath.ToPortable): выбранная
/// обзором абсолютная папка внутри общего диска сохраняется ХВОСТОМ от его корня.
///
/// Проверка заведена отдельно потому, что ошибиться здесь легко и незаметно: пока обзор писал
/// «Z:\Конфиг\Паспорта», у коллеги с той же шарой под «Y:» бланков не было вовсе, и выглядело это
/// как «программа их потеряла», а не как разъехавшаяся настройка.</summary>
public class PassportTemplateFolderPortabilityTests
{
    private static readonly string Disk = Path.Combine(Path.GetTempPath(), "antarus_disk_root");

    /// <summary>Папка внутри общего диска — остаётся хвост, буква не сохраняется.</summary>
    [Fact]
    public void AFolderInsideTheSharedDisk_IsStoredRelativeToItsRoot()
    {
        var chosen = Path.Combine(Disk, "Общее", "Бланки");

        var stored = SharedFolderPath.ToPortable(Disk, chosen, PassportService.DefaultTemplatesSubfolder);

        Assert.Equal(Path.Combine("Общее", "Бланки"), stored);
        Assert.Equal(chosen, PassportService.TemplatesFolder(Disk, stored));
    }

    /// <summary>Та же папка, но диск подключён другой буквой — путь всё равно сходится: в этом весь
    /// смысл хранить хвост.</summary>
    [Fact]
    public void TheSameSetting_ResolvesOnAMachineWithADifferentDriveLetter()
    {
        var stored = SharedFolderPath.ToPortable(Disk, Path.Combine(Disk, "Общее", "Бланки"),
            PassportService.DefaultTemplatesSubfolder);

        var elsewhere = Path.Combine(Path.GetTempPath(), "antarus_disk_root_other");
        Assert.Equal(Path.Combine(elsewhere, "Общее", "Бланки"), PassportService.TemplatesFolder(elsewhere, stored));
    }

    /// <summary>Выбрали ровно папку по умолчанию — настройка сворачивается в пустую строку: это то же
    /// самое место, но записанное как «настройку не трогали».</summary>
    [Fact]
    public void ChoosingTheDefaultFolder_StoresNothingAtAll()
    {
        var stored = SharedFolderPath.ToPortable(Disk,
            Path.Combine(Disk, PassportService.DefaultTemplatesSubfolder), PassportService.DefaultTemplatesSubfolder);

        Assert.Equal("", stored);
        Assert.Equal(Path.Combine(Disk, PassportService.DefaultTemplatesSubfolder),
            PassportService.TemplatesFolder(Disk, stored));
    }

    /// <summary>Папка вне общего диска (локальная, чужая шара) остаётся абсолютной — сокращать её
    /// не от чего.</summary>
    [Fact]
    public void AFolderOutsideTheSharedDisk_StaysAbsolute()
    {
        var outside = Path.Combine(Path.GetTempPath(), "antarus_outside", "Бланки");

        Assert.Equal(outside, SharedFolderPath.ToPortable(Disk, outside, PassportService.DefaultTemplatesSubfolder));
    }

    /// <summary>Правило у бланков и наклеек одно и то же — и должно таким остаться: это две половины
    /// одной общей папки настроек, и разъехавшись, они начали бы «теряться» по-разному.</summary>
    [Fact]
    public void BlanksAndStickers_FollowTheExactSameRule()
    {
        var chosen = Path.Combine(Disk, "Общее", "Что-то");

        Assert.Equal(
            SharedFolderPath.ToPortable(Disk, chosen, StickerTemplates.DefaultSubfolder),
            SharedFolderPath.ToPortable(Disk, chosen, PassportService.DefaultTemplatesSubfolder));
    }
}
