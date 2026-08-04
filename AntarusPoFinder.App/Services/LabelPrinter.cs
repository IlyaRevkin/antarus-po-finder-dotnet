using System;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace AntarusPoFinder.App.Services;

/// <summary>Печать этикетки с QR-кодом на принтер этикеток.
///
/// Почему не «отправить файл на печать ассоциацией», как это делается с PDF инструкции: этикетки нет
/// как файла — её надо СОБРАТЬ (QR + подписи) под конкретный размер наклейки. WPF это умеет напрямую:
/// собранный визуал печатается PrintDialog.PrintVisual, а размер задаётся в миллиметрах и переводится
/// в аппаратно-независимые единицы (мм × 96 / 25.4). Никаких внешних библиотек для этого не нужно.
///
/// Принтер выбирается по ИМЕНИ из настроек (per-machine): пусто — принтер Windows по умолчанию.
/// Неизвестное имя (принтер отключили/переименовали) не должно ронять печать: молча падаем на
/// принтер по умолчанию, вызывающий сообщает об этом человеку.</summary>
public static class LabelPrinter
{
    /// <summary>Миллиметры в аппаратно-независимые единицы WPF (1/96 дюйма).</summary>
    public static double MmToDiu(double mm) => mm * 96.0 / 25.4;

    public static BitmapSource MakeQr(string content, int pixelsPerModule = 12)
    {
        var data = new QRCodeGenerator().CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var bytes = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Сама этикетка: QR слева, подписи справа, ссылка мелким шрифтом внизу. Верстается на
    /// белом фоне и чёрным текстом ЯВНО, без ресурсов темы: печатается она на бумагу, и в тёмной теме
    /// приложения этикетка иначе ушла бы на принтер белым по чёрному.</summary>
    public static FrameworkElement BuildLabel(double widthMm, double heightMm, BitmapSource qr,
        string title, string subtitle, string caption)
    {
        var w = MmToDiu(widthMm);
        var h = MmToDiu(heightMm);
        var qrSide = Math.Max(24, Math.Min(h - MmToDiu(10), MmToDiu(45)));

        var grid = new Grid { Width = w, Height = h, Background = Brushes.White };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var image = new Image
        {
            Source = qr,
            Width = qrSide,
            Height = qrSide,
            Margin = new Thickness(MmToDiu(3)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Пиксельный QR не должен размываться интерполяцией при печати — иначе телефон его хуже ловит.
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Grid.SetRow(image, 0);
        Grid.SetColumn(image, 0);
        grid.Children.Add(image);

        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, MmToDiu(3), MmToDiu(3), MmToDiu(3)),
        };
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            texts.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = Brushes.Black,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        Grid.SetRow(texts, 0);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var link = new TextBlock
        {
            Text = caption,
            FontSize = 8,
            Foreground = Brushes.Black,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(MmToDiu(3), 0, MmToDiu(3), MmToDiu(2)),
        };
        Grid.SetRow(link, 1);
        Grid.SetColumn(link, 0);
        Grid.SetColumnSpan(link, 2);
        grid.Children.Add(link);

        var border = new Border
        {
            Width = w,
            Height = h,
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(0.6),
            Child = grid,
        };
        Layout(border, w, h);
        return border;
    }

    /// <summary>Разметка «вручную»: визуал, который никогда не был на экране, сам себя не измеряет, и
    /// на печать ушёл бы пустым прямоугольником.</summary>
    public static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    public sealed record PrintOutcome(bool Ok, string Message);

    /// <summary>Печать без диалога выбора принтера: наладчику незачем каждый раз подтверждать окно —
    /// принтер задан в Настройки → Печать. Возврат — что сказать человеку в статус-строке.</summary>
    public static PrintOutcome Print(FrameworkElement label, string printerName, string jobName)
    {
        try
        {
            var dlg = new PrintDialog();
            var note = "";
            if (!string.IsNullOrWhiteSpace(printerName))
            {
                var queue = TryFindQueue(printerName);
                if (queue is not null) dlg.PrintQueue = queue;
                else note = $" (принтер «{printerName}» не найден — печать на принтер по умолчанию)";
            }

            dlg.PrintVisual(label, jobName);
            return new PrintOutcome(true, $"Этикетка отправлена на печать{note}");
        }
        catch (Exception ex)
        {
            return new PrintOutcome(false, $"Не удалось напечатать: {ex.Message}");
        }
    }

    /// <summary>Имена принтеров, установленных на этой машине. Пустой список — служба печати
    /// недоступна; окно настроек в этом случае просто оставляет «принтер по умолчанию».</summary>
    public static string[] InstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
            var names = new System.Collections.Generic.List<string>();
            foreach (var q in queues)
            {
                names.Add(q.Name);
                q.Dispose();
            }
            return names.ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Сервер печати здесь СОЗНАТЕЛЬНО не оборачивается в using, в отличие от
    /// <see cref="InstalledPrinters"/>: там наружу уходят только строки, а здесь — сама очередь, и
    /// живёт она лишь пока жив её сервер. Освободив сервер, мы вернули бы очередь, на которой
    /// PrintVisual падает, — то есть печать на ЯВНО ВЫБРАННЫЙ принтер не работала бы вовсе.</summary>
    private static PrintQueue? TryFindQueue(string name)
    {
        try
        {
            var server = new LocalPrintServer();
            foreach (var q in server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }))
            {
                if (string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)) return q;
                q.Dispose();
            }
        }
        catch (Exception)
        {
            // Служба печати недоступна — вызывающий напечатает на принтер по умолчанию.
        }
        return null;
    }
}
