using System;
using System.IO;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Этикетка с QR на инструкцию и папка наклеек — две мелочи, у которых цена ошибки высокая:
/// битую ссылку в QR замечают уже на напечатанной наклейке у шкафа, а не в программе.</summary>
public class LabelAndStickersTests
{
    private static string Touch(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    // ── Ссылка в QR ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildUrl_EscapesCyrillicAndSpacesPerSegment()
    {
        var root = Path.Combine(Path.GetTempPath(), "disk");
        var file = Path.Combine(root, "ПО", "ПЖ ПИ", "SMH5", "1.0.0004.0003", "инструкция.pdf");

        var url = LabelLinkBuilder.BuildUrl("https://disk.antarus.su/instructions/", root, file);

        // Разделители остались слешами, а имена папок ушли закодированными — иначе «ПЖ ПИ» дало бы
        // битую ссылку, а экранирование строки целиком съело бы сами слеши.
        Assert.NotNull(url);
        Assert.StartsWith("https://disk.antarus.su/instructions/", url);
        Assert.Equal(5, url!["https://disk.antarus.su/instructions/".Length..].Split('/').Length);
        Assert.DoesNotContain(" ", url);
        Assert.Contains("%D0%9F%D0%96%20%D0%9F%D0%98", url);   // «ПЖ ПИ»
        Assert.EndsWith("/1.0.0004.0003/%D0%B8%D0%BD%D1%81%D1%82%D1%80%D1%83%D0%BA%D1%86%D0%B8%D1%8F.pdf", url);
    }

    [Fact]
    public void BuildUrl_NoBaseOrFileOutsideRoot_ReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "disk");
        var file = Path.Combine(root, "ПО", "инструкция.pdf");

        // Базовый адрес не задан — ссылки нет, вызывающий подставит сетевой путь.
        Assert.Null(LabelLinkBuilder.BuildUrl("", root, file));
        // Файл лежит вне корня диска — относительный путь не построить, чужой хвост подставлять нельзя.
        Assert.Null(LabelLinkBuilder.BuildUrl("https://x/y", root, Path.Combine(Path.GetTempPath(), "другой", "и.pdf")));
    }

    [Fact]
    public void RelativeTo_IgnoresCase_ButNotSiblingWithSamePrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "disk");

        Assert.Equal(Path.Combine("ПО", "и.pdf"),
            LabelLinkBuilder.RelativeTo(root.ToUpperInvariant(), Path.Combine(root, "ПО", "и.pdf")));
        // «disk2» не внутри «disk» — сравнение идёт по префиксу С РАЗДЕЛИТЕЛЕМ, иначе сосед с похожим
        // именем считался бы вложенным и ссылка собралась бы с чужим хвостом.
        Assert.Null(LabelLinkBuilder.RelativeTo(root, Path.Combine(root + "2", "и.pdf")));
    }

    // ── Наклейки ─────────────────────────────────────────────────────────────

    [Fact]
    public void StickersFolder_DefaultsToConfigSubfolder_AndHonoursRelativeAndAbsolute()
    {
        var root = Path.Combine(Path.GetTempPath(), "disk");

        Assert.Equal(Path.Combine(root, StickerTemplates.DefaultSubfolder), StickerTemplates.FolderFor(root, ""));
        Assert.Equal(Path.Combine(root, "Общее", "Наклейки"), StickerTemplates.FolderFor(root, @"Общее\Наклейки"));
        Assert.Equal(@"\\сервер\шара\Наклейки", StickerTemplates.FolderFor(root, @"\\сервер\шара\Наклейки"));
        // Диск не настроен и путь не абсолютный — показывать нечего (а не падать).
        Assert.Null(StickerTemplates.FolderFor("", ""));
    }

    [Fact]
    public void StickersList_SkipsShortcuts_IncludesSubfolders_AndSurvivesMissingFolder()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(root.Path, "Наклейки");
        Touch(folder, "Проверено ОТК.docx");
        Touch(Path.Combine(folder, "Предупреждения"), "Проверьте перед подключением.pdf");
        Touch(folder, "старая.docx.lnk");

        var files = StickerTemplates.List(folder);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => Path.GetFileName(f) == "Проверено ОТК.docx");
        Assert.Contains(files, f => Path.GetFileName(f) == "Проверьте перед подключением.pdf");
        Assert.DoesNotContain(files, f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));

        // Шара отвалилась — пустой список, а не исключение: окно наклеек из-за этого падать не должно.
        Assert.Empty(StickerTemplates.List(Path.Combine(root.Path, "нет-такой-папки")));
        Assert.Empty(StickerTemplates.List(null));
    }

    // ── Каноническое имя файла прошивки ──────────────────────────────────────

    [Fact]
    public void FirmwareFilename_EqualsVersionFolderName()
    {
        var withDate = FwVersionNumber.Parse("1.0.0004.0003.20260101_1200")!;
        var noDate = FwVersionNumber.Parse("1.0.0004.0003")!;

        // Имя файла = имя папки версии, буква в букву: раньше дата шла через «_» и регистр
        // поднимался до верхнего, из-за чего файл и папку нельзя было сверить глазами на диске.
        Assert.Equal("1.0.0004.0003.20260101_1200.psl", FirmwareNaming.BuildFirmwareFilename(withDate, ".psl"));
        Assert.Equal("1.0.0004.0003.lfs", FirmwareNaming.BuildFirmwareFilename(noDate, "lfs"));
        // ОПЦ-метки дописываются после номера версии и по-прежнему разбираются обратно.
        Assert.Equal("1.0.0004.0003_(01312)_SN00042.psl",
            FirmwareNaming.BuildFirmwareFilename(noDate, ".psl", "01312", "00042"));
    }
}
