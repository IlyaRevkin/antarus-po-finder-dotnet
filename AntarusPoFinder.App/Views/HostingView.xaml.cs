using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Страница «Хранилище» — ответ на дословную жалобу владельца: «Хер поймёшь, выгрузилась она
/// нет, надо добавить механизм чтобы было видно отгружена ли она на внешнее хранилище, добавить
/// кнопки синхронизация, возможность видеть процесс синхронизации если она в фоне, больше
/// возможностей для дебага и модерации».
///
/// До этого выкладка была побочным действием загрузки версии: получилось — хорошо, не получилось —
/// строчка в списке предупреждений, которую никто не читал, и узнать постфактум, лежит документ на
/// хостинге или нет, было неоткуда вовсе.
///
/// Три вкладки закрывают ровно эти три просьбы: «Состояние» — что есть и чего нет, с кнопками
/// выкладки и полосой прогресса; «Журнал» — почему не получилось, целиком, с кнопкой проверки
/// доступа; «Написание в адресах» — справочник транслита, от которого зависит, попадёт ли наклейка с
/// QR в выложенный файл.
///
/// Долгие операции идут в фоновом потоке с отменой: обход сотни версий с запросом на каждую — это
/// минуты, и замирающее окно в этот момент уже проходили (см. «приложение зависает, когда в фоне
/// что-то делает»).</summary>
public partial class HostingView : UserControl
{
    private readonly AppServices _services;
    private readonly MainWindowViewModel _host;

    private readonly ObservableCollection<Row> _rows = new();
    private readonly ObservableCollection<TranslitRow> _translit = new();
    private List<HostingItem> _items = new();
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cancel;

    public HostingView(AppServices services, MainWindowViewModel host)
    {
        _services = services;
        _host = host;
        InitializeComponent();

        ItemsGrid.ItemsSource = _rows;
        TranslitGrid.ItemsSource = _translit;
        RefreshIfActive();
    }

    /// <summary>Страницы живут в кэше между переходами (MainWindowViewModel._pageCache), поэтому
    /// свежие данные страница обязана забирать сама при возврате на неё — иначе останется с тем, что
    /// показывала полчаса назад.</summary>
    public void RefreshIfActive()
    {
        ShowStorageState();
        LoadTranslit();
        BuildList();
    }

    // ── Состояние хранилища ───────────────────────────────────────────────────

    private void ShowStorageState()
    {
        var s = _services.Cfg.S3();

        StorageStateText.Text = !s.HasAddress
            ? "Адрес хранилища не задан — выкладывать некуда."
            : !s.HasCredentials
                ? "Ключи доступа не загружены. Настройки → Сетевые диски → перетащите файл с ключами. До этого выкладка не делается."
                : !s.Enabled
                    ? "Выкладка выключена в настройках. Реквизиты на месте — включить можно там же."
                    : "Выкладка настроена и включена.";

        var limit = _services.Cfg.HostingMaxFileMb();
        var mode = _services.Cfg.HostingSizeLimitHard() ? "не выкладываются" : "выкладываются с предупреждением";
        // Пустой веб-адрес — не редкость (его задают отдельно, в Настройки → Печать), и строка
        // «адрес для ссылок » с пустотой на конце читается как поломка вёрстки, а не как «не задан».
        var webUrl = string.IsNullOrWhiteSpace(s.WebUrl) ? "не задан (Настройки → Печать)" : s.WebUrl;
        StorageAddressText.Text =
            $"{s.Endpoint} · бакет {s.Bucket} · регион {s.Region} · адрес для ссылок: {webUrl}\n" +
            $"Предел размера файла {limit} МБ, файлы сверх предела {mode}. " +
            $"Переопределений написания в адресах: {s.Translit.Count}.";

        var ready = s.CanPublish;
        CheckBtn.IsEnabled = ready;
        PublishMissingBtn.IsEnabled = ready;
        PublishSelectedBtn.IsEnabled = ready;
        RepublishAllBtn.IsEnabled = ready;
    }

    // ── Список ────────────────────────────────────────────────────────────────

