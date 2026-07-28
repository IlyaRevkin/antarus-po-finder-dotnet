using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

using AntarusPoFinder.App;
using AntarusPoFinder.App.Services;

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
        /// <summary>Самая свежая живая версия — выделяется жирным в таблице.</summary>
        public bool IsCurrent { get; init; }
        public string StatusLabel { get; init; } = "";
        public string DescriptionShort => Record.Description.Length > 80 ? Record.Description[..80] + "…" : Record.Description;
    }

    /// <summary>«Активна» стояло у КАЖДОЙ не откатанной строки — то есть у всей истории сразу
    /// (реальная жалоба: «загружаю прошивку, а в истории все активные»). Что именно считать
    /// актуальным — см. FwHistoryStatus; versions приходят от новых к старым, как их отдаёт
    /// Database.GetFwVersionsHistory.</summary>
    public HistoryDialog(string cabinetTitle, System.Collections.Generic.List<FwVersionRecord> versions)
    {
        InitializeComponent();
        Title = $"История версий — {cabinetTitle}";

        var labels = FwHistoryStatus.Labels(versions);
        VersionsGrid.ItemsSource = versions.Select((v, i) => new Row
        {
            Record = v,
            IsCurrent = labels[i] == FwHistoryStatus.Current,
            StatusLabel = labels[i],
        }).ToList();
        if (VersionsGrid.Items.Count > 0) VersionsGrid.SelectedIndex = 0;
    }

    /// <summary>Путь выбранной строки (диск в приоритете, иначе локальный) — открывается кликом
    /// по ссылке «Путь» под описанием и двойным кликом по строке.</summary>
    private string _selectedPath = "";

    /// <summary>Выбранная запись — нужна кнопкам «Скачать на этот ПК»/«Закрепить локально».</summary>
    private FwVersionRecord? _selectedRecord;

    private void VersionsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (VersionsGrid.SelectedItem is not Row row)
        {
            DetailText.Text = "";
            _selectedPath = "";
            _selectedRecord = null;
            PathPanel.Visibility = Visibility.Collapsed;
            RefreshLocalState();
            return;
        }
        var r = row.Record;
        _selectedRecord = r;
        var blocks = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(r.Description)) blocks.Add($"Описание:\n{r.Description}");
        if (!string.IsNullOrEmpty(r.Changelog) && r.Changelog != r.Description) blocks.Add($"Изменения:\n{r.Changelog}");
        DetailText.Text = string.Join("\n\n", blocks);

        _selectedPath = string.IsNullOrEmpty(r.DiskPath) ? r.LocalPath : r.DiskPath;
        if (string.IsNullOrEmpty(_selectedPath))
        {
            PathPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            PathRun.Text = _selectedPath;
            PathPanel.Visibility = Visibility.Visible;
        }
        RefreshLocalState();
    }

    // ── Локальная копия версии (#12): скачать / закрепить ─────────────────────

    /// <summary>Имя прошивки для локального кэша — ровно то же, что строит поиск при скачивании
    /// (SearchService.ToHierarchyResult), иначе метка .keep и папка версии разъехались бы с тем, куда
    /// кладёт файлы обычная синхронизация.</summary>
    private static string LocalName(FwVersionRecord r) => SearchService.ToHierarchyResult(r).Name;

    /// <summary>Пересчитать доступность и подписи кнопок по состоянию выбранной версии: есть ли она
    /// локально, закреплена ли, доступна ли на диске (только тогда её есть откуда скачать).</summary>
    private void RefreshLocalState()
    {
        var r = _selectedRecord;
        if (r is null)
        {
            LocalStatus.Text = "";
            DownloadBtn.IsEnabled = false;
            PinBtn.IsEnabled = false;
            PinBtn.Content = "Закрепить локально";
            return;
        }

        var name = LocalName(r);
        var isLocal = LocalFirmwareCache.HasVersion(name, r.VersionRaw);
        var isKept = LocalFirmwareCache.IsKept(name, r.VersionRaw);
        var diskAvailable = !string.IsNullOrEmpty(r.DiskPath) && Directory.Exists(r.DiskPath);

        LocalStatus.Text = isLocal
            ? (isKept ? "На этом ПК: да · закреплено" : "На этом ПК: да")
            : "На этом ПК: нет";

        // Скачать можно, только когда версия реально лежит на диске (удалённую скачивать неоткуда).
        DownloadBtn.IsEnabled = diskAvailable;
        // Открепить можно всегда, пока метка есть; закрепить — если версия уже локально или её можно
        // сейчас дотянуть с диска.
        PinBtn.Content = isKept ? "Открепить локально" : "Закрепить локально";
        PinBtn.IsEnabled = isKept || isLocal || diskAvailable;
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecord is not { } r) return;
        await DownloadAsync(r, pin: false);
    }

    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecord is not { } r) return;
        var name = LocalName(r);
        if (LocalFirmwareCache.IsKept(name, r.VersionRaw))
        {
            // Открепить — метку убираем сразу, файлы не трогаем (ближайшая авто-синхронизация решит,
            // оставлять ли версию как неактуальную).
            LocalFirmwareCache.SetKept(name, r.VersionRaw, false);
            RefreshLocalState();
            return;
        }
        // Закрепить: если версии ещё нет локально — сперва скачиваем её с диска, затем ставим метку.
        await DownloadAsync(r, pin: true);
    }

    /// <summary>Копирует версию с диска в локальный кэш (cleanup:false — не сносит уже лежащую под
    /// рукой текущую версию), при pin ещё и закрепляет её. Долгий сетевой обход — на фоне, кнопки на
    /// это время заблокированы.</summary>
    private async Task DownloadAsync(FwVersionRecord r, bool pin)
    {
        var name = LocalName(r);
        var diskAvailable = !string.IsNullOrEmpty(r.DiskPath) && Directory.Exists(r.DiskPath);
        var alreadyLocal = LocalFirmwareCache.HasVersion(name, r.VersionRaw);

        if (!diskAvailable && !alreadyLocal)
        {
            AppMessageBox.Show("Этой версии нет на сетевом диске — скачивать неоткуда.",
                "История", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DownloadBtn.IsEnabled = false;
        PinBtn.IsEnabled = false;
        LocalStatus.Text = pin ? "Закрепление…" : "Скачивание…";
        try
        {
            if (diskAvailable)
            {
                var result = SearchService.ToHierarchyResult(r);
                await Task.Run(() => FirmwareSync.CopyToLocal(result, cleanup: false));
            }
            if (pin) LocalFirmwareCache.SetKept(name, r.VersionRaw, true);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось скачать версию на этот компьютер:\n{ex.Message}",
                "История", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshLocalState();
        }
    }

    private void PathLink_Click(object sender, RoutedEventArgs e) => OpenFolder(_selectedPath);

    private void OpenFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            AppMessageBox.Show($"Папка не существует:\n{path}", "История", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void VersionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!DataGridClickGuard.IsOverDataRow(e)) return;
        if (VersionsGrid.SelectedItem is not Row row) return;
        OpenFolder(!string.IsNullOrEmpty(row.Record.DiskPath) ? row.Record.DiskPath : row.Record.LocalPath);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
