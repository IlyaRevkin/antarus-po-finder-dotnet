using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;
using Xunit;
using ZXing;
using ZXing.Common;

namespace AntarusPoFinder.Tests;

/// <summary>Фирменный QR, проверенный НАСТОЯЩИМ ДЕКОДЕРОМ, а не рассуждениями о геометрии.
///
/// Остальные проверки этикетки (QrReadabilityAndHeadlineTests) смотрят на числа: где стоят клетки,
/// какой у них размер, есть ли тихая зона. Числа можно подогнать так, что все они сойдутся, а телефон
/// код всё равно не возьмёт — ровно это и было в жалобе «фирменный QR почему-то не читается через
/// телефон при наведении». Поэтому здесь визуал РИСУЕТСЯ в растр так же, как он уйдёт на принтер, и
/// скармливается ZXing — тому же семейству декодеров, что стоит в камерах телефонов.
///
/// Тест дорогой (растеризация + разбор), поэтому его немного: он отвечает на один вопрос — «читается
/// или нет», — и делает это для тех случаев, из-за которых код и не читался.</summary>
public class QrDecoderTests
{
    /// <summary>Реальный адрес инструкции: кириллица идёт в него как есть (см. LabelLinkBuilder), и
    /// декодер обязан вернуть ЕЁ ЖЕ, а не мешанину — иначе телефон откроет не ту страницу.</summary>
    private const string Url =
        "https://disk.antarus.su/instructions/ПО/НГР/КНС/SMH5/2.1.0042.0001/Инструкция/инструкция_2.1.0042.0001.pdf";

    /// <summary>WPF-визуалы создаются только в STA-потоке (бегун живёт в MTA). Исключение из чужого
    /// потока пробрасывается обратно, иначе упавший Assert утонул бы молча.</summary>
    private static T OnStaThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    /// <summary>Растеризует визуал в квадрат <paramref name="pixels"/>×<paramref name="pixels"/> и
    /// пытается его прочитать. null — декодер кода не нашёл (ровно то, что видит телефон, когда
    /// «наводишь, а он не берёт»).</summary>
    private static string? Decode(FrameworkElement visual, int pixels)
    {
        var side = visual.Width;
        visual.Measure(new Size(side, side));
        visual.Arrange(new Rect(0, 0, side, side));
        visual.UpdateLayout();

        // dpi подобрано так, чтобы визуал занял ровно запрошенное число пикселей: «сколько пикселей
        // на модуль» — единственное, что решает, возьмёт код камера или нет.
        var dpi = 96.0 * pixels / side;
        var bitmap = new RenderTargetBitmap(pixels, pixels, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = pixels * 4;
        var buffer = new byte[stride * pixels];
        bitmap.CopyPixels(buffer, stride, 0);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions
            {
                PossibleFormats = new[] { BarcodeFormat.QR_CODE },
                TryHarder = true,
            },
        };
        return reader.Decode(new RGBLuminanceSource(buffer, pixels, pixels,
            RGBLuminanceSource.BitmapFormat.BGRA32))?.Text;
    }

    /// <summary>Визуал, поставленный в чёрную рамку ВПЛОТНУЮ — так код и живёт на этикетке: сразу за
    /// ним идёт рамка наклейки (ShowFrame) или название установки в соседней колонке.
    ///
    /// <paramref name="stripQuietZone"/> воспроизводит ПРЕЖНЮЮ отрисовку: тихая зона вырезалась из
    /// визуала «под неё отводится поле самой этикетки». Поля там нет — и код упирался в тёмное.</summary>
    private static FrameworkElement Framed(string content, double side, string hole, bool stripQuietZone)
    {
        var total = QrArt.ModuleCountWithQuietZone(content);
        var step = side / total;
        var code = QrArt.Build(content, side, hole);

        var offset = stripQuietZone ? -QrArt.QuietModules * step : 0;
        var innerSide = stripQuietZone ? side - 2 * QrArt.QuietModules * step : side;

        var inner = new Canvas
        {
            Width = innerSide, Height = innerSide, ClipToBounds = true, Background = Brushes.White,
        };
        Canvas.SetLeft(code, offset);
        Canvas.SetTop(code, offset);
        inner.Children.Add(code);

        // Чёрный фон хоста с отступом в один модуль = рамка наклейки, вплотную прилегающая к коду.
        var host = new Canvas
        {
            Width = innerSide + 2 * step, Height = innerSide + 2 * step, Background = Brushes.Black,
        };
        Canvas.SetLeft(inner, step);
        Canvas.SetTop(inner, step);
        host.Children.Add(inner);
        return host;
    }

