using System.Printing;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>«А что у принтера настроено сейчас?» — размер листа и непечатаемая кромка, прочитанные у
/// драйвера выбранного принтера.
///
/// <b>Тикетов у очереди ТРИ, и они разные — на этом кнопка и обожглась.</b> Жалоба Ильи дословно:
/// «считывает не текущее состояние драйвера, а по умолчанию, которое при первом подключении у него
/// стоит».
/// <list type="bullet">
/// <item><see cref="PrintQueue.DefaultPrintTicket"/> — ЗАВОДСКОЙ тикет принтера («Настройка по
///   умолчанию» на вкладке «Дополнительно» в свойствах). Его ставит драйвер при установке, и он же
///   остаётся, пока настройки не поменяет администратор. Это и есть «как было при первом подключении».</item>
/// <item><see cref="PrintQueue.UserPrintTicket"/> — «Настройка печати» ЭТОГО пользователя, то, что
///   человек выставил себе сам: размер наклейки, ориентация, лоток. Кнопке нужен именно он.</item>
/// <item><c>CurrentJobSettings.CurrentPrintTicket</c> — то же пользовательское, но в применении к
///   заданию; читать его вместо предыдущего смысла нет.</item>
/// </list>
/// <b>Ловушка</b>, из-за которой и была жалоба: очередь, добытая перечислением
/// (<c>GetPrintQueues</c>), отдаёт свойства из СНИМКА, сделанного при перечислении, и в этом снимке
/// на месте пользовательского тикета лежит заводской. Проверено на стенде: человек ставит в
/// «Настройке печати» A6, а <c>UserPrintTicket</c> до <see cref="PrintQueue.Refresh"/> отвечает A5
/// (заводской), и только после Refresh — честные A6. Поэтому Refresh здесь обязателен и убирать его
/// нельзя: без него кнопка подставляет ровно то, на что жаловались.
///
/// Печатаемая область берётся из возможностей очереди для этого тикета: драйвер отвечает, откуда
/// начинается и докуда идёт печать на выбранной бумаге. Разница между листом и этой областью и есть
/// та кромка, из-за которой у нулевых полей «обрезается верх».
///
/// Здесь нет НИ ОДНОГО решения о макете — только перевод единиц WPF в миллиметры. Что делать с
/// прочитанным, решает <see cref="PrinterPageFit"/> в Core, и это проверяется тестами без принтера.</summary>
public static class PrinterPageProbe
{
    /// <summary>Аппаратно-независимые единицы WPF (1/96 дюйма) в миллиметры — обратная сторона
    /// <see cref="LabelPrinter.MmToDiu"/>.</summary>
    public static double DiuToMm(double diu) => diu * 25.4 / 96.0;

    /// <summary>Либо настройки страницы, либо объяснение, почему их не получилось узнать. Ошибка —
    /// штатный исход: служба печати выключена, принтер отключили, драйвер не отвечает на вопрос о
    /// возможностях. Ронять из-за этого окно этикетки нельзя.</summary>
    public sealed record Probe(PrinterPageSetup? Setup, string? Error);

    /// <summary>Спросить драйвер. Пустое имя — принтер Windows по умолчанию, ровно как и при самой
    /// печати (см. <see cref="LabelPrinter.Print"/>): человек выбирает принтер один раз, в настройках.</summary>
    public static Probe Read(string? printerName)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = FindQueue(server, printerName);
            if (queue is null)
                return new Probe(null, string.IsNullOrWhiteSpace(printerName)
                    ? "принтер по умолчанию в Windows не выбран"
                    : $"принтер «{printerName}» не найден");

            var ticket = ReadTicket(queue);
            var caps = queue.GetPrintCapabilities(ticket);

            var (pageW, pageH) = PageSize(caps, ticket);
            if (pageW <= 0 || pageH <= 0)
                return new Probe(new PrinterPageSetup { PrinterName = queue.Name }, null);

            var area = caps.PageImageableArea;
            if (area is null)
                return new Probe(new PrinterPageSetup
                {
                    PrinterName = queue.Name,
                    PageWidthMm = DiuToMm(pageW),
                    PageHeightMm = DiuToMm(pageH),
                }, null);

            return new Probe(new PrinterPageSetup
            {
                PrinterName = queue.Name,
                PageWidthMm = DiuToMm(pageW),
                PageHeightMm = DiuToMm(pageH),
                LeftMm = DiuToMm(area.OriginWidth),
                TopMm = DiuToMm(area.OriginHeight),
                RightMm = DiuToMm(pageW - (area.OriginWidth + area.ExtentWidth)),
                BottomMm = DiuToMm(pageH - (area.OriginHeight + area.ExtentHeight)),
            }, null);
        }
        catch (Exception ex)
        {
            return new Probe(null, ex.Message);
        }
    }

    /// <summary>Настройки человека, а если очередь их не отдала — заводские. Обращение к тикету у
    /// части драйверов бросает исключение прямо здесь, поэтому оно отдельно и с запасным путём.
    ///
    /// <see cref="PrintQueue.Refresh"/> — не перестраховка, а само исправление жалобы: см.
    /// doc-комментарий класса, без него пользовательский тикет отдаёт заводской снимок.</summary>
    private static PrintTicket? ReadTicket(PrintQueue queue)
    {
        try
        {
            queue.Refresh();
        }
        catch (Exception)
        {
            // Очередь не перечиталась (нет прав, спулер занят) — читаем что есть: устаревший ответ
            // всё же лучше, чем отказ подставить хоть что-то.
        }

        try
        {
            if (queue.UserPrintTicket is { } user) return user;
        }
        catch (Exception)
        {
            // Драйвер не отдал пользовательский тикет — спросим возможности по заводскому.
        }

        try { return queue.DefaultPrintTicket; }
        catch (Exception) { return null; }
    }

    /// <summary>Размер листа С УЧЁТОМ ориентации — тот же, в котором драйвер отдаёт печатаемую
    /// область. Свойство бумаги из тикета (<see cref="PrintTicket.PageMediaSize"/>) хранит размер в
    /// её собственной ориентации, поэтому у альбомного листа стороны меняются местами; иначе поля
    /// считались бы от чужой стороны и получились бы отрицательными.</summary>
    private static (double Width, double Height) PageSize(PrintCapabilities caps, PrintTicket? ticket)
    {
        if (caps.OrientedPageMediaWidth is { } w && caps.OrientedPageMediaHeight is { } h && w > 0 && h > 0)
            return (w, h);

        var media = ticket?.PageMediaSize;
        if (media?.Width is not { } mw || media.Height is not { } mh || mw <= 0 || mh <= 0) return (0, 0);

        var landscape = ticket?.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape;
        return landscape ? (mh, mw) : (mw, mh);
    }

    /// <summary>Очередь по имени, иначе — очередь по умолчанию. Имя из настройки может оказаться
    /// чужим (настройка едет между машинами, а принтеры у всех называются по-разному), и это не
    /// ошибка: печать в таком случае тоже уходит на принтер по умолчанию.</summary>
    private static PrintQueue? FindQueue(LocalPrintServer server, string? printerName)
    {
        if (!string.IsNullOrWhiteSpace(printerName))
        {
            foreach (var q in server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }))
            {
                if (string.Equals(q.Name, printerName, StringComparison.OrdinalIgnoreCase)) return q;
                q.Dispose();
            }
        }

        try { return server.DefaultPrintQueue; }
        catch (Exception) { return null; }
    }
}
