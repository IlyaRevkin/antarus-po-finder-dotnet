using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using AntarusPoFinder.App.Services;
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
/// выпускать только после релиза, который умеет читать обе раскладки.</summary>
public partial class DiskMigrationDialog : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private DiskLayoutMigrator.MigrationPlan? _plan;
    private bool _applied;

    private sealed class OpRow
    {
        public DiskLayoutMigrator.Op Op { get; init; } = null!;
        public string KindLabel => Op.KindLabel;
        public string Note => Op.Note;
        public string Source => Op.Source;
        public string StatusLabel => Op.Status switch
        {
            "ok" => "готово",
            "skip" => "пропущено",
            "error" => "ошибка",
            _ => "",
        };
    }

    public DiskMigrationDialog(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        InstructionsCheck.IsEnabled = !string.IsNullOrWhiteSpace(services.Cfg.ThirdDiskPath());
        if (!InstructionsCheck.IsEnabled)
            InstructionsCheck.ToolTip = "Третий диск (инструкции) не настроен — Настройки → Сетевые диски.";
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
            _services.Cfg.ThirdDiskPath(),
            _services.Cfg.ThirdDiskShortcuts(),
            // Вместе с архивными: снятая с показа версия — это по-прежнему папка на диске, и оставить
            // её в старой раскладке значит оставить полперестройки недоделанной.
            _services.Db.GetAllFwVersionsWithNames(includeArchived: true),
            new DiskLayoutMigrator.MigrationOptions(
                RenameCheck.IsChecked == true,
                InstructionsCheck.IsChecked == true && InstructionsCheck.IsEnabled,
                FoldIntoVersionCheck.IsChecked == true,
                OpcCheck.IsChecked == true,
                InstructionNamingCheck.IsChecked == true));

        DiskLayoutMigrator.MigrationPlan plan;
        // Обход всех папок версий на сетевом диске — минуты; окно во время этого не должно висеть.
        using (_host.BeginBusy("Считаем план перестройки диска…"))
            plan = await Task.Run(() => DiskLayoutMigrator.Plan(input));

        _plan = plan;
        _applied = false;
        ShowPlan(plan);
        // «Создать недостающие папки» — самостоятельная работа: она бывает нужна и тогда, когда
        // двигать/переименовывать нечего (диск уже канонический, но папок под новые подтипы нет).
        RunButton.IsEnabled = plan.Ops.Count > 0 || FoldersCheck.IsChecked == true;
    }

    private void ShowPlan(DiskLayoutMigrator.MigrationPlan plan)
    {
        OpsGrid.ItemsSource = plan.Ops.Select(op => new OpRow { Op = op }).ToList();

        var parts = new List<string>();
        if (plan.Ops.Count == 0)
            parts.Add(_applied ? "Всё выполнено." : "Менять нечего — диск уже соответствует правилам.");
        else
            parts.Add(_applied
                ? $"Выполнено: {plan.Ops.Count(o => o.Status == "ok")}, пропущено: {plan.Ops.Count(o => o.Status == "skip")}, " +
                  $"ошибок: {plan.Ops.Count(o => o.Status == "error")}."
                : $"Операций в плане: {plan.Ops.Count}. Ничего ещё не изменено.");

        if (plan.Skipped.Count > 0)
            parts.Add("Пропущено при планировании:\n• " + string.Join("\n• ", plan.Skipped.Take(15)) +
                      (plan.Skipped.Count > 15 ? $"\n• …и ещё {plan.Skipped.Count - 15}" : ""));

        var errors = plan.Ops.Where(o => o.Status == "error").Take(5).Select(o => $"{o.Note}: {o.Error}").ToList();
        if (errors.Count > 0) parts.Add("Ошибки:\n• " + string.Join("\n• ", errors));

        SummaryText.Text = string.Join("\n\n", parts);
    }

    // ── Выполнение ──────────────────────────────────────────────────────────

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        if (_plan.Ops.Count == 0 && FoldersCheck.IsChecked != true) return;

        var reply = AppMessageBox.Show(
            $"Будет выполнено операций: {_plan.Ops.Count}." +
            (FoldersCheck.IsChecked == true ? "\nПлюс создание недостающих папок структуры." : "") + "\n\n" +
            "Запускать нужно на ОДНОЙ машине и когда коллеги не заливают прошивки. " +
            "Журнал операций сохранится на диск в папку «Конфиг».\n\nПродолжить?",
            "Перестроить структуру", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        var journalPath = WriteJournal(_plan, "plan");

        RunButton.IsEnabled = false;
        PlanButton.IsEnabled = false;
        try
        {
            // Правки в БД (имя файла и подсказка «чем открывать») собираем в очередь и применяем на
            // потоке интерфейса: соединение с базой у приложения одно, и писать в него из фонового
            // потока посреди обхода диска нельзя.
            var renames = new List<DiskLayoutMigrator.Op>();
            var repoints = new List<DiskLayoutMigrator.Op>();
            var shortcuts = new ShortcutCreator();

            using (_host.BeginBusy("Перестраиваем структуру диска…"))
                await Task.Run(() => DiskLayoutMigrator.Apply(_plan, op => renames.Add(op), shortcuts,
                    repointed: op => repoints.Add(op), stubs: new InstructionStubWriter()));

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

            if (FoldersCheck.IsChecked == true)
                await CreateMissingFoldersAsync();

            _applied = true;
            WriteJournal(_plan, "result", journalPath);
            ShowPlan(_plan);
            _host.ShowStatus($"Структура диска перестроена: {_plan.Ops.Count(o => o.Status == "ok")} операций",
                category: NotificationCategory.Sync);
        }
        finally
        {
            PlanButton.IsEnabled = true;
            RunButton.IsEnabled = false;
        }
    }

    private async Task CreateMissingFoldersAsync()
    {
        var root = _services.Cfg.RootPath();
        var plan = _services.Hierarchy.PlanStructure(root, _services.Cfg.ThirdDiskPath());
        EnsureStructureResult result;
        // Заглушка кладётся вместе с созданием папок: пустая папка «Инструкция» неотличима от
        // «инструкцию потеряли», а версия для общей папки контроллера не нужна — заглушка одна на
        // папку (см. InstructionStub).
        var stubs = InstructionNamingCheck.IsChecked == true ? new InstructionStubWriter() : null;
        using (_host.BeginBusy("Проверка структуры папок…"))
            result = await Task.Run(() => HierarchyService.ApplyStructurePlan(plan, stubs));
        if (result.CreatedCount > 0)
            _host.ShowStatus($"Создано папок: {result.CreatedCount}", category: NotificationCategory.Sync);
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
}
