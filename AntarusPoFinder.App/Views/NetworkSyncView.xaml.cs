using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

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
        ShowHostingPointer();

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
        LoadBackupSection();
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

    // ── Указатель на «Хранилище» ──────────────────────────────────────────────
    // Сами реквизиты хранилища (адрес, бакет, ключи, выкладка) живут теперь на странице «Хранилище».
    // Здесь — только указатель и короткая правда о том, настроено ли: человек, пришедший сюда по
    // старой памяти, должен уйти на нужную страницу, а не решить, что настройку убрали совсем.

    private void ShowHostingPointer()
    {
        var role = _services.Cfg.CurrentRole();
        var allowed = RolesConfig.RoleAccess.TryGetValue(role, out var pages) && pages.Contains("hosting");
        HostingPointer.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        if (!allowed) return;

        var s3 = _services.Cfg.S3();
        HostingPointerStatus.Text = !s3.HasAddress
            ? "Сейчас: адрес не задан."
            : !s3.HasCredentials
                ? "Сейчас: ключи доступа не загружены."
                : s3.Enabled ? "Сейчас: выкладка включена." : "Сейчас: реквизиты есть, выкладка выключена.";
    }

    private void OpenHosting_Click(object sender, RoutedEventArgs e) =>
        _host.Navigate("hosting", HostingView.HostingSection.Access);

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
        // Прошивки, которые синхронизация НЕ приняла. Счётчики для этого завели давно, но нигде не
        // показывали — и «у меня прошивка есть, а у остальных её нет» оставалось молчаливым: сводка
        // бодро рапортовала «изменений столько-то», а про непринятые записи не говорила ничего.
        // Показываем это заметно, а не сноской: человек открывает синхронизацию именно тогда, когда
        // чего-то не хватает.
        var skipNote = "";
        if (result.Counts.FwVersionsSkippedNoParent > 0)
            skipNote += $"\n\nНЕ ПРИНЯТО ПРОШИВОК: {result.Counts.FwVersionsSkippedNoParent}\n" +
                        "У них не нашлось подтипа или контроллера — справочник на этой машине старее, " +
                        "чем на той, откуда пришли прошивки. Обновите справочник и синхронизируйтесь ещё раз.";
        if (result.Counts.FwVersionsSkippedTombstone > 0)
            skipNote += $"\n\nПринято заново после удаления: {result.Counts.FwVersionsSkippedTombstone}\n" +
                        "Это версии с номером, который здесь когда-то удаляли.";

        // Снимок, который не приняли ЦЕЛИКОМ: его собрала машина с версией ниже минимальной.
        // Это не «изменений нет», и молчать об этом нельзя — иначе человек неделю считает, что
        // делится работой, а она никуда не едет.
        if (!string.IsNullOrEmpty(result.Counts.QuarantinedSender))
        {
            AppMessageBox.Show(
                "Снимок НЕ ПРИНЯТ: его собрала машина с устаревшей версией (" +
                result.Counts.QuarantinedSender + ")." + Environment.NewLine + Environment.NewLine +
                "Так убирают ошибки, которые расходились между старой и новой версией: пока та " +
                "машина не обновится, её правки здесь не применяются." + Environment.NewLine + Environment.NewLine +
                "Кто именно отстал — видно в списке подключений (Настройки → Подключения).",
                "Синхронизация остановлена", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AppMessageBox.Show(
            $"Экспорт от: {result.ExportedAt} ({result.ExportedBy})\n\n" +
            $"Настроек применено: {result.SettingsApplied}\n" +
            $"Изменений в справочнике: {result.Counts.TotalChanges}" + conflictNote + skipNote,
            "Синхронизация завершена", MessageBoxButton.OK,
            skipNote.Length > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        // «Получено 0 изменений» — это лог, а не событие: синхронизация идёт по таймеру и почти
        // всегда заканчивается ничем. В журнал (и письмом) уходит только то, где что-то произошло
        // или что-то не приняли — см. SyncReportNoise.
        var worthLogging = SyncReportNoise.Worth(result.Counts.TotalChanges, result.SettingsApplied,
            result.Counts.ConflictsFound, result.Counts.FwVersionsSkippedNoParent);
        _host.ShowStatus(SyncReportNoise.Describe(result.Counts.TotalChanges, result.SettingsApplied),
            category: NotificationCategory.Sync, record: worthLogging);

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

        // Список удалений, которые уедут вместе со снимком, — человек видит их и решает по каждому
        // (просьба «пусть будет опрос того, что затрётся у коллег, и чтобы это можно было настроить»).
        var tombstones = _services.Db.GetHierarchyTombstones();
        var diffDialog = new AuthoritativeDiffDialog(diff!, tombstones) { Owner = Application.Current.MainWindow };
        diffDialog.ShowDialog();
        if (!diffDialog.Confirmed) return;

        PushAuthoritativeButton.IsEnabled = false;
        try
        {
            var exportedBy = $"{_services.CurrentUserName} ({RolesConfig.RoleLabel(_services.Cfg.CurrentRole())})";
            ConfigExportResult result;
            using (_host.BeginSync("отправка эталонного справочника"))
                result = await ConfigSyncService.ExportAsync(_services, root, exportedBy, authoritative: true,
                    options: new ConfigSyncService.AuthoritativeOptions(diffDialog.WipeData, diffDialog.KeepTombstones));

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

    // ── Резервные копии (бета) ────────────────────────────────────────────────────────────────
    //
    // Зачем вообще — см. Core/Services/BackupService. Здесь только ручки, и все они сохраняются
    // сами, как и остальные поля этой страницы: кнопки «Сохранить» тут нет ни у чего.

    /// <summary>Карточку копий видит администратор и только в бете (функция обкатывается). На
    /// остальных машинах копирование при этом работает: оно включено по умолчанию и по умолчанию
    /// кладёт копии в профиль — то есть «не настроили» не означает «нет копий».</summary>
    private void LoadBackupSection()
    {
        BackupSection.Visibility =
            AppIdentity.IsBeta && _services.Cfg.CurrentRole() == "administrator"
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (BackupSection.Visibility != Visibility.Visible) return;

        BackupEnabledCheck.IsChecked = _services.Cfg.BackupEnabled();
        BackupFolderInput.Text = _services.Cfg.BackupFolder();
        BackupKeepDaysInput.Text = _services.Cfg.BackupKeepDays().ToString();
        BackupToS3Check.IsChecked = _services.Cfg.BackupToS3();
        // Сам пароль в поле НЕ подставляется: PasswordBox не для показа, а подставленные точки
        // выглядели бы как «пароль введён заново» и перезаписали бы его при уходе фокуса.
        BackupPasswordInput.Password = "";
        ShowBackupState();
    }

    private void ShowBackupState()
    {
        var hasPassword = _services.Cfg.BackupPassword().Length > 0;
        BackupPasswordState.Text = hasPassword
            ? "Пароль задан. Поле пустое — это нормально: введённый пароль не показывается. Чтобы сменить, введите новый."
            : "Пароль не задан — выкладка в хранилище выключена (в публично читаемый бакет незашифрованная копия не уходит никогда).";

        var last = DateTime.TryParse(_services.Cfg.Get(ConfigService.BackupLastAtKey), out var at)
            ? at.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : "";
        BackupStatusText.Text = last.Length == 0 ? "Копий ещё не снималось." : $"Последняя копия: {last}";
    }

    private void BackupEnabled_Click(object sender, RoutedEventArgs e)
    {
        var on = BackupEnabledCheck.IsChecked == true;
        _services.Cfg.SetBackupEnabled(on);
        _host.ShowStatus(on ? "Резервное копирование включено" : "Резервное копирование выключено",
            category: NotificationCategory.Sync);
    }

    private void BrowseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка для резервных копий" };
        if (dlg.ShowDialog() != true) return;
        BackupFolderInput.Text = dlg.FolderName;
        SaveBackupFolder();
    }

    private void BackupFolder_LostFocus(object sender, RoutedEventArgs e) => SaveBackupFolder();

    private void BackupFolder_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveBackupFolder();
    }

    private void SaveBackupFolder()
    {
        var path = BackupFolderInput.Text.Trim();
        if (!SettingsAutoSave.PathChanged(path, _services.Cfg.BackupFolder())) return;

        _services.Cfg.SetBackupFolder(path);
        // Очищенное поле — это возврат к папке по умолчанию (профиль), а не «никуда не класть»:
        // показываем сразу, чем оно обернулось, иначе выглядит как «настройка не сохранилась».
        BackupFolderInput.Text = _services.Cfg.BackupFolder();
        _host.ShowStatus("Папка резервных копий сохранена", category: NotificationCategory.Sync);
    }

    private void BackupKeepDays_LostFocus(object sender, RoutedEventArgs e) => SaveBackupKeepDays();

    private void BackupKeepDays_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveBackupKeepDays();
    }

    private void SaveBackupKeepDays()
    {
        var edit = SettingsAutoSave.ParseNumber(BackupKeepDaysInput.Text, _services.Cfg.BackupKeepDays(), min: 0,
            "Срок хранения копий — целое число суток (0 — не удалять старое)");
        if (edit.Invalid)
        {
            BackupKeepDaysInput.Text = _services.Cfg.BackupKeepDays().ToString();
            _host.ShowStatus(edit.Message, category: NotificationCategory.Sync);
            return;
        }
        if (!edit.Save) return;

        _services.Cfg.SetBackupKeepDays(edit.Value);
        _host.ShowStatus(edit.Value == 0
            ? "Старые копии больше не удаляются"
            : $"Копии хранятся {edit.Value} суток", category: NotificationCategory.Sync);
    }

    /// <summary>Выкладка в бакет. Без пароля включить нельзя — и это не придирка интерфейса, а то
    /// же самое правило, что в BackupTargets.CanUploadToS3: бакет открыт на чтение всему интернету.
    /// Здесь оно просто объясняется словами, а не молча не срабатывает.</summary>
    private void BackupToS3_Click(object sender, RoutedEventArgs e)
    {
        var on = BackupToS3Check.IsChecked == true;
        if (on && _services.Cfg.BackupPassword().Length == 0)
        {
            BackupToS3Check.IsChecked = false;
            AppMessageBox.Show(
                "Сначала задайте пароль шифрования.\n\n" +
                "Хранилище открыто на чтение всему интернету — по нему работают QR-коды на наклейках. " +
                "Незашифрованная копия базы там означала бы, что весь каталог предприятия может скачать кто угодно.",
                "Копия в хранилище", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _services.Cfg.SetBackupToS3(on);
        _host.ShowStatus(on
            ? "Копия будет выкладываться в хранилище — зашифрованной"
            : "Выкладка копий в хранилище выключена", category: NotificationCategory.Sync);
    }

    private void BackupPassword_LostFocus(object sender, RoutedEventArgs e) => SaveBackupPassword();

    private void BackupPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveBackupPassword();
    }

    private void SaveBackupPassword()
    {
        var typed = BackupPasswordInput.Password;
        // Пустое поле — «не трогали», а НЕ «стереть пароль»: уход фокуса с нетронутого поля не
        // должен обнулять шифрование и втихую выключать выкладку.
        if (typed.Length == 0) return;

        _services.Cfg.SetBackupPassword(typed);
        BackupPasswordInput.Password = "";
        ShowBackupState();

        AppMessageBox.Show(
            "Пароль сохранён.\n\n" +
            "Запишите его там же, где хранятся остальные пароли конторы: внутри программы его нет, " +
            "и восстановить копии из хранилища без него будет нечем. Копии в папке паролем не закрыты " +
            "и открываются обычным архиватором.",
            "Пароль резервных копий", MessageBoxButton.OK, MessageBoxImage.Information);
        _host.ShowStatus("Пароль резервных копий сохранён", category: NotificationCategory.Sync);
    }

    /// <summary>«Сделать копию сейчас» — та же копия, что снимается сама, просто не дожидаясь суток.
    /// В отличие от фонового тика, здесь о результате ГОВОРЯТ: человек нажал кнопку и имеет право
    /// знать, чем кончилось. Молчит только автоматика.</summary>
    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        BackupNowButton.IsEnabled = false;
        BackupStatusText.Text = "Снимаю копию…";
        try
        {
            var targets = _services.Cfg.Backup();
            var root = _services.Cfg.RootPath();
            var sharedConfig = string.IsNullOrWhiteSpace(root) ? null : ConfigSyncService.ConfigPathFor(root);

            BackupOutcome outcome;
            using (_host.BeginSync("резервная копия"))
                outcome = await Task.Run(() => new BackupService()
                    .RunAsync(_services.Db, Environment.MachineName, sharedConfig, targets));

            if (outcome.Ok && !outcome.Skipped)
            {
                _services.Cfg.Set(ConfigService.BackupLastAtKey, DateTime.UtcNow.ToString("O"));
                ShowBackupState();
                var where = outcome.FolderFile is not null ? $"\nФайл: {outcome.FolderFile}" : "";
                var cloud = outcome.Uploaded ? "\nВ хранилище выложена зашифрованная копия." : "";
                var partial = outcome.Error is not null ? $"\n\nЧастично не удалось: {outcome.Error}" : "";
                AppMessageBox.Show($"Копия снята ({BackupArchive.HumanSize(outcome.Bytes)}).{where}{cloud}{partial}",
                    "Резервная копия", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                BackupStatusText.Text = "Копию снять не удалось";
                AppMessageBox.Show(
                    outcome.Skipped
                        ? "Копию класть некуда: папка не задана, а выкладка в хранилище выключена или без пароля."
                        : $"Копию снять не удалось:\n{outcome.Error}",
                    "Резервная копия", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            BackupNowButton.IsEnabled = true;
        }
    }

    private void ShowBackups_Click(object sender, RoutedEventArgs e)
    {
        new BackupListDialog(_services) { Owner = Application.Current.MainWindow }.ShowDialog();
        ShowBackupState();
    }
}
