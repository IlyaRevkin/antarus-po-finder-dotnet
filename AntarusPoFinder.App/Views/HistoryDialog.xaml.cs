using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AntarusPoFinder.Core.Domain;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public partial class HistoryDialog : Window
{
    private class Row
    {
        public FwVersionRecord Record { get; init; } = null!;
        public string VersionRaw => Record.VersionRaw;
        public string DateDisplay => Record.DtStr.Length == 13
            ? $"{Record.DtStr[6..8]}.{Record.DtStr[4..6]}.{Record.DtStr[0..4]} {Record.DtStr[9..11]}:{Record.DtStr[11..13]}"
            : Record.UploadDate;
        public string CtrlName => Record.CtrlName;
        public bool IsRolledBack => Record.Status == "rolled_back";
        public string StatusLabel => IsRolledBack ? "Откатана" : "Активна";
        public string DescriptionShort => Record.Description.Length > 80 ? Record.Description[..80] + "…" : Record.Description;
    }

    public HistoryDialog(string cabinetTitle, System.Collections.Generic.List<FwVersionRecord> versions)
    {
        InitializeComponent();
        Title = $"История версий — {cabinetTitle}";
        VersionsGrid.ItemsSource = versions.Select(v => new Row { Record = v }).ToList();
        if (VersionsGrid.Items.Count > 0) VersionsGrid.SelectedIndex = 0;
    }

    private void VersionsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (VersionsGrid.SelectedItem is not Row row) { DetailText.Text = ""; return; }
        var r = row.Record;
        var blocks = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(r.Description)) blocks.Add($"Описание:\n{r.Description}");
        if (!string.IsNullOrEmpty(r.Changelog) && r.Changelog != r.Description) blocks.Add($"Изменения:\n{r.Changelog}");
        blocks.Add($"Путь:\n{(string.IsNullOrEmpty(r.DiskPath) ? r.LocalPath : r.DiskPath)}");
        DetailText.Text = string.Join("\n\n", blocks);
    }

    private void VersionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!DataGridClickGuard.IsOverDataRow(e)) return;
        if (VersionsGrid.SelectedItem is not Row row) return;
        var path = !string.IsNullOrEmpty(row.Record.DiskPath) ? row.Record.DiskPath : row.Record.LocalPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            AppMessageBox.Show($"Папка не существует:\n{path}", "История", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
