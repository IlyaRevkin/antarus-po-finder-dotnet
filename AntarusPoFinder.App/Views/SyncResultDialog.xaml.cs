using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

public partial class SyncResultDialog : Window
{
    private readonly string _localDir;

    public SyncResultDialog(HierarchyResult result, string localDir)
    {
        InitializeComponent();
        _localDir = localDir;
        InfoText.Text = $"Оборудование: {result.Name}\nВерсия: {result.VersionRaw}\nКонтроллер: {result.Controller}\nПуть: {localDir}";
    }

    private void OpenFirmware_Click(object sender, RoutedEventArgs e)
    {
        var file = Directory.Exists(_localDir) ? Directory.EnumerateFiles(_localDir).FirstOrDefault() : null;
        Process.Start(new ProcessStartInfo(file ?? _localDir) { UseShellExecute = true });
        Close();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_localDir))
            Process.Start(new ProcessStartInfo(_localDir) { UseShellExecute = true });
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
