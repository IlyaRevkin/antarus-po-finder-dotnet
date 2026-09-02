using System.Collections.Generic;
using System.Linq;
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
/// <b>Макет настраивается</b> (<see cref="StubLayout"/>). Эту страницу видят, наведя
/// телефон на наклейку, — до тех пор пока инструкцию не допишут, она и есть «инструкция». Раньше её
/// текст и вид были зашиты здесь в коде, и поменять слово значило выпустить релиз.</summary>
public sealed class InstructionStubWriter : IInstructionStubWriter
{
    /// <summary>Страница A4 при 150 точках на дюйм: достаточно, чтобы надпись была резкой и на
    /// экране, и на бумаге, и при этом файл остаётся в десятках килобайт.</summary>
    private const int Dpi = 150;
    public const int PageWidthPx = (int)(210 / 25.4 * Dpi);
    public const int PageHeightPx = (int)(297 / 25.4 * Dpi);

    private readonly StubLayoutSet _layouts;

    public InstructionStubWriter(StubLayoutSet? layouts = null) => _layouts = (layouts ?? StubLayoutSet.Default).Sane();

    /// <summary>Набор из трёх макетов и общих контактов, которым рисует этот писатель. Читается
    /// отсюда и Core — по нему считается отпечаток, которым помечается готовый файл.</summary>
    public StubLayoutSet Layouts => _layouts;

    /// <summary>Прежняя подпись без вида страницы. Рисует «в разработке» — единственный вид, который
    /// существовал, пока вид был один.</summary>
    public void Write(string path, string text) => Write(path, StubKind.InDevelopment, null);

