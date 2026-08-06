using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Общая для всех ролей страница "Сетевые диски и синхронизация" — раньше пути к дискам
/// и интервал синхронизации жили только в Настройки → Общие, доступной одному администратору, хотя
/// их реально нужно настраивать на каждом компьютере отдельно. Качество сканирования сюда не
/// относится (используется только в Осмотре при самом сканировании) — живёт в InspectionView.</summary>
public partial class NetworkSyncView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;

    public NetworkSyncView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Loaded += (_, _) => Load();
    }

    /// <summary>Called on every navigation to this page (it's cached, not recreated) so a role
    /// switch immediately shows/hides the admin-only push section without needing a fresh instance.
    /// Also re-reads the last-sync/last-push timestamps — these are updated silently in the
    /// background (auto-pull/auto-push timers no longer show a banner, see MainWindowViewModel.
    /// CheckForConfigUpdate), so this is the only place the user sees them, and it must reflect
    /// whatever happened since the page was last visited, not just its own construction time.</summary>
    public void RefreshIfActive()
    {
        PushSection.Visibility = _services.Cfg.CurrentRole() == "administrator" ? Visibility.Visible : Visibility.Collapsed;

        var lastSync = _services.Cfg.ConfigLastSyncedAt();
        LastSyncText.Text = string.IsNullOrEmpty(lastSync) ? "" : $"Последняя синхронизация: {lastSync}";

        var lastCheck = _services.Cfg.ConfigLastCheckedAt();
        LastCheckText.Text = string.IsNullOrEmpty(lastCheck) ? "" : $"Последняя проверка диска: {lastCheck}";

        var lastPush = _services.Cfg.ConfigLastPushedAt();
        LastPushText.Text = string.IsNullOrEmpty(lastPush) ? "" : $"Последняя отправка: {lastPush}";

        // Задача 2 — watermark ревизии маркера (config_last_synced_revision), читается напрямую
        // через ConfigService.Get, т.к. типизированного свойства для него нет (см. ConfigSyncService.
        // LocalWatermarkRevision — это его же ключ). "0"/пусто — либо синхронизации ещё не было,
        // либо общий диск ещё не знает о ревизиях (общий конфиг от версии приложения без маркера).
        var revision = _services.Cfg.Get("config_last_synced_revision");
        RevisionText.Text = string.IsNullOrEmpty(revision) || revision == "0" ? "" : $"Ревизия конфига на этой машине: {revision}";

        var conflictCount = _services.Db.PendingHierarchyConflictCount();
        ConflictStatusPanel.Visibility = conflictCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (conflictCount > 0)
            ConflictStatusText.Text = $"Конфликты синхронизации, требуют решения: {conflictCount}";

        SyncVerboseCheck.IsChecked = _services.Cfg.SyncVerbose();
    }

    private void SyncVerbose_Click(object sender, RoutedEventArgs e)
    {
        var on = SyncVerboseCheck.IsChecked == true;
        _services.Cfg.SetSyncVerbose(on);
        _host.ShowStatus(on
            ? "Подробный режим синхронизации включён — тики синхры будут писать в статус-строку"
            : "Подробный режим синхронизации выключен", category: NotificationCategory.Sync);
    }

    private void ShowConflicts_Click(object sender, RoutedEventArgs e)
    {
        var pending = _services.Db.GetPendingHierarchyConflicts();
        if (pending.Count == 0)
        {
            RefreshIfActive();
            return;
        }

        var dlg = new ConflictResolutionDialog(_services, pending) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        if (dlg.ResolvedCount > 0)
            _host.ShowStatus($"Разрешено конфликтов синхронизации: {dlg.ResolvedCount}", category: NotificationCategory.Sync);
        RefreshIfActive();
    }

    private void Load()
    {
        RootPathInput.Text = _services.Cfg.RootPath();
        SecondDiskInput.Text = _services.Cfg.SecondDiskPath();
        LoadS3();
        InspectionFolderInput.Text = _services.Cfg.Get("inspection_folder");

        PushIntervalInput.Text = _services.Cfg.ConfigPushIntervalMin().ToString();

        RefreshIfActive();
    }

    // ── Автосохранение путей ──────────────────────────────────────────────────
    // Кнопок «Сохранить» у трёх путей больше нет: выбрал папку через «…» — сохранилось сразу,
    // набрал руками — сохранилось по уходу фокуса или по Enter. Ничего не сохраняем и молчим, если
    // значение не изменилось (SettingsAutoSave.PathChanged), иначе каждый переход по вкладке сыпал
    // бы «Путь сохранён» в нижнюю строку.

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Путь к диску" };
        if (dlg.ShowDialog() != true) return;
        RootPathInput.Text = dlg.FolderName;
        SaveRootPath();
    }

    private void RootPath_LostFocus(object sender, RoutedEventArgs e) => SaveRootPath();

    private void RootPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveRootPath();
    }

    private void SaveRootPath()
    {
        var path = RootPathInput.Text.Trim();
        if (!SettingsAutoSave.PathChanged(path, _services.Cfg.RootPath())) return;

        _services.Cfg.SetRootPath(path);
        _host.ShowStatus("Путь к диску сохранён", category: NotificationCategory.Sync);
        // Create the folder tree on the new path and refresh the footer "Диск: …" indicator right
        // away — otherwise the footer stays stale (contradicting the toast above) until the next
        // periodic sync tick, and on sync_interval_min=0 it never updates until the app restarts.
        _host.OnRootPathChanged();
    }

    private void BrowseSecondDisk_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Второй диск" };
        if (dlg.ShowDialog() != true) return;
        SecondDiskInput.Text = dlg.FolderName;
        SaveSecondDiskPath();
    }

    private void SecondDisk_LostFocus(object sender, RoutedEventArgs e) => SaveSecondDiskPath();

    private void SecondDisk_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveSecondDiskPath();
    }

    private void SaveSecondDiskPath()
    {
        var path = SecondDiskInput.Text.Trim();
        if (!SettingsAutoSave.PathChanged(path, _services.Cfg.SecondDiskPath())) return;

        _services.Cfg.SetSecondDiskPath(path);
        _host.ShowStatus("Путь второго диска сохранён", category: NotificationCategory.Sync);
    }

    // ── Хранилище на хостинге (S3) ────────────────────────────────────────────
    // Реквизиты хранилища выданы отдельно от ключей (ключи обещаны файлом secrets позже), поэтому
    // страница обязана быть работоспособной с пустыми ключами: адрес заполнен, вписать ключи можно
    // в любой день, ничего не переустанавливая. Сохранение — по уходу фокуса и по Enter, как у путей
    // выше: отдельной кнопки «Сохранить» на этой странице нет нигде, и заводить её только здесь
    // значило бы, что часть полей сохраняется сама, а часть нет.

    private void LoadS3()
    {
        S3EndpointInput.Text = _services.Cfg.S3Endpoint();
        S3BucketInput.Text = _services.Cfg.S3Bucket();
        S3RegionInput.Text = _services.Cfg.S3Region();
        S3PrefixInput.Text = _services.Cfg.S3Prefix();
        S3PublishCheck.IsChecked = _services.Cfg.S3Publish();
        RefreshS3Keys();
        RefreshS3Status();
    }

    private void S3Field_LostFocus(object sender, RoutedEventArgs e) => SaveS3Fields();

    private void S3Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveS3Fields();
    }

    private void SaveS3Fields()
    {
        var changed =
            Save(S3EndpointInput.Text, _services.Cfg.S3Endpoint(), _services.Cfg.SetS3Endpoint) |
            Save(S3BucketInput.Text, _services.Cfg.S3Bucket(), _services.Cfg.SetS3Bucket) |
            Save(S3RegionInput.Text, _services.Cfg.S3Region(), _services.Cfg.SetS3Region) |
            Save(S3PrefixInput.Text, _services.Cfg.S3Prefix(), _services.Cfg.SetS3Prefix);

        if (!changed) return;
        _host.ShowStatus("Реквизиты хранилища сохранены", category: NotificationCategory.Sync);
        RefreshS3Status();

        static bool Save(string typed, string current, Action<string> set)
        {
            var value = typed.Trim();
            if (string.Equals(value, current, StringComparison.Ordinal)) return false;
            set(value);
            return true;
        }
    }

    // ── Ключи доступа: файлом, а не руками ────────────────────────────────────
    // Ключи выдаёт хостинг готовым файлом (просьба Ивана Герасимова от 06.08.2026 — «чтобы просто
    // файл туда перенести»). Полей ввода здесь больше нет намеренно: сорок случайных символов,
    // перепечатанные руками, дают опечатку, которая молчит до первой выкладки, — а файл либо
    // разбирается, либо честно говорит, что в нём не то. Формат файла мы не выбираем, поэтому разбор
    // терпимый (см. Core.Services.S3SecretsFile).

    private void S3Secrets_Click(object sender, MouseButtonEventArgs e) => PickS3SecretsFile();

    private void S3SecretsBrowse_Click(object sender, RoutedEventArgs e) => PickS3SecretsFile();

    private void S3Secrets_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void S3Secrets_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            ApplyS3SecretsFile(paths[0]);
    }

    private void PickS3SecretsFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать файл с ключами доступа к хранилищу",
            // «Все файлы» первым фильтром: имя и расширение файла с ключами задаёт хостинг, и файл
            // без расширения (так их отдаёт часть панелей) не должен оказаться невидимым в диалоге.
            Filter = "Все файлы (*.*)|*.*|Файлы с ключами (*.txt;*.csv;*.json;*.env)|*.txt;*.csv;*.json;*.env",
        };
        if (dlg.ShowDialog() == true) ApplyS3SecretsFile(dlg.FileName);
    }

    /// <summary>Читает и применяет файл с ключами. Всё, что может пойти не так (папку перетащили,
    /// файл занят, внутри не то), заканчивается понятной строкой в состоянии карточки, а не
    /// исключением: это настройка, которую делает не программист.</summary>
    private void ApplyS3SecretsFile(string path)
    {
        string content;
        try
        {
            if (Directory.Exists(path))
            {
                S3Status.Text = "Это папка. Нужен сам файл с ключами, который прислал хостинг.";
                return;
            }
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                S3Status.Text = "Файл не найден.";
                return;
            }
            if (info.Length > Core.Services.S3SecretsFile.MaxReasonableBytes)
            {
                S3Status.Text = "Файл слишком большой для файла с ключами — похоже, это не он.";
                return;
            }
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            S3Status.Text = $"Не удалось прочитать файл: {ex.Message}";
            return;
        }

        var parsed = Core.Services.S3SecretsFile.Parse(content);
        if (!parsed.Ok)
        {
            S3Status.Text = parsed.Error!;
            return;
        }

        _services.Cfg.SetS3AccessKey(parsed.AccessKey);
        _services.Cfg.SetS3SecretKey(parsed.SecretKey);

        // Адрес/бакет/регион в файле бывают не всегда, но если есть — они точнее того, что стоит по
        // умолчанию: файл выдаёт тот же, кто выдал ключи. Пустые значения не затирают заполненные.
        var extras = new List<string>();
        Apply(parsed.Endpoint, _services.Cfg.S3Endpoint(), _services.Cfg.SetS3Endpoint, "адрес хранилища");
        Apply(parsed.Bucket, _services.Cfg.S3Bucket(), _services.Cfg.SetS3Bucket, "бакет");
        Apply(parsed.Region, _services.Cfg.S3Region(), _services.Cfg.SetS3Region, "регион");

        S3EndpointInput.Text = _services.Cfg.S3Endpoint();
        S3BucketInput.Text = _services.Cfg.S3Bucket();
        S3RegionInput.Text = _services.Cfg.S3Region();

        RefreshS3Keys();
        RefreshS3Status();
        _host.ShowStatus("Ключи доступа к хранилищу сохранены", category: NotificationCategory.Sync);

        if (extras.Count > 0)
            S3Status.Text += $" Из файла также взято: {string.Join(", ", extras)}.";
        if (parsed.OrderGuessed)
            S3Status.Text += " В файле не было подписей, какой ключ какой, — порядок определён по " +
                             "виду ключей. Если проверка доступа не пройдёт, ключи в файле шли наоборот.";

        void Apply(string value, string current, Action<string> set, string what)
        {
            if (value.Length == 0 || string.Equals(value, current, StringComparison.Ordinal)) return;
            set(value);
            extras.Add(what);
        }
    }

    private void S3SecretsClear_Click(object sender, RoutedEventArgs e)
    {
        if (!_services.Cfg.S3().HasCredentials) return;
        _services.Cfg.SetS3AccessKey("");
        _services.Cfg.SetS3SecretKey("");
        RefreshS3Keys();
        RefreshS3Status();
        _host.ShowStatus("Ключи доступа к хранилищу убраны", category: NotificationCategory.Sync);
    }

    /// <summary>Что написано в зоне перетаскивания. Access Key ID показывается целиком — он не
    /// секрет и по нему видно, те ли ключи стоят; секретный ключ не показывается никогда, включая
    /// «первые символы»: по ним ключ не опознать, а утечь через плечо они вполне могут.</summary>
    private void RefreshS3Keys()
    {
        var s3 = _services.Cfg.S3();
        S3SecretsClearButton.IsEnabled = s3.HasCredentials;

        S3SecretsLabel.Text = s3.HasCredentials
            ? $"Ключи загружены\nAccess Key ID: {s3.AccessKey}\nСекретный ключ сохранён.\n" +
              "Чтобы заменить — перетащите сюда новый файл"
            : "Перетащите сюда файл с ключами доступа,\nкоторый прислал хостинг,\nили нажмите, чтобы выбрать его";
    }

    private void S3Publish_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetS3Publish(S3PublishCheck.IsChecked == true);
        RefreshS3Status();
    }

    /// <summary>Строка под кнопкой: в каком состоянии выкладка ПРЯМО СЕЙЧАС. Главный случай, ради
    /// которого она есть, — «адрес есть, ключей нет»: это не поломка, а ожидание файла secrets, и
    /// человек должен видеть именно это, а не «ничего не настроено».</summary>
    private void RefreshS3Status()
    {
        var s3 = _services.Cfg.S3();
        S3CheckButton.IsEnabled = s3.HasAddress && s3.HasCredentials;

        if (!s3.HasAddress)
        {
            S3Status.Text = "Не настроено — укажите адрес хранилища и бакет.";
            return;
        }
        if (!s3.HasCredentials)
        {
            S3Status.Text = "Осталось загрузить файл с ключами доступа — до этого инструкции на хостинг не выкладываются.";
            return;
        }
        if (string.IsNullOrWhiteSpace(s3.WebUrl))
        {
            S3Status.Text = "Ключи есть, но не задан веб-адрес диска инструкций " +
                            "(Настройки → Печать) — без него в QR-код нечего положить.";
            return;
        }
        S3Status.Text = s3.Enabled
            ? "Настроено — копия инструкции уходит на хостинг при загрузке версии."
            : "Реквизиты заполнены, но выкладка выключена галочкой выше.";
    }

    private async void S3Check_Click(object sender, RoutedEventArgs e)
    {
        SaveS3Fields();

        var s3 = _services.Cfg.S3();
        S3CheckButton.IsEnabled = false;
        S3Status.Text = "Проверяем…";
        try
        {
            var result = await new Core.Services.S3Client().CheckAsync(s3);
            S3Status.Text = result.Ok
                ? "Доступ есть — хранилище отвечает, ключи подходят. Право на запись проверится первой выложенной инструкцией."
                : $"Не получилось: {result.Error}";
        }
        finally
        {
            S3CheckButton.IsEnabled = true;
        }
    }

    private void BrowseInspectionFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка осмотра" };
        if (dlg.ShowDialog() != true) return;
        InspectionFolderInput.Text = dlg.FolderName;
        SaveInspectionFolderPath();
    }

    private void InspectionFolder_LostFocus(object sender, RoutedEventArgs e) => SaveInspectionFolderPath();

    private void InspectionFolder_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveInspectionFolderPath();
    }

    private void SaveInspectionFolderPath()
    {
        var path = InspectionFolderInput.Text.Trim();
        if (!SettingsAutoSave.PathChanged(path, _services.Cfg.Get("inspection_folder"))) return;

        _services.Cfg.SetInspectionFolder(path);
        _host.ShowStatus("Папка осмотра сохранена", category: NotificationCategory.Sync);
    }


    /// <summary>Асинхронная, как и фоновый тик синхронизации: обе долгие части (чтение общего
    /// конфига с шары и досмотр папок версий) уходят в фоновый поток, а внизу окна всё это время
    /// висит индикатор — раньше нажатие «Синхронизировать сейчас» просто вешало окно до конца.</summary>
    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Повторный клик, пока идёт первый, дал бы два параллельных импорта одного и того же файла.
        SyncNowButton.IsEnabled = false;
        try
        {
            ConfigUpdateInfo? info;
            string? error;
            SharedConfigSnapshot? snapshot;
            // Пилюля синхры, а не общий индикатор фоновой работы: он остался за поиском и прочей
            // работой страницы (см. IAppHost.BeginSync).
            using (_host.BeginSync("проверка обновлений на диске"))
                (info, error, snapshot) = await ConfigSyncService.CheckForUpdateAsync(_services);

            if (error is not null)
            {
                _host.NoteSyncResult($"Не удалось проверить обновление конфига: {error}", isError: true);
                AppMessageBox.Show($"Не удалось проверить обновление конфига:\n{error}", "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (info is null || snapshot is null)
            {
                LastSyncText.Text = $"Изменений нет. Последняя синхронизация: {_services.Cfg.ConfigLastSyncedAt()}";
                _host.NoteSyncResult(null, isError: false);
                _host.ShowStatus("Изменений на диске нет — конфиг уже актуален", category: NotificationCategory.Sync);
                return;
            }

            ConfigApplyResult result;
            using (_host.BeginSync("приём справочника и прошивок"))
                result = await ConfigSyncService.ApplyAsync(_services, snapshot, root);

            _host.NoteSyncResult(null, isError: false);
            ShowSyncResult(result);
        }
        finally
        {
            SyncNowButton.IsEnabled = true;
        }
    }

    private void ShowSyncResult(ConfigApplyResult result)
    {
        _host.ReloadSidebarApps();
        LastSyncText.Text = $"Последняя синхронизация: {result.ExportedAt} (от {result.ExportedBy})";

        var conflictNote = result.Counts.ConflictsFound > 0 ? $"\nКонфликтов, требующих решения: {result.Counts.ConflictsFound}" : "";
        AppMessageBox.Show(
            $"Экспорт от: {result.ExportedAt} ({result.ExportedBy})\n\n" +
            $"Настроек применено: {result.SettingsApplied}\n" +
            $"Изменений в справочнике: {result.Counts.TotalChanges}" + conflictNote,
            "Синхронизация завершена", MessageBoxButton.OK, MessageBoxImage.Information);
        _host.ShowStatus($"Конфиг обновлён: настроек {result.SettingsApplied}, изменений {result.Counts.TotalChanges}", category: NotificationCategory.Sync);

        // A manual sync is already a deliberate, blocking action — open the resolution dialog right
        // here instead of just raising the passive banner the periodic auto-pull uses (see
        // MainWindowViewModel.CheckForHierarchyConflicts), so the operator resolves it immediately
        // while they're already looking at this page.
        var pending = _services.Db.GetPendingHierarchyConflicts();
        if (pending.Count > 0)
        {
            var dlg = new ConflictResolutionDialog(_services, pending) { Owner = Application.Current.MainWindow };
            dlg.ShowDialog();
            if (dlg.ResolvedCount > 0)
                _host.ShowStatus($"Разрешено конфликтов синхронизации: {dlg.ResolvedCount}", category: NotificationCategory.Sync);
        }
    }

    private void PushInterval_LostFocus(object sender, RoutedEventArgs e) => SavePushInterval();

    private void PushInterval_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SavePushInterval();
    }

    /// <summary>Same "0 = off, any other number = on with that interval" pattern as Осмотра's
    /// auto-cleanup — a separate "отправлять автоматически" checkbox used to sit next to this field,
    /// redundant with it (the interval already had its own "0 disables" meaning, see the footnote
    /// text below the field), so it was removed rather than kept as a second way to express the same
    /// on/off state.
    ///
    /// Сохраняется само (кнопки «Сохранить» больше нет). Мусорный ввод не показывает модальное окно —
    /// по уходу фокуса это было бы навязчиво: поле возвращается к сохранённому значению, а причина
    /// уходит в нижнюю строку состояния.</summary>
    private void SavePushInterval()
    {
        var edit = SettingsAutoSave.ParseNumber(PushIntervalInput.Text, _services.Cfg.ConfigPushIntervalMin(), min: 0,
            "Интервал отправки: нужно целое число минут (0 — отключить автоотправку)");
        if (edit.Invalid)
        {
            PushIntervalInput.Text = edit.Value.ToString();
            _host.ShowStatus(edit.Message, category: NotificationCategory.Sync);
            return;
        }
        if (!edit.Save) return;

        _services.Cfg.SetConfigPushIntervalMin(edit.Value);
        _host.RefreshConfigSync();
        _host.ShowStatus(edit.Value == 0 ? "Автоотправка на диск отключена" : $"Интервал отправки: {edit.Value} мин", category: NotificationCategory.Sync);
    }

    private async void PushNow_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var exportedBy = $"{_services.CurrentUserName} ({RolesConfig.RoleLabel(_services.Cfg.CurrentRole())})";
            ConfigExportResult result;
            using (_host.BeginSync("отправка конфига на диск"))
                result = await ConfigSyncService.ExportAsync(_services, root, exportedBy);
            _host.NoteSyncResult(null, isError: false);
            LastPushText.Text = $"Последняя отправка: {result.ExportedAt}";
            AppMessageBox.Show(
                // Файлы параметров считаем ЖИВЫЕ: в снимок теперь входят и архивные — это тумбстоуны
                // удаления (см. Database.ConfigExchange.cs), показывать их оператору как «отправлено
                // столько-то файлов» было бы враньём.
                $"Отправлено:\nПрошивок: {result.Hierarchy.FwVersions.Count}\nФайлов параметров: {result.Hierarchy.ParamFiles.Count(p => p.Archived == 0)}\n" +
                $"Групп: {result.Hierarchy.EquipmentGroups.Count}, Модификаций: {result.Hierarchy.ControllerModifications.Count}\n" +
                $"Тегов: {result.Hierarchy.Tags?.Count ?? 0}, Резервов номеров: {result.Hierarchy.Reservations.Count}",
                "Конфиг отправлен на диск", MessageBoxButton.OK, MessageBoxImage.Information);
            _host.ShowStatus("Конфиг отправлен на диск", category: NotificationCategory.Sync);
        }
        catch (Exception ex)
        {
            _host.NoteSyncResult($"Не удалось отправить конфиг: {ex.Message}", isError: true);
            AppMessageBox.Show($"Не удалось отправить конфиг:\n{ex.Message}", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>«Сделать это состояние эталонным для всех» — в отличие от обычной PushNow_Click
    /// (аддитивная отправка: получатели только дополняют свой справочник), authoritative-экспорт
    /// говорит получателям при следующем их Apply() ПОЛНОСТЬЮ заменить восемь справочных сущностей
    /// (типы шкафов/подтипы/контроллеры/модификации/производители/теги/оба списка расширений) —
    /// удалить у себя всё, чего нет в этом снимке (см. Database.ImportHierarchyData(authoritative),
    /// FK-предохранитель там же). Прошивки/параметры/резервы/пользователей это НЕ касается вообще —
    /// см. SkipKeys/ImportHierarchyDataCore. Необратимая для чужих машин операция.
    ///
    /// Задача 1: раньше здесь был просто текстовый YesNo без списка того, что реально изменится —
    /// оператор подтверждал операцию вслепую. Теперь ПЕРЕД подтверждением считается и показывается
    /// разница (Database.PreviewAuthoritativeDiff/ConfigSyncService.PreviewAuthoritativeSyncAsync —
    /// свой справочник против того, что СЕЙЧАС на диске) в AuthoritativeDiffDialog: что добавится и
    /// что удалится по каждой категории. Точное число записей, которые уедут как удаление НА КАЖДОЙ
    /// ИЗ чужих машин, всё равно показать нельзя — эта машина не видит их локальные БД, только диск
    /// как приближение к тому, что получатели уже применили; сам диалог явно об этом предупреждает.</summary>
    private async void PushAuthoritative_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Эталонная синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AuthoritativeSyncDiff? diff;
        string? diffError;
        using (_host.BeginBusy("Сравнение справочника с диском…"))
            (diff, diffError) = await ConfigSyncService.PreviewAuthoritativeSyncAsync(_services, root);

        if (diffError is not null)
        {
            AppMessageBox.Show($"Не удалось сравнить справочник с диском:\n{diffError}", "Эталонная синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var diffDialog = new AuthoritativeDiffDialog(diff!) { Owner = Application.Current.MainWindow };
        diffDialog.ShowDialog();
        if (!diffDialog.Confirmed) return;

        PushAuthoritativeButton.IsEnabled = false;
        try
        {
            var exportedBy = $"{_services.CurrentUserName} ({RolesConfig.RoleLabel(_services.Cfg.CurrentRole())})";
            ConfigExportResult result;
            using (_host.BeginSync("отправка эталонного справочника"))
                result = await ConfigSyncService.ExportAsync(_services, root, exportedBy, authoritative: true);

            LastPushText.Text = $"Последняя отправка: {result.ExportedAt}";
            AppMessageBox.Show(
                $"Эталонный снимок отправлен на диск ({result.ExportedAt}).\n\n" +
                $"Типов шкафов: {result.Hierarchy.EquipmentGroups.Count}, Подтипов: {result.Hierarchy.EquipmentSubtypes.Count}\n" +
                $"Контроллеров: {result.Hierarchy.ControllerModels.Count}, Модификаций: {result.Hierarchy.ControllerModifications.Count}\n" +
                $"Производителей: {result.Hierarchy.ParamManufacturers?.Count ?? 0}, Тегов: {result.Hierarchy.Tags?.Count ?? 0}\n\n" +
                "На остальных компьютерах записи справочника, которых нет в этом списке, удалятся при их следующей синхронизации с диском.",
                "Эталонная синхронизация отправлена", MessageBoxButton.OK, MessageBoxImage.Information);
            _host.ShowStatus("Эталонный справочник отправлен на диск", category: NotificationCategory.Sync);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось отправить эталонный справочник:\n{ex.Message}", "Эталонная синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PushAuthoritativeButton.IsEnabled = true;
        }
    }
}
