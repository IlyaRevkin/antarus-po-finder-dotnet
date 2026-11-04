using System;
using System.IO;
using System.Windows;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>⚠️ ВРЕМЕННОЕ ОКНО — осмотр того, что осталось от прежней модели исполнений
/// (см. LegacyExecutionScan). Когда дерево перестроено, окно вместе со своим классом выкидывается
/// целиком: кроме кнопки в «Иерархии», от него ничего не зависит.
///
/// Окно ТОЛЬКО ПОКАЗЫВАЕТ. Ни один файл и ни одна запись отсюда не меняются — порядок «сначала
/// осмотр, потом действие» на общей сетевой шаре это единственная защита от перемещения чужих
/// файлов по догадке машины.</summary>
public partial class LegacyExecutionScanDialog : Window
{
    private string _report = "";

    public LegacyExecutionScanDialog() => InitializeComponent();

    public static void Show(Window? owner, LegacyExecutionScan.Result result)
    {
        var report = LegacyExecutionScan.Report(result, DateTime.Now);
        var dlg = new LegacyExecutionScanDialog
        {
            Owner = owner,
            _report = report,
        };
        dlg.SummaryText.Text = result.Summary;
        dlg.ReportText.Text = report;
        dlg.ShowDialog();
    }

    /// <summary>Отчёт сохраняется файлом намеренно: разговор «почему эти две записи считаются одной
    /// прошивкой» должен опираться на то, что человек видел, а не на то, что он запомнил.</summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить отчёт осмотра",
            FileName = $"исполнения-осмотр-{DateTime.Now:yyyyMMdd_HHmm}.txt",
            Filter = "Текстовый файл (*.txt)|*.txt",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, _report);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не получилось сохранить отчёт: {ex.Message}",
                "Осмотр исполнений", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
