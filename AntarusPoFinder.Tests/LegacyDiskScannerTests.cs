using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Разбор старого диска: что удаётся понять из пути и имени файла, накопленных до Финдера.
///
/// Стенд повторяет реальную раскладку, с которой всё и началось:
/// «1. ПЖ\1.1. Антарус 2.0\SMH4\пж_smh4_v4.31.16.pass.psl.zip». Ничего из угаданного не является
/// приговором — в окне разбора каждое поле правится руками, — но чем больше угадано, тем меньше
/// строк оператору придётся разбирать вручную.</summary>
public class LegacyDiskScannerTests
{
    private static readonly LegacyCatalog Catalog = new(
        Groups: new[] { "ПЖ", "НГР", "ТГР", "ВЗУ", "ШУЗ" },
        Subtypes: new[]
        {
            ("ПЖ", "2.0"), ("ПЖ", "FD"), ("ПЖ", "КПЧ"), ("ПЖ", "ХП"), ("ПЖ", "ПИ"), ("ПЖ", "ПКР"),
            ("НГР", "2.0"), ("НГР", "КНС"), ("НГР", "УПД"),
            ("ТГР", "—"),
        },
        Controllers: new[] { "SMH4", "SMH5", "SMH2010", "KINCO", "PIXEL", "PIXEL2", "FORTUS" });

