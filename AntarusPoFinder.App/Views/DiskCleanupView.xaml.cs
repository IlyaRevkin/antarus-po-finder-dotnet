using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Microsoft.Win32;

namespace AntarusPoFinder.App.Views;

/// <summary>Страница «Чистка диска»: ищет мусорные файлы, не подходящие под структуру, и предлагает
/// либо переименовать их как положено, либо удалить. Вся логика — в
/// <see cref="DiskCleanupScanner"/>; здесь только предпросмотр и подтверждение.
///
/// Порядок жёстко двухшаговый, как у «Перестроить структуру диска» (DiskMigrationDialog): сначала
/// «Проверить диск» — обход без единой правки, потом «Применить отмеченное». Перед первой операцией
/// на диск ложится журнал <c>Конфиг\cleanup_log_&lt;дата&gt;.json</c>: чистка удаляет файлы, и вопрос
/// «что именно она унесла» обязан иметь письменный ответ.
///
/// Страница только для администратора (см. RolesConfig.RoleAccess): наладчик и программист работают
/// с диском через загрузку и выдачу, а тут одним нажатием двигается и удаляется чужая работа.</summary>
public partial class DiskCleanupView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private DiskCleanupScanner.CleanupPlan? _plan;
    private bool _applied;

    /// <summary>Строка таблицы. Действие держится строкой, а не перечислением: выпадающий список в
    /// DataGrid связывается со строкой без конвертеров, а имена действий всё равно нужны те же
    /// самые и в журнале.</summary>
    private sealed class Row
    {
        public DiskCleanupScanner.Finding Finding { get; init; } = null!;
        public string Root { get; init; } = "";

        public bool Selected
        {
            get => Finding.Selected;
            set => Finding.Selected = value;
        }

        /// <summary>Путь от корня диска: полный путь к сетевой шаре в каждой строке съедал бы всю
        /// колонку, а различаются строки как раз хвостом.</summary>
        public string FileDisplay
        {
            get
            {
                try { return Path.GetRelativePath(Root, Finding.Path); }
                catch (Exception) { return Finding.Path; }
            }
        }

        /// <summary>Полный путь — только в подсказке при наведении: в колонке он не помещается, а
        /// знать «а это точно тот файл?» перед удалением надо.</summary>
        public string FullPath => Finding.Path;

        public string IssueLabel => Finding.IssueLabel;
        public string Reason => Finding.Reason;

        public IReadOnlyList<string> ActionOptions =>
            Finding.AllowedActions.Select(DiskCleanupScanner.Finding.ActionLabel).ToList();

        public string ActionLabel
        {
            get => DiskCleanupScanner.Finding.ActionLabel(Finding.Action);
            set
            {
                foreach (var act in Finding.AllowedActions)
                    if (DiskCleanupScanner.Finding.ActionLabel(act) == value)
                    {
                        Finding.Action = act;
                        return;
                    }
            }
        }

        public string StatusLabel => Finding.Status switch
        {
            "ok" => "готово",
            "skip" => "пропущено",
            "error" => "ошибка",
            _ => "",
        };
    }

    public DiskCleanupView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
    }

    /// <summary>Возврат на страницу сбрасывает показанный список. Держать его между заходами нельзя:
    /// пока оператор был на другой вкладке, коллега мог залить прошивку в ту же папку, и «применить
    /// отмеченное» по устаревшему списку удаляло бы уже не то, что человек видел глазами. Сам
    /// <see cref="DiskCleanupScanner.Apply"/> от этого защищён (пропавший файл и занятая цель — skip),
    /// но показывать заведомо неверный список всё равно нельзя.</summary>
    public void RefreshIfActive()
    {
        _plan = null;
        _applied = false;
        FindingsGrid.ItemsSource = null;
        ApplyButton.IsEnabled = false;
        SummaryText.Text = "Нажмите «Проверить диск». Обход сетевой папки занимает минуты — окно при этом остаётся рабочим.";
    }

    // ── Проверка ────────────────────────────────────────────────────────────

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Чистка диска", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Всё, что читается из базы, читается ЗДЕСЬ, на потоке интерфейса: соединение SQLite у
        // приложения одно и не потокобезопасно (см. HierarchyService), а обход диска ниже уходит в
        // фон целиком.
        var input = new DiskCleanupScanner.CleanupInput(
            root,
            // Вместе с архивными: снятая с показа версия — это по-прежнему папка на диске, и её файлы
            // тоже защищены от удаления ссылкой из базы.
            _services.Db.GetAllFwVersionsWithNames(includeArchived: true),
            _services.Db.GetAllowedExtensions(),
            _services.Db.GetAllowedExtensionsHmi(),
            _services.Db.GetAllowedExtensionsSchematic(),
            ReferencedParamFiles());

        DiskCleanupScanner.CleanupPlan plan;
        ScanButton.IsEnabled = false;
        try
        {
            using (_host.BeginBusy("Ищем мусор на диске…"))
                plan = await Task.Run(() => DiskCleanupScanner.Plan(input));
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }

        _plan = plan;
        _applied = false;
        Show(plan);
        ApplyButton.IsEnabled = plan.Findings.Count > 0;
    }

    /// <summary>Файлы параметров ПЧ/УПП — вторая после прошивок группа записей, которые ссылаются на
    /// файлы на диске. Без них файл параметров с незнакомым расширением выглядел бы для чистильщика
    /// ничьим.</summary>
    private List<string> ReferencedParamFiles() =>
        _services.Db.GetParamFiles()
            .Where(pf => !string.IsNullOrWhiteSpace(pf.DiskPath) && !string.IsNullOrWhiteSpace(pf.Filename))
            .Select(pf => Path.Combine(pf.DiskPath, pf.Filename))
            .ToList();

    private void Show(DiskCleanupScanner.CleanupPlan plan)
    {
        var root = _services.Cfg.RootPath();
        FindingsGrid.ItemsSource = plan.Findings.Select(f => new Row { Finding = f, Root = root }).ToList();

        var parts = new List<string>();
        if (plan.Findings.Count == 0)
            parts.Add(_applied ? "Всё выполнено." : "Ничего лишнего не нашлось — диск соответствует правилам.");
        else if (_applied)
            parts.Add($"Выполнено: {plan.Findings.Count(f => f.Status == "ok")}, " +
                      $"пропущено: {plan.Findings.Count(f => f.Status == "skip")}, " +
                      $"ошибок: {plan.Findings.Count(f => f.Status == "error")}.");
        else
            parts.Add($"Находок: {plan.Findings.Count} " +
                      $"(переименовать — {Count(plan, DiskCleanupScanner.Issue.FirmwareName)}, " +
                      $"перенести — {Count(plan, DiskCleanupScanner.Issue.WrongFolder)}, " +
                      $"мусор — {Count(plan, DiskCleanupScanner.Issue.Junk)}, " +
                      $"нужно решить — {Count(plan, DiskCleanupScanner.Issue.NeedsDecision)}). " +
                      "Ничего ещё не изменено. Двойной клик по строке открывает файл в проводнике.");

        if (plan.Skipped.Count > 0)
            parts.Add("Пропущено при проверке:\n• " + string.Join("\n• ", plan.Skipped.Take(10)) +
                      (plan.Skipped.Count > 10 ? $"\n• …и ещё {plan.Skipped.Count - 10}" : ""));

        var errors = plan.Findings.Where(f => f.Status == "error").Take(5)
            .Select(f => $"{Path.GetFileName(f.Path)}: {f.Error}").ToList();
        if (errors.Count > 0) parts.Add("Ошибки:\n• " + string.Join("\n• ", errors));

        SummaryText.Text = string.Join("\n\n", parts);
    }

    private static int Count(DiskCleanupScanner.CleanupPlan plan, DiskCleanupScanner.Issue issue) =>
        plan.Findings.Count(f => f.Issue == issue);

    // ── Отметки ─────────────────────────────────────────────────────────────

    /// <summary>Отмечает только обратимые операции — переименование и перенос: файл после них
    /// остаётся на диске и находится программой. Удаление сюда не входит намеренно, его человек
    /// отмечает построчно.</summary>
    private void CheckSafe_Click(object sender, RoutedEventArgs e) =>
        SetSelection(f => f.Action is DiskCleanupScanner.Act.Rename or DiskCleanupScanner.Act.Move);

    private void UncheckAll_Click(object sender, RoutedEventArgs e) => SetSelection(_ => false);

    private void SetSelection(Func<DiskCleanupScanner.Finding, bool> selected)
    {
        if (_plan is null) return;
        foreach (var f in _plan.Findings) f.Selected = selected(f);
        FindingsGrid.Items.Refresh();
    }

    private void FindingsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!DataGridClickGuard.IsOverDataRow(e) || FindingsGrid.SelectedItem is not Row row) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Finding.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.ShowStatus($"Не удалось открыть проводник: {ex.Message}");
        }
    }

    // ── Применение ──────────────────────────────────────────────────────────

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;

        var todo = _plan.Findings.Where(f => f.Selected && f.Action != DiskCleanupScanner.Act.None).ToList();
        if (todo.Count == 0)
        {
            AppMessageBox.Show("Не отмечено ни одной строки.", "Чистка диска",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var deletes = todo.Count(f => f.Action == DiskCleanupScanner.Act.Delete);
        var reply = AppMessageBox.Show(
            $"Будет выполнено операций: {todo.Count}." +
            (deletes > 0 ? $"\nИз них удаление файлов: {deletes} — безвозвратно, мимо корзины." : "") + "\n\n" +
            "Запускать нужно на ОДНОЙ машине и когда коллеги не заливают прошивки. " +
            "Журнал сохранится на диск в папку «Конфиг».\n\nПродолжить?",
            "Чистка диска", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        var journalPath = WriteJournal(_plan, "plan");

        ScanButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        try
        {
            // Правки в базе (имя файла и подсказка «чем открывать») собираем в очередь и применяем
            // на потоке интерфейса — соединение SQLite одно, и писать в него из фонового потока
            // посреди обхода диска нельзя. Ровно так же поступает DiskMigrationDialog.
            var renames = new List<DiskCleanupScanner.Finding>();
            using (_host.BeginBusy("Чистим диск…"))
                await Task.Run(() => DiskCleanupScanner.Apply(_plan, renames.Add));

            // Записей на одну папку может быть несколько (конфигурации шкафа делят файлы), и часть
            // disk_path устарела — правим по каждому известному пути.
            foreach (var f in renames)
                foreach (var dbPath in f.RecordPaths)
                    _services.Db.RenameFirmwareFileRecords(dbPath, f.OldName, f.NewName);

            _applied = true;
            WriteJournal(_plan, "result", journalPath);
            Show(_plan);
            _host.ShowStatus($"Чистка диска: выполнено операций {_plan.Findings.Count(f => f.Status == "ok")}",
                category: NotificationCategory.Sync);
            if (renames.Count > 0) _host.InvalidateSearchResults();
        }
        finally
        {
            ScanButton.IsEnabled = true;
            // Список после выполнения уже неактуален — за новыми находками надо проверить диск заново.
            ApplyButton.IsEnabled = false;
        }
    }

    // ── Журнал и отчёт ──────────────────────────────────────────────────────

    /// <summary>Журнал пишется ДО первой операции (stage=plan) и переписывается после (stage=result).
    /// Недоступный диск не должен отменять саму чистку — тогда журнал просто не сохранится, о чём
    /// говорит статус-строка.</summary>
    private string? WriteJournal(DiskCleanupScanner.CleanupPlan plan, string stage, string? path = null)
    {
        try
        {
            var dir = Path.Combine(_services.Cfg.RootPath(), "Конфиг");
            Directory.CreateDirectory(dir);
            path ??= Path.Combine(dir, $"cleanup_log_{DateTime.Now:yyyyMMdd_HHmmss}.json");

            var payload = new
            {
                stage,
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                by = _services.CurrentUserName,
                machine = Environment.MachineName,
                skipped = plan.Skipped,
                findings = plan.Findings.Select(f => new
                {
                    issue = f.Issue.ToString(),
                    action = f.Action.ToString(),
                    f.Selected,
                    f.Path,
                    f.Target,
                    f.Reason,
                    f.Status,
                    f.Error,
                }),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            return path;
        }
        catch (Exception ex)
        {
            _host.ShowStatus($"Журнал чистки не сохранён: {ex.Message}");
            return path;
        }
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null)
        {
            AppMessageBox.Show("Сначала проверьте диск.", "Чистка диска", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            FileName = $"cleanup_{DateTime.Now:yyyyMMdd_HHmm}.txt",
            Filter = "Текстовый файл (*.txt)|*.txt",
        };
        if (dlg.ShowDialog() != true) return;

        var lines = _plan.Findings.Select(f =>
            $"{f.IssueLabel}\t{DiskCleanupScanner.Finding.ActionLabel(f.Action)}\t{f.Path}\t{f.Target}\t{f.Reason}\t{f.Status}" +
            (f.Error.Length > 0 ? " — " + f.Error : ""));
        var text = string.Join(Environment.NewLine, lines);
        if (_plan.Skipped.Count > 0)
            text += Environment.NewLine + Environment.NewLine + "Пропущено:" + Environment.NewLine +
                    string.Join(Environment.NewLine, _plan.Skipped);
        try
        {
            File.WriteAllText(dlg.FileName, text);
            _host.ShowStatus($"Отчёт сохранён: {dlg.FileName}");
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось сохранить: {ex.Message}", "Чистка диска",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
