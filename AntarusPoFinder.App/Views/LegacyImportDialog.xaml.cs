using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>«Разобрать старый диск» — перенос того, что копилось до Финдера, в его структуру.
///
/// Просьба Ильи: «добавь возможность выбрать папку, где будет производиться анализ и поиск прошивок,
/// там старая структура… боюсь, автоматически это сложно будет сделать, так что при нахождении
/// прошивки нужно, чтобы я сам мог выбирать, куда что и как пойдёт и какие файлы». Отсюда и устройство
/// окна: программа только ИЩЕТ и ПРЕДПОЛАГАЕТ (<see cref="LegacyDiskScanner"/>), а решение по каждой
/// строке принимает человек — тип, подтип, контроллер и заявка правятся прямо в таблице, а у архива
/// можно распаковать содержимое и выбрать, какие файлы брать (то же окно, что и при обычной загрузке
/// папкой, см. <see cref="FolderContentsDialog"/>).
///
/// Сам перенос делает ОБЫЧНАЯ загрузка (<see cref="FirmwareUploadService"/>), а не своя укладка файлов:
/// у перенесённой версии обязаны быть ровно те же номер, имя файла, раскладка папок, CHANGELOG.md и
/// запись в базе, что и у залитой руками. Иначе через месяц половина диска жила бы по своим правилам.</summary>
public partial class LegacyImportDialog : Window
{
    private sealed class Row : INotifyPropertyChanged
    {
        private readonly LegacyImportDialog _owner;
        private EquipmentGroup? _group;
        private EquipmentSubType? _subtype;
        private ControllerModification? _modification;
        private bool _take;
        private string _requestNum = "";
        private string _cabinetSn = "";
        private string _result = "";
        private string _sourcePath;
        private string _mainFile = "";
        private List<string> _extraFiles = new();

        public Row(LegacyImportDialog owner, LegacyFinding finding)
        {
            _owner = owner;
            Finding = finding;
            _sourcePath = finding.FullPath;
            _requestNum = finding.RequestNum;
            _cabinetSn = finding.CabinetSn;

            _group = owner._groups.FirstOrDefault(g =>
                string.Equals(g.Name, finding.GroupName, StringComparison.OrdinalIgnoreCase));
            SubtypeOptions = owner.SubtypesFor(_group);
            _subtype = SubtypeOptions.FirstOrDefault(s =>
                string.Equals(s.Name, finding.SubtypeName, StringComparison.OrdinalIgnoreCase));
            _modification = owner._mods.FirstOrDefault(m =>
                string.Equals(m.ControllerName, finding.ControllerName, StringComparison.OrdinalIgnoreCase));
            _take = finding.WorthTakingByDefault && _group is not null && _subtype is not null && _modification is not null;
        }

        public LegacyFinding Finding { get; }
        public string RelativePath => Finding.RelativePath;
        public string FullPath => Finding.FullPath;

        public IReadOnlyList<EquipmentGroup> GroupOptions => _owner._groups;
        public IReadOnlyList<ControllerModification> ControllerOptions => _owner._mods;
        public List<EquipmentSubType> SubtypeOptions { get; private set; }

        /// <summary>Что именно поедет на диск: сам найденный файл или выбранное из распакованного
        /// архива. Пустой MainFile означает «источник — файл целиком», как при обычной загрузке
        /// одиночного файла.</summary>
        public string SourcePath => _sourcePath;
        public string MainFile => _mainFile;
        public IReadOnlyList<string> ExtraFiles => _extraFiles;

        public string SourceLabel
        {
            get
            {
                if (_mainFile.Length > 0)
                    return _extraFiles.Count > 0
                        ? $"из архива: {_mainFile} (+{_extraFiles.Count})"
                        : $"из архива: {_mainFile}";
                if (Finding.IsArchive) return "архив целиком (не распакован)";
                return Finding.LooksLikeDocument ? "файл целиком · похоже на документ" : "файл целиком";
            }
        }

