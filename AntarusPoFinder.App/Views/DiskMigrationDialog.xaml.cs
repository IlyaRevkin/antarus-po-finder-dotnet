using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Microsoft.Win32;

namespace AntarusPoFinder.App.Views;

/// <summary>Окно разовой операции «Перестроить структуру диска» — то, чего не хватало после смены
/// правил раскладки: правила поменялись только для НОВЫХ загрузок, а всё накопленное на диске
/// осталось как было.
///
/// Порядок работы жёсткий и намеренно двухшаговый: сначала «Показать план» (ничего не меняет,
/// перечисляет операции), потом «Выполнить». Перед первой операцией на диск кладётся журнал
/// <c>Конфиг\migration_log_&lt;дата&gt;.json</c> — по нему видно, что именно двигали, если потом
/// придётся разбираться.
///
/// Что операция НЕ делает (и почему): не переименовывает папки версий, не переносит ОПЦ и не
/// затаскивает пять папок внутрь версии (docs/hierarchy-rework-plan.md, этапы 4–5). Всё это меняет
/// <c>disk_path</c>, а он у коллег при импорте общего конфига не обновляется — такой переезд можно
/// выпускать только после релиза, который умеет читать обе раскладки.
///
/// ⚠️ Окно НЕмодальное. Перестройка обходит весь диск и идёт минутами, а раньше всё это время
/// программа стояла — та же жалоба, что и про заливку в ПЛК. Что из этого следует:
///
/// • Право на работу берётся в LongOperationRegistry как DiskRebuild: пока перестройка идёт, ни она
///   сама, ни заливка, ни сборка LFS второй раз не запустятся — перекладывать файлы под ногами у
///   работающей операции нельзя.
/// • Обрыв возможен, но ПО ГРАНИЦЕ ОПЕРАЦИИ (см. DiskLayoutMigrator.Apply): между строками плана
///   диск в согласованном состоянии, внутри переноса папки — нет.
/// • Пока работа идёт, окно не закрывается: план и журнал живут в нём, и потерять их на середине
///   значит не узнать, что успело переехать.</summary>
public partial class DiskMigrationDialog : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private DiskLayoutMigrator.MigrationPlan? _plan;
    private List<OpRow> _rows = new();
    private bool _applied;
    private CancellationTokenSource? _cts;
    private bool _running;

    private sealed class OpRow : INotifyPropertyChanged
    {
        public DiskLayoutMigrator.Op Op { get; init; } = null!;
        public string KindLabel => Op.KindLabel;
        public string Note => Op.Note;

        /// <summary>Где это происходит: у обычной операции — сам файл/папка, у строки, ждущей
        /// ручного выбора, — папка версии (файла-то ещё нет).</summary>
        public string Source => Op.Source.Length > 0 ? Op.Source : Op.VersionDir;

        /// <summary>Строку, ждущую ручного выбора файла, отметить нельзя: сначала файл, потом
        /// галочка. Иначе «Выполнить» тихо пропускала бы её и человек считал бы, что сделано.</summary>
        public bool CanRun => !Op.NeedsChoice;

        public bool Selected
        {
            get => Op.Selected;
            set
            {
                if (Op.Selected == value) return;
                Op.Selected = value;
                OnPropertyChanged();
            }
        }

        public string StatusLabel => Op.Status switch
        {
            "ok" => "готово",
            "skip" => "пропущено",
            "error" => "ошибка",
            "off" => "не выбрано",
            "cancel" => "не успели",
            _ => "",
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh()
        {
            OnPropertyChanged(nameof(KindLabel));
            OnPropertyChanged(nameof(Note));
            OnPropertyChanged(nameof(Source));
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(StatusLabel));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public DiskMigrationDialog(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
    }

    // ── План ────────────────────────────────────────────────────────────────

    private async void Plan_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Перестроить структуру", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var input = new DiskLayoutMigrator.MigrationInput(
            root,
            // Вместе с архивными: снятая с показа версия — это по-прежнему папка на диске, и оставить
            // её в старой раскладке значит оставить полперестройки недоделанной.
            _services.Db.GetAllFwVersionsWithNames(includeArchived: true),
            new DiskLayoutMigrator.MigrationOptions(
                RenameCheck.IsChecked == true,
                FoldIntoVersionCheck.IsChecked == true,
                OpcCheck.IsChecked == true,
                InstructionNamingCheck.IsChecked == true));

        // Подсчёт плана ничего не двигает — он ЧИТАЕТ диск. Поэтому права «занимаю весь диск» он не
        // берёт (иначе чужая заливка получала бы отказ «перестройка переносит файлы», хотя ничего
        // ещё не переносится), но спрашивает, не переезжает ли диск прямо сейчас: план, посчитанный
        // посреди чужого переезда, к моменту «Выполнить» уже врёт.
        if (_services.Operations.WholeDiskBusyReason() is { } busyNow)
        {
            AppMessageBox.Show(busyNow, "Перестроить структуру", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DiskLayoutMigrator.MigrationPlan plan;
        // Обход всех папок версий на сетевом диске — минуты; окно во время этого не должно висеть.
        using (_host.BeginBusy("Считаем план перестройки диска…"))
            plan = await Task.Run(() => DiskLayoutMigrator.Plan(input));

        _plan = plan;
        _applied = false;
        ShowPlan(plan);
        // «Создать недостающие папки» — самостоятельная работа: она бывает нужна и тогда, когда
        // двигать/переименовывать нечего (диск уже канонический, но папок под новые подтипы нет).
        RunButton.IsEnabled = plan.Ops.Any(o => o.Selected) || FoldersCheck.IsChecked == true;
    }

    private void ShowPlan(DiskLayoutMigrator.MigrationPlan plan)
    {
        _rows = plan.Ops.Select(op => new OpRow { Op = op }).ToList();
        OpsGrid.ItemsSource = _rows;

        var parts = new List<string>();
        if (plan.Ops.Count == 0)
            parts.Add(_applied ? "Всё выполнено." : "Менять нечего — диск уже соответствует правилам.");
        else if (_applied)
        {
            var line = $"Выполнено: {plan.Ops.Count(o => o.Status == "ok")}, пропущено: {plan.Ops.Count(o => o.Status == "skip")}, " +
                       $"ошибок: {plan.Ops.Count(o => o.Status == "error")}";
            var off = plan.Ops.Count(o => o.Status == "off");
            parts.Add(off > 0 ? $"{line}, не выбрано: {off}." : line + ".");
        }
        else
        {
            var waiting = plan.Ops.Count(o => o.NeedsChoice);
            parts.Add($"Операций в плане: {plan.Ops.Count}, отмечено: {plan.Ops.Count(o => o.Selected)}. Ничего ещё не изменено." +
                      (waiting > 0
                          ? $"\nЖдут ручного выбора файла: {waiting} — выделите строку и нажмите «Указать файл прошивки…»."
                          : ""));
        }

        if (plan.Skipped.Count > 0)
            parts.Add("Пропущено при планировании:\n• " + string.Join("\n• ", plan.Skipped.Take(15)) +
                      (plan.Skipped.Count > 15 ? $"\n• …и ещё {plan.Skipped.Count - 15}" : ""));

        var errors = plan.Ops.Where(o => o.Status == "error").Take(5).Select(o => $"{o.Note}: {o.Error}").ToList();
        if (errors.Count > 0) parts.Add("Ошибки:\n• " + string.Join("\n• ", errors));

        SummaryText.Text = string.Join("\n\n", parts);
        if (!_applied) RunButton.IsEnabled = plan.Ops.Any(o => o.Selected) || FoldersCheck.IsChecked == true;
    }

    /// <summary>Галочку строки сняли/поставили — пересчитать «отмечено: N» и доступность «Выполнить».
    /// Иначе кнопка оставалась бы активной у плана, где не отмечено ничего, и нажатие тихо ничего
    /// не делало бы.</summary>
    private void RowSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _applied) return;
        ShowPlanSummaryOnly(_plan);
    }

    /// <summary>Пересчёт подписи и кнопки без пересборки таблицы: пересобрать её прямо из обработчика
    /// галочки значит выдернуть у этой галочки её же строку посреди события.</summary>
    private void ShowPlanSummaryOnly(DiskLayoutMigrator.MigrationPlan plan)
    {
        var waiting = plan.Ops.Count(o => o.NeedsChoice);
        SummaryText.Text = $"Операций в плане: {plan.Ops.Count}, отмечено: {plan.Ops.Count(o => o.Selected)}. Ничего ещё не изменено." +
                           (waiting > 0
                               ? $"\nЖдут ручного выбора файла: {waiting} — выделите строку и нажмите «Указать файл прошивки…»."
                               : "");
        RunButton.IsEnabled = plan.Ops.Any(o => o.Selected) || FoldersCheck.IsChecked == true;
    }

    // ── Выполнение ──────────────────────────────────────────────────────────

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        var chosen = _plan.Ops.Count(o => o.Selected);
        if (chosen == 0 && FoldersCheck.IsChecked != true) return;

        var reply = AppMessageBox.Show(
            $"Будет выполнено операций: {chosen} из {_plan.Ops.Count}." +
            (FoldersCheck.IsChecked == true ? "\nПлюс создание недостающих папок структуры." : "") + "\n\n" +
            "Запускать нужно на ОДНОЙ машине и когда коллеги не заливают прошивки. " +
            "Журнал операций сохранится на диск в папку «Конфиг».\n\nПродолжить?",
            "Перестроить структуру", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        // Перестройка занимает ВЕСЬ диск: пока она идёт, ни заливка, ни сборка LFS, ни вторая
        // перестройка не запустятся (LongOperationRules.TouchesWholeDisk). Раньше это обеспечивала
        // модальность окна — теперь реестр.
        if (!_services.Operations.TryBegin(LongOperationKind.DiskRebuild, LongOperationSubject.None,
                "Перестройка структуры диска", out var lease, out var busyRefusal))
        {
            AppMessageBox.Show(busyRefusal, "Перестроить структуру", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var journalPath = WriteJournal(_plan, "plan");

        _cts = new CancellationTokenSource();
        SetRunning(true);
        var stopped = false;
        try
        {
            // Правки в БД (имя файла и подсказка «чем открывать») собираем в очередь и применяем на
            // потоке интерфейса: соединение с базой у приложения одно, и писать в него из фонового
            // потока посреди обхода диска нельзя.
            var renames = new List<DiskLayoutMigrator.Op>();
            var repoints = new List<DiskLayoutMigrator.Op>();

            // Инструкции и заглушки, легшие при перестройке на первый диск, уходят на хостинг под теми
            // же ключами, что и при обычной загрузке — иначе после перестройки QR вёл бы в никуда, а
            // заглушек «в разработке» на хостинге не появилось бы вовсе. Хостинг не настроен (ключей
            // нет) — For() вернёт null, и выкладка просто не делается.
            var publisher = _services.Publisher();
            var firstRoot = _services.Cfg.RootPath();
            var token = _cts.Token;
            // Индикатор внизу главного окна показывает долю сделанного: окно перестройки можно
            // отодвинуть и работать дальше, но «сколько ещё ждать» должно быть видно и без него.
            using (var busy = _host.BeginBusy("Перестраиваем структуру диска…"))
            {
                var scope = busy;
                await Task.Run(() => DiskLayoutMigrator.Apply(_plan, op => renames.Add(op),
                    progress: (done, total) => Dispatcher.BeginInvoke(new Action(() => scope.Report(done, total))),
                    repointed: op => repoints.Add(op), stubs: _services.StubWriter(),
                    publisher: publisher, firstRoot: firstRoot, cancellationToken: token));
            }
            // Именно у плана, а не у токена: нажатие «Остановить» на последней операции взводит
            // токен, но пропускать уже нечего (см. MigrationPlan.Cancelled).
            stopped = _plan.Cancelled;

            // Записей на одну папку может быть несколько, и disk_path у части из них — устаревший
            // (папку переименовали на диске); правим по каждому известному пути, см. Op.RecordPaths.
            foreach (var op in renames)
                foreach (var dbPath in op.RecordPaths)
                    _services.Db.RenameFirmwareFileRecords(dbPath, op.OldName, op.NewName);

            // Перенос ОПЦ — единственная операция, которая меняет путь версии. Правим строго ПОСЛЕ
            // удачного переноса на диске (см. DiskLayoutMigrator.Apply): иначе прерванный обрывом шары
            // прогон оставил бы записи, указывающие туда, куда папка так и не уехала.
            foreach (var op in repoints)
                if (op.FwVersionId > 0)
                    _services.Db.RepointFwVersionDiskPath(op.FwVersionId, op.Target);

            // Диск перестроен под «пять папок внутри версии» — с этого момента новые версии должны
            // рождаться в той же раскладке, причём НА ВСЕХ машинах, а не только на этой. Настройка
            // общая и уезжает синхронизацией (см. ConfigService.DiskLayoutV2); ставим её только по
            // факту удавшихся операций, а не по одной галочке: неудачный прогон не должен переключать
            // раскладку записи на диске, где ничего не переехало.
            if (_plan.Ops.Any(o => o.Kind == DiskLayoutMigrator.OpKind.FoldIntoVersion && o.Status == "ok")
                && !_services.Cfg.DiskLayoutV2())
            {
                _services.Cfg.SetDiskLayoutV2(true);
                _host.PushCatalogChange("Диск перестроен: новые версии заводятся с папками «Прошивка», «Инструкция», «Карта ВВ», «Карта Modbus», «HMI»");
            }

            if (!stopped && FoldersCheck.IsChecked == true)
                await CreateMissingFoldersAsync();

            _applied = true;
            WriteJournal(_plan, "result", journalPath);
            ShowPlan(_plan);
            var doneCount = _plan.Ops.Count(o => o.Status == "ok");
            var leftCount = _plan.Ops.Count(o => o.Status == "cancel");
            // Итог — уведомлением, а не только текстом в окне: окно немодальное, и к этому моменту
            // человек вполне может смотреть на другую страницу.
            _host.ShowStatus(stopped
                    ? $"\u26a0 Перестройка диска остановлена: сделано {doneCount}, осталось {leftCount}. " +
                      "Повторный запуск предложит недоделанное снова."
                    : $"Структура диска перестроена: {doneCount} операций",
                stopped ? 12000 : 6000, NotificationCategory.Sync);
        }
        catch (Exception ex)
        {
            // Немодальное окно могли отодвинуть — об ошибке надо сказать так, чтобы её не потеряли.
            _host.ShowStatus($"\u26a0 Перестройка диска не удалась: {ex.Message}", 12000, NotificationCategory.Sync);
            AppMessageBox.Show($"Перестройка не завершена:{Environment.NewLine}{ex.Message}",
                "Перестроить структуру", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
            lease!.Dispose();
        }
    }

    /// <summary>Кнопки и подпись на время работы. Окно немодальное, поэтому «идёт» и «не идёт»
    /// должно быть видно по самому окну, а не по тому, что программа не отвечает.</summary>
    private void SetRunning(bool running)
    {
        _running = running;
        RunButton.IsEnabled = false;
        PlanButton.IsEnabled = !running;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = running;
        CloseButton.IsEnabled = !running;
        CancelPolicyLabel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelPolicyLabel.Text = running
            ? "Остановить можно: перестройка прервётся на границе очередной операции, уже переехавшее " +
              "останется переехавшим, недоделанное попадёт в журнал и будет предложено снова. " +
              "Закрыть окно во время работы нельзя — в нём план и журнал."
            : "";
    }

    /// <summary>Остановка. Подтверждения не спрашиваем: обрыв здесь безопасен по построению (см.
    /// DiskLayoutMigrator.Apply), а лишний вопрос посреди получасовой операции только раздражает.</summary>
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopButton.IsEnabled = false;
        CancelPolicyLabel.Text = "Останавливаемся — доделываем текущую операцию…";
    }

    private async Task CreateMissingFoldersAsync()
    {
        var root = _services.Cfg.RootPath();
        var plan = _services.Hierarchy.PlanStructure(root);
        EnsureStructureResult result;
        // Заглушка кладётся вместе с созданием папок: пустая папка «Инструкция» неотличима от
        // «инструкцию потеряли», а версия для общей папки контроллера не нужна — заглушка одна на
        // папку (см. InstructionStub).
        var stubs = InstructionNamingCheck.IsChecked == true ? _services.StubWriter() : null;
        using (_host.BeginBusy("Проверка структуры папок…"))
            result = await Task.Run(() => HierarchyService.ApplyStructurePlan(plan, stubs));
        if (result.CreatedCount > 0)
            _host.ShowStatus($"Создано папок: {result.CreatedCount}", category: NotificationCategory.Sync);
    }

    // ── Ручной разбор плана ─────────────────────────────────────────────────

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllSelected(false);

    private void SetAllSelected(bool value)
    {
        foreach (var row in _rows)
        {
            // Строку, ждущую выбора файла, «отметить всё» не включает: делать в ней пока нечего.
            if (value && !row.CanRun) continue;
            row.Selected = value;
        }
        if (_plan is not null) ShowPlan(_plan);
    }

    /// <summary>Ручной выбор файла прошивки в многофайловой папке версии. Раньше такая папка
    /// пропускалась целиком («в многофайловой папке имя файла привязано к подсказке „чем открывать“»),
    /// и привести её имя к норме было нечем вовсе. Теперь оператор указывает файл сам — и ровно этот
    /// файл переименовывается, а подсказка «чем открывать» правится вместе с ним (Op.RecordPaths).</summary>
    private void PickFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (OpsGrid.SelectedItem is not OpRow row)
        {
            AppMessageBox.Show("Выделите в таблице строку «Указать файл прошивки».", "Перестроить структуру",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        PickFirmwareFor(row);
    }

    private void OpsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (OpsGrid.SelectedItem is not OpRow row) return;
        if (row.Op.NeedsChoice) { PickFirmwareFor(row); return; }

        // Обычная строка — просто показать, о чём речь: открыть папку версии в проводнике.
        var folder = row.Op.VersionDir.Length > 0 ? row.Op.VersionDir : Path.GetDirectoryName(row.Op.Source);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.ShowStatus($"Не удалось открыть папку: {ex.Message}");
        }
    }

    private void PickFirmwareFor(OpRow row)
    {
        var op = row.Op;
        if (!op.NeedsChoice)
        {
            AppMessageBox.Show("У этой строки файл выбирать не нужно.", "Перестроить структуру",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picked = PickFileDialog.Pick(this, "Какой файл здесь прошивка",
            $"В папке версии {op.VersionRaw} несколько файлов. Укажите файл прошивки — он будет " +
            "переименован по норме, остальные останутся как есть:",
            op.VersionDir);
        if (string.IsNullOrEmpty(picked)) return;

        var source = Path.Combine(op.VersionDir, picked);
        var number = FwVersionNumber.Parse(op.VersionRaw);
        if (number is null || !File.Exists(source)) return;

        var canonical = FirmwareNaming.BuildFirmwareFilename(number, Path.GetExtension(source),
            op.RequestNum, op.CabinetSn);
        var target = Path.Combine(Path.GetDirectoryName(source)!, canonical);
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            AppMessageBox.Show($"«{Path.GetFileName(source)}» уже назван по норме — переименовывать нечего.",
                "Перестроить структуру", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        op.Source = source;
        op.Target = target;
        op.Note = $"{Path.GetFileName(source)} → {canonical}";
        op.Selected = true;
        row.Refresh();
        if (_plan is not null) ShowPlan(_plan);
    }

    // ── Журнал ──────────────────────────────────────────────────────────────

    /// <summary>Журнал пишется ДО первой операции (stage=plan) и переписывается после (stage=result).
    /// Недоступный диск не должен отменять саму операцию — тогда журнал просто не сохранится, о чём
    /// говорит статус-строка.</summary>
    private string? WriteJournal(DiskLayoutMigrator.MigrationPlan plan, string stage, string? path = null)
    {
        try
        {
            var root = _services.Cfg.RootPath();
            var dir = Path.Combine(root, "Конфиг");
            Directory.CreateDirectory(dir);
            path ??= Path.Combine(dir, $"migration_log_{DateTime.Now:yyyyMMdd_HHmmss}.json");

            var payload = new
            {
                stage,
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                by = _services.CurrentUserName,
                machine = Environment.MachineName,
                skipped = plan.Skipped,
                ops = plan.Ops.Select(o => new { kind = o.Kind.ToString(), o.Source, o.Target, o.Note, o.Status, o.Error }),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return path;
        }
        catch (Exception ex)
        {
            _host.ShowStatus($"Журнал перестройки не сохранён: {ex.Message}");
            return path;
        }
    }

    private void SavePlan_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null)
        {
            AppMessageBox.Show("Сначала постройте план.", "Перестроить структуру", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            FileName = $"migration_plan_{DateTime.Now:yyyyMMdd_HHmm}.txt",
            Filter = "Текстовый файл (*.txt)|*.txt",
        };
        if (dlg.ShowDialog() != true) return;

        var lines = _plan.Ops.Select(o => $"{o.KindLabel}\t{o.Note}\t{o.Source}\t{o.Target}\t{o.Status}{(o.Error.Length > 0 ? " — " + o.Error : "")}");
        var text = string.Join(Environment.NewLine, lines);
        if (_plan.Skipped.Count > 0)
            text += Environment.NewLine + Environment.NewLine + "Пропущено:" + Environment.NewLine +
                    string.Join(Environment.NewLine, _plan.Skipped);
        try
        {
            File.WriteAllText(dlg.FileName, text);
            _host.ShowStatus($"План сохранён: {dlg.FileName}");
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось сохранить: {ex.Message}", "Перестроить структуру",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Пока перестройка идёт, окно не закрывается: в нём и план, и то, что уже успело
    /// переехать, — потеряв его на середине, разбираться придётся по журналу на диске. Остановить
    /// работу можно кнопкой «Остановить», и это отдельное осознанное действие.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            AppMessageBox.Show(
                "Перестройка ещё идёт. Закрыть окно нельзя — в нём план и то, что уже сделано." +
                Environment.NewLine + Environment.NewLine +
                "Программой при этом можно пользоваться: окно не модальное, просто отодвиньте его. " +
                "Прервать работу — кнопкой «Остановить».",
                "Перестроить структуру", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        base.OnClosing(e);
    }
}
