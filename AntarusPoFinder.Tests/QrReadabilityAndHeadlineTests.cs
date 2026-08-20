using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Фирменный QR на этикетке: почему его не брал телефон и почему заказчик не понимал, что
/// это вообще за код.
///
/// <b>Жалоба дословно:</b> «фирменный QR почему-то не читается через телефон при наведении, а ещё
/// нигде не указано, что это инструкция для заказчика — просто QR с названием установки и
/// непонятными цифрами».
///
/// Причин нечитаемости было три, и все здесь заперты тестами:
/// <list type="number">
/// <item><b>сломанные угловые маркеры</b> — ГЛАВНАЯ. Рамка маркера рисовалась шириной шесть модулей
///       вместо семи и со сдвигом на полмодуля, разрез через центр давал 1 : 0.5 : 3 : 0.5 : 1 вместо
///       канонического 1 : 1 : 3 : 1 : 1. По маркерам сканер код и находит — не найдя их, он не видит
///       кода вовсе, ни при каком размере наклейки. Проверяется
///       <see cref="FinderMarkers_AreExactlySevenModulesWide_AndSitOnTheGrid"/> и настоящим декодером
///       в QrDecoderTests;</item>
/// <item><b>вырезанная тихая зона</b> — QrArt срезал вложенные QRCoder-ом четыре модуля поля, отдавая
///       их «полю самой этикетки». Полем этикетки тихая зона не работает: сразу за кодом идёт рамка
///       наклейки или название установки в соседней колонке, и сканер не находит границ кода;</item>
/// <item><b>зазор между модулями</b> — каждая клетка рисовалась на 8 % меньше своей ячейки «ради
///       фирменного точечного вида». На 203 dpi это разрывало связные тёмные области, и сканеру
///       доставалась россыпь пятен.</item>
/// </list>
/// Четвёртая, уже про размер, — слишком мелкий модуль из-за длинной ссылки — лечится в
/// LabelAndStickersTests (кириллица в адрес как есть) и предупреждением
/// <see cref="LabelPlanner.ModulesAreReadable"/>.</summary>
public class QrReadabilityAndHeadlineTests
{
    private const string Url = "https://disk.antarus.su/instructions/ПО/НГР/КНС/SMH5/2.1.0042.0001/инструкция.pdf";

    /// <summary>Визуал кода — это WPF-элементы, а они создаются только в STA-потоке (тестовый бегун
    /// живёт в MTA). Гоняем такие проверки в своём потоке и пробрасываем исключение обратно, чтобы
    /// упавший Assert оставался упавшим тестом, а не тихо утонул в чужом потоке.</summary>
    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // ── Тихая зона и плотность кода ──────────────────────────────────────────

    /// <summary>Тихая зона обязана быть ЧАСТЬЮ визуала кода, а не надеждой на поле наклейки: по
    /// ISO/IEC 18004 её минимум — четыре модуля с каждой стороны.</summary>
    [Fact]
    public void TheQuietZone_IsPartOfTheCodeVisual_NotBorrowedFromTheLabelMargin() => OnStaThread(() =>
    {
        Assert.Equal(4, QrArt.QuietModules);

        // Матрица от QRCoder = данные + по четыре модуля поля с каждой стороны.
        var withZone = QrArt.ModuleCountWithQuietZone(Url);
        var data = withZone - 2 * QrArt.QuietModules;
        Assert.True(data > 20, "матрица подозрительно мала — проверка потеряла смысл");

        const double side = 200;
        var visual = (Canvas)QrArt.Build(Url, side, "");
        var step = side / withZone;

        // Сторона визуала — ровно та, что просили: тихая зона не «прибавилась» сверху, а вошла внутрь.
        Assert.Equal(side, visual.Width, 3);
        Assert.Equal(side, visual.Height, 3);

        // Ни одна нарисованная клетка не заходит в поле тихой зоны.
        var dots = visual.Children.OfType<System.Windows.Shapes.Path>().Select(p => p.Data.Bounds)
            .Where(b => !b.IsEmpty).ToList();
        Assert.NotEmpty(dots);
        var drawn = dots.Aggregate(Rect.Empty, Rect.Union);
        Assert.True(drawn.Left >= QrArt.QuietModules * step - 0.01,
            $"код начинается в {drawn.Left:0.##}, а тихая зона — {QrArt.QuietModules * step:0.##}");
        Assert.True(drawn.Top >= QrArt.QuietModules * step - 0.01);
        Assert.True(drawn.Right <= side - QrArt.QuietModules * step + 0.01);
        Assert.True(drawn.Bottom <= side - QrArt.QuietModules * step + 0.01);
    });

