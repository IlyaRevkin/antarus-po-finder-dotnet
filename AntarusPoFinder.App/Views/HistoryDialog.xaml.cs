using System;
using System.Collections.Generic;
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
        /// <summary>Сколько раз версию выбирали в поиске по всем запросам вместе (Database.
        /// GetFwUsageTotal) — тот же счётчик, что на карточке результата.</summary>
        public int UsageTotal { get; init; }
        public bool IsRolledBack => Record.Status == "rolled_back";
        /// <summary>Самая свежая живая версия — выделяется жирным в таблице.</summary>
        public bool IsCurrent { get; init; }
        public string StatusLabel { get; init; } = "";
        public string DescriptionShort => Record.Description.Length > 80 ? Record.Description[..80] + "…" : Record.Description;
    }

    private readonly AppServices _services;
    private readonly IAppHost _host;
    private readonly int _subtypeId;
    private readonly int _controllerId;

    /// <summary>Оператор правил историю (сменил контроллер / счётчик / откат) — вызывающий обновит
    /// после закрытия то, что от этого зависит (выдачу поиска). Правки применяются сразу в БД, флаг
    /// нужен только чтобы не дёргать перерисовку зря, если ничего не трогали.</summary>
    public bool Changed { get; private set; }

    /// <summary>«Активна» стояло у КАЖДОЙ не откатанной строки — то есть у всей истории сразу
    /// (реальная жалоба: «загружаю прошивку, а в истории все активные»). Что именно считать
    /// актуальным — см. FwHistoryStatus; versions приходят от новых к старым, как их отдаёт
    /// Database.GetFwVersionsHistory.</summary>
    public HistoryDialog(string cabinetTitle, List<FwVersionRecord> versions,
        AppServices services, int subtypeId, int controllerId, IAppHost host)
    {
        InitializeComponent();
        Title = $"История версий — {cabinetTitle}";
        _services = services;
        _host = host;
        _subtypeId = subtypeId;
        _controllerId = controllerId;

        LoadRows(versions, selectVersionId: null);
    }

    /// <summary>Заполняет таблицу по списку версий: считает статусы и счётчик обращений для каждой,
    /// сохраняет выбор по id (после правки строка остаётся выделенной). versions — от новых к старым.</summary>
    private void LoadRows(List<FwVersionRecord> versions, int? selectVersionId)
    {
        var labels = FwHistoryStatus.Labels(versions);
        var rows = versions.Select((v, i) => new Row
        {
            Record = v,
            IsCurrent = labels[i] == FwHistoryStatus.Current,
            StatusLabel = labels[i],
            UsageTotal = v.Id is int id ? _services.Db.GetFwUsageTotal(id) : 0,
        }).ToList();
        VersionsGrid.ItemsSource = rows;

        var pick = selectVersionId is int want ? rows.FirstOrDefault(r => r.Record.Id == want) : null;
        VersionsGrid.SelectedItem = pick ?? rows.FirstOrDefault();
    }

    /// <summary>Перечитывает историю той же пары подтип/контроллер из БД — после правки, меняющей
    /// набор/атрибуты версий (счётчик, откат). Версия, у которой сменили контроллер, из этой пары
    /// уходит: показываем ту же историю без неё, ничего не выдумывая.</summary>
    private void Reload(int? selectVersionId)
    {
        var versions = _services.Db.GetFwVersionsHistory(_subtypeId, _controllerId);
        LoadRows(versions, selectVersionId);
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
            RefreshEditState();
            return;
        }
        var r = row.Record;
        _selectedRecord = r;
        var blocks = new List<string>();
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
        RefreshEditState();
    }

    // ── Правка выбранной версии: контроллер / счётчик / откат ─────────────────

    /// <summary>Кнопки правки доступны, только когда версия выбрана; подпись «Откатить» меняется на
    /// «Вернуть в активные» для уже откатанной, чтобы одна кнопка работала в обе стороны.</summary>
    private void RefreshEditState()
    {
        var has = _selectedRecord is not null;
        ChangeCtrlBtn.IsEnabled = has;
        EditUsageBtn.IsEnabled = has;
        RollbackBtn.IsEnabled = has;
        RollbackBtn.Content = _selectedRecord?.Status == "rolled_back" ? "Вернуть в активные" : "Откатить";
        DeleteBtn.IsEnabled = has;
    }

    /// <summary>Удалить версию из каталога совсем (в отличие от «Откатить», который лишь помечает её
    /// заменённой и оставляет в истории). Через Database.TombstoneFwVersion — тот же безопасный путь,
    /// что у «Настройки → Прошивки → Удалить прошивку»: запись помечается тумбстоуном, исчезает из всех
    /// списков и поиска на этом ПК и переносит удаление на другие ПК при синхронизации, а не воскресает
    /// при следующем экспорте. Файлы на диске не трогаем — как и «Откатить»/«Сменить контроллер».</summary>
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecord is not { Id: int id } r) return;

        var reply = AppMessageBox.Show(
            $"Удалить версию {r.VersionRaw} из каталога?\n\n" +
            "Запись исчезнет из истории и поиска на этом компьютере, а при синхронизации — и на других.\n" +
            "Файлы прошивки на диске останутся на месте — при необходимости удалите папку версии вручную.\n\n" +
            "Отменить удаление нельзя.",
            "Удалить версию", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.TombstoneFwVersion(id);
        Changed = true;
        _host.InvalidateSearchResults();
        // Версия удалена — показываем ту же историю без неё, выбор сбросится на первую оставшуюся.
        Reload(selectVersionId: null);
        _host.ShowStatus($"Версия {r.VersionRaw} удалена из каталога",
            category: NotificationCategory.FirmwareAndParams);
    }

    private void ChangeController_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecord is not { Id: int id } r) return;

        var options = _services.Db.GetAllControllerModels()
            .Where(c => c.Id is not null)
            .Select(c => new PickOptionDialog.Option(c.Id!.Value, c.Name))
            .ToList();
        if (options.Count == 0) return;

        var picked = PickOptionDialog.Pick(this, "Сменить контроллер",
            $"Контроллер для версии {r.VersionRaw}:", options, r.ControllerId);
        if (picked is not int newCtrl || newCtrl == r.ControllerId) return;

        // Файлы на диске и папку не двигаем — как и откат, это «поправить запись». Предупреждаем, что
        // папка версии осталась под старым контроллером.
        var confirm = AppMessageBox.Show(
            $"Перенести версию {r.VersionRaw} на другой контроллер?\n\n" +
            "Изменится только запись в каталоге. Файлы прошивки на диске останутся на месте — при " +
            "необходимости перенесите их папку под новый контроллер вручную.",
            "Сменить контроллер", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        if (_services.Db.ReassignFwVersionController(id, newCtrl))
        {
            Changed = true;
            _host.InvalidateSearchResults();
            // Версия уехала на другой контроллер — в истории этой пары её больше нет.
            Reload(selectVersionId: null);
            _host.ShowStatus($"Версия {r.VersionRaw} перенесена на другой контроллер",
                category: NotificationCategory.FirmwareAndParams);
        }
    }

    private void EditUsage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecord is not { Id: int id } r) return;

        var current = _services.Db.GetLocalFwUsageTotal(id);
        var input = TextPromptDialog.Prompt(this, "Кол-во обращений",
            $"Сколько раз выбирали версию {r.VersionRaw} (вклад этого компьютера):", current.ToString());
        if (input is null) return;

        if (!int.TryParse(input.Trim(), out var n) || n < 0)
        {
            AppMessageBox.Show("Введите целое число не меньше нуля.", "Кол-во обращений",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _services.Db.SetLocalFwUsageVersionTotal(id, n);
        Changed = true;
        Reload(selectVersionId: id);
    }

    private void RollbackToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecord is not { Id: int id } r) return;

        if (r.Status == "rolled_back")
        {
            var back = AppMessageBox.Show(
                $"Вернуть версию {r.VersionRaw} в активные?\n\n" +
                "Статус в базе снова станет обычным. Папку на диске, переименованную при откате " +
                "(«_ОТКАТАНО»), при необходимости переименуйте обратно вручную.",
                "Вернуть в активные", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (back != MessageBoxResult.Yes) return;

            if (_services.Db.UnrollbackFwVersion(id))
            {
                Changed = true;
                _host.InvalidateSearchResults();
                Reload(selectVersionId: id);
                _host.ShowStatus($"Версия {r.VersionRaw} возвращена в активные",
                    category: NotificationCategory.FirmwareAndParams);
            }
            return;
        }

        var reply = AppMessageBox.Show(
            $"Откатить версию {r.VersionRaw}?\n\n" +
            "Запись в базе будет помечена как откатанная.\nСледующая загрузка получит тот же SW-номер заново.\n" +
            "Файлы на диске останутся нетронутыми (папка получит пометку «_ОТКАТАНО»).",
            "Откат версии", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        if (_services.Db.RollbackFwVersion(id))
        {
            Changed = true;
            _host.InvalidateSearchResults();
            Reload(selectVersionId: id);
            _host.ShowStatus($"Версия {r.VersionRaw} откатана",
                category: NotificationCategory.FirmwareAndParams);
        }
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