    private static string Make(TempRoot root, string relativePath, string content = "firmware")
    {
        var full = Path.Combine(root.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void Scan_RecognizesGroupSubtypeAndControllerFromTheLegacyPath()
    {
        using var root = new TempRoot();
        Make(root, @"1. ПЖ\1.1. Антарус 2.0\SMH4\пж_smh4_v4.31.16.pass.psl.zip");

        var found = Assert.Single(LegacyDiskScanner.Scan(root.Path, Catalog));

        Assert.Equal("ПЖ", found.GroupName);
        Assert.Equal("2.0", found.SubtypeName);
        Assert.Equal("SMH4", found.ControllerName);
        Assert.Equal("4.31.16", found.VersionHint);
        Assert.True(found.IsArchive);
        Assert.True(found.FullyRecognized);
    }

    [Fact]
    public void Scan_KnowsTheLegacyNameOfEachSubtype()
    {
        using var root = new TempRoot();
        Make(root, @"1. ПЖ\1.2. F-Drive\SMH4\пж_fd_smh4.psl");
        Make(root, @"1. ПЖ\1.3. ПЖ-ХП\ПЖ-ХП_SMH4\пж_хп_smh4_v6.7.9.psl");
        Make(root, @"1. ПЖ\1.5. ПЖ-КПЧ\SMH4\пж_кпч.psl");

        var found = LegacyDiskScanner.Scan(root.Path, Catalog);

        Assert.Equal("FD", found.Single(f => f.RelativePath.Contains("F-Drive")).SubtypeName);
        Assert.Equal("ХП", found.Single(f => f.RelativePath.Contains("ПЖ-ХП")).SubtypeName);
        Assert.Equal("КПЧ", found.Single(f => f.RelativePath.Contains("ПЖ-КПЧ")).SubtypeName);
    }

    /// <summary>«ПИ» не должно находиться внутри «ПИКСЕЛЬ», а «PIXEL2» не должен читаться как
    /// «PIXEL»: совпадение ищется по границам слова и побеждает самое длинное.</summary>
    [Fact]
    public void Scan_MatchesWholeWordsAndPrefersTheLongestName()
    {
        using var root = new TempRoot();
        Make(root, @"1. ПЖ\ПИКСЕЛЬНАЯ ПАПКА\PIXEL2\прошивка.psl");

        var found = Assert.Single(LegacyDiskScanner.Scan(root.Path, Catalog));

        Assert.Equal("PIXEL2", found.ControllerName);
        Assert.NotEqual("ПИ", found.SubtypeName);
    }

    [Fact]
    public void Scan_ReadsRequestNumberFromTheOpcFolderName()
    {
        using var root = new TempRoot();
        Make(root, @"1. ПЖ\1.4. ОПЦ\13948 (7526) 3 уровня\SMH4\пж_smh4.psl");
        Make(root, @"1. ПЖ\1.4. ОПЦ\40289\SMH4\пж_smh4_v4.31.16.40289.pass.rar");

        var found = LegacyDiskScanner.Scan(root.Path, Catalog);

        var first = found.Single(f => f.RelativePath.Contains("13948"));
        Assert.Equal("13948", first.RequestNum);
        Assert.Equal("7526", first.CabinetSn);
        Assert.Equal("40289", found.Single(f => f.RelativePath.Contains("40289")).RequestNum);
    }

    [Fact]
    public void Scan_ReadsRequestAndSnFromTheFinderStyleFileName()
    {
        using var root = new TempRoot();
        Make(root, @"1. ПЖ\1.1. Антарус 2.0\SMH5\1.0.0005.0001_(01312)_SN00042.psl");

        var found = Assert.Single(LegacyDiskScanner.Scan(root.Path, Catalog));

        Assert.Equal("01312", found.RequestNum);
        Assert.Equal("00042", found.CabinetSn);
    }

    [Fact]
    public void Scan_MarksWhatLiesInTheArchiveFolder()
    {
        using var root = new TempRoot();
        Make(root, @"1. ПЖ\1.2. F-Drive\Архив\ПЖ_МК070_v4_25.7z");
        Make(root, @"1. ПЖ\1.2. F-Drive\SMH4\свежая.psl");

        var found = LegacyDiskScanner.Scan(root.Path, Catalog);

        Assert.True(found.Single(f => f.RelativePath.Contains("Архив")).InArchiveFolder);
        Assert.False(found.Single(f => f.RelativePath.Contains("свежая")).InArchiveFolder);
    }

    /// <summary>Всё, что не прошивка и не архив, в список не попадает: «История версий.txt» и
    /// «readme.txt» на старом диске лежат в каждой второй папке.</summary>
    [Fact]
    public void Scan_SkipsNotesAndJunk()
    {
        using var root = new TempRoot();
        Make(root, @"1. ПЖ\1.1. Антарус 2.0\SMH5\История версий.txt");
        Make(root, @"1. ПЖ\1.1. Антарус 2.0\SMH5\Thumbs.db");
        Make(root, @"1. ПЖ\1.1. Антарус 2.0\SMH5\прошивка.psl");

        var found = LegacyDiskScanner.Scan(root.Path, Catalog);

        Assert.Equal("прошивка.psl", Path.GetFileName(Assert.Single(found).FullPath));
    }

    /// <summary>Не понял — значит не понял: пустые поля и «не отмечать по умолчанию», а не
    /// правдоподобная выдумка.</summary>
    [Fact]
    public void Scan_UnrecognizedPath_LeavesTheFieldsEmpty()
    {
        using var root = new TempRoot();
        Make(root, @"Разное\что-то старое\прошивка.psl");

        var found = Assert.Single(LegacyDiskScanner.Scan(root.Path, Catalog));

        Assert.Equal("", found.GroupName);
        Assert.Equal("", found.SubtypeName);
        Assert.Equal("", found.ControllerName);
        Assert.False(found.FullyRecognized);
    }

    /// <summary>У типа с единственным подтипом («—» у ТГР) спрашивать нечего.</summary>
    [Fact]
    public void Scan_SingleSubtypeGroup_IsFilledInWithoutGuessing()
    {
        using var root = new TempRoot();
        Make(root, @"3. ТГР\SMH4\тгр.psl");

        var found = Assert.Single(LegacyDiskScanner.Scan(root.Path, Catalog));

        Assert.Equal("ТГР", found.GroupName);
        Assert.Equal("—", found.SubtypeName);
        Assert.True(found.FullyRecognized);
    }

    /// <summary>«2. КПЧ\…» — тип шкафа в старом пути не назван вовсе, назван только подтип. Если
    /// такой подтип есть ровно у одного типа, вопрос снят; у нескольких — молчим.</summary>
    [Fact]
    public void Scan_InfersTheGroupFromAnUnambiguousSubtype()
    {
        using var root = new TempRoot();
        Make(root, @"2. КПЧ\1.1. Антарус 2.0\КПЧ_SMH4_v3.41_Pass.psl");
        Make(root, @"4. КНС\SMH5\кнс.psl");

        var found = LegacyDiskScanner.Scan(root.Path, Catalog);

        // КПЧ есть только у ПЖ — тип подставлен.
        Assert.Equal("ПЖ", found.Single(f => f.RelativePath.Contains("КПЧ_SMH4")).GroupName);
        // КНС есть только у НГР — тоже.
        Assert.Equal("НГР", found.Single(f => f.RelativePath.Contains("кнс.psl")).GroupName);
    }

    /// <summary>Папка-год в пути ОПЦ («…\ОПЦ\SMH\2025\…») — не номер заявки.</summary>
    [Fact]
    public void Scan_YearFolder_IsNotMistakenForARequestNumber()
    {
        using var root = new TempRoot();
        Make(root, @"1. ПЖ\1.4. ОПЦ\SMH\2025\КПЧ_SMH4_v3.39.psl");
        Make(root, @"1. ПЖ\1.4. ОПЦ\SMH\2025\41845 наполнение резервуара\КПЧ_SMH4.psl");

        var found = LegacyDiskScanner.Scan(root.Path, Catalog);

        Assert.Equal("", found.Single(f => f.RelativePath.EndsWith("v3.39.psl")).RequestNum);
        Assert.Equal("41845", found.Single(f => f.RelativePath.Contains("41845")).RequestNum);
    }

    /// <summary>«Инструкция.zip» лежит рядом с прошивками и по расширению от них не отличается —
    /// в список попадает, но по умолчанию не отмечается.</summary>
    [Fact]
    public void Scan_DocumentLookingArchive_IsShownButNotTakenByDefault()
    {
        using var root = new TempRoot();
        Make(root, @"1. ПЖ\1.5. ПЖ-КПЧ\SMH4\Инструкция.zip");
        Make(root, @"1. ПЖ\1.5. ПЖ-КПЧ\SMH4\пж_smh4_v4.31.16.psl");

        var found = LegacyDiskScanner.Scan(root.Path, Catalog);

        var doc = found.Single(f => f.RelativePath.Contains("Инструкция"));
        Assert.True(doc.LooksLikeDocument);
        Assert.False(doc.WorthTakingByDefault);

        var firmware = found.Single(f => f.RelativePath.Contains("пж_smh4"));
        Assert.False(firmware.LooksLikeDocument);
        Assert.True(firmware.WorthTakingByDefault);
    }

    /// <summary>Лежащее в «Архиве» тоже не отмечается само: на старом диске так помечали заведомо
    /// устаревшее, и тащить его в общий диск по умолчанию не нужно.</summary>
    [Fact]
    public void Scan_ArchiveFolder_IsNotTakenByDefault()
    {
        using var root = new TempRoot();
        Make(root, @"1. ПЖ\1.1. Антарус 2.0\SMH4\Архив\пж_smh4_v4.31.11.psl");

        Assert.False(Assert.Single(LegacyDiskScanner.Scan(root.Path, Catalog)).WorthTakingByDefault);
    }

    [Fact]
    public void GuessVersion_TakesTheDottedNumberOutOfTheName()
    {
        Assert.Equal("4.31.16", LegacyDiskScanner.GuessVersion("пж_smh4_v4.31.16.pass.psl"));
        Assert.Equal("6.7.9", LegacyDiskScanner.GuessVersion("пж_хп_smh4_v6.7.9.pass.psl.zip"));
        Assert.Equal("", LegacyDiskScanner.GuessVersion("прошивка.psl"));
    }
}