    /// <summary>Клетка рисуется целиком, во всю свою ячейку: соседние модули обязаны смыкаться.
    /// Прежний зазор в 8 % стороны и разваливал код на печати.</summary>
    [Fact]
    public void DarkModules_FillTheirWholeCell_SoNeighboursTouch() => OnStaThread(() =>
    {
        const double side = 200;
        var total = QrArt.ModuleCountWithQuietZone(Url);
        var step = side / total;

        var visual = (Canvas)QrArt.Build(Url, side, "");
        var group = Assert.IsType<GeometryGroup>(visual.Children.OfType<System.Windows.Shapes.Path>().First().Data);

        // Каждая клетка — квадрат ровно в шаг матрицы (скругление углов размер не меняет).
        foreach (var rect in group.Children.OfType<RectangleGeometry>().Take(50))
        {
            Assert.Equal(step, rect.Rect.Width, 3);
            Assert.Equal(step, rect.Rect.Height, 3);
        }
    });

    /// <summary>Угловой маркер занимает РОВНО семь модулей и стоит по сетке.
    ///
    /// Это третья и главная причина нечитаемости. Рамка маркера задавалась прямоугольником 6×6 со
    /// сдвигом на полмодуля — исходя из того, что WPF рисует обводку по центру контура и иначе маркер
    /// «вылезет за свои семь модулей». Для <see cref="System.Windows.Shapes.Rectangle"/> это не так:
    /// фигура вписывает обводку внутрь своих Width/Height сама. Маркер выходил шириной шесть модулей
    /// и съезжал на полмодуля, разрез через его центр давал 1 : 0.5 : 3 : 0.5 : 1 вместо 1 : 1 : 3 : 1 : 1,
    /// а по этому соотношению сканер маркеры и ищет — код не читался ВООБЩЕ ни при каком размере.
    /// Проверку дублирует настоящий декодер (QrDecoderTests); здесь она заперта числом, чтобы поломка
    /// называла себя сама.</summary>
    [Fact]
    public void FinderMarkers_AreExactlySevenModulesWide_AndSitOnTheGrid() => OnStaThread(() =>
    {
        const double side = 200;
        var total = QrArt.ModuleCountWithQuietZone(Url);
        var modules = total - 2 * QrArt.QuietModules;
        var step = side / total;

        var visual = (Canvas)QrArt.Build(Url, side, "");
        var markers = visual.Children.OfType<Canvas>().ToList();
        Assert.Equal(3, markers.Count);

        foreach (var marker in markers)
        {
            Assert.Equal(7 * step, marker.Width, 3);
            Assert.Equal(7 * step, marker.Height, 3);

            // Обводка укладывается ВНУТРЬ маркера: её элемент занимает те же семь модулей и не сдвинут.
            var ring = marker.Children.OfType<System.Windows.Shapes.Rectangle>()
                .Single(r => r.Fill is null);
            Assert.Equal(7 * step, ring.Width, 3);
            Assert.Equal(step, ring.StrokeThickness, 3);
            // Сдвига нет вовсе: полмодуля, которые здесь были, и уводили маркер с сетки.
            // Незаданное Canvas.Left — это NaN, то есть «в начале координат».
            Assert.True(double.IsNaN(Canvas.GetLeft(ring)) || Canvas.GetLeft(ring) == 0,
                $"рамка маркера сдвинута на {Canvas.GetLeft(ring)}");
            Assert.True(double.IsNaN(Canvas.GetTop(ring)) || Canvas.GetTop(ring) == 0);

            // Залитый центр — 3×3 ровно посередине: между ним и рамкой остаётся белое кольцо в модуль.
            var core = marker.Children.OfType<System.Windows.Shapes.Rectangle>()
                .Single(r => r.Fill is not null);
            Assert.Equal(3 * step, core.Width, 3);
            Assert.Equal(2 * step, Canvas.GetLeft(core), 3);
            Assert.Equal(2 * step, Canvas.GetTop(core), 3);
        }

        // Маркеры стоят в трёх углах САМОГО кода, то есть сразу за тихой зоной.
        var corners = markers.Select(m => (Canvas.GetLeft(m), Canvas.GetTop(m))).ToList();
        var q = QrArt.QuietModules * step;
        var far = (QrArt.QuietModules + modules - 7) * step;
        Assert.Contains((q, q), corners);
        Assert.Contains((far, q), corners);
        Assert.Contains((q, far), corners);
    });

