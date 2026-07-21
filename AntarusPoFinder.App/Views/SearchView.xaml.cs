using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public partial class SearchView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private Dictionary<int, EquipmentSubType> _subtypesById = new();

    private static readonly string[] KincoPlcExts = { ".kpr", ".kpj", ".kpro", ".cpj", ".prj" };
    private static readonly string[] KincoHmiExts = { ".dpj", ".emt", ".emtp", ".emsln" };

    /// <summary>Exposes specific named controls to OnboardingOverlay (MainWindow.ShowOnboarding) —
    /// x:Name fields are private to this partial class by default, so the tour can't reach them
    /// directly. Returns null for an unknown key rather than throwing, so a tour step silently
    /// skips instead of crashing if this ever falls out of sync with the tour's step list.</summary>
    public FrameworkElement? OnboardingTarget(string key) => key switch
    {
        "input" => SearchInput,
        "mode" => ModeSelectorPanel,
        _ => null,
    };

    public SearchView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
    }

    // ── Search ────────────────────────────────────────────────────────────

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PerformSearch();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => PerformSearch();

    /// <summary>Re-runs the last query so results (rollback status, tags, etc.) don't go stale —
    /// the page instance is cached across navigation, so switching away and back would otherwise
    /// keep showing whatever was on screen before other tabs changed the data.</summary>
    public void RefreshIfActive()
    {
        if (!string.IsNullOrWhiteSpace(SearchInput.Text))
            PerformSearch();
    }

    private void ResetSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchInput.Text = "";
        ResultsPanel.Children.Clear();
        StatusLabel.Text = "";
        EmptyLabel.Text = "Введите запрос и нажмите «Найти»";
        EmptyLabel.Visibility = Visibility.Visible;
        SearchInput.Focus();
    }

    /// <summary>Re-runs the current query in the new mode as soon as the user flips Прошивки/
    /// Параметры/Схемы or the exact-word checkbox — matches the immediate feedback of a live
    /// filter instead of requiring another click on «Найти».</summary>
    private void SearchMode_Changed(object sender, RoutedEventArgs e)
    {
        AnimateModeThumb();
        if (!string.IsNullOrWhiteSpace(SearchInput.Text)) PerformSearch();
    }

    /// <summary>Width of one segment in the three-way Прошивки/Параметры/Схемы slider — must match
    /// the Width set on each RadioButton and on ModeThumb in SearchView.xaml.</summary>
    private const double ModeSegmentWidth = 150;

    /// <summary>Glides ModeThumb under whichever segment is now checked instead of each segment
    /// flipping its own background — a real sliding toggle, not three independently-styled pills.
    /// Guarded with null-conditionals: FwModeRadio's IsChecked="True" in XAML fires its Checked
    /// event the moment InitializeComponent parses that element, which is BEFORE ParamsModeRadio/
    /// SchemasModeRadio (declared later in the same XAML) get their x:Name fields connected —
    /// reading them unconditionally here crashed the app on every startup.</summary>
    private void AnimateModeThumb()
    {
        if (ModeThumbTransform is null) return;
        var index = SchemasModeRadio?.IsChecked == true ? 2 : ParamsModeRadio?.IsChecked == true ? 1 : 0;
        ModeThumbTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(index * ModeSegmentWidth, TimeSpan.FromSeconds(0.15)));
    }

    private void PerformSearch()
    {
        var query = SearchInput.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        StatusLabel.Text = "Поиск…";
        ResultsPanel.Children.Clear();
        EmptyLabel.Visibility = Visibility.Collapsed;

        if (SchemasModeRadio.IsChecked == true)
            PerformSchemasSearch(query);
        else if (ParamsModeRadio.IsChecked == true)
            PerformParamsSearch(query);
        else
            PerformFirmwareSearch(query);
    }

    private void PerformFirmwareSearch(string query)
    {
        var exact = ExactWordCheck.IsChecked == true;
        var results = SearchService.Search(_services.Db, query, exact);
        if (results.Count == 0)
        {
            StatusLabel.Text = $"По запросу «{query}» ничего не найдено";
            EmptyLabel.Text = "Ничего не найдено — попробуйте другой запрос или снимите «Точное совпадение слова»";
            EmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        StatusLabel.Text = $"Найдено: {results.Count}";
        _subtypesById = _services.Db.GetAllEquipmentSubtypes().Where(s => s.Id is not null).ToDictionary(s => s.Id!.Value);
        var canEditTags = _services.Cfg.CurrentRole() is "administrator";

        foreach (var result in results)
        {
            var hasLocal = HasLocal(result);
            var hasAnyLocal = HasAnyLocal(result);
            var subtypeName = _subtypesById.TryGetValue(result.SubtypeId, out var sub) ? sub.Name : "";
            var hasParams = subtypeName != "ПП" && _services.Db.GetParamFiles(subtypeId: result.SubtypeId).Count > 0;
            var hasHmi = FindFiltered(result, KincoHmiExts) is not null;
            var hasMap = string.IsNullOrEmpty(result.IoMapPath) && FindSiblingFolder(result, "Карта ВВ") is not null;

            var card = new FirmwareCard();
            card.Configure(result, hasLocal, hasAnyLocal, hasParams, hasHmi, hasMap, canEditTags);
            card.OpenRequested += (s, _) => OpenFirmware(((FirmwareCard)s!).Result);
            card.OpenFolderRequested += (s, _) => OpenFirmwareFolder(((FirmwareCard)s!).Result);
            card.OpenPlcRequested += (s, _) => OpenFiltered(((FirmwareCard)s!).Result, KincoPlcExts, "ПЛК");
            card.OpenHmiRequested += (s, _) => OpenFiltered(((FirmwareCard)s!).Result, KincoHmiExts, "HMI");
            card.DownloadRequested += (s, _) => DownloadFirmware(((FirmwareCard)s!).Result);
            card.MapRequested += (s, _) => OpenMap(((FirmwareCard)s!).Result);
            card.ParamsRequested += (s, _) => OpenParams(((FirmwareCard)s!).Result);
            card.InstructionsRequested += (s, _) => OpenInstructions(((FirmwareCard)s!).Result);
            card.HistoryRequested += (s, _) => ShowHistory(((FirmwareCard)s!).Result);
            card.CopyNameRequested += (s, _) => CopyName(((FirmwareCard)s!).Result);
            card.TagsEditRequested += (s, _) => EditTags(((FirmwareCard)s!).Result);
            ResultsPanel.Children.Add(card);
        }
    }

    private void PerformParamsSearch(string query)
    {
        var exact = ExactWordCheck.IsChecked == true;
        var files = SearchService.SearchWithLayoutFallback(query, exact, (q, ex) =>
        {
            var tokens = SearchService.Normalize(q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return _services.Db.SearchParamFilesByTokens(tokens, ex);
        });
        if (files.Count == 0)
        {
            StatusLabel.Text = $"По запросу «{query}» ничего не найдено";
            EmptyLabel.Text = "Ничего не найдено — попробуйте другой запрос или снимите «Точное совпадение слова»";
            EmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        StatusLabel.Text = $"Найдено: {files.Count}";
        var canEditTags = _services.Cfg.CurrentRole() is "administrator";
        foreach (var file in files)
            ResultsPanel.Children.Add(MakeParamFileCard(file, canEditTags));
    }

    private Border MakeParamFileCard(ParamFile file, bool canEditTags)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{file.Filename} [{file.Manufacturer}]",
            Style = (Style)FindResource("SubtitleText"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(" / ", new[] { file.GroupName, file.SubtypeName }.Where(s => !string.IsNullOrEmpty(s))),
            Style = (Style)FindResource("MutedText"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        if (!string.IsNullOrEmpty(file.Description))
            panel.Children.Add(new TextBlock { Text = file.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

        var tags = file.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tags.Length > 0)
        {
            var tagsView = new TagBubbleEditor { Margin = new Thickness(0, 4, 0, 0) };
            tagsView.Configure(tags, null, readOnly: true);
            panel.Children.Add(tagsView);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var openBtn = new Button { Content = "Открыть", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        openBtn.Click += (_, _) => OpenParamFile(file);
        var openFolderBtn = new Button { Content = "Открыть папку с файлом", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        openFolderBtn.Click += (_, _) => OpenParamFileFolder(file);
        actions.Children.Add(openBtn);
        actions.Children.Add(openFolderBtn);
        if (canEditTags)
        {
            var tagsBtn = new Button { Content = "Теги", Style = (Style)FindResource("SecondaryButton") };
            tagsBtn.Click += (_, _) => EditParamTags(file);
            actions.Children.Add(tagsBtn);
        }
        panel.Children.Add(actions);

        return new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 10), Child = panel };
    }

    private void PerformSchemasSearch(string query)
    {
        var diskPath = _services.Cfg.SecondDiskPath();
        if (string.IsNullOrEmpty(diskPath))
        {
            StatusLabel.Text = "Путь ко второму диску не задан";
            EmptyLabel.Text = "Второй диск не настроен — укажите его в разделе «Настройки»";
            EmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        var exact = ExactWordCheck.IsChecked == true;
        var hits = _services.Schematics.Matches(diskPath, query, exact);
        if (hits.Count == 0)
        {
            StatusLabel.Text = $"По запросу «{query}» ничего не найдено";
            EmptyLabel.Text = "Схема не найдена — проверьте название шкафа или второй диск";
            EmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        StatusLabel.Text = $"Найдено: {hits.Count}";
        foreach (var hit in hits)
            ResultsPanel.Children.Add(MakeSchematicCard(hit));
    }

    private Border MakeSchematicCard(SchematicHit hit)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = hit.CabinetName,
            Style = (Style)FindResource("SubtitleText"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = hit.Path,
            Style = (Style)FindResource("MutedText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var openBtn = new Button { Content = "Открыть", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        openBtn.Click += (_, _) => OpenSchematic(hit);
        actions.Children.Add(openBtn);
        panel.Children.Add(actions);

        return new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 10), Child = panel };
    }

    private void OpenSchematic(SchematicHit hit)
    {
        if (!File.Exists(hit.Path))
        {
            AppMessageBox.Show($"Файл схемы не найден:\n{hit.Path}", "Схема", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(hit.Path);
    }

    private void EditParamTags(ParamFile file)
    {
        var title = $"{file.Filename} [{file.Manufacturer}]";
        var dlg = new EditParamTagsDialog(_services.Db, file, title) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _services.Db.UpdateParamFileTags(file.Id!.Value, dlg.ResultTags);
        _host.ShowStatus($"Теги обновлены: {file.Filename}", category: NotificationCategory.FirmwareAndParams);
        PerformSearch();
    }

    private static void OpenParamFile(ParamFile file)
    {
        var full = Path.Combine(file.DiskPath, file.Filename);
        if (File.Exists(full)) TryOpen(full);
        else if (Directory.Exists(file.DiskPath)) TryOpen(file.DiskPath);
        else AppMessageBox.Show($"Файл не найден:\n{full}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OpenParamFileFolder(ParamFile file)
    {
        if (Directory.Exists(file.DiskPath)) TryOpen(file.DiskPath);
        else AppMessageBox.Show($"Папка не найдена:\n{file.DiskPath}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void EditTags(HierarchyResult result)
    {
        var v = _services.Db.GetFwVersionById(result.FwVersionId);
        if (v is null)
        {
            AppMessageBox.Show("Версия не найдена в базе.", "Теги", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new EditFirmwareDialog(_services.Db, v, $"{result.Name} {result.VersionRaw}") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _services.Db.UpdateFwVersion(v.Id!.Value, dlg.ResultDescription, dlg.ResultTags, dlg.ResultLaunchTypes);
        _host.ShowStatus($"Теги обновлены: {result.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        PerformSearch();
    }

    // ── Local cache helpers ───────────────────────────────────────────────

    private static string SanitizeName(string name) => LocalFirmwareCache.SanitizeName(name);

    private static bool HasLocal(HierarchyResult result) => LocalFirmwareCache.HasVersion(result.Name, result.VersionRaw);

    private static bool HasAnyLocal(HierarchyResult result) => LocalFirmwareCache.HasAny(result.Name);

    private static string? FindUsableFile(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).FirstOrDefault(f =>
                Path.GetExtension(f).ToLowerInvariant() is var ext && ext != ".md" && ext != ".txt" && ext != ".log")
            : null;

    private static string? FindFilteredIn(string dir, string[] exts) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            : null;

    private static string? FindFiltered(HierarchyResult result, string[] exts)
    {
        var localDir = Path.Combine(ConfigService.LocalFw, SanitizeName(result.Name));
        return FindFilteredIn(localDir, exts) ?? FindFilteredIn(result.FirmwareDir, exts);
    }

    private static string? FindSiblingFolder(HierarchyResult result, string folderName)
    {
        if (!Directory.Exists(result.FirmwareDir)) return null;
        var ctrlDir = Directory.GetParent(result.FirmwareDir)?.FullName;
        if (ctrlDir is null) return null;
        var candidate = Path.Combine(ctrlDir, folderName);
        return Directory.Exists(candidate) ? candidate : null;
    }

    // ── Card actions ──────────────────────────────────────────────────────

    private static string? ResolveOpenTarget(HierarchyResult result)
    {
        var localDir = SanitizeName(result.Name);
        string? target = FindUsableFile(Path.Combine(ConfigService.LocalFw, localDir, result.VersionRaw));

        if (target is null)
        {
            var baseDir = Path.Combine(ConfigService.LocalFw, localDir);
            if (Directory.Exists(baseDir))
            {
                foreach (var sub in Directory.EnumerateDirectories(baseDir).OrderByDescending(d => d))
                {
                    target = FindUsableFile(sub);
                    if (target is not null) break;
                }
            }
        }

        if (target is null && Directory.Exists(result.FirmwareDir))
            target = FindUsableFile(result.FirmwareDir) ?? result.FirmwareDir;

        return target;
    }

    private void OpenFirmware(HierarchyResult result)
    {
        var target = ResolveOpenTarget(result);
        if (target is null)
        {
            AppMessageBox.Show("Прошивка не найдена локально.\nНажмите «Скачать» для копирования с сервера.", "Открыть",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private void OpenFirmwareFolder(HierarchyResult result)
    {
        var target = ResolveOpenTarget(result);
        if (target is null)
        {
            AppMessageBox.Show("Прошивка не найдена локально.\nНажмите «Скачать» для копирования с сервера.", "Открыть папку",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Directory.Exists(target))
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
    }

    private void OpenFiltered(HierarchyResult result, string[] exts, string label)
    {
        var target = FindFiltered(result, exts);
        if (target is null)
        {
            AppMessageBox.Show("Прошивка не найдена локально.\nНажмите «Скачать» для копирования с сервера.", $"Открыть {label}",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private void DownloadFirmware(HierarchyResult result)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(result.FirmwareDir))
        {
            AppMessageBox.Show("Путь к диску или папка прошивки не заданы.", "Скачать", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Directory.Exists(result.FirmwareDir))
        {
            AppMessageBox.Show($"Папка не найдена на диске:\n{result.FirmwareDir}", "Скачать", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string dst;
        try
        {
            dst = FirmwareSync.CopyToLocal(result);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(ex.Message, "Скачать", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _host.ShowStatus($"Скопировано: {result.Name}");

        var dlg = new SyncResultDialog(result, dst) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();

        PerformSearch();
    }

    private void OpenMap(HierarchyResult result)
    {
        var path = result.IoMapPath;
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
            path = FindSiblingFolder(result, "Карта ВВ") ?? path;

        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
        {
            AppMessageBox.Show($"Файл карты не найден.\nПуть: {result.IoMapPath}", "Карта in/out", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(path);
    }

    private void OpenParams(HierarchyResult result)
    {
        var files = _services.Db.GetParamFiles(subtypeId: result.SubtypeId);
        if (files.Count == 0)
        {
            AppMessageBox.Show("Параметры для этого типа шкафа не найдены.\nЗагрузите параметры в разделе «Параметры».",
                "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new CardParamsDialog(files, _services.Cfg) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private void OpenInstructions(HierarchyResult result)
    {
        var path = result.InstructionsPath;
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
            path = FindSiblingFolder(result, "Инструкция") ?? path;

        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
        {
            AppMessageBox.Show($"Файл инструкций не найден.\nПуть: {result.InstructionsPath}", "Инструкции", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(path);
    }

    private void ShowHistory(HierarchyResult result)
    {
        var versions = _services.Db.GetFwVersionsHistory(result.SubtypeId, result.ControllerId);
        var dlg = new HistoryDialog(result.Name, versions) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    /// <summary>Copies just the numeric version stem — since Round 31 (see FirmwareNaming.
    /// BuildFirmwareFilename) that IS the on-disk firmware filename; the group/subtype/controller
    /// prefix this used to also copy (e.g. "НГР-КПЧ_SMH5_2.1.042...") was the OLD naming convention,
    /// dropped when the filename was simplified — copying it here was stale and no longer matched
    /// what's actually on disk. ToUpperInvariant matches BuildFirmwareFilename's own casing (moot in
    /// practice since VersionRaw is purely digits/dots/underscore, but kept for consistency/safety).</summary>
    private void CopyName(HierarchyResult result)
    {
        var text = result.VersionRaw.ToUpperInvariant();
        Clipboard.SetText(text);
        _host.ShowStatus($"Скопировано: {text}");
    }

    private static void TryOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            var reply = AppMessageBox.Show(
                $"Не удалось открыть файл:\n{path}\n\nВозможно, не установлена программа для этого типа файлов.\n\nОткрыть папку с файлом?",
                "Открыть файл", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (reply != MessageBoxResult.Yes) return;
            try
            {
                var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (folder is not null) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch { /* give up gracefully, matches Python's swallowed OSError here */ }
        }
    }
}