    /// <summary>Сколько пикселей растра давать на визуал. Восемь на модуль — с запасом: тест про «код
    /// в принципе читается», а не про предел разрешения (за предел отвечает LabelPlanner.MinModuleMm,
    /// и он проверяется числом, а не растеризацией).</summary>
    private static int PixelsPerModule(FrameworkElement host, double side) =>
        (int)Math.Round(8 * QrArt.ModuleCountWithQuietZone(Url) * host.Width / side);

    // ── Главная проверка: код читается ───────────────────────────────────────

    /// <summary>Фирменный код с подписью в центре читается настоящим декодером, и прочитанное — тот
    /// самый адрес, кириллицей и без процентной мешанины.</summary>
    [Fact]
    public void TheFancyCode_IsActuallyReadable_AndGivesBackTheCyrillicUrlItself() => OnStaThread(() =>
    {
        const double side = 600;
        var code = QrArt.Build(Url, side, LabelLayout.DefaultHoleText);
        var text = Decode(code, 8 * QrArt.ModuleCountWithQuietZone(Url));

        Assert.Equal(Url, text);
        return 0;
    });

    /// <summary>Тот же код без окошка по центру — на случай, если подпись стёрли в настройках.</summary>
    [Fact]
    public void TheCodeWithoutTheCentreCaption_IsReadableToo() => OnStaThread(() =>
    {
        var code = QrArt.Build(Url, 600, "");
        Assert.Equal(Url, Decode(code, 8 * QrArt.ModuleCountWithQuietZone(Url)));
        return 0;
    });

    // ── Ради чего чинили: рамка наклейки вплотную ────────────────────────────

    /// <summary>Ключевая проверка правки. Один и тот же код, одна и та же рамка вплотную: с тихой
    /// зоной внутри визуала он читается, с прежней вырезанной — нет. Это и есть «не читается через
    /// телефон при наведении», воспроизведённое декодером.</summary>
    [Fact]
    public void WithTheLabelFrameTouchingIt_TheQuietZoneIsWhatMakesTheCodeReadable() => OnStaThread(() =>
    {
        const double side = 600;

        var withZone = Framed(Url, side, LabelLayout.DefaultHoleText, stripQuietZone: false);
        var fixedUp = Decode(withZone, PixelsPerModule(withZone, side));
        Assert.Equal(Url, fixedUp);

        var without = Framed(Url, side, LabelLayout.DefaultHoleText, stripQuietZone: true);
        var asBefore = Decode(without, PixelsPerModule(without, side));
        Assert.True(asBefore is null || asBefore != Url,
            "код без тихой зоны, упирающийся в рамку наклейки, не должен читаться — иначе проверка " +
            "ничего не сторожит и правку можно откатить незамеченной");
        return 0;
    });

    // ── Короткая ссылка — прямая выгода для читаемости ───────────────────────

    /// <summary>Кириллица как есть (LabelLinkBuilder) — это не только «ссылку видно глазами»: тот же
    /// адрес с процентным кодированием даёт ЗАМЕТНО более плотный код, а плотность — и есть то, из-за
    /// чего камера промахивается на маленькой наклейке.</summary>
    [Fact]
    public void PercentEncodingTheSameAddress_MakesTheCodeDenser()
    {
        var asIs = QrArt.ModuleCountWithQuietZone(Url);
        var escaped = QrArt.ModuleCountWithQuietZone(
            string.Join("/", Url.Split('/').Select((s, i) => i < 3 ? s : Uri.EscapeDataString(s))));

        Assert.True(escaped > asIs, $"процентное кодирование обязано раздувать код: {escaped} против {asIs}");
    }
}