    /// <summary>Уровень коррекции — Q (до 25 %): именно он оплачивает вырез под подпись в центре.</summary>
    [Fact]
    public void ErrorCorrection_StaysAtLevelQ()
    {
        // Тот же контент на уровне L дал бы матрицу МЕНЬШЕ — если бы уровень понизили ради
        // «пореже», подпись в центре стало бы нечем восстанавливать.
        var q = QrArt.Encode(Url).ModuleMatrix.Count;
        var l = new QRCoder.QRCodeGenerator()
            .CreateQrCode(Url, QRCoder.QRCodeGenerator.ECCLevel.L).ModuleMatrix.Count;

        Assert.True(q > l, $"уровень коррекции похож на L: {q} против {l}");
    }

    // ── Слишком мелкий модуль ────────────────────────────────────────────────

    /// <summary>Код может «влезть» на наклейку и всё равно не читаться: решает не сторона кода, а
    /// размер ОДНОГО модуля на бумаге.</summary>
    [Fact]
    public void TooManyModulesOnASmallLabel_AreReportedAsUnreadable()
    {
        var modules = QrArt.ModuleCountWithQuietZone(Url);

        // На маленькой наклейке 40×30 сторона кода мала, а модулей столько же — камера промахнётся.
        Assert.False(LabelPlanner.ModulesAreReadable(15, modules, out var small));
        Assert.True(small < LabelPlanner.MinModuleMm);

        // На обычной 97.5×72 всё в порядке.
        var plan = LabelPlanner.Plan(new LabelLayout(), "ЩУН-3", "2.1.0042.0001", Url);
        Assert.True(LabelPlanner.ModulesAreReadable(plan.Qr.W, modules, out var normal),
            $"модуль {normal:0.###} мм");
        Assert.True(normal >= LabelPlanner.MinModuleMm);
    }

    /// <summary>Вырожденный ввод не должен объявлять код нечитаемым: «модулей ноль» — это «кода нет
    /// вовсе», а не «плохо напечатан».</summary>
    [Fact]
    public void ModulesAreReadable_SurvivesDegenerateInput()
    {
        Assert.True(LabelPlanner.ModulesAreReadable(0, 0, out var mm));
        Assert.Equal(0, mm);
    }

    // ── Подпись назначения ───────────────────────────────────────────────────

