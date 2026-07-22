using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public partial class UploadView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;

    private List<EquipmentGroup> _groups = new();
    private List<ControllerModification> _mods = new();
    private string? _srcPath;
    private int? _autoDetectedModId;
    private string? _executableHint;
    private string? _hmiPath;
    private string? _hmiExecutableHint;

    /// <summary>Recognized firmware-project extensions per field — used only to decide whether a
    /// picked FOLDER needs the operator to disambiguate which file inside is the executable (see
    /// PromptExecutableHint). Single-file picks never need this: the file itself is unambiguous.</summary>
    private static readonly string[] MainExecutableExts = { ".psl", ".lfs", ".kpr", ".kpj", ".dpj" };
    private static readonly string[] HmiExecutableExts = { ".fsprj" };
    private readonly Dictionary<string, CheckBox> _launchChecks = new();

    private record SubtypeOption(string Label, EquipmentSubType Subtype);
    private record ReservationOption(string Label, FwVersionReservation? Reservation);

    /// <summary>See SearchView.OnboardingTarget for why this exists — same reasoning.</summary>
    public FrameworkElement? OnboardingTarget(string key) => key switch
    {
        "dropzone" => DropZone,
        "opc" => VersionOptionsPanel,
        "reserve" => ReserveVersionBtn,
        _ => null,
    };

    public UploadView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;

        foreach (var lt in ConfigService.LaunchTypes)
        {
            var cb = new CheckBox { Content = lt, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            _launchChecks[lt] = cb;
            LaunchTypesPanel.Children.Add(cb);
        }

        Loaded += (_, _) => ReloadCombos();
        TagsEditor.Configure(System.Array.Empty<string>(), () => _services.Db.GetAllTags());
    }

    // ── Combo population / reload ────────────────────────────────────────────

    private void ReloadCombos()
    {
        var prevGroupId = (GroupCombo.SelectedItem as EquipmentGroup)?.Id;
        var prevModId = (CtrlCombo.SelectedItem as ControllerModification)?.Id;

        // No previous selection (first load of the page) leaves the combo EMPTY (-1), not auto-
        // picked to the first item — autodetect may fail to match a file (wrong/unknown model), and
        // a silently pre-selected group/controller meant it was easy to upload under the wrong one
        // without noticing. The user must pick explicitly.
        _groups = _services.Db.GetAllEquipmentGroups();
        GroupCombo.ItemsSource = _groups;
        GroupCombo.SelectedIndex = prevGroupId is not null
            ? Math.Max(0, _groups.FindIndex(g => g.Id == prevGroupId))
            : -1;

        _mods = _services.Db.GetAllModifications();
        CtrlCombo.ItemsSource = _mods;
        CtrlCombo.SelectedIndex = prevModId is not null
            ? Math.Max(0, _mods.FindIndex(m => m.Id == prevModId))
            : -1;

        PopulateSubtypes();
        OnCtrlChanged();
    }

    private void PopulateSubtypes()
    {
        var prevSubId = (SubCombo.SelectedItem as SubtypeOption)?.Subtype.Id;
        if (GroupCombo.SelectedItem is not EquipmentGroup group)
        {
            SubCombo.ItemsSource = null;
            UpdatePreview();
            return;
        }

        var subtypes = _services.Db.GetSubtypesForGroup(group.Id!.Value);
        var options = subtypes
            .Select(s => new SubtypeOption(s.Name == "—" ? s.FolderName : $"{s.FolderName} ({s.Name})", s))
            .ToList();
        SubCombo.ItemsSource = options;
        SubCombo.SelectedIndex = prevSubId is not null
            ? Math.Max(0, options.FindIndex(o => o.Subtype.Id == prevSubId))
            : -1;

        RefreshReservationPicker();
        UpdatePreview();
    }

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateSubtypes();
    private void SubCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { RefreshReservationPicker(); UpdatePreview(); }
    private void CtrlCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnCtrlChanged();

    private void OnCtrlChanged()
    {
        if (CtrlCombo.SelectedItem is ControllerModification mod)
        {
            var parts = new List<string>();
            if (mod.Id == _autoDetectedModId) parts.Add("🔍 автоопределено из файла");
            if (!string.IsNullOrEmpty(mod.Description)) parts.Add(mod.Description);
            CtrlDescLabel.Text = string.Join("  —  ", parts);
        }
        else
        {
            CtrlDescLabel.Text = "";
        }

        RefreshReservationPicker();
        UpdatePreview();
        OfferCarryOver();
    }

    // ── Version reservations ─────────────────────────────────────────────────

    /// <summary>Reloads the "use a reservation" picker for the currently selected subtype/controller
    /// combo. Called whenever Group/Subtype/Controller changes — NOT from UpdatePreview itself, to
    /// avoid a feedback loop (this sets ReservationCombo.SelectedIndex, which fires SelectionChanged,
    /// which calls UpdatePreview — but UpdatePreview must never call back into this method).</summary>
    private void RefreshReservationPicker()
    {
        if (SubCombo.SelectedItem is not SubtypeOption subOption || CtrlCombo.SelectedItem is not ControllerModification mod)
        {
            ReservationPanel.Visibility = Visibility.Collapsed;
            ReservationCombo.ItemsSource = null;
            return;
        }

        var reservations = _services.Db.GetActiveReservations(subOption.Subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
        if (reservations.Count == 0)
        {
            ReservationPanel.Visibility = Visibility.Collapsed;
            ReservationCombo.ItemsSource = null;
            return;
        }

        var options = new List<ReservationOption> { new("Сгенерировать новый номер", null) };
        options.AddRange(reservations.Select(r => new ReservationOption($"{r.VersionRaw}  —  {r.ReservedAt}  —  {r.ReservedBy}", r)));
        ReservationCombo.ItemsSource = options;
        ReservationCombo.SelectedIndex = 0;
        ReservationPanel.Visibility = Visibility.Visible;
    }

    private void ReservationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private FwVersionReservation? GetSelectedReservation() => (ReservationCombo.SelectedItem as ReservationOption)?.Reservation;

    private void ReserveVersion_Click(object sender, RoutedEventArgs e)
    {
        if (GroupCombo.SelectedItem is not EquipmentGroup group ||
            SubCombo.SelectedItem is not SubtypeOption subOption ||
            CtrlCombo.SelectedItem is not ControllerModification mod)
        {
            AppMessageBox.Show("Укажите тип шкафа, подтип и контроллер.", "Резерв номера", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var defaultTtl = _services.Cfg.ReservationTtlHours();
        var ttlPrompt = defaultTtl == 0
            ? "Срок резерва в часах (пусто — без ограничения):"
            : $"Срок резерва в часах (пусто — по умолчанию {defaultTtl} ч, 0 — без ограничения):";
        var ttlStr = TextPromptDialog.Prompt(Window.GetWindow(this), "Зарезервировать номер версии", ttlPrompt);
        if (ttlStr is null) return; // user cancelled the dialog outright — abort the reservation too
        int? ttlHours = defaultTtl == 0 ? null : defaultTtl;
        if (!string.IsNullOrWhiteSpace(ttlStr))
        {
            if (!int.TryParse(ttlStr.Trim(), out var customTtl) || customTtl < 0)
            {
                AppMessageBox.Show("Введите целое число часов.", "Резерв номера", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ttlHours = customTtl == 0 ? null : customTtl;
        }

        var reservation = _services.Db.ReserveNextVersion(
            subOption.Subtype.Id!.Value, mod.ControllerId, mod.HwVersion,
            group.Prefix, subOption.Subtype.Prefix, _services.CurrentUserName, IncludeDateCheck.IsChecked == true, ttlHours);
        var fwv = FwVersionNumber.Parse(reservation.VersionRaw)!;

        var subDisplay = subOption.Subtype.Name == "—" ? "" : subOption.Subtype.Name;
        var groupSub = string.Join("-", new[] { group.Name, subDisplay }.Where(p => !string.IsNullOrEmpty(p)));
        var label = string.Join("_", new[] { groupSub, mod.ControllerName, fwv.Raw }.Where(p => !string.IsNullOrEmpty(p)));

        var dlg = new ReserveVersionDialog(label) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();

        RefreshReservationPicker();
        _host.ShowStatus($"Зарезервирован номер: {fwv.Raw}", category: NotificationCategory.FirmwareAndParams);
    }

    /// <summary>Заявка и SN независимы — наладчик/программист включает и заполняет только то, что
    /// у него есть для конкретного нестандартного шкафа. Папка "ОПЦ" на диске (см. IsOpc) при этом
    /// используется, если включён хотя бы один из двух.</summary>
    private void OpcReqNum_Toggled(object sender, RoutedEventArgs e)
    {
        bool isChecked = OpcReqNumCheck.IsChecked == true;
        ReqNumLabel.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        ReqNumInput.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private void OpcSn_Toggled(object sender, RoutedEventArgs e)
    {
        bool isChecked = OpcSnCheck.IsChecked == true;
        CabinetSnLabel.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        CabinetSnInput.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private bool IsOpc => OpcReqNumCheck.IsChecked == true || OpcSnCheck.IsChecked == true;

    /// <summary>Формат для имени файла — "(01312)"/"SN00042": число дополняется нулями до 5 цифр.
    /// Не блокирует нечисловой ввод (возвращает как есть) — это косметика имени файла, а не строгая
    /// валидация поля.</summary>
    private static string Format5Digits(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return "";
        return int.TryParse(trimmed, out var n) && n is >= 0 and <= 99999 ? n.ToString("D5") : trimmed;
    }

    private void ReqNum_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void CabinetSn_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void IncludeDate_Toggled(object sender, RoutedEventArgs e) => UpdatePreview();

    // ── Carry-over prompt ─────────────────────────────────────────────────────

    private void OfferCarryOver()
    {
        if (!string.IsNullOrEmpty(IoMapInput.Text) || !string.IsNullOrEmpty(InstructionsInput.Text) || !string.IsNullOrEmpty(ModbusMapInput.Text)) return;
        if (SubCombo.SelectedItem is not SubtypeOption subOption) return;
        if (CtrlCombo.SelectedItem is not ControllerModification mod) return;

        var prev = _services.Db.GetLastActiveFwVersion(subOption.Subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
        if (prev is null) return;
        if (string.IsNullOrEmpty(prev.IoMapPath) && string.IsNullOrEmpty(prev.InstructionsPath) && string.IsNullOrEmpty(prev.ModbusMapPath)) return;

        var result = AppMessageBox.Show(
            $"Предыдущая версия: {prev.VersionRaw}\nПеренести Карту in/out, Карту modbus и Инструкцию из неё?",
            "Перенести файлы", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (result == MessageBoxResult.Yes)
        {
            IoMapInput.Text = prev.IoMapPath;
            InstructionsInput.Text = prev.InstructionsPath;
            ModbusMapInput.Text = prev.ModbusMapPath;
        }
    }

    // ── Path preview ──────────────────────────────────────────────────────────

    private void UpdatePreview()
    {
        if (GroupCombo.SelectedItem is not EquipmentGroup group ||
            SubCombo.SelectedItem is not SubtypeOption subOption ||
            CtrlCombo.SelectedItem is not ControllerModification mod)
        {
            PreviewLabel.Text = "—";
            return;
        }

        int hwInt = mod.HwVersion;
        var reservation = GetSelectedReservation();
        var fwv = reservation is not null ? FwVersionNumber.Parse(reservation.VersionRaw) : null;
        if (fwv is null)
        {
            int swInt = _services.Db.GetNextSwVersion(subOption.Subtype.Id!.Value, mod.ControllerId, hwInt);
            fwv = FwVersionNumber.Build(group.Prefix, subOption.Subtype.Prefix, hwInt, swInt, includeDate: IncludeDateCheck.IsChecked == true);
        }

        bool isOpc = IsOpc;
        string ctrlFolder = isOpc ? "ОПЦ" : mod.ControllerName;
        string ext = string.IsNullOrEmpty(_srcPath) || Directory.Exists(_srcPath) ? ".psl" : Path.GetExtension(_srcPath);

        var subDisplay = subOption.Subtype.Name == "—" ? "" : subOption.Subtype.Name;
        var pathStr = string.Join(" / ", new[] { "ПО", group.Name, subDisplay, ctrlFolder, fwv.Raw }.Where(p => !string.IsNullOrEmpty(p)));

        string reqNum = OpcReqNumCheck.IsChecked == true ? Format5Digits(ReqNumInput.Text) : "";
        string cabinetSn = OpcSnCheck.IsChecked == true ? Format5Digits(CabinetSnInput.Text) : "";
        string filename = FirmwareNaming.BuildFirmwareFilename(fwv, ext, reqNum, cabinetSn);

        PreviewLabel.Text = $"{pathStr}\n{filename}";
    }

    // ── File selection ────────────────────────────────────────────────────────

    private void DropZone_Click(object sender, MouseButtonEventArgs e) => BrowseFile_Click(sender, e);

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать файл прошивки",
            Filter = "Файлы прошивок (*.psl;*.lfs;*.kpr;*.kpj;*.dpj)|*.psl;*.lfs;*.kpr;*.kpj;*.dpj|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            OnFileDropped(dlg.FileName);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Выбрать папку прошивки" };
        if (dlg.ShowDialog() == true)
            OnFileDropped(dlg.FolderName);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            OnFileDropped(paths[0]);
    }

    private void OnFileDropped(string path)
    {
        _srcPath = path;
        IoMapInput.Text = "";
        InstructionsInput.Text = "";
        _executableHint = null;
        StatusLabel.Text = $"Файл: {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}";
        _autoDetectedModId = null;
        DropZoneLabel.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

        if (!Directory.Exists(path))
        {
            var ext = Path.GetExtension(path);
            if (string.Equals(ext, ".psl", StringComparison.OrdinalIgnoreCase))
                TryPslAutodetect(path);
            else if (KincoProjectExtensions.Contains(ext))
                TryKincoAutodetect();
        }
        else
        {
            _executableHint = PromptExecutableHint(path, MainExecutableExts, "прошивки");
            if (!string.IsNullOrEmpty(_executableHint))
                StatusLabel.Text += $"  —  исполняемый: {_executableHint}";
        }

        UpdatePreview();
    }

    /// <summary>When a picked FOLDER has no file matching a known extension, content-based/extension
    /// autodetect (TryPslAutodetect/TryKincoAutodetect above) has nothing to go on — this is the
    /// fallback: ask the operator directly which file in the folder is the one to run. Purely a
    /// display hint (FwVersionRecord.ExecutableHint/HmiExecutableHint); the whole folder is always
    /// copied regardless (support files/drivers alongside the executable are often required).
    /// Silent when exactly one candidate matches a known extension — only ambiguous/empty cases
    /// interrupt the operator.</summary>
    private string? PromptExecutableHint(string folderPath, string[] knownExts, string contextLabel)
    {
        List<string> topLevelFiles;
        try { topLevelFiles = Directory.EnumerateFiles(folderPath).Select(Path.GetFileName).ToList()!; }
        catch { return null; }
        if (topLevelFiles.Count == 0) return null;

        var matches = topLevelFiles.Where(f => knownExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        return PickFileDialog.Pick(Window.GetWindow(this), "Выбрать исполняемый файл",
            $"В папке «{folderName}» не найден файл со стандартным расширением для {contextLabel}.\n" +
            "Укажите, какой файл в папке является исполняемым — сама папка будет скопирована целиком в любом случае:",
            topLevelFiles);
    }

    // ── PSL / KINCO auto-detection ────────────────────────────────────────────

    private static readonly HashSet<string> KincoProjectExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".dpj", ".kpr", ".kpj" };

    /// <summary>Unlike PSL (which has an inspectable internal format — see PslInspector), .dpj/
    /// .kpr/.kpj don't have a documented structure to parse a model out of, and KINCO has exactly
    /// one modification in the hierarchy (see HierarchyDefaultsData) — so just recognizing the
    /// extension is already an unambiguous match, no file content needs reading.</summary>
    private void TryKincoAutodetect()
    {
        var match = _mods.FirstOrDefault(m => string.Equals(m.ControllerName, "KINCO", StringComparison.OrdinalIgnoreCase));
        if (match is null) return;
        ApplyAutoDetectedModification(match);
    }

    private void TryPslAutodetect(string path)
    {
        PslInfo info;
        try { info = PslInspector.Inspect(path); }
        catch { return; }

        var deviceKey = info.Plc.DeviceKey;
        if (string.IsNullOrEmpty(deviceKey)) return;

        var match = FindModificationByPslKey(info.Plc.Model, info.Plc.Modification);
        if (match is null)
        {
            _host.ShowStatus($"Обнаружен контроллер «{deviceKey}» — такой модификации нет в справочнике. Выберите вручную или добавьте её в Настройки → Иерархия.", category: NotificationCategory.Hierarchy);
            return;
        }

        ApplyAutoDetectedModification(match);
    }

    private void ApplyAutoDetectedModification(ControllerModification match)
    {
        _autoDetectedModId = match.Id;
        var idx = _mods.FindIndex(m => m.Id == match.Id);
        if (idx < 0) return;
        if (CtrlCombo.SelectedIndex == idx) OnCtrlChanged();
        else CtrlCombo.SelectedIndex = idx;
    }

    private ControllerModification? FindModificationByPslKey(string model, string modification)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var expected = string.IsNullOrEmpty(modification) ? model : $"{model}-{modification}";
        return _mods.FirstOrDefault(m =>
            string.Equals(m.ControllerName, model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.DisplayName, expected, StringComparison.OrdinalIgnoreCase));
    }

    // ── Attachment fields ─────────────────────────────────────────────────────

    private void IoMapBrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog();
        if (dlg.ShowDialog() == true) IoMapInput.Text = dlg.FileName;
    }

    private void IoMapBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() == true) IoMapInput.Text = dlg.FolderName;
    }

    private void IoMapClear_Click(object sender, RoutedEventArgs e) => IoMapInput.Text = "";

    private void InstructionsBrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog();
        if (dlg.ShowDialog() == true) InstructionsInput.Text = dlg.FileName;
    }

    private void InstructionsBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() == true) InstructionsInput.Text = dlg.FolderName;
    }

    private void InstructionsClear_Click(object sender, RoutedEventArgs e) => InstructionsInput.Text = "";

    private void ModbusMapBrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog();
        if (dlg.ShowDialog() == true) ModbusMapInput.Text = dlg.FileName;
    }

    private void ModbusMapBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() == true) ModbusMapInput.Text = dlg.FolderName;
    }

    private void ModbusMapClear_Click(object sender, RoutedEventArgs e) => ModbusMapInput.Text = "";

    private const string HmiPathPlaceholder = "Перетащите файл или папку HMI-проекта сюда\nили нажмите для выбора";

    private void HmiCheck_Toggled(object sender, RoutedEventArgs e)
    {
        bool isChecked = HmiCheck.IsChecked == true;
        HmiSection.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        if (!isChecked)
        {
            _hmiPath = null;
            _hmiExecutableHint = null;
            HmiPathLabel.Text = HmiPathPlaceholder;
        }
    }

    /// <summary>Clicking the drop zone itself (outside a drag&amp;drop) is a shortcut for "Файл..." —
    /// same click-to-browse behavior as the main firmware DropZone above.</summary>
    private void HmiDropZone_Click(object sender, MouseButtonEventArgs e) => HmiBrowseFile_Click(sender, e);

    private void HmiDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void HmiDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            OnHmiPathPicked(paths[0]);
    }

    private void HmiBrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать файл HMI-проекта",
            Filter = "HMI-проект (*.fsprj)|*.fsprj|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        OnHmiPathPicked(dlg.FileName);
    }

    private void HmiBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Выбрать папку HMI-проекта" };
        if (dlg.ShowDialog() != true) return;
        OnHmiPathPicked(dlg.FolderName);
    }

    private void HmiClear_Click(object sender, RoutedEventArgs e)
    {
        _hmiPath = null;
        _hmiExecutableHint = null;
        HmiPathLabel.Text = HmiPathPlaceholder;
    }

    private void OnHmiPathPicked(string path)
    {
        _hmiPath = path;
        _hmiExecutableHint = Directory.Exists(path) ? PromptExecutableHint(path, HmiExecutableExts, "HMI-проекта") : null;
        HmiPathLabel.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
    }

    private void ClearData_Click(object sender, RoutedEventArgs e) => ResetForm();

    // ── Upload ────────────────────────────────────────────────────────────────

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_srcPath))
        {
            // HMI is versioned as an attachment to a firmware upload (see the "{fwv.Raw}_hmi" naming
            // below and 2f21975) — there is no standalone "just the HMI project" slot to drop it into,
            // so a user who filled in only the HMI drop zone and left the main one empty needs a
            // specific explanation, not the generic firmware-missing warning: they may not realize HMI
            // rides along with a PLC firmware version rather than replacing it. Filed after a real
            // report of "загрузил папку с HMI, а он просит .psl" — the operator had only used the HMI
            // zone and had no idea the main drop zone was still mandatory.
            var noFwMsg = HmiCheck.IsChecked == true && !string.IsNullOrEmpty(_hmiPath)
                ? "HMI-проект не загружается отдельно — он прикладывается к версии прошивки ПЛК.\n\n" +
                  "Выберите файл или папку прошивки ПЛК в верхней области (даже если в этой версии изменился только HMI) — HMI-проект уйдёт вместе с ней."
                : "Выберите файл прошивки.";
            AppMessageBox.Show(noFwMsg, "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (GroupCombo.SelectedItem is not EquipmentGroup group ||
            SubCombo.SelectedItem is not SubtypeOption subOption ||
            CtrlCombo.SelectedItem is not ControllerModification mod)
        {
            AppMessageBox.Show("Укажите тип шкафа, подтип и контроллер.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var launchTypes = _launchChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        if (launchTypes.Count == 0)
        {
            AppMessageBox.Show("Выберите хотя бы один тип пуска.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var desc = DescInput.Text.Trim();
        if (string.IsNullOrEmpty(desc))
        {
            AppMessageBox.Show("Укажите описание изменений в этой версии.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            DescInput.Focus();
            return;
        }

        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен. Проверьте настройки.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool isDir = Directory.Exists(_srcPath);
        if (!isDir)
        {
            var ext = Path.GetExtension(_srcPath).TrimStart('.').ToLowerInvariant();
            var allowed = new HashSet<string>(_services.Db.GetAllowedExtensions().Select(x => x.ToLowerInvariant()));
            if (allowed.Count > 0 && !allowed.Contains(ext))
            {
                var reply = AppMessageBox.Show(
                    $"Расширение «.{ext}» не входит в список разрешённых (Настройки → Иерархия → Разрешённые расширения).\nТочно загрузить этот файл?",
                    "Неизвестное расширение", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (reply != MessageBoxResult.Yes) return;
            }
        }

        bool isOpc = IsOpc;
        var reqNumRaw = OpcReqNumCheck.IsChecked == true ? ReqNumInput.Text.Trim() : "";
        var cabinetSnRaw = OpcSnCheck.IsChecked == true ? CabinetSnInput.Text.Trim() : "";
        var reqNum = Format5Digits(reqNumRaw);
        var cabinetSn = Format5Digits(cabinetSnRaw);
        int hwInt = mod.HwVersion;

        // If a reservation is picked, consume its EXACT locked-in number (Parse, never recompute) —
        // that's the whole point: the number inside the compiled firmware must match what gets saved.
        var reservation = GetSelectedReservation();
        FwVersionNumber fwv;
        int swInt;
        if (reservation is not null)
        {
            fwv = FwVersionNumber.Parse(reservation.VersionRaw)!;
            swInt = fwv.SwVersion;
        }
        else
        {
            swInt = _services.Db.GetNextSwVersion(subOption.Subtype.Id!.Value, mod.ControllerId, hwInt);
            fwv = FwVersionNumber.Build(group.Prefix, subOption.Subtype.Prefix, hwInt, swInt, includeDate: IncludeDateCheck.IsChecked == true);
        }

        var dstFolder = _services.Hierarchy.FwPath(root, group.Name, subOption.Subtype.Name, mod.ControllerName, fwv.Raw, isOpc);

        if (Directory.Exists(dstFolder))
        {
            var overwrite = AppMessageBox.Show(
                $"Папка {fwv.Raw} уже существует.\nПерезаписать?",
                "Версия существует", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (overwrite != MessageBoxResult.Yes) return;
        }

        string dstName;
        try
        {
            Directory.CreateDirectory(dstFolder);
            if (isDir)
            {
                CopyDirectoryContents(_srcPath, dstFolder);
                dstName = Path.GetFileName(_srcPath.TrimEnd(Path.DirectorySeparatorChar));
            }
            else
            {
                var ext = Path.GetExtension(_srcPath);
                dstName = FirmwareNaming.BuildFirmwareFilename(fwv, ext, reqNum, cabinetSn);
                File.Copy(_srcPath, Path.Combine(dstFolder, dstName), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(ex.Message, "Ошибка файла", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var user = _services.Db.GetOrCreateUser(_services.CurrentUserName, _services.CurrentUserName);

        var warnings = new List<string>();
        try { WriteChangelog(dstFolder, fwv, launchTypes, desc); }
        catch (Exception ex) { warnings.Add($"CHANGELOG.md: {ex.Message}"); }

        string ioMapStored = "";
        if (!string.IsNullOrEmpty(IoMapInput.Text))
        {
            try { ioMapStored = FileSystemHelpers.CopyFileOrFolderShallow(IoMapInput.Text, _services.Hierarchy.IoMapPath(root, group.Name, subOption.Subtype.Name, mod.ControllerName)); }
            catch (Exception ex) { warnings.Add($"Карта ВВ: {ex.Message}"); }
        }
        string instrStored = "";
        if (!string.IsNullOrEmpty(InstructionsInput.Text))
        {
            try { instrStored = FileSystemHelpers.CopyFileOrFolderShallow(InstructionsInput.Text, _services.Hierarchy.InstrPath(root, group.Name, subOption.Subtype.Name, mod.ControllerName)); }
            catch (Exception ex) { warnings.Add($"Инструкция: {ex.Message}"); }
        }
        string modbusStored = "";
        if (!string.IsNullOrEmpty(ModbusMapInput.Text))
        {
            try { modbusStored = FileSystemHelpers.CopyFileOrFolderShallow(ModbusMapInput.Text, _services.Hierarchy.ModbusMapPath(root, group.Name, subOption.Subtype.Name, mod.ControllerName)); }
            catch (Exception ex) { warnings.Add($"Карта modbus: {ex.Message}"); }
        }
        string hmiStored = "";
        if (HmiCheck.IsChecked == true && !string.IsNullOrEmpty(_hmiPath))
        {
            try
            {
                var hmiRootFolder = _services.Hierarchy.HmiPath(root, group.Name, subOption.Subtype.Name, mod.ControllerName);
                Directory.CreateDirectory(hmiRootFolder);
                // Named after the PLC version + "_hmi" (not just copied under the shared "HMI" folder
                // with its original name) — the HMI folder used to be flat/unversioned, so every
                // upload for this controller silently overwrote whatever HMI project was there before
                // (CopyDirectoryContents doesn't delete files removed from the new source, so a stale
                // file from an older HMI could even survive alongside the new ones). Giving each
                // upload its own version-named entry makes every version's HMI project a distinct,
                // non-destructive slot, same as how "ПО" version folders already work per firmware.
                if (Directory.Exists(_hmiPath))
                {
                    var hmiDstFolder = Path.Combine(hmiRootFolder, $"{fwv.Raw}_hmi");
                    Directory.CreateDirectory(hmiDstFolder);
                    CopyDirectoryContents(_hmiPath, hmiDstFolder);
                    hmiStored = hmiDstFolder;
                }
                else
                {
                    var hmiExt = Path.GetExtension(_hmiPath);
                    var hmiDstName = $"{fwv.Raw}_hmi{hmiExt}";
                    File.Copy(_hmiPath, Path.Combine(hmiRootFolder, hmiDstName), overwrite: true);
                    hmiStored = Path.Combine(hmiRootFolder, hmiDstName);
                }
            }
            catch (Exception ex) { warnings.Add($"HMI-проект: {ex.Message}"); }
        }

        // Group/subtype/controller no longer go into the filename itself (see FirmwareNaming.
        // BuildFirmwareFilename) — added here as ordinary tags instead, so a search for "НГР" or
        // "SMH5" still finds the file by the same words it used to carry in its name. "—" is the
        // "no real subtype" placeholder (see HierarchyDefaults), not a real word to tag with.
        var tags = TagsEditor.Tags.ToList();
        foreach (var autoTag in new[] { group.Name, subOption.Subtype.Name == "—" ? null : subOption.Subtype.Name, mod.ControllerName })
            if (!string.IsNullOrWhiteSpace(autoTag) && !tags.Contains(autoTag, StringComparer.OrdinalIgnoreCase))
                tags.Add(autoTag);
        foreach (var tag in tags) _services.Db.AddTag(tag);

        var newFwId = _services.Db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subOption.Subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subOption.Subtype.Prefix,
            HwVersion = hwInt,
            SwVersion = swInt,
            DtStr = fwv.DtStr,
            VersionRaw = fwv.Raw,
            Filename = dstName,
            DiskPath = dstFolder,
            Description = desc,
            Changelog = desc,
            LaunchTypes = launchTypes,
            IoMapPath = ioMapStored,
            InstructionsPath = instrStored,
            ModbusMapPath = modbusStored,
            HmiPath = hmiStored,
            ExecutableHint = _executableHint ?? "",
            HmiExecutableHint = HmiCheck.IsChecked == true ? _hmiExecutableHint ?? "" : "",
            IsOpc = isOpc,
            RequestNum = reqNum,
            CabinetSn = cabinetSn,
            AuthorId = user.Id,
            Status = "active",
            Tags = string.Join(' ', tags),
        });

        if (reservation is not null && newFwId > 0)
            _services.Db.FulfillReservation(reservation.Id!.Value, newFwId);

        _host.ShowStatus($"Загружено: {fwv.Raw}", category: NotificationCategory.FirmwareAndParams);

        var msg = $"Прошивка загружена:\n{dstFolder}";
        if (warnings.Count > 0)
            msg += "\n\nПредупреждения:\n" + string.Join("\n", warnings);
        AppMessageBox.Show(msg, "Готово", MessageBoxButton.OK, MessageBoxImage.Information);

        ResetForm();
    }

    private static void CopyDirectoryContents(string srcDir, string dstDir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(srcDir))
        {
            var name = Path.GetFileName(entry);
            var dest = Path.Combine(dstDir, name);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(dest);
                CopyDirectoryContents(entry, dest);
            }
            else
            {
                File.Copy(entry, dest, overwrite: true);
            }
        }
    }

    private static void WriteChangelog(string folder, FwVersionNumber fwv, List<string> launchTypes, string desc)
    {
        var lines = new List<string>
        {
            $"# {fwv.Raw}",
            $"Дата: {fwv.DtStr}",
            $"Тип пуска: {string.Join(", ", launchTypes)}",
        };
        if (!string.IsNullOrEmpty(desc))
        {
            lines.Add("");
            lines.Add(desc);
        }
        File.WriteAllText(Path.Combine(folder, "CHANGELOG.md"), string.Join("\n", lines), new UTF8Encoding(false));
    }

    private void ResetForm()
    {
        _srcPath = null;
        _autoDetectedModId = null;
        _executableHint = null;
        _hmiExecutableHint = null;
        DropZoneLabel.Text = "Перетащите файл или папку сюда\n\nили нажмите для выбора";
        GroupCombo.SelectedIndex = -1;
        CtrlCombo.SelectedIndex = -1;
        OpcReqNumCheck.IsChecked = false;
        OpcSnCheck.IsChecked = false;
        ReqNumInput.Text = "";
        CabinetSnInput.Text = "";
        foreach (var cb in _launchChecks.Values) cb.IsChecked = false;
        DescInput.Text = "";
        TagsEditor.Configure(System.Array.Empty<string>(), () => _services.Db.GetAllTags());
        IoMapInput.Text = "";
        InstructionsInput.Text = "";
        ModbusMapInput.Text = "";
        HmiCheck.IsChecked = false;
        HmiSection.Visibility = Visibility.Collapsed;
        _hmiPath = null;
        _hmiExecutableHint = null;
        HmiPathLabel.Text = HmiPathPlaceholder;
        StatusLabel.Text = "";
        RefreshReservationPicker();
        UpdatePreview();
    }

    // ── Rollback ──────────────────────────────────────────────────────────────

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (SubCombo.SelectedItem is not SubtypeOption subOption || CtrlCombo.SelectedItem is not ControllerModification mod)
        {
            AppMessageBox.Show("Выберите тип шкафа, подтип и контроллер.", "Откат", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var last = _services.Db.GetLastActiveFwVersion(subOption.Subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
        if (last is null)
        {
            AppMessageBox.Show("Нет активных версий для отката.", "Откат", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var reply = AppMessageBox.Show(
            $"Откатить версию {last.VersionRaw}?\n\nЗапись в базе будет помечена как откатанная.\nСледующая загрузка получит тот же SW-номер заново.\nФайлы на диске останутся нетронутыми.",
            "Откат версии", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.RollbackFwVersion(last.Id!.Value);
        UpdatePreview();
        _host.ShowStatus($"Откатано: {last.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
    }
}
