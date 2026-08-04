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

    /// <summary>Жалоба «папка наклеек должна синхрониться тоже, она общая, только буква разная».
    /// Обзор папки всегда отдаёт абсолютный путь с буквой ЭТОЙ машины, а настройка уезжает
    /// синхронизацией дословно — записав «Z:\Конфиг\Наклейки», мы прятали бы папку от всех, у кого та
    /// же шара подключена под другой буквой. Хранится хвост от корня диска.</summary>
    [Fact]
    public void ToPortable_KeepsOnlyTheTailInsideTheSharedDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "Z_disk");

        // Внутри диска — остаётся хвост, и он сходится на машине с другой буквой.
        Assert.Equal(Path.Combine("Общее", "Наклейки"),
            SharedFolderPath.ToPortable(root, Path.Combine(root, "Общее", "Наклейки"), StickerTemplates.DefaultSubfolder));

        // Ровно подпапка по умолчанию — сворачивается в «настройку не трогали».
        Assert.Equal("", SharedFolderPath.ToPortable(root,
            Path.Combine(root, StickerTemplates.DefaultSubfolder), StickerTemplates.DefaultSubfolder));

        // Снаружи диска (сетевая шара, локальная папка) — остаётся как есть: хвоста от корня нет.
        Assert.Equal(@"\\сервер\шара\Наклейки",
            SharedFolderPath.ToPortable(root, @"\\сервер\шара\Наклейки", StickerTemplates.DefaultSubfolder));
        // Диск не настроен — трогать нечего.
        Assert.Equal(@"D:\Наклейки", SharedFolderPath.ToPortable("", @"D:\Наклейки", StickerTemplates.DefaultSubfolder));
    }

    /// <summary>Сохранённый хвост должен разворачиваться обратно в ту же папку на любой машине — это
    /// и есть весь смысл ToPortable, поэтому проверяется парой с Resolve/FolderFor.</summary>
    [Fact]
    public void ToPortable_RoundTripsThroughStickerFolderResolution()
    {
        var mine = Path.Combine(Path.GetTempPath(), "Z_disk");
        var colleague = Path.Combine(Path.GetTempPath(), "Y_disk");

        var stored = SharedFolderPath.ToPortable(mine, Path.Combine(mine, "Общее", "Наклейки"),
            StickerTemplates.DefaultSubfolder);

        Assert.Equal(Path.Combine(colleague, "Общее", "Наклейки"), StickerTemplates.FolderFor(colleague, stored));
    }

    /// <summary>ToPortable чинит настройку только в момент выбора папки — а значение, УЖЕ уехавшее
    /// синхронизацией абсолютным («Z:\Конфиг\Наклейки»), у коллеги с буквой «Y:» так и осталось бы
    /// битым до тех пор, пока администратор не переназначит папку заново. Поэтому чужая буква
    /// спасается на чтении: записанной папки нет — ищем такую же под СВОИМ корнем диска.</summary>
    [Fact]
    public void Resolve_RescuesAbsolutePathWrittenByAnotherMachine()
    {
        using var root = new TempRoot();
        var mine = Path.Combine(root.Path, "Y_disk");
        var real = Path.Combine(mine, "Конфиг", "Наклейки");
        Directory.CreateDirectory(real);

        // Путь коллеги: та же шара, другая буква и другой промежуточный сегмент — ведущие сегменты
        // отбрасываются по одному, пока не найдётся существующая папка.
        Assert.Equal(real, StickerTemplates.FolderFor(mine, @"Z:\Antarus\Конфиг\Наклейки"));

        // Своя же папка существует — спасать нечего, путь остаётся дословным.
        Assert.Equal(real, StickerTemplates.FolderFor(mine, real));

        // Ничего похожего под своим корнем нет — подставлять чужую папку нельзя, остаётся как
        // записано: «папка недоступна» честнее молчаливой подмены.
        Assert.Equal(@"Z:\Совсем\Другое", StickerTemplates.FolderFor(mine, @"Z:\Совсем\Другое"));
    }

    // ── Макет этикетки ───────────────────────────────────────────────────────

    /// <summary>«Что 97, что 90, что 100 ставлю — верх обрезается»: причина была в нулевых полях, а
    /// не в размере. Поле по умолчанию — 3 мм, с запасом больше непечатаемой зоны.</summary>
    [Fact]
    public void LabelLayout_DefaultsHaveMarginsAndReadableCaption()
    {
        var layout = new LabelLayout();

        Assert.Equal(3, layout.MarginMm);
        Assert.True(layout.CaptionPt >= 9, "кегль ссылки ниже 9 pt на 203 dpi кириллицей разваливается");
        Assert.True(layout.ShowLink);
    }

    /// <summary>Значения приводятся к рабочему диапазону при КАЖДОМ чтении — в настройки может
    /// приехать что угодно, в том числе синхронизацией с чужой машины.</summary>
    [Fact]
    public void LabelLayout_Clamped_KeepsLabelPhysicallyPossible()
    {
        var v = new LabelLayout
        {
            WidthMm = 5000, HeightMm = -10, MarginMm = 999,
            OffsetXMm = 100, OffsetYMm = -100, TitlePt = 500, CaptionPt = 0,
        }.Clamped();

        Assert.InRange(v.WidthMm, 20, 300);
        Assert.InRange(v.HeightMm, 15, 300);
        // Поля не съедают больше четверти меньшей стороны — иначе содержимому не осталось бы места.
        Assert.InRange(v.MarginMm, 0, Math.Min(v.WidthMm, v.HeightMm) / 4);
        Assert.InRange(v.OffsetXMm, -20, 20);
        Assert.InRange(v.OffsetYMm, -20, 20);
        Assert.InRange(v.TitlePt, 6, 48);
        Assert.InRange(v.CaptionPt, 5, 24);
    }

    /// <summary>Сторона QR всегда помещается внутрь полей — и когда её считают сами, и когда её задал
    /// человек. Иначе код уезжал бы за край ровно так же, как раньше уезжал верх.</summary>
    [Fact]
    public void LabelLayout_EffectiveQr_FitsInsideMargins()
    {
        var auto = new LabelLayout { WidthMm = 97.5, HeightMm = 72, MarginMm = 3 }.Clamped();
        Assert.InRange(auto.EffectiveQrMm(), 8, auto.WidthMm - 2 * auto.MarginMm);
        Assert.True(auto.EffectiveQrMm() <= auto.HeightMm - 2 * auto.MarginMm);

        var huge = new LabelLayout { WidthMm = 60, HeightMm = 40, MarginMm = 4, QrMm = 200 }.Clamped();
        Assert.True(huge.EffectiveQrMm() <= huge.WidthMm - 2 * huge.MarginMm);
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