    /// <summary>Подпись назначения — отдельный блок, читающийся ПЕРВЫМ, но место она берёт у текста,
    /// а не у кода: садится первой строкой в текстовую колонку, выше названия установки. Полосу во
    /// всю ширину она получает только там, где текста нет вовсе и отнимать не у кого.</summary>
    [Fact]
    public void Headline_IsItsOwnBlockAboveTheTitle()
    {
        var layout = LabelLayout.Default with
        {
            HeadlineText = LabelLayout.DefaultHeadline,
            ShowHeadline = true,
            // Проверка именно про «сам»: подпись садится первой строкой в текстовую колонку и не
            // отнимает места у кода. Умолчание с тех пор — полоса сверху (так попросили), и
            // полагаться здесь на него значило бы проверять другую раскладку под старым названием.
            HeadlinePlace = HeadlinePlacement.Auto,
            QrPlace = QrPlacement.Auto,
        };
        var plan = LabelPlanner.Plan(layout, "ЩУН-3", "2.1.0042.0001", Url);

        Assert.True(plan.HasHeadline);
        Assert.True(plan.FitsInsideBand(), plan.WarningText);
        Assert.True(plan.HeadlinePt > 0);
        Assert.True(plan.Headline.Y >= plan.Band.Y - 0.01, "подпись не должна вылезать за печатную область");
        Assert.True(plan.Headline.Y <= plan.Qr.Y + plan.Qr.H, "подпись обязана читаться вместе с кодом, а не под ним");
        Assert.True(plan.HasTitle);
        // Выше названия установки и в той же колонке — это одна связка «что это» → «от чего это».
        Assert.True(plan.Headline.Y + plan.Headline.H <= plan.Title.Y + 0.01, "подпись обязана быть выше названия");
        Assert.Equal(plan.Title.X, plan.Headline.X, 3);
        Assert.Equal(plan.Title.W, plan.Headline.W, 3);

        // Без текста подпись разворачивается во всю ширину и встаёт своей полосой НАД кодом —
        // отнимать место больше не у кого.
        var alone = LabelPlanner.Plan(layout, "", "", Url);
        Assert.True(alone.HasHeadline);
        Assert.True(alone.FitsInsideBand(), alone.WarningText);
        Assert.True(alone.Headline.W > plan.Headline.W, "без текстовой колонки подписи достаётся вся ширина");
        Assert.True(alone.Headline.X < plan.Headline.X, "и начинается она у левого края, а не после кода");
        Assert.True(alone.Headline.Y + alone.Headline.H <= alone.Qr.Y + 0.01, "полоса подписи стоит над кодом");
    }

    /// <summary>Выключенная галочка и пустой текст — разные намерения, но результат на бумаге один:
    /// блока нет, а место достаётся коду.</summary>
    [Fact]
    public void Headline_OffOrEmpty_TakesNoSpace()
    {
        var withText = LabelPlanner.Plan(
            new LabelLayout { HeadlineText = "Инструкция для заказчика", ShowHeadline = true },
            "ЩУН-3", "2.1.0042.0001", Url);

        foreach (var layout in new[]
                 {
                     new LabelLayout { HeadlineText = "Инструкция для заказчика", ShowHeadline = false },
                     new LabelLayout { HeadlineText = "", ShowHeadline = true },
                 })
        {
            var plan = LabelPlanner.Plan(layout, "ЩУН-3", "2.1.0042.0001", Url);
            Assert.False(plan.HasHeadline);
            Assert.True(plan.Qr.W >= withText.Qr.W - 0.01, "без подписи коду должно достаться не меньше места");
        }
    }

    /// <summary>Длинная подпись не ломает вёрстку: сначала мельчает кегль, потом честно предупреждаем.
    /// Молча обрезать её нельзя — человек должен знать, что на шкаф уедет половина фразы.</summary>
    [Fact]
    public void LongHeadline_ShrinksThenWarns_ButNeverOverflows()
    {
        var layout = LabelLayout.Default with
        {
            WidthMm = 40, HeightMm = 30,
            HeadlineText = "Инструкция по эксплуатации шкафа управления для заказчика",
            ShowHeadline = true,
            // «Сам» — та раскладка, на которой ужимание подписи и писалось (см. соседний тест).
            HeadlinePlace = HeadlinePlacement.Auto,
            QrPlace = QrPlacement.Auto,
        };

        var plan = LabelPlanner.Plan(layout, "ЩУН-3", "2.1.0042.0001", Url);

        Assert.True(plan.FitsInsideBand(), plan.WarningText);
        Assert.NotEmpty(plan.Warnings);
        Assert.Contains(plan.Warnings, w => w.Contains("одпись назначения"));
    }

