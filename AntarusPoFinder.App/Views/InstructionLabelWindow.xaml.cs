using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>«QR и этикетка» — печать наклейки со ссылкой на инструкцию, чтобы наладчик у шкафа
/// открыл документ телефоном, а не искал его по диску. Открывается кнопкой прямо с карточки версии.
///
/// Что попадает в QR — решает <see cref="LabelLinkBuilder"/>: веб-ссылка, если администратор задал
/// адрес диска инструкций (Настройки → Печать), иначе сетевой путь к самому файлу. Второй вариант с
/// телефона не открыть, поэтому окно честно говорит, что именно зашито в код.
///
/// <b>Макет правится здесь же, с живым предпросмотром.</b> Раньше настраивались только ширина и
/// высота, и в настройках — отсюда «что 97, что 90, что 100 ставлю, верх обрезается»: подобрать
/// поля под непечатаемую кромку принтера вслепую нельзя, а других ручек не было. Теперь справа
/// стоят все параметры <see cref="LabelLayout"/>, и каждое изменение сразу перерисовывает ту самую
/// этикетку, которая уйдёт на принтер. Сохранение — отдельной кнопкой: подгонка под одну наклейку
/// не должна менять настройку у всех.</summary>
public partial class InstructionLabelWindow : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private readonly string _qrContent;
    private readonly string _title;
    private readonly string _subtitle;
    private LabelLayout _layout;

    /// <summary>Пока поля заполняются из настроек, их TextChanged не должен пересобирать этикетку и
    /// уж тем более читать полузаполненную форму.</summary>
    private bool _filling;

    public InstructionLabelWindow(AppServices services, IAppHost host, string title, string subtitle, string? instructionFile)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _title = title;
        _subtitle = subtitle;
        _layout = LabelLayout.FromConfig(services.Cfg);

        var (content, explanation) = ResolveQrContent(services, instructionFile);
        _qrContent = content;

        HeaderText.Text = $"{title}\n{subtitle}".Trim();
        // Печатать наклейку до готовности документа — нормально и задумано: ссылка постоянная, по
        // ней сейчас откроется заглушка, а потом ровно тот же QR откроет саму инструкцию.
        LinkText.Text = InstructionStub.IsStub(instructionFile)
            ? "Инструкция ещё в разработке — по ссылке пока откроется страница «Инструкция в разработке». "
              + "Ссылка постоянная: когда документ допишут, тот же код откроет его, наклейку переделывать не нужно.\n"
              + explanation
            : explanation;

        FillLayoutInputs(_layout);
        Redraw();
    }

    // ── Макет ────────────────────────────────────────────────────────────────

    private void FillLayoutInputs(LabelLayout v)
    {
        _filling = true;
        try
        {
            WidthInput.Text = Num(v.WidthMm);
            HeightInput.Text = Num(v.HeightMm);
            MarginInput.Text = Num(v.MarginMm);
            OffsetXInput.Text = Num(v.OffsetXMm);
            OffsetYInput.Text = Num(v.OffsetYMm);
            QrInput.Text = Num(v.QrMm);
            TitlePtInput.Text = Num(v.TitlePt);
            CaptionPtInput.Text = Num(v.CaptionPt);
            ShowLinkCheck.IsChecked = v.ShowLink;
            ShowFrameCheck.IsChecked = v.ShowFrame;
            FancyQrCheck.IsChecked = v.FancyQr;
            ShowHeadlineCheck.IsChecked = v.ShowHeadline;
            HeadlineInput.Text = v.HeadlineText;
            HoleTextInput.Text = v.HoleText;
        }
        finally
        {
            _filling = false;
        }
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>Недописанное число («3,» посреди набора) не должно ни ронять предпросмотр, ни
    /// прыгать на подставленный ноль — оставляем прежнее значение до тех пор, пока в поле не
    /// появится что-то разбираемое.</summary>
    private static double Read(TextBox box, double fallback)
    {
        var raw = (box.Text ?? "").Trim().Replace(',', '.');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private LabelLayout ReadLayout() => new LabelLayout
    {
        WidthMm = Read(WidthInput, _layout.WidthMm),
        HeightMm = Read(HeightInput, _layout.HeightMm),
        MarginMm = Read(MarginInput, _layout.MarginMm),
        OffsetXMm = Read(OffsetXInput, _layout.OffsetXMm),
        OffsetYMm = Read(OffsetYInput, _layout.OffsetYMm),
        QrMm = Read(QrInput, _layout.QrMm),
        TitlePt = Read(TitlePtInput, _layout.TitlePt),
        CaptionPt = Read(CaptionPtInput, _layout.CaptionPt),
        ShowLink = ShowLinkCheck.IsChecked == true,
        ShowFrame = ShowFrameCheck.IsChecked == true,
        FancyQr = FancyQrCheck.IsChecked == true,
        ShowHeadline = ShowHeadlineCheck.IsChecked == true,
        // Текст читается СЫРЫМ, без Clamped-обрезки на каждое нажатие клавиши: иначе набор длинной
        // подписи обрубался бы прямо под пальцами. Приведение к рабочему виду делает Clamped ниже.
        HeadlineText = HeadlineInput.Text ?? "",
        HoleText = HoleTextInput.Text ?? "",
    }.Clamped();

    private void Layout_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling) return;
        _layout = ReadLayout();
        Redraw();
    }

    private void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        _layout.SaveTo(_services.Cfg);
        _host.ShowStatus($"Макет этикетки сохранён: {_layout.SizeCaption()} мм, поля {Num(_layout.MarginMm)} мм");
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        _layout = new LabelLayout();
        FillLayoutInputs(_layout);
        Redraw();
    }

    private void Redraw()
    {
        LabelHost.Content = BuildLabel(out var plan);

        // Раскладка сама ужимает то, что не помещается, — значит, обрезанного содержимого не будет
        // ни при каких настройках. Но молчать о том, что напечатается не ровно заказанное, нельзя:
        // человек ставит «сторону QR 85» и должен видеть здесь же, что реально уйдёт 55.
        WarningText.Text = plan.WarningText;
        WarningBox.Visibility = plan.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var printer = _services.Cfg.LabelPrinter();
        PrinterText.Text = (string.IsNullOrWhiteSpace(printer) ? "Принтер: по умолчанию" : $"Принтер: {printer}")
                           + $" · этикетка {_layout.SizeCaption()} мм · поля {Num(_layout.MarginMm)} мм"
                           + $" · QR {Num(plan.Qr.W)} мм"
                           + " · сменить принтер — Настройки → Печать";
    }

    /// <summary>«ИНСТ» в окошке кода — не украшение: наклейки на шкафу оказываются рядом (паспорт,
    /// ОТК, инструкция), и по одному взгляду должно быть понятно, куда ведёт именно эта.</summary>
    private FrameworkElement BuildLabel() => BuildLabel(out _);

    private FrameworkElement BuildLabel(out LabelPlan plan) =>
        LabelPrinter.BuildLabel(_layout, _qrContent, _title, _subtitle, _qrContent, _layout.EffectiveHoleText(), out plan);

    // ── Содержимое кода ──────────────────────────────────────────────────────

    /// <summary>Что зашить в QR и как это объяснить человеку. Ссылка считается от корня диска
    /// прошивок: относительный путь файла инструкции надо брать от него, иначе адрес соберётся с
    /// чужим хвостом.</summary>
    private static (string Content, string Explanation) ResolveQrContent(AppServices services, string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return ("", "Файл инструкции не найден — печатать нечего.");

        var baseUrl = services.Cfg.InstructionBaseUrl();
        var root = services.Cfg.RootPath();

        // Ссылка обязана совпасть с тем, что реально ляжет на хостинг, причём в двух местах сразу:
        // справочник написаний тот же (см. TranslitMap), и расширение — тоже итоговое. Инструкция в
        // формате Word уезжает туда собранным PDF (см. InstructionPublisher), и наклейка, ведущая на
        // .docx, вела бы в пустоту — при том что напечатана и наклеена она обычно раньше, чем кто-то
        // это заметит.
        var published = InstructionPublisher.AsPublishedName(file);
        if (LabelLinkBuilder.BuildUrl(baseUrl, root, published, services.Cfg.Translit()) is { } url)
            return (url, $"В QR: {url}");

        var unc = NetworkPathHelper.TryResolveUnc(file) ?? file;
        var why = string.IsNullOrWhiteSpace(baseUrl)
            ? "Веб-адрес диска инструкций не задан (Настройки → Печать), поэтому в QR — сетевой путь: с компьютера откроется, с телефона нет."
            : "Файл лежит вне настроенного диска инструкций, поэтому в QR — сетевой путь, а не ссылка.";
        return (unc, $"{why}\nВ QR: {unc}");
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_qrContent))
        {
            AppMessageBox.Show("Печатать нечего: файл инструкции не найден.", "QR и этикетка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Печатаем СВЕЖИЙ визуал, а не тот, что показан в окне: показанный уже «занят» деревом
        // предпросмотра, и печать чужого визуального родителя в WPF не работает.
        var outcome = LabelPrinter.Print(BuildLabel(), _services.Cfg.LabelPrinter(), $"Этикетка — {_title}");
        _host.ShowStatus(outcome.Message, category: NotificationCategory.General);
        if (!outcome.Ok)
            AppMessageBox.Show(outcome.Message, "QR и этикетка", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_qrContent)) return;
        try
        {
            Clipboard.SetText(_qrContent);
            _host.ShowStatus("Ссылка скопирована");
        }
        catch (Exception)
        {
            // Буфер обмена занят другим приложением — не повод показывать ошибку.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Открыть окно для конкретной прошивки. Файл инструкции ищется тем же резолвером, что и
    /// пункты «открыть/печать инструкции» на карточке — QR обязан вести на тот же документ.</summary>
    public static void ShowFor(Window? owner, AppServices services, IAppHost host,
        string title, string subtitle, string? instructionFile)
    {
        var dlg = new InstructionLabelWindow(services, host, title, subtitle, instructionFile) { Owner = owner };
        dlg.ShowDialog();
    }
}
