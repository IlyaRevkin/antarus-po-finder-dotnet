using System.Windows;
using AntarusPoFinder.App.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Показать QR на уже готовую ссылку — «дай посмотреть, что получится», не поднимая окно
/// этикетки и ничего не печатая.
///
/// Зачем отдельно от «QR и этикетка»: то окно про наклейку на шкаф — макет, поля, принтер. Здесь
/// вопрос другой и куда более частый: проверить телефоном, что выложенный документ открывается.
/// Раньше для этого ссылку копировали и пересылали себе в мессенджер.</summary>
public partial class QrPeekWindow : Window
{
    private readonly string _url;

    private QrPeekWindow(string url)
    {
        InitializeComponent();
        _url = url;
        UrlText.Text = url;
        QrImage.Source = LabelPrinter.MakeQr(url);
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => ClipboardSafe.TrySetText(_url);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Открыть окно. Пустой адрес — не повод показывать пустую рамку: у строки, которая
    /// ещё не выложена, ссылки просто нет, и честнее сказать об этом словами.</summary>
    public static void ShowFor(Window? owner, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            AppMessageBox.Show("У этой строки ещё нет ссылки на хостинге — сначала выложите файл.",
                "QR на ссылку", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new QrPeekWindow(url) { Owner = owner }.ShowDialog();
    }
}
