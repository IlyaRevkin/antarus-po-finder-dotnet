using System;
using System.Collections.Generic;
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
/// приложения; если приложения нет вовсе (консольный запуск, тесты), рисуем на месте.
///
/// <b>Макет настраивается</b> (<see cref="StubLayout"/>). Эту страницу видит заказчик, наведя
/// телефон на наклейку, — до тех пор пока инструкцию не допишут, она и есть «инструкция». Раньше её
/// текст и вид были зашиты здесь в коде, и поменять слово значило выпустить релиз.</summary>
public sealed class InstructionStubWriter : IInstructionStubWriter
{
    /// <summary>Страница A4 при 150 точках на дюйм: достаточно, чтобы надпись была резкой и на
    /// экране, и на бумаге, и при этом файл остаётся в десятках килобайт.</summary>
    private const int Dpi = 150;
    public const int PageWidthPx = (int)(210 / 25.4 * Dpi);
    public const int PageHeightPx = (int)(297 / 25.4 * Dpi);

    private readonly StubLayout _layout;

    public InstructionStubWriter(StubLayout? layout = null) => _layout = (layout ?? StubLayout.Default).Sane();

    /// <summary>Текстом сюда приходит заголовок (InstructionStub.Text) — он же подставляется в макет
    /// как <see cref="StubLayout.Title"/>, если человек его не менял. Номер версии вытаскивается из
    /// имени файла: заглушка называется «инструкция_&lt;версия&gt;.pdf», и другого источника версии у
    /// рисовальщика нет — интерфейс на всех реализациях один и расширять его ради одного поля
    /// значило бы трогать шесть точек вызова.</summary>
    public void Write(string path, string text)
    {
        var version = InstructionNaming.VersionFromFileName(path);
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Render(path, _layout, version));
            return;
        }
        Render(path, _layout, version);
    }

    private static void Render(string path, StubLayout layout, string? versionRaw)
    {
        var bitmap = Draw(layout, versionRaw);

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);

        SimplePdfWriter.WriteJpegAsPdf(ms.ToArray(), PageWidthPx, PageHeightPx, Dpi, path);
    }

    /// <summary>Та же самая страница, но картинкой — ею живёт предпросмотр в редакторе макета.
    /// Отдельного «почти такого же» кода для предпросмотра нет намеренно: разойдись он с настоящей
    /// отрисовкой хоть на отступ, и подгонять макет пришлось бы вслепую, ровно как когда-то с
    /// наклейкой («что 97, что 90 ставлю, верх обрезается»).</summary>
    public static RenderTargetBitmap Draw(StubLayout raw, string? versionRaw)
    {
        var layout = raw.Sane();
        var muted = new SolidColorBrush(Color.FromRgb((byte)layout.MutedTone, (byte)layout.MutedTone, (byte)layout.MutedTone));

        var title = Formatted(layout.Fill(layout.Title, versionRaw), PageWidthPx * layout.TitleSize, Brushes.Black);
        title.MaxTextWidth = PageWidthPx * 0.8;

        var blocks = new List<FormattedText> { title };

        var hintText = layout.Fill(layout.Hint, versionRaw);
        if (hintText.Length > 0)
        {
            var hint = Formatted(hintText, PageWidthPx * layout.HintSize, muted);
            hint.MaxTextWidth = PageWidthPx * 0.7;
            blocks.Add(hint);
        }

        var footerText = layout.Fill(layout.Footer, versionRaw);
        FormattedText? footer = null;
        if (footerText.Length > 0)
        {
            footer = Formatted(footerText, PageWidthPx * layout.FooterSize, muted);
            footer.MaxTextWidth = PageWidthPx * 0.8;
        }

        var gap = PageWidthPx * 0.04;
        var stackHeight = 0d;
        foreach (var block in blocks) stackHeight += block.Height;
        stackHeight += gap * (blocks.Count - 1);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidthPx, PageHeightPx));

            if (layout.ShowFrame)
            {
                var inset = PageWidthPx * 0.03;
                dc.DrawRectangle(null, new Pen(muted, 2),
                    new Rect(inset, inset, PageWidthPx - inset * 2, PageHeightPx - inset * 2));
            }

            var y = (PageHeightPx - stackHeight) / 2;
            foreach (var block in blocks)
            {
                dc.DrawText(block, new Point((PageWidthPx - block.Width) / 2, y));
                y += block.Height + gap;
            }

            // Подпись прижата к низу, а не идёт следующей строкой в стопке: это выходные данные
            // страницы, а не продолжение текста.
            if (footer is not null)
                dc.DrawText(footer, new Point((PageWidthPx - footer.Width) / 2,
                    PageHeightPx - footer.Height - PageWidthPx * 0.06));
        }

        var bitmap = new RenderTargetBitmap(PageWidthPx, PageHeightPx, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>Гарнитура задаётся явно: визуал собирается вне окна, наследовать шрифт не от кого
    /// (та же причина, что и у подписи в центре фирменного QR — см. <see cref="QrArt"/>).</summary>
    private static FormattedText Formatted(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size, brush, 96)
        {
            TextAlignment = TextAlignment.Center,
        };
}
