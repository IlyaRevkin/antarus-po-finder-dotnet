using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;
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

    // ── Image preview editing state (rotate + save) ──────────────────────────
    private BitmapSource? _previewOriginalImage;
    private string? _previewImagePath;
    private int _previewRotationDeg;

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
        LoadScanResolution();
        StartPhotoServer();
        RefreshFileList();
    }

    /// <summary>Качество сканирования используется только здесь (WiaScanner.TryScan ниже) — раньше
    /// жило в Настройки → Сетевые диски рядом с путями к дискам, что не имело отношения к делу и
    /// было видно только наладчику там же, где он не сканирует. Значение сохраняется сразу при
    /// смене — отдельная кнопка "Сохранить" не нужна для одной настройки прямо у кнопки "Сканировать".</summary>
    private void LoadScanResolution()
    {
        var dpi = _services.Cfg.ScanResolutionDpi().ToString();
        ScanResolutionCombo.SelectionChanged -= ScanResolutionCombo_SelectionChanged;
        ScanResolutionCombo.SelectedItem = ScanResolutionCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Content == dpi) ?? ScanResolutionCombo.Items[2];
        ScanResolutionCombo.SelectionChanged += ScanResolutionCombo_SelectionChanged;
    }

    private void ScanResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScanResolutionCombo.SelectedItem is ComboBoxItem { Content: string dpiText } && int.TryParse(dpiText, out var dpi))
            _services.Cfg.SetScanResolutionDpi(dpi);
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
        _host.ShowStatus("Папка осмотра очищена", category: NotificationCategory.Inspection);
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
        _host.ShowStatus($"Скан сохранён: {destName}", category: NotificationCategory.Inspection);
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
                _host.ShowStatus($"Загружено фото: {count}", category: NotificationCategory.Inspection);
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
        _previewOriginalImage = null;
        _previewImagePath = null;
        _previewRotationDeg = 0;
        ImageEditToolbar.Visibility = Visibility.Collapsed;

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
                // Viewbox (see XAML) auto-scales this to fit the preview pane, both down for a
                // full-size photo and up for a small scan thumbnail — the Image control itself
                // previously sat inside a ScrollViewer, which hands its child effectively unlimited
                // space, so Stretch="Uniform" never had anything to actually fit against and every
                // image just rendered at its native pixel size regardless of the pane's size.
                _previewOriginalImage = bmp;
                _previewImagePath = row.FullPath;
                PreviewImage.Source = bmp;
                PreviewImageScroll.Visibility = Visibility.Visible;
                ImageEditToolbar.Visibility = Visibility.Visible;
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

    // ── Image preview editing: rotate + save (minimal — no crop, see round notes) ────────────

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

    private void Rotate(int deltaDeg)
    {
        if (_previewOriginalImage is null) return;
        _previewRotationDeg = ((_previewRotationDeg + deltaDeg) % 360 + 360) % 360;
        ApplyPreviewRotation();
    }

    /// <summary>Re-derives the displayed bitmap from the untouched original every time (never
    /// rotates the already-rotated preview) — rotating 90°/90°/90° stays lossless and always ends up
    /// pixel-identical to a single 270° rotation, instead of accumulating repeated re-render error.</summary>
    private void ApplyPreviewRotation()
    {
        if (_previewOriginalImage is null) return;
        if (_previewRotationDeg == 0)
        {
            PreviewImage.Source = _previewOriginalImage;
            return;
        }
        var rotated = new TransformedBitmap(_previewOriginalImage, new System.Windows.Media.RotateTransform(_previewRotationDeg));
        rotated.Freeze();
        PreviewImage.Source = rotated;
    }

    private void SaveRotationOverwrite_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRotationToSave(out var name)) return;

        var reply = AppMessageBox.Show(
            $"Перезаписать файл «{name}» повёрнутым изображением?\n\nИсходный файл будет заменён — отменить нельзя.",
            "Сохранить поворот", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        if (!TrySaveRotatedImage(_previewImagePath!, out var error))
        {
            AppMessageBox.Show($"Не удалось сохранить: {error}", "Сохранить поворот", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _previewRotationDeg = 0;
        _host.ShowStatus($"Файл перезаписан: {name}", category: NotificationCategory.Inspection);
        RefreshFileList();
        ReloadPreviewFromDisk();
    }

    private void SaveRotationAsCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureRotationToSave(out _)) return;

        var proto = _services.Cfg.Get("inspection_folder");
        var ext = Path.GetExtension(_previewImagePath!);
        var suggested = Path.Combine(proto, $"{Path.GetFileNameWithoutExtension(_previewImagePath!)}_повёрнуто{ext}");

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить копию с поворотом",
            InitialDirectory = proto,
            FileName = Path.GetFileName(suggested),
            Filter = $"Изображение (*{ext})|*{ext}",
        };
        if (dlg.ShowDialog() != true) return;

        if (!TrySaveRotatedImage(dlg.FileName, out var error))
        {
            AppMessageBox.Show($"Не удалось сохранить: {error}", "Сохранить поворот", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _host.ShowStatus($"Копия сохранена: {Path.GetFileName(dlg.FileName)}", category: NotificationCategory.Inspection);
        RefreshFileList();
    }

    private bool EnsureRotationToSave(out string name)
    {
        name = _previewImagePath is not null ? Path.GetFileName(_previewImagePath) : "";
        if (_previewImagePath is null || _previewOriginalImage is null) return false;
        if (_previewRotationDeg == 0)
        {
            AppMessageBox.Show("Сначала поверните изображение — сохранять нечего.", "Сохранить поворот", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }

    /// <summary>Encodes the currently-rotated bitmap and writes it to <paramref name="destPath"/>,
    /// picking the encoder from its extension (falls back to PNG for anything not recognized —
    /// lossless, never a wrong-format guess). Writes to a sibling temp file first and only replaces
    /// the destination once the encode fully succeeded, so a mid-write failure never leaves a
    /// half-written/corrupt file in the (working, not archival) inspection folder.</summary>
    private bool TrySaveRotatedImage(string destPath, out string error)
    {
        error = "";
        try
        {
            var rotated = _previewRotationDeg == 0
                ? _previewOriginalImage!
                : new TransformedBitmap(_previewOriginalImage!, new System.Windows.Media.RotateTransform(_previewRotationDeg));

            var encoder = CreateEncoderForExtension(Path.GetExtension(destPath));
            encoder.Frames.Add(BitmapFrame.Create(rotated));

            var tmp = destPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                encoder.Save(fs);

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static BitmapEncoder CreateEncoderForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
        ".bmp" => new BmpBitmapEncoder(),
        ".gif" => new GifBitmapEncoder(),
        ".tif" or ".tiff" => new TiffBitmapEncoder(),
        _ => new PngBitmapEncoder(),
    };

    /// <summary>Re-reads the just-saved file from disk (instead of just resetting the in-memory
    /// bitmap) so the preview reflects exactly what's now on disk, including whatever the encoder
    /// actually produced (e.g. JPEG recompression), not the pre-save in-memory version.</summary>
    private void ReloadPreviewFromDisk()
    {
        if (_previewImagePath is null || !File.Exists(_previewImagePath)) return;
        try
        {
            var bmp = new BitmapImage();
            using var stream = new FileStream(_previewImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            _previewOriginalImage = bmp;
            PreviewImage.Source = bmp;
        }
        catch { /* best effort — file list/thumbnail still updated, just the live preview stays stale */ }
    }

    private void PhoneInstructions_Click(object sender, RoutedEventArgs e)
    {
        var proto = _services.Cfg.Get("inspection_folder");
        if (string.IsNullOrEmpty(proto))
        {
            AppMessageBox.Show("Сначала укажите папку осмотра в Настройках.", "Подключение с телефона", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new PhoneNetworkInstructionsDialog(proto) { Owner = Window.GetWindow(this) }.ShowDialog();
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
    /// context menu commands would act on whatever was selected before, not what was clicked.
    /// Exception: if the clicked item is already part of a multi-selection, keep the whole
    /// selection (Explorer-style right-click on a multi-selected block), instead of collapsing it
    /// down to just the one item under the cursor.</summary>
    private void FilesList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FilesList, e.OriginalSource as DependencyObject) is ListBoxItem item
            && !item.IsSelected)
            item.IsSelected = true;
    }

    /// <summary>"Открыть файл"/"Открыть папку" only make sense for a single file — disabled when
    /// several are selected instead of silently acting on just the first one.</summary>
    private void FilesContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var count = FilesList.SelectedItems.Count;
        OpenFileMenuItem.IsEnabled = count == 1;
        OpenFolderMenuItem.IsEnabled = count == 1;
        DeleteMenuItem.Header = count > 1 ? $"Удалить выбранные ({count})" : "Удалить файл";
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

    /// <summary>Handles both the single-selection preview-pane button and the (possibly
    /// multi-selection) context-menu item with one confirmation dialog — deleting N selected files
    /// no longer pops up N separate "are you sure" boxes.</summary>
    private void DeleteFile_Click(object sender, RoutedEventArgs e)
    {
        var rows = FilesList.SelectedItems.Cast<FileRow>().Where(r => File.Exists(r.FullPath)).ToList();
        if (rows.Count == 0)
        {
            AppMessageBox.Show("Выберите файл в списке.", "Удаление файла", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string message;
        if (rows.Count == 1)
        {
            message = $"Удалить файл:\n{rows[0].Name}?\n\nОтменить нельзя.";
        }
        else
        {
            var preview = string.Join("\n", rows.Take(5).Select(r => $"• {r.Name}"));
            var more = rows.Count > 5 ? $"\n… и ещё {rows.Count - 5}" : "";
            message = $"Удалить выбранные файлы ({rows.Count}):\n{preview}{more}\n\nОтменить нельзя.";
        }

        var reply = AppMessageBox.Show(message, "Удаление файла", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        var failed = new List<string>();
        var deletedNames = new List<string>();
        foreach (var row in rows)
        {
            try { File.Delete(row.FullPath); deletedNames.Add(row.Name); }
            catch (Exception ex) { failed.Add($"{row.Name}: {ex.Message}"); }
        }

        _host.ShowStatus(deletedNames.Count == 1 ? $"Файл удалён: {deletedNames[0]}" : $"Удалено файлов: {deletedNames.Count}", category: NotificationCategory.Inspection);
        if (failed.Count > 0)
            AppMessageBox.Show($"Не удалось удалить:\n{string.Join("\n", failed)}", "Удаление файла", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshFileList();
    }
}