    private void BuildList()
    {
        var settings = _services.Cfg.S3();
        var root = _services.Cfg.RootPath();

        // Построение списка в сеть не ходит — только база и диск (см. HostingSyncService.Plan).
        _items = new HostingSyncService(_services.Db).Plan(settings, root).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var onlyProblems = OnlyProblemsCheck.IsChecked == true;
        _rows.Clear();
        foreach (var item in _items)
        {
            if (onlyProblems && item.State == HostingState.Published) continue;
            _rows.Add(new Row(item));
        }

        var published = _items.Count(i => i.State == HostingState.Published);
        var missing = _items.Count(i => i.State == HostingState.Missing);
        var noSource = _items.Count(i => i.State == HostingState.NoSource);
        var failed = _items.Count(i => i.State == HostingState.Failed);
        var unknown = _items.Count(i => i.State == HostingState.Unknown);

        SummaryText.Text = _items.Count == 0
            ? "Показывать нечего: у версий нет папок инструкций либо не задан путь к диску прошивок."
            : $"Всего {_items.Count}. На хостинге {published}, нет {missing}, нет файла на диске {noSource}, " +
              $"ошибок {failed}, не проверено {unknown}.";
    }

    private void OnlyProblems_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    // ── Долгие операции ───────────────────────────────────────────────────────

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Проверка на хостинге", async (progress, ct) =>
        {
            var service = new HostingSyncService(_services.Db);
            var checkedItems = await service.CheckAsync(_services.Cfg.S3(), _items, progress, ct);
            _items = checkedItems.ToList();
            RememberChecks();

            foreach (var item in _items.Where(i => i.State == HostingState.Failed))
                AppendLog($"{item.VersionRaw}: не удалось проверить — {item.Error}");

            return $"Проверено {_items.Count}: на хостинге {_items.Count(i => i.State == HostingState.Published)}, " +
                   $"нет {_items.Count(i => i.State == HostingState.Missing)}.";
        });
    }

    private async void PublishMissing_Click(object sender, RoutedEventArgs e) =>
        await PublishAsync(_items, onlyMissing: true, "Выкладка недостающего");

    private async void PublishSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ItemsGrid.SelectedItems.OfType<Row>().Select(r => r.Item).ToList();
        if (selected.Count == 0)
        {
            AppMessageBox.Show("Выберите строки в списке.", "Хранилище", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await PublishAsync(selected, onlyMissing: false, "Выкладка выбранного");
    }

    private async void RepublishAll_Click(object sender, RoutedEventArgs e)
    {
        var reply = AppMessageBox.Show(
            $"Выложить заново все {_items.Count} документов, включая уже лежащие на хостинге?\n\n" +
            "Нужно, когда документы правили на диске, а на хостинге осталась прошлая редакция. " +
            "На большом диске это займёт заметное время — прогон можно остановить, ничего не сломав.",
            "Перезалить всё", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        await PublishAsync(_items, onlyMissing: false, "Перезаливка всего");
    }

    private async Task PublishAsync(IReadOnlyList<HostingItem> items, bool onlyMissing, string title)
    {
        var root = _services.Cfg.RootPath();
        var settings = _services.Cfg.S3();

        await RunAsync(title, async (progress, ct) =>
        {
            var service = new HostingSyncService(_services.Db, client: null, new Services.DocxToPdfConverter.Adapter());
            var result = await Task.Run(() => service.Publish(settings, root, items, onlyMissing, progress, ct), ct);

            foreach (var message in result.Messages) AppendLog(message);

            // После выкладки состояние строк заведомо изменилось — перепроверяем правдой, а не
            // догадкой «раз отправили, значит лежит»: ровно этой догадкой страница и была бы
            // бесполезна.
            var rechecked = await service.CheckAsync(settings, _items, progress, ct);
            _items = rechecked.ToList();
            RememberChecks();

            return $"Выложено {result.Published}, пропущено {result.Skipped}, не удалось {result.Failed}.";
        });
    }

    /// <summary>Сохранить наблюдения, чтобы карточка прошивки и окно QR могли показать «на хостинге»
    /// сразу, без запроса к сети на каждую строку выдачи (см. Database.HostingChecks). Строки, по
    /// которым проверка не удалась, в кэш НЕ пишутся: «не смогли спросить» — это не «нет».</summary>
    private void RememberChecks()
    {
        foreach (var item in _items)
        {
            if (item.State is not (HostingState.Published or HostingState.Missing)) continue;
            _services.Db.SaveHostingCheck(item.ObjectKey, item.State == HostingState.Published, item.Url);
        }
    }

    /// <summary>Общая обвязка длинной операции: кнопки гаснут, появляется прогресс и «Остановить»,
    /// по завершении список перерисовывается, а итог уходит и в строку внизу, и в журнал.
    /// Отмена — это штатный исход, а не ошибка: прогон на сотни файлов человек имеет право
    /// прекратить, и ничего при этом не ломается (уже выложенное остаётся выложенным).</summary>
    private async Task RunAsync(string title, Func<IProgress<HostingProgress>, CancellationToken, Task<string>> work)
    {
        if (_cancel is not null) return;

        _cancel = new CancellationTokenSource();
        SetBusy(true);
        AppendLog($"— {title} —");

        var progress = new Progress<HostingProgress>(p =>
        {
            Progress.Value = p.Percent;
            ProgressText.Text = $"{p.Done} из {p.Total} · {p.What}";
        });

        try
        {
            var summary = await work(progress, _cancel.Token);
            AppendLog(summary);
            _host.ShowStatus($"{title}: {summary}", category: NotificationCategory.Sync);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Прогон остановлен вручную. Уже выложенное осталось на хостинге.");
            _host.ShowStatus($"{title}: остановлено", category: NotificationCategory.Sync);
        }
        catch (Exception ex)
        {
            AppendLog($"Сорвалось: {ex.Message}");
            AppMessageBox.Show($"{title} не удалась: {ex.Message}", "Хранилище",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            SetBusy(false);
            ApplyFilter();
        }
    }

    private void SetBusy(bool busy)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StopBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = busy ? ProgressText.Text : "";
        CheckBtn.IsEnabled = !busy;
        PublishMissingBtn.IsEnabled = !busy;
        PublishSelectedBtn.IsEnabled = !busy;
        RepublishAllBtn.IsEnabled = !busy;
        if (!busy) ShowStorageState();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();

    // ── Действия по строке ────────────────────────────────────────────────────

    private Row? Selected => ItemsGrid.SelectedItem as Row;

    private void ItemsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl_Click(sender, e);

    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || string.IsNullOrEmpty(row.Item.Url)) return;
        try { Process.Start(new ProcessStartInfo(row.Item.Url) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog($"Не удалось открыть ссылку: {ex.Message}"); }
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Selected?.Item.Url);

    private void CopyKey_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Selected?.Item.ObjectKey);

    private void ShowOnDisk_Click(object sender, RoutedEventArgs e)
    {
        var path = Selected?.Item.SourcePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog($"Не удалось показать файл: {ex.Message}"); }
    }

    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            _host.ShowStatus("Скопировано", category: NotificationCategory.General);
        }
        catch (Exception ex) { AppendLog($"Буфер обмена занят: {ex.Message}"); }
    }

    // ── Журнал ────────────────────────────────────────────────────────────────

    private void AppendLog(string line)
    {
        _log.AppendLine($"{DateTime.Now:HH:mm:ss}  {line}");
        LogBox.Text = _log.ToString();
        LogBox.ScrollToEnd();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_log.ToString());

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        LogBox.Text = "";
    }

    private async void CheckAccess_Click(object sender, RoutedEventArgs e)
    {
        var settings = _services.Cfg.S3();
        AppendLog("Проверка доступа к хранилищу…");
        var result = await new S3Client().CheckAsync(settings);
        AppendLog(result.Ok
            ? $"Доступ есть: {result.Url}"
            : $"Доступа нет: {result.Error}");
    }

    // ── Написание в адресах ───────────────────────────────────────────────────

    private void LoadTranslit()
    {
        var map = _services.Cfg.Translit();
        _translit.Clear();
        foreach (var (source, latin) in map.Overrides.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            _translit.Add(new TranslitRow(source, latin, manual: true));
        UpdateTranslitHint();
    }

    /// <summary>Собрать имена, у которых перевод вообще нужен: справочник иерархии (типы, подтипы,
    /// контроллеры) и постоянные папки раскладки. Имена без кириллицы («SMH5», номера версий) в
    /// таблицу не попадают — переводить там нечего, и строки-пустышки только мешали бы найти те, что
    /// действительно стоит проверить глазами.</summary>
    private void CollectNames_Click(object sender, RoutedEventArgs e)
    {
        var known = new HashSet<string>(_translit.Select(r => r.Source), StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        foreach (var g in _services.Db.GetAllEquipmentGroups()) names.Add(g.Name);
        foreach (var s in _services.Db.GetAllEquipmentSubtypes())
        {
            if (s.Name != "—") names.Add(s.Name);
            if (!string.IsNullOrWhiteSpace(s.FolderName)) names.Add(s.FolderName);
        }
        foreach (var c in _services.Db.GetAllControllerModels()) names.Add(c.Name);
        names.Add(VersionLayout.FirmwareFolderName);
        names.AddRange(VersionLayout.SlotFolderNames);
        names.Add(HierarchyFolders.Opc);
        names.Add(HierarchyFolders.Passports);

        var added = 0;
        foreach (var name in names.Where(Transliteration.HasCyrillic).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!known.Add(name)) continue;
            _translit.Add(new TranslitRow(name, Transliteration.Auto(name), manual: false));
            added++;
        }

        UpdateTranslitHint();
        TranslitHintText.Text = added > 0
            ? $"Добавлено имён: {added}. Поправьте нужные и нажмите «Сохранить»."
            : "Новых имён с кириллицей не нашлось — все уже в таблице.";
    }

    private void TranslitGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(UpdateTranslitHint));

    private void ResetRow_Click(object sender, RoutedEventArgs e)
    {
        if (TranslitGrid.SelectedItem is not TranslitRow row) return;
        row.Latin = Transliteration.Auto(row.Source);
        row.Origin = "автоперевод";
        TranslitGrid.Items.Refresh();
        UpdateTranslitHint();
    }

    private void SaveTranslit_Click(object sender, RoutedEventArgs e)
    {
        // В справочник уходят ТОЛЬКО отличия от автоперевода. Записывать совпадающие значило бы
        // намертво зафиксировать сегодняшнюю таблицу звучаний: поправь её потом — и старые записи
        // молча продолжили бы переопределять новое поведение.
        var pairs = _translit
            .Where(r => !string.IsNullOrWhiteSpace(r.Latin))
            .Where(r => !string.Equals(r.Latin.Trim(), Transliteration.Auto(r.Source), StringComparison.Ordinal))
            .Select(r => new KeyValuePair<string, string>(r.Source, r.Latin.Trim()));

        var map = TranslitMap.FromPairs(pairs);
        _services.Cfg.SetTranslit(map);
        _host.PushCatalogChange("Изменено написание имён в адресах на хостинге");

        LoadTranslit();
        ShowStorageState();
        BuildList();
        TranslitHintText.Text = $"Сохранено. Переопределений: {map.Count}.";
    }

    private void UpdateTranslitHint()
    {
        foreach (var row in _translit)
        {
            var auto = Transliteration.Auto(row.Source);
            row.Origin = string.Equals(row.Latin?.Trim(), auto, StringComparison.Ordinal) ? "автоперевод" : "задано вручную";
            row.Example = $"…/{(string.IsNullOrWhiteSpace(row.Latin) ? auto : row.Latin.Trim())}/…";
        }
        TranslitGrid.Items.Refresh();
    }

    // ── Строки таблиц ─────────────────────────────────────────────────────────

    /// <summary>Обёртка над <see cref="HostingItem"/> для DataGrid: сама модель — неизменяемая запись
    /// из ядра, а таблице нужны готовые к показу подписи.</summary>
    private sealed class Row
    {
        public Row(HostingItem item) => Item = item;

        public HostingItem Item { get; }
        public string StateLabel => Item.StateLabel;
        public string VersionRaw => Item.VersionRaw;
        public string Where => Item.Where;
        public string Kind => Item.Kind;
        public string Url => Item.Url;
        public string? Error => Item.Error;

        public string SizeLabel => Item.Size is not { } bytes
            ? ""
            : bytes >= 1024 * 1024
                ? $"{bytes / 1024d / 1024d:0.#} МБ"
                : $"{Math.Max(1, bytes / 1024)} КБ";
    }

    private sealed class TranslitRow
    {
        public TranslitRow(string source, string latin, bool manual)
        {
            Source = source;
            Latin = latin;
            Origin = manual ? "задано вручную" : "автоперевод";
            Example = $"…/{latin}/…";
        }

        public string Source { get; }
        public string Latin { get; set; }
        public string Origin { get; set; }
        public string Example { get; set; }
    }
}