    /// <summary>Значения чистятся там же, где и числа: подпись могла приехать синхронизацией с чужой
    /// машины, и переводы строки/двойные пробелы в вёрстке этикетки не нужны.</summary>
    [Fact]
    public void Clamped_TidiesHeadlineAndClipsTheCentreCaption()
    {
        var v = new LabelLayout
        {
            HeadlineText = "  Инструкция \r\n  для   заказчика  ",
            HoleText = "ИНСТРУКЦИЯ ПО ЭКСПЛУАТАЦИИ",
        }.Clamped();

        Assert.Equal("Инструкция для заказчика", v.HeadlineText);
        // Подпись в центре не «ужимается», а отсекается: плашка не растёт, и то, что в неё не влезает
        // тремя строками, на печати всё равно превратилось бы в точки.
        Assert.Equal(LabelLayout.MaxHoleTextLength, v.HoleText.Length);
        Assert.Equal("ИНСТРУКЦИЯ ПО Э", v.HoleText);

        // «ИНСТРУКЦИЯ» отсекать больше не за что — она укладывается в три строки.
        Assert.Equal("ИНСТРУКЦИЯ", new LabelLayout { HoleText = "ИНСТРУКЦИЯ" }.Clamped().HoleText);

        // Слишком длинная подпись назначения обрезается по разумной границе, но не пропадает.
        var long60 = new LabelLayout { HeadlineText = new string('и', 200) }.Clamped();
        Assert.Equal(60, long60.HeadlineText.Length);
    }

    /// <summary>У обычной растровой матрицы окна в центре нет вовсе — подпись туда писать некуда.</summary>
    [Fact]
    public void CentreCaption_OnlyExists_ForTheFancyCode()
    {
        Assert.Equal("ИНСТ", new LabelLayout { Style = QrStyle.Rounded, HoleText = "ИНСТ" }.EffectiveHoleText());
        Assert.Equal("ИНСТ", new LabelLayout { Style = QrStyle.Dots, HoleText = "ИНСТ" }.EffectiveHoleText());
        Assert.Equal("", new LabelLayout { Style = QrStyle.Classic, HoleText = "ИНСТ" }.EffectiveHoleText());
        Assert.Equal("", new LabelLayout { ShowHeadline = false, HeadlineText = "х" }.EffectiveHeadline());
    }

    // ── Хранение настроек ────────────────────────────────────────────────────

    /// <summary>Стёртая подпись обязана остаться стёртой. Пустая строка здесь — ЗНАЧЕНИЕ, а не
    /// «настройку не трогали»: без различия по факту наличия ключа умолчание возвращало бы её при
    /// каждом чтении, и снять подпись было бы нельзя вовсе.</summary>
    [Fact]
    public void ErasedCaptions_StayErased_AfterReload()
    {
        using var db = new TempDb();
        using var database = new Database(db.Path);
        var cfg = new ConfigService(database);

        // Ничего не задавали — берутся умолчания.
        var fresh = LabelLayout.FromConfig(cfg);
        Assert.Equal(LabelLayout.DefaultHeadline, fresh.HeadlineText);
        Assert.Equal(LabelLayout.DefaultHoleText, fresh.HoleText);
        Assert.True(fresh.ShowHeadline);

        // Свой текст доезжает до следующего чтения.
        (fresh with { HeadlineText = "Руководство по эксплуатации", HoleText = "РЭ" }).SaveTo(cfg);
        var custom = LabelLayout.FromConfig(cfg);
        Assert.Equal("Руководство по эксплуатации", custom.HeadlineText);
        Assert.Equal("РЭ", custom.HoleText);

        // И пустая строка тоже — именно это и не работало без Database.HasSetting.
        (custom with { HeadlineText = "", HoleText = "" }).SaveTo(cfg);
        var erased = LabelLayout.FromConfig(cfg);
        Assert.Equal("", erased.HeadlineText);
        Assert.Equal("", erased.HoleText);
        Assert.Equal("", erased.EffectiveHeadline());
        Assert.Equal("", erased.EffectiveHoleText());
    }

