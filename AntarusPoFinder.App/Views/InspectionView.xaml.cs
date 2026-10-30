using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using QRCoder;

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

    /// <summary>Файлы, которые наблюдатель уже увидел, но которые ещё НЕ дописаны (телефон кладёт
    /// снимок по SMB, и до конца копирования это обрезанный файл). Ключ — полный путь, значение —
    /// длина на прошлом тике таймера ниже. Пока файл здесь, он показывается в списке как
    /// «копируется…» и не открывается в предпросмотре.</summary>
    private readonly Dictionary<string, long> _incoming = new(StringComparer.OrdinalIgnoreCase);
    private System.Windows.Threading.DispatcherTimer? _incomingTimer;

    /// <summary>Подпись текущего содержимого списка — чтобы не пересобирать ItemsSource на каждое
    /// событие наблюдателя (их на одно копирование прилетает пачка) и не сбрасывать этим выделение
    /// и предпросмотр под руками у человека.</summary>
    private string _fileListSignature = "";

    /// <summary>Путь, который показывается телефону (UNC) — null, когда показывать нечего.</summary>
    private string? _smbPath;

    // ── Image preview state (rotate, view-only — and zoom) ───────────────────
    private static readonly double[] ZoomSteps = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0];
    private BitmapSource? _previewOriginalImage;
    private int _previewRotationDeg;
    private bool _zoomFit = true;
    private double _zoomScale = 1.0;

    private class FileRow
    {
        public string FullPath { get; init; } = "";
        /// <summary>Файл ещё дописывается (копируется с телефона по сети) — трогать его рано.</summary>
        public bool Incomplete { get; init; }
        public string Name => Path.GetFileName(FullPath);
        public string DateText => Incomplete
            ? "копируется…"
            : File.Exists(FullPath) ? File.GetLastWriteTime(FullPath).ToString("dd.MM HH:mm") : "";
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
        UpdateSmbPath();
        LoadScanResolution();
        LoadInspectionCleanupInputs();
        StartPhotoServer();
        RefreshFileList();
    }

    /// <summary>Splits the stored total-minutes setting back into the three inputs — see
    /// ConfigService.InspectionAutoCleanupMinutes for the days->minutes migration this also
    /// transparently picks up for existing installs that only ever configured whole days.</summary>
    private void LoadInspectionCleanupInputs()
    {
        var totalMinutes = _services.Cfg.InspectionAutoCleanupMinutes();
        InspectionCleanupDaysInput.Text = (totalMinutes / 1440).ToString();
        InspectionCleanupHoursInput.Text = (totalMinutes % 1440 / 60).ToString();
        InspectionCleanupMinutesInput.Text = (totalMinutes % 60).ToString();
    }

    /// <summary>Автоочистка/качество сканирования are "set once, rarely touched" settings — collapsed
    /// behind this toggle instead of sitting permanently on the toolbar, so the main Осмотр area isn't
    /// cluttered with controls most sessions never need to look at again.</summary>
    private void InspectionSettingsToggle_Click(object sender, RoutedEventArgs e) =>
        InspectionSettingsPanel.Visibility = InspectionSettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Moved here from Настройки → Сетевые диски — logically belongs right next to the
    /// folder it actually cleans, same reasoning as scan resolution living here instead of there.
    /// Round 34: days-only widened to days/hours/minutes (see ConfigService.
    /// InspectionAutoCleanupMinutes) so a short age like "2 hours" can actually be configured
    /// instead of rounding down to whole days.</summary>
    ///
    /// Кнопки «Сохранить» здесь больше нет — три поля сохраняются сами, по уходу фокуса и по Enter
    /// (как и остальные настройки приложения). Мусорный ввод не открывает модальное окно: поля
    /// возвращаются к сохранённому значению, а причина уходит в нижнюю строку состояния.</summary>
    private void InspectionCleanup_LostFocus(object sender, RoutedEventArgs e) => SaveInspectionCleanup();

    private void InspectionCleanup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveInspectionCleanup();
    }

    private void SaveInspectionCleanup()
    {
        if (!int.TryParse(InspectionCleanupDaysInput.Text.Trim(), out var d) || d < 0 ||
            !int.TryParse(InspectionCleanupHoursInput.Text.Trim(), out var h) || h < 0 ||
            !int.TryParse(InspectionCleanupMinutesInput.Text.Trim(), out var m) || m < 0)
        {
            LoadInspectionCleanupInputs();
            _host.ShowStatus("Автоочистка папки осмотра: нужны целые неотрицательные числа (0/0/0 — отключить)",
                category: NotificationCategory.Inspection);
            return;
        }

        var totalMinutes = d * 24 * 60 + h * 60 + m;
        if (totalMinutes == _services.Cfg.InspectionAutoCleanupMinutes()) return;

        _services.Cfg.SetInspectionAutoCleanupMinutes(totalMinutes);
        _host.ShowStatus(totalMinutes == 0 ? "Автоочистка папки осмотра отключена" : $"Автоочистка папки осмотра: файлы старше {InspectionCleanupService.FormatAge(totalMinutes)}", category: NotificationCategory.Inspection);
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

    /// <summary>Уход с вкладки обязан снять ВСЁ фоновое: и сокет приёма фото, и наблюдатель за
    /// папкой, и таймер дозаписи. Наблюдатель держит дескриптор каталога (на сетевой шаре — ещё и
    /// сессию SMB) и зовёт обратно в этот UserControl; переживи он вкладку — события продолжали бы
    /// приходить в уже выброшенную страницу, а на закрытии окна упирались бы в мёртвый диспетчер.</summary>
    private void Deactivate()
    {
        _server?.Dispose();
        _server = null;
        _watcher?.Dispose();
        _watcher = null;
        _incomingTimer?.Stop();
        _incomingTimer = null;
        _incoming.Clear();
        DisposePdfView();
    }

    // ── Путь для телефона (SMB) ─────────────────────────────────────────────

    /// <summary>Показывает адрес папки осмотра в том виде, в котором его можно ввести на телефоне.
    ///
    /// Почему именно UNC, а не то, что записано в настройках. Папка осмотра почти всегда задана
    /// буквой подключённого сетевого диска (<c>Z:\…</c>), а буква — выдумка ЭТОГО компьютера: она
    /// живёт в его таблице подключений и за его пределами не значит ничего. Телефону нужен адрес
    /// самой шары, поэтому букву разворачиваем через WNetGetConnection
    /// (<see cref="NetworkPathHelper.TryResolveUnc"/>) в <c>\сервер\шара\…</c>.
    ///
    /// Если развернуть не удалось — папка локальная и по сети не раздаётся. Показывать её путь
    /// нельзя: человек перепишет его на телефон и будет думать, что это он ошибся, а зайти туда
    /// нельзя в принципе. Поэтому вместо пути — прямой текст о том, что так не получится.
    ///
    /// Только бета (решение по фотографиям осмотра от 30.10.2026) — в рабочей версии панель не
    /// появляется вовсе.</summary>
    private void UpdateSmbPath()
    {
        if (!AntarusPoFinder.Core.Domain.AppIdentity.IsBeta)
        {
            SmbPathPanel.Visibility = Visibility.Collapsed;
            _smbPath = null;
            return;
        }
        SmbPathPanel.Visibility = Visibility.Visible;

        var proto = _services.Cfg.Get("inspection_folder");
        if (string.IsNullOrEmpty(proto))
        {
            ShowSmbWarning("Папка осмотра не задана — задать её можно в разделе «Настройки». Пока её нет, класть снимки с телефона некуда.");
            return;
        }

        var unc = NetworkPathHelper.TryResolveUnc(proto);
        if (unc is null)
        {
            ShowSmbWarning($"Папка осмотра лежит на этом компьютере ({proto}) и по сети не раздаётся — с телефона в неё не зайти. "
                + "Чтобы класть снимки прямо в папку, выберите в «Настройках» папку на сетевом диске или попросите администратора открыть к этой общий доступ.");
            return;
        }

        _smbPath = unc;
        SmbPathText.Text = unc;
        SmbPathRow.Visibility = Visibility.Visible;
        SmbPathWarning.Visibility = Visibility.Collapsed;
    }

    private void ShowSmbWarning(string text)
    {
        _smbPath = null;
        SmbPathText.Text = "";
        SmbPathRow.Visibility = Visibility.Collapsed;
        SmbPathWarning.Text = text;
        SmbPathWarning.Visibility = Visibility.Visible;
    }

    private void CopySmbPath_Click(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (string.IsNullOrEmpty(_smbPath)) return;
        if (!ClipboardSafe.TrySetTextWithNote(_smbPath, this, _host, "Путь скопирован"))
            _host.ShowStatus("Буфер обмена занят другой программой — повторите", category: NotificationCategory.Inspection);
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

        // Папка осмотра теперь обычно лежит на сетевом диске (туда телефон кладёт снимки по SMB), а
        // сетевой диск отваливается: сервер выключили, ноутбук унесли из конторы, VPN отвалился.
        // Голый Directory.CreateDirectory в этом случае бросал исключение прямо из Loaded — то есть
        // вкладка «Осмотр» не открывалась вовсе и роняла программу. Папка недоступна — это повод
        // сказать об этом и не поднимать приём фото, а не падать.
        try
        {
            Directory.CreateDirectory(proto);
        }
        catch (Exception ex)
        {
            QrImage.Source = null;
            QrInfoText.Text = $"Папка осмотра недоступна:\n{ex.Message}";
            return;
        }

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

    /// <summary>Наблюдатель за папкой осмотра — тот же приём, что у Конфиг\revision.json в
    /// MainWindowViewModel.SetupConfigWatcher, и с теми же оговорками: сетевая шара может не
    /// поддерживать уведомления вовсе, пропасть в момент вызова или отвалиться посреди работы. Во
    /// всех случаях молча остаёмся на ручном «Обновить» и обходе при открытии вкладки.
    ///
    /// События приходят на СВОЁМ потоке, а не на UI, — отсюда Dispatcher. Именно BeginInvoke, а не
    /// Invoke: Invoke синхронный, и если поток наблюдателя встанет в него ровно тогда, когда UI-поток
    /// выполняет Deactivate, Dispose наблюдателя будет ждать завершения обратного вызова, а тот —
    /// освобождения UI-потока. Ставить задачу в очередь и отпускать поток наблюдателя безопаснее.</summary>
    private void RestartWatcher(string proto)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = new FileSystemWatcher(proto)
            {
                // Size/LastWrite — чтобы видеть дозапись копируемого файла, а не только его появление.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, e) => OnWatcherFileAppeared(e.FullPath);
            _watcher.Changed += (_, e) => OnWatcherFileAppeared(e.FullPath);
            _watcher.Renamed += (_, e) => OnWatcherFileAppeared(e.FullPath);
            _watcher.Deleted += (_, _) => BeginRefresh();
        }
        catch { /* best effort — list still refreshes manually / on tab visit */ }
    }

    private void BeginRefresh()
    {
        try { Dispatcher.BeginInvoke(RefreshFileList); }
        catch { /* страница уже выброшена, диспетчер закрывается — событию просто некуда деться */ }
    }

    /// <summary>В папке что-то появилось или дописалось. Сразу показывать файл как готовый нельзя:
    /// по SMB он приезжает кусками, и в момент события это, как правило, ещё обрезанный JPEG — как
    /// раз тот случай, когда предпросмотр показывает половину снимка или вообще отказывается его
    /// открывать. Поэтому файл берётся «на карандаш», а в список попадает пометкой «копируется…»,
    /// пока таймер ниже не убедится, что запись кончилась.</summary>
    private void OnWatcherFileAppeared(string path)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded) return;
                if (string.Equals(Path.GetFileName(path), "Thumbs.db", StringComparison.OrdinalIgnoreCase)) return;
                if (!File.Exists(path)) { RefreshFileList(); return; }
                if (_incoming.ContainsKey(path)) return; // уже ждём этот файл — пачка событий на одну запись

                // -1 = длину ещё не мерили: первый тик её запомнит, второй сравнит.
                _incoming[path] = -1;
                EnsureIncomingTimer();
                RefreshFileList();
            });
        }
        catch { /* см. BeginRefresh */ }
    }

    private void EnsureIncomingTimer()
    {
        if (_incomingTimer is not null) return;
        _incomingTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _incomingTimer.Tick += (_, _) => PollIncoming();
        _incomingTimer.Start();
    }

    /// <summary>Раз в секунду проверяет, дописались ли ожидаемые файлы. Готовность — два признака
    /// сразу: файл открывается на чтение БЕЗ общего доступа (значит, писатель его отпустил) и его
    /// длина не изменилась с прошлого тика. Одной блокировки мало: часть реализаций SMB не держит
    /// файл заблокированным между порциями записи, и в паузе он открывается как ни в чём не
    /// бывало. Одной длины тоже мало: копирование может встать на секунду посреди большого файла.</summary>
    private void PollIncoming()
    {
        if (!IsLoaded) { _incomingTimer?.Stop(); _incomingTimer = null; _incoming.Clear(); return; }

        var ready = new List<string>();
        var vanished = new List<string>();
        foreach (var path in _incoming.Keys.ToList())
        {
            if (!File.Exists(path)) { vanished.Add(path); continue; }

            var length = IncomingFileProbe.TryMeasureIfFree(path);
            if (length is null)
            {
                // Занят писателем (или отобрали права/отвалилась шара) — ждём следующего тика.
                // Сброс запомненной длины: после перерыва её надо мерить заново.
                _incoming[path] = -1;
                continue;
            }

            if (_incoming[path] == length.Value) ready.Add(path);
            else _incoming[path] = length.Value;
        }

        foreach (var path in vanished) _incoming.Remove(path);
        foreach (var path in ready) _incoming.Remove(path);

        if (_incoming.Count == 0) { _incomingTimer?.Stop(); _incomingTimer = null; }
        if (ready.Count == 0 && vanished.Count == 0) return;

        RefreshFileList();
        if (ready.Count == 1)
            _host.ShowStatus($"В папке осмотра новый файл: {Path.GetFileName(ready[0])}", category: NotificationCategory.Inspection);
        else if (ready.Count > 1)
            _host.ShowStatus($"В папке осмотра новых файлов: {ready.Count}", category: NotificationCategory.Inspection);
    }

    private void RefreshFileList()
    {
        UpdateProtoLabel();
        var proto = _services.Cfg.Get("inspection_folder");
        if (string.IsNullOrEmpty(proto) || !Directory.Exists(proto))
        {
            FilesList.ItemsSource = null;
            FileCountText.Text = "Файлы";
            _fileListSignature = "";
            return;
        }
        if (_watcher is null || !string.Equals(_watcher.Path, proto, StringComparison.OrdinalIgnoreCase))
            RestartWatcher(proto);

        List<FileRow> files;
        try
        {
            files = Directory.EnumerateFiles(proto)
                .Where(f => !string.Equals(Path.GetFileName(f), "Thumbs.db", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTime)
                .Select(f => new FileRow { FullPath = f, Incomplete = _incoming.ContainsKey(f) })
                .ToList();
        }
        catch
        {
            // Шара отвалилась ровно между Directory.Exists выше и обходом — оставляем список как
            // есть, следующий тик наблюдателя или «Обновить» его поправят.
            return;
        }

        FileCountText.Text = files.Count > 0 ? $"Файлы ({files.Count})" : "Файлы — папка пуста";

        // Наблюдатель за папкой шлёт пачку событий на одно копирование, и пересборка ItemsSource на
        // каждое из них сбрасывала бы выделение и перезагружала предпросмотр прямо под руками.
        // Поэтому список трогаем, только когда он действительно изменился.
        var signature = string.Join("|", files.Select(f => $"{f.FullPath}?{f.Incomplete}?{f.DateText}"));
        if (signature == _fileListSignature && FilesList.ItemsSource is not null) return;
        _fileListSignature = signature;

        var selected = FilesList.SelectedItems.Cast<FileRow>().Select(r => r.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FilesList.ItemsSource = files;
        if (selected.Count == 0) return;
        foreach (var row in files.Where(f => selected.Contains(f.FullPath)))
            FilesList.SelectedItems.Add(row);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateSmbPath();
        RefreshFileList();
    }

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
        _previewRotationDeg = 0;
        _zoomFit = true;
        _zoomScale = 1.0;
        ImageEditToolbar.Visibility = Visibility.Collapsed;

        if (FilesList.SelectedItem is not FileRow row || !File.Exists(row.FullPath))
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewTitleText.Text = "Предпросмотр";
            return;
        }

        // Файл ещё приезжает по сети — открывать его рано: получится половина снимка или ошибка
        // чтения. Как только копирование кончится (см. PollIncoming), список обновится сам.
        if (row.Incomplete)
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewTitleText.Text = row.Name;
            PreviewPlaceholder.Text = "Файл ещё копируется — предпросмотр появится, когда копирование закончится.";
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
                // Starts at "fit to pane" (like a PDF viewer's default open) — the Image control
                // used to sit directly inside a ScrollViewer with Stretch="Uniform", which hands its
                // child effectively unlimited space, so Stretch never had anything to actually fit
                // against and every image rendered at native pixel size regardless of pane size.
                // Now an explicit ScaleTransform drives the size (see UpdateZoomDisplayAndScale), so
                // the user can zoom in/out from that starting point instead of only ever seeing fit.
                _previewOriginalImage = bmp;
                _zoomFit = true;
                PreviewImage.Source = bmp;
                PreviewImageScroll.Visibility = Visibility.Visible;
                ImageEditToolbar.Visibility = Visibility.Visible;
                UpdateZoomDisplayAndScale();
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

    // ── Image preview: rotate + zoom (view-only — no crop, no save; see round notes:
    //    Осмотр is a working folder, this preview is deliberately read-only) ──────────────────

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

    private void Rotate(int deltaDeg)
    {
        if (_previewOriginalImage is null) return;
        _previewRotationDeg = ((_previewRotationDeg + deltaDeg) % 360 + 360) % 360;
        ApplyPreviewRotation();
        // A 90°/270° rotation swaps width/height, which changes what "fit" means — recompute it
        // immediately instead of waiting for the next resize event.
        UpdateZoomDisplayAndScale();
    }

    /// <summary>Re-derives the displayed bitmap from the untouched original every time (never
    /// rotates the already-rotated preview) — rotating 90°/90°/90° stays lossless and always ends up
    /// pixel-identical to a single 270° rotation, instead of accumulating repeated re-render error.
    /// View-only: this never touches the file on disk, only what's shown here.</summary>
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

    // ── Zoom (PDF-viewer style: opens at "fit to pane", then +/- step through fixed levels,
    //    "По размеру окна" jumps back to auto-fit) ────────────────────────────────────────────

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(+1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        _zoomFit = true;
        UpdateZoomDisplayAndScale();
    }

    /// <summary>Moves to the next fixed zoom level strictly above/below whatever is currently
    /// effective — including out of "fit" mode, starting from whatever percentage fit currently
    /// happens to equal, so the first click after opening an image steps from a sensible point
    /// instead of jumping to an arbitrary fixed level.</summary>
    private void StepZoom(int direction)
    {
        if (_previewOriginalImage is null) return;
        var current = _zoomFit ? ComputeFitScale() : _zoomScale;
        _zoomScale = direction > 0
            ? ZoomSteps.FirstOrDefault(s => s > current + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(s => s < current - 0.001, ZoomSteps[0]);
        _zoomFit = false;
        UpdateZoomDisplayAndScale();
    }

    private double ComputeFitScale()
    {
        var current = PreviewImage.Source as BitmapSource;
        if (current is null || PreviewImageScroll.ActualWidth <= 0 || PreviewImageScroll.ActualHeight <= 0)
            return 1.0;
        var availW = Math.Max(PreviewImageScroll.ActualWidth - 4, 10);
        var availH = Math.Max(PreviewImageScroll.ActualHeight - 4, 10);
        return Math.Max(Math.Min(availW / current.PixelWidth, availH / current.PixelHeight), 0.02);
    }

    private void UpdateZoomDisplayAndScale()
    {
        if (_previewOriginalImage is null) return;
        var scale = _zoomFit ? ComputeFitScale() : _zoomScale;
        PreviewImageScale.ScaleX = scale;
        PreviewImageScale.ScaleY = scale;
        ZoomLevelText.Text = _zoomFit ? "По размеру окна" : $"{Math.Round(scale * 100)}%";
    }

    /// <summary>Recomputes "fit" on resize (window resize, sidebar collapse, etc.) — only while
    /// actually in fit mode, an explicit zoom level the user picked must stay put across a resize.</summary>
    private void PreviewImageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoomFit) UpdateZoomDisplayAndScale();
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
