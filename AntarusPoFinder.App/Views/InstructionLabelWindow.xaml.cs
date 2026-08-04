using System;
using System.IO;
using System.Windows;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>«QR и этикетка» — печать наклейки со ссылкой на инструкцию, чтобы наладчик у шкафа
/// открыл документ телефоном, а не искал его по диску.
///
/// Что попадает в QR — решает <see cref="LabelLinkBuilder"/>: веб-ссылка, если администратор задал
/// адрес диска инструкций (Настройки → Печать), иначе сетевой путь к самому файлу. Второй вариант с
/// телефона не открыть, поэтому окно честно говорит, что именно зашито в код.
///
/// Размер этикетки и принтер — тоже из Настройки → Печать: у наклеек предприятия свой формат, а
/// принтер этикеток обычно не тот, что стоит принтером по умолчанию.</summary>
public partial class InstructionLabelWindow : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private readonly string _qrContent;
    private readonly string _title;
    private readonly string _subtitle;

    public InstructionLabelWindow(AppServices services, IAppHost host, string title, string subtitle, string? instructionFile)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _title = title;
        _subtitle = subtitle;

        var (content, explanation) = ResolveQrContent(services, instructionFile);
        _qrContent = content;

        HeaderText.Text = $"{title}\n{subtitle}".Trim();
        LinkText.Text = explanation;
        var printer = services.Cfg.LabelPrinter();
        PrinterText.Text = string.IsNullOrWhiteSpace(printer)
            ? $"Принтер: по умолчанию · этикетка {Size()} мм · сменить — Настройки → Печать"
            : $"Принтер: {printer} · этикетка {Size()} мм · сменить — Настройки → Печать";

        LabelHost.Content = BuildLabel();
    }

    private string Size() =>
        $"{_services.Cfg.LabelWidthMm():0.##} × {_services.Cfg.LabelHeightMm():0.##}";

    private FrameworkElement BuildLabel() =>
        LabelPrinter.BuildLabel(_services.Cfg.LabelWidthMm(), _services.Cfg.LabelHeightMm(),
            LabelPrinter.MakeQr(_qrContent), _title, _subtitle, _qrContent);

    /// <summary>Что зашить в QR и как это объяснить человеку. Ссылка считается от того диска, на
    /// котором файл реально лежит: инструкции могут быть уведены на третий диск, и относительный путь
    /// надо брать от ЕГО корня, иначе адрес соберётся с чужим хвостом.</summary>
    private static (string Content, string Explanation) ResolveQrContent(AppServices services, string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return ("", "Файл инструкции не найден — печатать нечего.");

        var baseUrl = services.Cfg.InstructionBaseUrl();
        var third = services.Cfg.ThirdDiskPath();
        var root = services.Cfg.RootPath();

        var diskRoot = !string.IsNullOrWhiteSpace(third) && LabelLinkBuilder.RelativeTo(third, file) is not null
            ? third
            : root;

        if (LabelLinkBuilder.BuildUrl(baseUrl, diskRoot, file) is { } url)
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