    /// <summary>Положение кода, вид кода и положение подписи хранятся словами, а не номерами: конфиг
    /// общий, и от перестановки пунктов местами в списке настройка на соседней машине не должна
    /// превращаться в другую.</summary>
    [Fact]
    public void PlacementAndStyle_SurviveReload()
    {
        using var db = new TempDb();
        using var database = new Database(db.Path);
        var cfg = new ConfigService(database);

        // Чистая база — это умолчания программы, и сверяемся мы именно с ними, а не с их копией
        // числами: список умолчаний живёт в одном месте (LabelLayout.Default), и повторять его
        // здесь значило бы завести второй, который однажды разойдётся с первым.
        var fresh = LabelLayout.FromConfig(cfg);
        Assert.Equal(LabelLayout.Default.QrPlace, fresh.QrPlace);
        Assert.Equal(LabelLayout.Default.Style, fresh.Style);
        Assert.Equal(LabelLayout.Default.HeadlinePlace, fresh.HeadlinePlace);
        Assert.Equal(LabelLayout.Default.HeadlineAlign, fresh.HeadlineAlign);

        // Сохраняем ЗАВЕДОМО не умолчания — иначе тест прошёл бы и на настройке, которая никуда не
        // записалась: прочиталось бы то же самое умолчание.
        (fresh with
        {
            QrPlace = QrPlacement.Left,
            Style = QrStyle.Dots,
            HeadlinePlace = HeadlinePlacement.Bottom,
            HeadlineAlign = HeadlineAlignment.Left,
        }).SaveTo(cfg);

        var back = LabelLayout.FromConfig(cfg);
        Assert.Equal(QrPlacement.Left, back.QrPlace);
        Assert.Equal(QrStyle.Dots, back.Style);
        Assert.Equal(HeadlinePlacement.Bottom, back.HeadlinePlace);
        Assert.Equal(HeadlineAlignment.Left, back.HeadlineAlign);
    }

    /// <summary>Снятая когда-то галочка «фирменный QR» обязана пережить обновление: на машине лежит
    /// только старый ключ label_fancy_qr, нового ещё нет — и код должен остаться классическим, а не
    /// молча стать скруглённым.</summary>
    [Fact]
    public void OldFancyQrFlag_StillDecidesTheStyle()
    {
        using var db = new TempDb();
        using var database = new Database(db.Path);
        var cfg = new ConfigService(database);

        cfg.SetLabelFlag("label_fancy_qr", false);
        Assert.Equal(QrStyle.Classic, LabelLayout.FromConfig(cfg).Style);

        // А новый ключ, когда он есть, главнее старого.
        (LabelLayout.FromConfig(cfg) with { Style = QrStyle.Dots }).SaveTo(cfg);
        Assert.Equal(QrStyle.Dots, LabelLayout.FromConfig(cfg).Style);
        // …и старый ключ при этом остаётся правдивым для прежних версий программы на соседних машинах.
        Assert.True(cfg.LabelFlag("label_fancy_qr", false));
    }

    /// <summary>Неизвестное значение (записала более новая версия программы) — не повод падать или
    /// потерять этикетку: берётся умолчание.</summary>
    [Fact]
    public void UnknownStoredPlacement_FallsBackToDefault()
    {
        using var db = new TempDb();
        using var database = new Database(db.Path);
        var cfg = new ConfigService(database);

        cfg.SetLabelText("label_qr_place", "ПоДиагонали");
        cfg.SetLabelText("label_headline_place", "");
        Assert.Equal(LabelLayout.Default.QrPlace, LabelLayout.FromConfig(cfg).QrPlace);
        Assert.Equal(LabelLayout.Default.HeadlinePlace, LabelLayout.FromConfig(cfg).HeadlinePlace);
    }
}