    /// <summary>Номер версии берётся из параметра, а если его не передали — из имени файла
    /// («инструкция_&lt;версия&gt;.pdf»). Второе нужно перерисовке уже лежащей страницы: она идёт по
    /// файлу, и версию вызывающему брать больше неоткуда.</summary>
    public void Write(string path, StubKind kind, string? versionRaw)
    {
        var version = string.IsNullOrWhiteSpace(versionRaw) ? InstructionNaming.VersionFromFileName(path) : versionRaw;
        var layout = _layouts.For(kind);
        var contacts = _layouts.Contacts;

        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Render(path, layout, version, contacts));
            return;
        }
        Render(path, layout, version, contacts);
    }

    private static void Render(string path, StubLayout layout, string? versionRaw, string? contacts)
    {
        var bitmap = Draw(layout, versionRaw, contacts);

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);

        SimplePdfWriter.WriteJpegAsPdf(ms.ToArray(), PageWidthPx, PageHeightPx, Dpi, path);
    }

    /// <summary>Та же самая страница, но картинкой — ею живёт предпросмотр в редакторе макета.
    /// Отдельного «почти такого же» кода для предпросмотра нет намеренно: разойдись он с настоящей
    /// отрисовкой хоть на отступ, и подгонять макет пришлось бы вслепую, ровно как когда-то с
    /// наклейкой («что 97, что 90 ставлю, верх обрезается»).
    ///
    /// <b>Вёрстка — документа, а не записки.</b> Первая версия просто ставила текст столбиком по
    /// центру листа: сверху и снизу оставалась пустая треть, ни шапки, ни подвала, ни единой линии,
    /// заголовок огромный, всё остальное мелкое. Для страницы, которая уходит ЗАКАЗЧИКУ, это
    /// выглядело черновиком. Теперь лист собран по обычной для документа сетке:
    ///
    /// <list type="bullet">
    /// <item><description><b>шапка</b> — фирменный знак и название компании, под ней линия;</description></item>
    /// <item><description><b>тело</b> — заголовок, короткий акцентный штрих под ним и пояснение;
    /// выключка влево, как в документе, а не по центру;</description></item>
    /// <item><description><b>блок контактов</b> — на подложке с синей полосой слева и с QR-кодом:
    /// это главное на странице, ради него она и существует, поэтому он выделен, а не набран тем же
    /// текстом, что и всё остальное;</description></item>
    /// <item><description><b>подвал</b> — линия, слева версия, справа адрес сайта.</description></item>
    /// </list>
    ///
    /// Тело выравнивается по середине промежутка между шапкой и подвалом, а не по середине ЛИСТА:
    /// иначе при короткой подсказке оно снова сползало бы вниз, оставляя пустую шапку.</summary>
    public static RenderTargetBitmap Draw(StubLayout raw, string? versionRaw, string? contacts = null)
    {
        var layout = raw.Sane();
        var muted = new SolidColorBrush(Color.FromRgb((byte)layout.MutedTone, (byte)layout.MutedTone, (byte)layout.MutedTone));
        var brand = new SolidColorBrush(AntarusMark.Blue);

        // Поля как у обычного документа (около 19 мм), а не «пустая треть сверху».
        var margin = PageWidthPx * 0.09;
        var contentWidth = PageWidthPx - margin * 2;

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

            var headerBottom = DrawHeader(dc, layout, margin, contentWidth, brand, muted);
            var footerTop = DrawFooter(dc, layout, versionRaw, contacts, margin, contentWidth, muted);

            DrawBody(dc, layout, versionRaw, contacts, margin, contentWidth, headerBottom, footerTop, brand, muted);
        }

        var bitmap = new RenderTargetBitmap(PageWidthPx, PageHeightPx, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>Шапка: знак, название компании и линия под ними. Возвращает Y, ниже которого можно
    /// рисовать тело.</summary>
    private static double DrawHeader(DrawingContext dc, StubLayout layout, double margin, double contentWidth,
        Brush brand, Brush muted)
    {
        var top = margin;
        var markSide = PageWidthPx * 0.072;
        var textLeft = margin;

        if (layout.ShowLogo)
        {
            AntarusMark.Draw(dc, new Point(margin, top), markSide);
            textLeft = margin + markSide + PageWidthPx * 0.022;
        }

        var name = Formatted("ANTARUS", PageWidthPx * 0.032, brand, FontWeights.Bold);
        name.TextAlignment = TextAlignment.Left;
        var tagline = Formatted("Шкафы управления и автоматика", PageWidthPx * 0.014, muted);
        tagline.TextAlignment = TextAlignment.Left;

        // Название с подписью прижаты по вертикали к центру знака — иначе шапка «съезжает».
        var blockHeight = name.Height + tagline.Height;
        var textTop = top + (markSide - blockHeight) / 2;
        dc.DrawText(name, new Point(textLeft, textTop));
        dc.DrawText(tagline, new Point(textLeft, textTop + name.Height));

        var lineY = top + markSide + PageWidthPx * 0.028;
        dc.DrawLine(new Pen(brand, 2), new Point(margin, lineY), new Point(margin + contentWidth, lineY));
        return lineY;
    }

    /// <summary>Подвал: линия, слева подпись (обычно версия), справа сайт. Возвращает Y линии — выше
    /// неё заканчивается тело.</summary>
    private static double DrawFooter(DrawingContext dc, StubLayout layout, string? versionRaw, string? contacts,
        double margin, double contentWidth, Brush muted)
    {
        var lineY = PageHeightPx - margin - PageWidthPx * 0.030;
        dc.DrawLine(new Pen(muted, 1), new Point(margin, lineY), new Point(margin + contentWidth, lineY));

        var textTop = lineY + PageWidthPx * 0.010;
        var footerText = layout.Fill(layout.Footer, versionRaw, contacts);
        if (footerText.Length > 0)
        {
            var left = Formatted(footerText, PageWidthPx * layout.FooterSize, muted);
            left.TextAlignment = TextAlignment.Left;
            left.MaxTextWidth = contentWidth * 0.6;
            dc.DrawText(left, new Point(margin, textTop));
        }

        var right = Formatted(ServiceContacts.Site.Replace("https://", ""), PageWidthPx * layout.FooterSize, muted);
        right.TextAlignment = TextAlignment.Right;
        right.MaxTextWidth = contentWidth;
        dc.DrawText(right, new Point(margin, textTop));

        return lineY;
    }

    /// <summary>Тело: заголовок, штрих, пояснение и выделенный блок контактов с QR-кодом.</summary>
    private static void DrawBody(DrawingContext dc, StubLayout layout, string? versionRaw, string? contacts,
        double margin, double contentWidth, double headerBottom, double footerTop, Brush brand, Brush muted)
    {
        var title = Formatted(layout.Fill(layout.Title, versionRaw, contacts), PageWidthPx * layout.TitleSize,
            Brushes.Black, FontWeights.Bold);
        title.TextAlignment = TextAlignment.Left;
        title.MaxTextWidth = contentWidth;

        FormattedText? hint = null;
        var hintText = layout.Fill(layout.Hint, versionRaw, contacts);
        if (hintText.Length > 0)
        {
            hint = Formatted(hintText, PageWidthPx * layout.HintSize, muted);
            hint.TextAlignment = TextAlignment.Left;
            hint.MaxTextWidth = contentWidth * 0.92;
        }

        var contactsText = layout.Fill(layout.Contacts, versionRaw, contacts);

        var afterTitle = PageWidthPx * 0.030;
        var rule = PageWidthPx * 0.012;                 // акцентный штрих под заголовком

        // Заголовок ставится сразу под шапкой, а не по середине листа: раньше тело центрировалось в
        // промежутке, и при короткой подсказке над ним оставалась пустая треть — то самое, из-за чего
        // страница выглядела недовёрстанной.
        var y = headerBottom + PageWidthPx * 0.085;

        dc.DrawText(title, new Point(margin, y));
        y += title.Height + afterTitle * 0.35;

        dc.DrawRectangle(brand, null, new Rect(margin, y, PageWidthPx * 0.075, rule));
        y += rule + afterTitle * 0.65;

        if (hint is not null)
        {
            dc.DrawText(hint, new Point(margin, y));
            y += hint.Height;
        }

        if (contactsText.Length == 0) return;

        // Свободное место делится между «над блоком» и «под блоком», а не собирается в одну
        // дыру. Прижми блок к подвалу — между подсказкой и контактами поллиста пустоты; поставь сразу
        // за подсказкой — лист становится верхом тяжёлым. Поэтому около половины запаса сверху.
        var panelHeight = MeasureContactsPanel(layout, contactsText, contentWidth, out var lines);
        var lowest = footerTop - PageWidthPx * 0.055 - panelHeight;
        var earliest = y + PageWidthPx * 0.06;
        var panelTop = Math.Max(earliest, earliest + (lowest - earliest) * 0.55);

        DrawContactsPanel(dc, layout, lines, new Rect(margin, panelTop, contentWidth, panelHeight), brand, muted);
    }

    /// <summary>Высота блока контактов и разобранные строки. Меряется отдельно от рисования, потому
    /// что блок прижимается к подвалу — а для этого его высоту надо знать заранее.</summary>
    private static double MeasureContactsPanel(StubLayout layout, string text, double contentWidth,
        out FormattedText[] lines)
    {
        var pad = PageWidthPx * 0.030;
        var qrSide = layout.ShowQr ? PageWidthPx * 0.135 : 0;
        var textWidth = contentWidth - pad * 2 - (qrSide > 0 ? qrSide + pad : 0);

        var raw = text.Replace("\r\n", "\n").Split('\n');
        var built = new List<FormattedText>();
        var muted = new SolidColorBrush(Color.FromRgb((byte)layout.MutedTone, (byte)layout.MutedTone, (byte)layout.MutedTone));
        var basis = PageWidthPx * layout.ContactsSize;

        for (var i = 0; i < raw.Length; i++)
        {
            var line = raw[i].Trim();
            if (line.Length == 0) continue;

            // Роли строк — см. ServiceContacts.Block: подпись, НОМЕР, условие, адреса.
            var item = i switch
            {
                0 => Formatted(line, basis * 0.82, new SolidColorBrush(AntarusMark.Blue), FontWeights.Bold),
                1 => Formatted(line, basis * 2.0, Brushes.Black, FontWeights.Bold),
                2 => Formatted(line, basis * 0.85, muted),
                _ => Formatted(line, basis, Brushes.Black),
            };
            item.MaxTextWidth = textWidth;
            built.Add(item);
        }

        lines = built.ToArray();
        var height = 0d;
        foreach (var l in lines) height += l.Height;
        height += PageWidthPx * 0.006 * Math.Max(0, lines.Length - 1);
        return Math.Max(height, qrSide) + pad * 2;
    }

    private static void DrawContactsPanel(DrawingContext dc, StubLayout layout, FormattedText[] lines,
        Rect panel, Brush brand, Brush muted)
    {
        var pad = PageWidthPx * 0.030;
        var qrSide = layout.ShowQr ? PageWidthPx * 0.135 : 0;

        dc.DrawRectangle(PanelBrush, null, panel);
        dc.DrawRectangle(brand, null, new Rect(panel.X, panel.Y, PageWidthPx * 0.006, panel.Height));

        var gap = PageWidthPx * 0.006;
        var textHeight = 0d;
        foreach (var l in lines) textHeight += l.Height;
        textHeight += gap * Math.Max(0, lines.Length - 1);

        var y = panel.Y + (panel.Height - textHeight) / 2;
        foreach (var line in lines)
        {
            dc.DrawText(line, new Point(panel.X + pad, y));
            y += line.Height + gap;
        }

        if (qrSide > 0)
            DrawServiceQr(dc, new Rect(panel.Right - pad - qrSide, panel.Y + (panel.Height - qrSide) / 2,
                qrSide, qrSide));
    }

    /// <summary>Подложка блока контактов — очень светлый оттенок фирменного синего: на печати не
    /// съедает тонер, на экране телефона сразу выделяет главное.</summary>
    /// Заморожена не ради скорости: незамороженная кисть привязывается к диспетчеру того
    /// потока, где её создали, а статическое поле переживёт и его, и второй поток. В приложении
    /// отрисовка всегда уходит на один диспетчер и это не всплывало, а в тестах каждая страница
    /// рисуется своим потоком — и вторая падала с EnsureConsistentDispatchers.
    private static readonly Brush PanelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0xF4, 0xFA)));

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>QR прямо на странице — чтобы позвонить в сервис можно было и с БУМАГИ.
    ///
    /// Телефоном со шкафа человек попадает сюда по наклейке и номер уже видит. Но эта же страница
    /// печатается и кладётся в карман шкафа, а с бумаги ссылка не работает: остаётся набирать
    /// одиннадцать цифр руками. Код содержит <c>tel:</c> — один скан, и звонок уже набирается. Номер
    /// берётся из <see cref="ServiceContacts"/>, то есть меняется там же, где и напечатанный рядом,
    /// и разойтись они не могут.</summary>
    private static void DrawServiceQr(DrawingContext dc, Rect box)
    {
        var content = "tel:" + new string(ServiceContacts.Phone.Where(char.IsDigit).ToArray());
        var code = QrArt.Build(content, box.Width, hole: "", style: QrStyle.Classic);
        code.Measure(new Size(box.Width, box.Height));
        code.Arrange(new Rect(0, 0, box.Width, box.Height));
        code.UpdateLayout();

        dc.DrawRectangle(Brushes.White, null, box);
        dc.DrawRectangle(new VisualBrush(code), null, box);
    }

    /// <summary>Гарнитура задаётся явно: визуал собирается вне окна, наследовать шрифт не от кого
    /// (та же причина, что и у подписи в центре фирменного QR — см. <see cref="QrArt"/>).</summary>
    private static FormattedText Formatted(string text, double size, Brush brush, FontWeight? weight = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.SemiBold, FontStretches.Normal),
            size, brush, 96)
        {
            TextAlignment = TextAlignment.Left,
        };
}