        public bool Take
        {
            get => _take;
            set { if (_take == value) return; _take = value; OnPropertyChanged(); }
        }

        public EquipmentGroup? Group
        {
            get => _group;
            set
            {
                if (ReferenceEquals(_group, value)) return;
                _group = value;
                // Подтипы принадлежат типу: сменили тип — прежний выбор подтипа бессмыслен.
                SubtypeOptions = _owner.SubtypesFor(value);
                _subtype = SubtypeOptions.Count == 1 ? SubtypeOptions[0] : null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubtypeOptions));
                OnPropertyChanged(nameof(Subtype));
                _owner.RefreshSummary();
            }
        }

        public EquipmentSubType? Subtype
        {
            get => _subtype;
            set { if (ReferenceEquals(_subtype, value)) return; _subtype = value; OnPropertyChanged(); _owner.RefreshSummary(); }
        }

        public ControllerModification? Modification
        {
            get => _modification;
            set { if (ReferenceEquals(_modification, value)) return; _modification = value; OnPropertyChanged(); _owner.RefreshSummary(); }
        }

        public string RequestNum
        {
            get => _requestNum;
            set { if (_requestNum == value) return; _requestNum = value ?? ""; OnPropertyChanged(); }
        }

        public string CabinetSn
        {
            get => _cabinetSn;
            set { if (_cabinetSn == value) return; _cabinetSn = value ?? ""; OnPropertyChanged(); }
        }

        public string Result
        {
            get => _result;
            set { if (_result == value) return; _result = value; OnPropertyChanged(); }
        }

        public bool Ready => Group is not null && Subtype is not null && Modification is not null;

        public void UseUnpacked(string folder, string mainFile, List<string> extras)
        {
            _sourcePath = folder;
            _mainFile = mainFile;
            _extraFiles = extras;
            OnPropertyChanged(nameof(SourceLabel));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly AppServices _services;
    private readonly IAppHost _host;

    /// <summary>Отмена переноса. Обрывается ПО ГРАНИЦЕ ВЕРСИИ: одна строка — одна обычная загрузка
    /// целиком, между строками на диске полноценные версии, а не полповерсии.</summary>
    private CancellationTokenSource? _cts;

    private bool _running;
    private readonly LaunchTypeChecks _launchChecks;
    private List<EquipmentGroup> _groups = new();
    private List<ControllerModification> _mods = new();
    private List<EquipmentSubType> _allSubtypes = new();
    private List<Row> _rows = new();

    /// <summary>Временные папки распакованных архивов — убираются при закрытии окна: они нужны только
    /// на время выбора файлов и переноса.</summary>
    private readonly List<string> _unpacked = new();

    public LegacyImportDialog(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _launchChecks = new LaunchTypeChecks(LaunchTypesPanel);

        _groups = _services.Db.GetAllEquipmentGroups();
        _mods = _services.Db.GetAllModifications();
        _allSubtypes = _services.Db.GetAllEquipmentSubtypes();
        Closed += (_, _) => Cleanup();
    }

    private List<EquipmentSubType> SubtypesFor(EquipmentGroup? group) =>
        group?.Id is null
            ? new List<EquipmentSubType>()
            : _allSubtypes.Where(s => s.GroupId == group.Id.Value).ToList();

    // ── Поиск ───────────────────────────────────────────────────────────────

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка со старой структурой" };
        if (dlg.ShowDialog() == true) SourceInput.Text = dlg.FolderName;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var root = SourceInput.Text.Trim();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Укажите существующую папку.", "Разобрать старый диск",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Справочник берётся из базы: набор шкафов у каждой конторы свой, зашивать его в разбор нельзя.
        var catalog = new LegacyCatalog(
            _groups.Select(g => g.Name).ToList(),
            _allSubtypes
                .Select(s => (GroupName: _groups.FirstOrDefault(g => g.Id == s.GroupId)?.Name ?? "", s.Name))
                .Where(x => x.GroupName.Length > 0)
                .ToList(),
            _mods.Select(m => m.ControllerName).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        // Поиск только ЧИТАЕТ (причём чужую папку) — права на перенос он не берёт. Но если наш
        // собственный диск прямо сейчас перекладывают, разбирать нечего: справочник и пути под
        // ногами меняются.
        if (_services.Operations.WholeDiskBusyReason() is { } busyNow)
        {
            AppMessageBox.Show(busyNow, "Разобрать старый диск", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        List<LegacyFinding> found;
        ScanButton.IsEnabled = false;
        try
        {
            // Обход чужой папки — это тысячи файлов и почти всегда сеть; окно висеть не должно.
            using (_host.BeginBusy("Ищем прошивки на старом диске…"))
                found = await Task.Run(() => LegacyDiskScanner.Scan(root, catalog));
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }

        _rows = found.Select(f => new Row(this, f)).ToList();
        FoundGrid.ItemsSource = _rows;
        RefreshSummary();
    }

    // ── Отметки и ручные правки ─────────────────────────────────────────────

    private void RowSelection_Changed(object sender, RoutedEventArgs e) => RefreshSummary();

    private void SelectRecognized_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Take = row.Ready && row.Finding.WorthTakingByDefault;
        RefreshSummary();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Take = false;
        RefreshSummary();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (FoundGrid.SelectedItem is not Row row) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{row.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.ShowStatus($"Не удалось открыть проводник: {ex.Message}");
        }
    }

    /// <summary>Распаковать архив выделенной строки и выбрать, что из него брать. Ровно то же окно,
    /// что при обычной загрузке папкой: прошивка ляжет под именем версии, отмеченные файлы — рядом,
    /// остальное не поедет.</summary>
    private void Unpack_Click(object sender, RoutedEventArgs e)
    {
        if (FoundGrid.SelectedItem is not Row row)
        {
            AppMessageBox.Show("Выделите строку с архивом.", "Разобрать старый диск",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!row.Finding.IsArchive)
        {
            AppMessageBox.Show("Это не архив — файл перенесётся как есть.", "Разобрать старый диск",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dest = Path.Combine(Path.GetTempPath(), $"antarus_legacy_{Guid.NewGuid():N}");
        var (ok, message) = ArchiveExtractor.Extract(row.FullPath, dest);
        if (!ok)
        {
            // .rar без WinRAR и архивы с паролем — честно говорим и оставляем «перенести как есть»:
            // потерять файл из-за того, что мы не смогли в него заглянуть, хуже.
            try { Directory.Delete(dest, recursive: true); } catch (Exception) { /* и не было */ }
            AppMessageBox.Show($"Распаковать не удалось: {message}\n\nСтроку можно перенести как есть — " +
                               "архив ляжет в папку версии целиком.", "Разобрать старый диск",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _unpacked.Add(dest);

        var pick = FolderContentsDialog.Pick(this, dest, LegacyDiskScanner.FirmwareExtensions, takeAllByDefault: false,
            $"Архив «{Path.GetFileName(row.FullPath)}» распакован. Отметьте, какой файл является прошивкой " +
            "и что взять вместе с ним:");
        if (pick is null) return;

        row.UseUnpacked(dest, pick.MainFile, pick.ExtraFiles);
        row.Take = row.Ready;
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var taken = _rows.Count(r => r.Take);
        var notReady = _rows.Count(r => r.Take && !r.Ready);
        ImportButton.IsEnabled = taken > 0 && notReady == 0;

        SummaryText.Text = _rows.Count == 0
            ? "Ничего не найдено. Укажите папку и нажмите «Найти прошивки»."
            : $"Найдено: {_rows.Count}, отмечено к переносу: {taken}." +
              (notReady > 0 ? $" У отмеченных не заполнено полностью: {notReady} — укажите тип, подтип и контроллер." : "");
    }

    // ── Перенос ─────────────────────────────────────────────────────────────

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var launchTypes = _launchChecks.Selected;
        if (launchTypes.Count == 0)
        {
            AppMessageBox.Show("Выберите хотя бы один тип пуска — он обязателен у каждой версии.",
                "Разобрать старый диск", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var todo = _rows.Where(r => r.Take && r.Ready).ToList();
        if (todo.Count == 0) return;

        var reply = AppMessageBox.Show(
            $"Перенести версий: {todo.Count}?\n\nФайлы будут СКОПИРОВАНЫ на сетевой диск " +
            "(старая папка останется нетронутой), каждая версия получит свой номер и запись в базе.",
            "Разобрать старый диск", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        if (!_services.Operations.TryBegin(LongOperationKind.LegacyImport, LongOperationSubject.None,
                "Перенос со старого диска", out var lease, out var busyRefusal))
        {
            AppMessageBox.Show(busyRefusal, "Разобрать старый диск", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _cts = new CancellationTokenSource();
        SetRunning(true);
        var done = 0;
        var stopped = false;
        try
        {
            // Индикатор внизу главного окна считает версии, а не байты: окно переноса можно отодвинуть
            // и работать дальше, но «сколько из скольки уже сделано» должно быть видно и без него.
            using (var busy = _host.BeginBusy("Переносим найденное на диск…"))
                foreach (var row in todo)
                {
                    if (_cts.IsCancellationRequested) { stopped = true; break; }
                    busy.Text = $"Переносим со старого диска: {row.RelativePath}";
                    busy.Report(done, todo.Count);
                    var ok = await ImportOneAsync(row, launchTypes);
                    if (ok) done++;
                }
        }
        catch (Exception ex)
        {
            // Окно немодальное — на него могли и не смотреть. Уведомление с категорией попадает в
            // историю колокольчика, а сообщение поднимает окно обратно.
            _host.ShowStatus($"\u26a0 Перенос со старого диска прерван ошибкой: {ex.Message}",
                12000, NotificationCategory.FirmwareAndParams);
            AppMessageBox.Show($"Перенос прерван:{Environment.NewLine}{ex.Message}", "Разобрать старый диск",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshSummary();
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
            lease!.Dispose();
        }

        _host.InvalidateSearchResults();
        _host.ShowStatus(stopped
                ? $"\u26a0 Перенос со старого диска остановлен: перенесено {done} из {todo.Count}. " +
                  "Непереснесённые строки остались отмеченными — можно продолжить."
                : $"Перенос со старого диска: перенесено версий {done} из {todo.Count}",
            stopped ? 12000 : 6000, NotificationCategory.FirmwareAndParams);
    }

    /// <summary>Кнопки на время работы. Окно немодальное, поэтому «идёт» и «не идёт» должно читаться
    /// по самому окну, а не по тому, что программа перестала отвечать.</summary>
    private void SetRunning(bool running)
    {
        _running = running;
        ImportButton.IsEnabled = !running;
        ScanButton.IsEnabled = !running;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = running;
        CloseButton.IsEnabled = !running;
        CancelPolicyLabel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelPolicyLabel.Text = running
            ? "Остановить можно: перенос прервётся между версиями, уже перенесённые останутся на диске " +
              "целыми. Закрыть окно во время работы нельзя — в нём разметка найденного."
            : "";
    }

    /// <summary>Остановка. Без подтверждения: обрыв здесь безопасен по построению — одна строка это
    /// одна обычная загрузка целиком, и между строками недоделанного не бывает.</summary>
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopButton.IsEnabled = false;
        CancelPolicyLabel.Text = "Останавливаемся — доносим текущую версию…";
    }

    /// <summary>Одна строка — одна обычная загрузка, разбитая на фазы: проверки и номер (БД, поток
    /// интерфейса) → копирование (фон) → запись (БД, поток интерфейса). Соединение с базой у
    /// приложения одно и не потокобезопасно, поэтому в фон уходит только дисковая фаза.</summary>
    private async Task<bool> ImportOneAsync(Row row, List<string> launchTypes)
    {
        var request = new FirmwareUploadRequest
        {
            SourcePath = row.SourcePath,
            SourceMainFile = row.MainFile,
            SourceFolderFiles = row.ExtraFiles.ToList(),
            Group = row.Group,
            Subtype = row.Subtype,
            Modification = row.Modification,
            LaunchTypes = launchTypes,
            Description = $"Перенос со старого диска: {row.RelativePath}" +
                          (row.Finding.VersionHint.Length > 0 ? $" (прежний номер {row.Finding.VersionHint})" : ""),
            IncludeDateInVersion = true,
            // Дата сборки — дата файла: перенос не делает прошивку 2019 года сегодняшней.
            VersionDate = DateFromFileCheck.IsChecked == true && row.Finding.Modified > DateTime.MinValue
                ? row.Finding.Modified
                : null,
            OpcEnabled = row.RequestNum.Trim().Length > 0 || row.CabinetSn.Trim().Length > 0,
            RequestNumRaw = row.RequestNum.Trim(),
            CabinetSnRaw = row.CabinetSn.Trim(),
            RootPath = _services.Cfg.RootPath(),
            InstructionPublisher = _services.Publisher(),
            NewDiskLayout = true,
            // Расширение спрашивать не у кого: файл выбрал оператор, и на старом диске половина
            // прошивок лежит архивами. Перезапись существующей папки версии, наоборот, НЕ
            // подтверждается заранее — про такую строку он должен узнать и решить сам.
            ConfirmUnknownExtension = true,
            AuthorUserName = _services.CurrentUserName,
        };

        var (plan, failure) = FirmwareUploadService.Prepare(_services.Db, _services.Hierarchy, request);
        if (plan is null)
        {
            row.Result = failure!.Outcome == FirmwareUploadOutcome.NeedsConfirmation
                ? $"нужно решение: {failure.ConfirmationMessage?.Replace("\n", " ")}"
                : string.Join("; ", failure.Errors.Concat(new[] { failure.IoErrorMessage ?? "" })
                    .Where(x => x.Length > 0));
            return false;
        }

        var copy = await Task.Run(() => FirmwareUploadService.CopyFiles(plan, new Services.ShortcutCreator(),
            _services.StubWriter()));
        if (copy.IoErrorMessage is not null)
        {
            row.Result = $"ошибка копирования: {copy.IoErrorMessage}";
            return false;
        }

        var result = FirmwareUploadService.Register(_services.Db, _services.Hierarchy, plan, copy);
        if (!result.IsSuccess)
        {
            row.Result = string.Join("; ", result.Errors);
            return false;
        }

        row.Result = $"перенесено: {result.Record!.VersionRaw}" +
                     (result.Warnings.Count > 0 ? $" · {string.Join("; ", result.Warnings)}" : "");
        row.Take = false;
        return true;
    }

    private void Cleanup()
    {
        foreach (var folder in _unpacked)
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception) { /* временная папка — не повод показывать ошибку */ }
        }
        _unpacked.Clear();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Пока перенос идёт, окно не закрывается: в нём вся ручная разметка найденного (тип,
    /// подтип, контроллер, выбранные файлы), которую собирали руками и восстановить нечем.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            AppMessageBox.Show(
                "Перенос ещё идёт. Закрыть окно нельзя — в нём вся разметка найденного." +
                Environment.NewLine + Environment.NewLine +
                "Программой при этом можно пользоваться: окно не модальное, просто отодвиньте его. " +
                "Прервать работу — кнопкой «Остановить».",
                "Разобрать старый диск", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        base.OnClosing(e);
    }
}
