using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Рисует одностраничный PDF-заглушку «Инструкция в разработке» (см.
/// <see cref="InstructionStub"/>).
///
/// <b>Почему картинкой, а не текстом PDF.</b> Свой генератор PDF в приложении есть, но он умеет
/// ровно одно — завернуть JPEG в страницу (<see cref="SimplePdfWriter"/>). Настоящий текстовый PDF с
/// кириллицей требует встроенного шрифта с CID-кодировкой: это отдельная возня с разбором TrueType,
/// а выгоды никакой — заглушку не ищут поиском по тексту и не копируют из неё строки. Поэтому текст
/// рисуется средствами WPF и кладётся на страницу картинкой, ровно как советует
/// docs/hierarchy-rework-plan.md (этап 1b).
///
/// <b>Про поток.</b> Заглушки создаются посреди копирования файлов, то есть из фонового потока, а
/// визуалы WPF живут на потоке интерфейса. Поэтому вся отрисовка перебрасывается на диспетчер
/// приложения; если приложения нет вовсе (консольный запуск, тесты), рисуем на месте.</summary>
public sealed class InstructionStubWriter : IInstructionStubWriter
{
    /// <summary>Страница A4 при 150 точках на дюйм: достаточно, чтобы надпись была резкой и на
    /// экране, и на бумаге, и при этом файл остаётся в десятках килобайт.</summary>
    private const int Dpi = 150;
    private const int PageWidthPx = (int)(210 / 25.4 * Dpi);
    private const int PageHeightPx = (int)(297 / 25.4 * Dpi);

    public void Write(string path, string text)
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Render(path, text));
            return;
        }
        Render(path, text);
    }

    private static void Render(string path, string text)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidthPx, PageHeightPx));

            var line = Formatted(text, PageWidthPx * 0.06);
            var hint = Formatted("Документ ещё не готов. Файл заменится настоящей инструкцией, " +
                                 "как только её приложат к этой версии.", PageWidthPx * 0.022);
            line.MaxTextWidth = PageWidthPx * 0.8;
            hint.MaxTextWidth = PageWidthPx * 0.7;
            hint.SetForegroundBrush(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));

            var top = (PageHeightPx - line.Height - hint.Height - PageWidthPx * 0.04) / 2;
            dc.DrawText(line, new Point((PageWidthPx - line.Width) / 2, top));
            dc.DrawText(hint, new Point((PageWidthPx - hint.Width) / 2, top + line.Height + PageWidthPx * 0.04));
        }

        var bitmap = new RenderTargetBitmap(PageWidthPx, PageHeightPx, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);

        SimplePdfWriter.WriteJpegAsPdf(ms.ToArray(), PageWidthPx, PageHeightPx, Dpi, path);
    }

    /// <summary>Гарнитура задаётся явно: визуал собирается вне окна, наследовать шрифт не от кого
    /// (та же причина, что и у подписи в центре фирменного QR — см. <see cref="QrArt"/>).</summary>
    private static FormattedText Formatted(string text, double size) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size, Brushes.Black, 96)
        {
            TextAlignment = TextAlignment.Center,
        };
}
