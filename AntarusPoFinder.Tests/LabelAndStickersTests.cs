using System;
using System.IO;
using System.Linq;
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
    public void BuildUrl_KeepsCyrillicReadable_AndEscapesOnlySpaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "disk");
        var file = Path.Combine(root, "ПО", "ПЖ ПИ", "SMH5", "1.0.0004.0003", "инструкция_1.0.0004.0003.pdf");

        var url = LabelLinkBuilder.BuildUrl("https://disk.antarus.su/instructions/", root, file);

        // Разделители остались слешами, а кириллица — кириллицей: процентное кодирование раздувало
        // каждую букву до шести символов, ссылка под кодом становилась лапшой, а QR — втрое плотнее.
        Assert.NotNull(url);
        Assert.StartsWith("https://disk.antarus.su/instructions/", url);
        Assert.Equal(5, url!["https://disk.antarus.su/instructions/".Length..].Split('/').Length);
        Assert.Contains("/ПО/", url);
        Assert.EndsWith("/1.0.0004.0003/инструкция_1.0.0004.0003.pdf", url);
        // Пробел всё равно кодируется — иначе адрес разваливается при вставке в браузер.
        Assert.DoesNotContain(" ", url);
        Assert.Contains("ПЖ%20ПИ", url);
    }

    [Fact]
    public void BuildUrl_CyrillicLinkIsMuchShorterThanPercentEncodedOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "disk");
        var file = Path.Combine(root, "ПО", "НГР", "КНС", "SMH5", "2.1.0042.0001.20260422_1348",
            "Инструкция", "инструкция_2.1.0042.0001.20260422_1348.pdf");

        var url = LabelLinkBuilder.BuildUrl("https://disk.antarus.su/i/", root, file)!;
        var old = "https://disk.antarus.su/i/" + string.Join("/", LabelLinkBuilder.RelativeTo(root, file)!
            .Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));

        // Ровно та причина, по которой ссылка «получалась ОЧЕНЬ длинной»: прежний вариант почти вдвое
        // длиннее, и весь этот излишек — служебные «%D0».
        Assert.True(url.Length * 2 < old.Length * 3, $"новая {url.Length}, прежняя {old.Length}");
        Assert.DoesNotContain("%D0", url);
    }

    /// <summary>Длина ссылки — не эстетика, а физика кода. Байты уходят в QR как есть (кириллица —
    /// байтовый режим UTF-8, два байта на букву), а процентное кодирование раздувало каждую букву до
    /// ШЕСТИ байт. От объёма зависит версия кода, от версии — число модулей, а от него — размер
    /// модуля на бумаге: чем он мельче, тем хуже наклейку берёт камера телефона. Поэтому здесь
    /// меряется не строка, а сам код и то, каким он выйдет из принтера.
    ///
    /// Числа приколочены намеренно (как <c>Qr.W = 54.96</c> в соседнем тесте): если раскладка или
    /// правила экранирования поедут, это должно быть видно сразу, а не по жалобе «код не читается».</summary>
    [Fact]
    public void TheCyrillicLink_MakesTheQrSparser_AndTheModuleBiggerOnPaper()
    {
        var root = Path.Combine(Path.GetTempPath(), "disk");
        var file = Path.Combine(root, "ПО", "НГР", "КНС", "SMH5", "2.1.0042.0001.20260422_1348",
            "Инструкция", "инструкция_2.1.0042.0001.20260422_1348.pdf");
        const string baseUrl = "https://disk.antarus.su/instructions";

        var url = LabelLinkBuilder.BuildUrl(baseUrl, root, file)!;
        var escaped = baseUrl + "/" + string.Join("/", LabelLinkBuilder.RelativeTo(root, file)!
            .Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));

        var now = QrModules(url);
        var before = QrModules(escaped);

        // 162 байта против 274 — это версия кода 11 против версии 13, то есть 61 модуль против 69
        // (сторона матрицы = 21 + 4 × (версия − 1)).
        Assert.Equal(162, System.Text.Encoding.UTF8.GetByteCount(url));
        Assert.Equal(274, System.Text.Encoding.UTF8.GetByteCount(escaped));
        Assert.Equal(61, now);
        Assert.Equal(69, before);

        // На обычной наклейке 97.5×72 модуль стал заметно крупнее — а это и есть «берёт телефон».
        var plan = LabelPlanner.Plan(new LabelLayout(), "ЩУН-3", "2.1.0042.0001.20260422_1348", url);
        Assert.True(plan.FitsInsideBand(), plan.WarningText);
        Assert.True(plan.Qr.W / now >= 0.8,
            $"модуль {plan.Qr.W / now:0.###} мм при стороне кода {plan.Qr.W:0.##} мм и {now} модулях");
        Assert.True(plan.Qr.W / now > plan.Qr.W / before);
    }

    /// <summary>Сторона матрицы БЕЗ тихой зоны — сам код, по которому и считается его версия. На
    /// этикетке QrArt рисует его вместе с вложенными QRCoder-ом четырьмя модулями поля с каждой
    /// стороны (тихая зона входит в визуал кода — см. QrReadabilityAndHeadlineTests).</summary>
    private static int QrModules(string content) =>
        AntarusPoFinder.App.Services.QrArt.Encode(content).ModuleMatrix.Count - 8;

    [Theory]
    [InlineData("Карта ВВ", "Карта%20ВВ")]
    [InlineData("100%", "100%25")]
    [InlineData("что#где?когда", "что%23где%3Fкогда")]
    [InlineData("2.1.0042.0001", "2.1.0042.0001")]
    [InlineData("(01312)_SN00042", "(01312)_SN00042")]
    public void EscapeSegment_EncodesOnlyWhatBreaksTheAddress(string segment, string expected) =>
        Assert.Equal(expected, LabelLinkBuilder.EscapeSegment(segment));

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
