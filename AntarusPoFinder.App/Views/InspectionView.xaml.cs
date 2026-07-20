using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;
using QRCoder;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

/// <summary>Осмотр: extracted out of SearchView's old inline toolbar (see git history) into its
/// own tab so the QR photo panel and the inspection-folder file list are always visible together,
/// instead of a separate "Фото" button opening a popup dialog.</summary>
public partial class InspectionView : UserControl
{
    private static readonly string[] ImageExtensions = [".bmp", ".jpg", ".jpeg", ".png", ".gif", ".tif", ".tiff"];

    private readonly AppServices _services;
    private readonly IAppHost _host;
    private PhotoUploadServer? _server;
    private FileSystemWatcher? _watcher;
    private Microsoft.Web.WebView2.Wpf.WebView2? _pdfView;

    private class FileRow
    {
        public string FullPath { get; init; } = "";
        public string Name => Path.GetFileName(FullPath);
        public string DateText => File.Exists(FullPath) ? File.GetLastWriteTime(FullPath).ToString("dd.MM HH:mm") : "";
    }

    public InspectionView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Loaded += (_, _) => Activate();
        Unloaded += (_, _) => Deactivate();
    }

    public void RefreshIfActive()
    {
        if (IsLoaded) RefreshFileList();
    }

    private void Activate()
    {
        UpdateProtoLabel();
        StartPhotoServer();
        RefreshFileList();
    }

    private void Deactivate()
    {
        _server?.Dispose();
        _server = null;
        _watcher?.Dispose();
        _watcher = null;
        DisposePdfView();
    }

    // ── Protocol / inspection folder ────────────────────────────────────────

    private void UpdateProtoLabel()
    {
        var raw = _services.Cfg.Get("inspection_folder");
        ProtoPathLabel.Text = string.IsNullOrEmpty(raw) ? "не задана" : raw;
        ProtoPathLabel.ToolTip = raw;
    }

    /// <summary>Folder picking itself lives only in Settings (see SettingsView) — from here we just
    /// point the user there instead of prompting inline, so there's a single place to change it.</summary>
    private bool EnsureProtocolFolder(out string proto)
    {
        proto = _services.Cfg.Get("inspection_folder");
        if (!string.IsNullOrEmpty(proto)) return true;

        if (_services.Cfg.CurrentRole() is "administrator")
        {
            var reply = AppMessageBox.Show(
                "Папка осмотра не задана.\nВыбрать её можно в разделе «Настройки».\n\nОткрыть настройки сейчас?",
                "Папка осмотра", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (reply == MessageBoxResult.Yes) _host.Navigate("settings");
        }
        else
        {
            AppMessageBox.Show(
                "Папка осмотра не задана.\nОбратитесь к администратору — папка выбирается в разделе «Настройки».",
                "Папка осмотра", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return false;
    }

    private void OpenProtocolFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProtocolFolder(out var proto)) return;
        Directory.CreateDirectory(proto);
        Process.Start(new ProcessStartInfo(proto) { UseShellExecute = true });
    }

    private void ClearProtocolFolder_Click(object sender, RoutedEventArgs e)
    {
        var proto = _services.Cfg.Get("inspection_folder");
        if (string.IsNullOrEmpty(proto) || !Directory.Exists(proto))
        {
            AppMessageBox.Show("Папка осмотра не задана.", "Очистка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var reply = AppMessageBox.Show($"Удалить все файлы из:\n{proto}\n\nОтменить нельзя.", "Очистка",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(proto))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch { /* best effort per entry, matches Python's swallowed per-file errors */ }
        }
        _host.ShowStatus("Папка осмотра очищена");
        RefreshFileList();
    }

    // ── Scan (real WIA acquire dialog — no separate app process) ─────────────

    private void ScanDocument_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProtocolFolder(out var proto)) return;
        Directory.CreateDirectory(proto);

        var destName = $"scan_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
        var dest = Path.Combine(proto, destName);
        var dpi = _services.Cfg.ScanResolutionDpi();

        if (!WiaScanner.TryScan(dest, dpi, out var error))
        {
            if (error is not null)
                AppMessageBox.Show(error, "Сканирование", MessageBoxButton.OK, MessageBoxImage.Warning);
            return; // null error = user cancelled the WIA dialog, nothing to report
        }

        // Deliberately doesn't auto-open the result anymore — the operator scans several pages in
        // a row, and a viewer window popping up (and grabbing focus) after each one just got in
        // the way. It's right there in the list below, one click away.
        _host.ShowStatus($"Скан сохранён: {destName}");
        RefreshFileList();
    }

    // ── Photo / QR upload — persistent panel, starts as soon as the tab opens ─

    private void StartPhotoServer()
    {
        var proto = _services.Cfg.Get("inspection_folder");
        if (string.IsNullOrEmpty(proto))
        {
            QrImage.Source = null;
            QrInfoText.Text = "Укажите папку осмотра, чтобы включить приём фото по QR.";
            return;
        }
        Directory.CreateDirectory(proto);

        try
        {
            _server = new PhotoUploadServer(proto, _services.Cfg.ImageServerPort());
            _server.FilesUploaded += count => Dispatcher.Invoke(() =>
            {
                _host.ShowStatus($"Загружено фото: {count}");
                RefreshFileList();
            });

            var qr = new QRCodeGenerator().CreateQrCode(_server.Url, QRCodeGenerator.ECCLevel.Q);
            var bytes = new PngByteQRCode(qr).GetGraphic(16);
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            QrImage.Source = bmp;
            QrInfoText.Text = $"Отсканируйте QR с телефона\nили перейдите по ссылке:\n{_server.Url}";
        }
        catch (System.Net.Sockets.SocketException)
        {
            QrImage.Source = null;
            QrInfoText.Text = $"Порт {_services.Cfg.ImageServerPort()} занят — измените его в настройках.";
        }
    }

    // ── File list ──────────────────────────────────────────────────────────

    private void RestartWatcher(string proto)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = new FileSystemWatcher(proto) { EnableRaisingEvents = true };
            _watcher.Created += (_, _) => Dispatcher.Invoke(RefreshFileList);
            _watcher.Deleted += (_, _) => Dispatcher.Invoke(RefreshFileList);
            _watcher.Renamed += (_, _) => Dispatcher.Invoke(RefreshFileList);
        }
        catch { /* best effort — list still refreshes manually / on tab visit */ }
    }

    private void RefreshFileList()
    {
        UpdateProtoLabel();
        var proto = _services.Cfg.Get("inspection_folder");
        if (string.IsNullOrEmpty(proto) || !Directory.Exists(proto))
        {
            FilesList.ItemsSource = null;
            FileCountText.Text = "Файлы";
            return;
        }
        if (_watcher is null || !string.Equals(_watcher.Path, proto, StringComparison.OrdinalIgnoreCase))
            RestartWatcher(proto);

        var files = Directory.EnumerateFiles(proto)
            .Where(f => !string.Equals(Path.GetFileName(f), "Thumbs.db", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTime)
            .Select(f => new FileRow { FullPath = f })
            .ToList();
        FilesList.ItemsSource = files;
        FileCountText.Text = files.Count > 0 ? $"Файлы ({files.Count})" : "Файлы — папка пуста";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshFileList();

    /// <summary>Double-click opens with the default app — single-click already shows the inline
    /// preview pane (see FilesList_SelectionChanged), matching Windows Explorer's convention.</summary>
    private void FilesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FilesList.SelectedItem is FileRow row && File.Exists(row.FullPath))
            Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true });
    }

    // ── Inline preview pane (Explorer-style: updates on selection, no popup) ─

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DisposePdfView();
        PreviewImageScroll.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewPlaceholder.Text = "Выберите файл в списке слева";

        if (FilesList.SelectedItem is not FileRow row || !File.Exists(row.FullPath))
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewTitleText.Text = "Предпросмотр";
            return;
        }

        PreviewTitleText.Text = row.Name;
        var ext = Path.GetExtension(row.FullPath).ToLowerInvariant();

        if (Array.IndexOf(ImageExtensions, ext) >= 0)
        {
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            try
            {
                var bmp = new BitmapImage();
                using var stream = new FileStream(row.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                PreviewImage.Source = bmp;
                PreviewImageScroll.Visibility = Visibility.Visible;
            }
            catch
            {
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewPlaceholder.Text = "Не удалось открыть файл для просмотра";
            }
        }
        else if (ext == ".pdf")
        {
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            _ = ShowPdfAsync(row.FullPath);
        }
        else
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewPlaceholder.Text = "Нет встроенного просмотра для этого типа файла — двойной клик открывает файл в связанном приложении.";
        }
    }

    /// <summary>The WebView2 control is created on demand, only while a PDF is actually selected —
    /// never added to the visual tree for images or when nothing's selected, so its own startup
    /// behavior (Evergreen Runtime check/first-run) can never fire outside that one case.</summary>
    private async System.Threading.Tasks.Task ShowPdfAsync(string path)
    {
        try
        {
            _pdfView ??= new Microsoft.Web.WebView2.Wpf.WebView2();
            if (!PreviewHost.Children.Contains(_pdfView))
                PreviewHost.Children.Add(_pdfView);
            await _pdfView.EnsureCoreWebView2Async();
            _pdfView.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
        }
        catch (Exception ex)
        {
            DisposePdfView();
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewPlaceholder.Text = $"Не удалось открыть предпросмотр PDF:\n{ex.Message}";
        }
    }

    private void DisposePdfView()
    {
        if (_pdfView is null) return;
        PreviewHost.Children.Remove(_pdfView);
        _pdfView.Dispose();
        _pdfView = null;
    }

    // ── Context menu: open / preview / open folder / delete ─────────────────

    /// <summary>Right-click doesn't select the item under the cursor by itself — without this the
    /// context menu commands would act on whatever was selected before, not what was clicked.</summary>
    private void FilesList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FilesList, e.OriginalSource as DependencyObject) is ListBoxItem item)
            item.IsSelected = true;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is FileRow row && File.Exists(row.FullPath))
            Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true });
    }

    private void OpenContainingFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not FileRow row || !File.Exists(row.FullPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.FullPath}\"") { UseShellExecute = true });
    }

    private void DeleteFile_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not FileRow row || !File.Exists(row.FullPath))
        {
            AppMessageBox.Show("Выберите файл в списке.", "Удаление файла", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reply = AppMessageBox.Show($"Удалить файл:\n{row.Name}?\n\nОтменить нельзя.", "Удаление файла",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(row.FullPath);
            _host.ShowStatus($"Файл удалён: {row.Name}");
            RefreshFileList();
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось удалить файл:\n{ex.Message}", "Удаление файла",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
