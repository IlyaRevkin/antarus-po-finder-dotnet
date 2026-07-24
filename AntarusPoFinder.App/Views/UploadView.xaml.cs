using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.Core.Domain;
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

    /// <summary>Бета: единая drag&amp;drop-зона ПЛК+HMI вместо двух раздельных (см. ConfigService.
    /// UnifiedPlcHmiZoneEnabled, Настройки → Общие → ЗАГРУЗКА). Перечитывается в ReloadCombos при
    /// каждом открытии этой страницы — переключение настройки применяется не мгновенно, а при
    /// следующем возврате на «Загрузка прошивки» (см. ApplyUnifiedZoneMode).</summary>
    private bool _unifiedZoneMode;

    /// <summary>Recognized firmware-project extensions per field — used only to decide whether a
    /// picked FOLDER needs the operator to disambiguate which file inside is the executable (see
    /// PromptExecutableHint). Single-file picks never need this: the file itself is unambiguous.</summary>
    private static readonly string[] MainExecutableExts = { ".psl", ".lfs", ".kpr", ".kpj", ".dpj" };
    private static readonly string[] HmiExecutableExts = { ".fsprj" };
    private readonly LaunchTypeChecks _launchChecks;

    // Плитки-слоты доп.файлов (Карта ВВ/Карта modbus/Инструкция) — см. FileSlotController и стиль
    // FileSlot в Styles.xaml. Каждая связывает Border-плитку в XAML с соответствующим скрытым
    // TextBox (IoMapInput/ModbusMapInput/InstructionsInput), который остаётся единственным
    // носителем пути для FirmwareUploadService.
    private readonly FileSlotController _ioMapSlot;
    private readonly FileSlotController _modbusMapSlot;
    private readonly FileSlotController _instrSlot;
    private readonly FilePickerRow _hmiPicker;

    private record ReservationOption(string Label, FwVersionReservation? Reservation);

    /// <summary>See SearchView.OnboardingTarget for why this exists — same reasoning.</summary>
    public FrameworkElement? OnboardingTarget(string key) => key switch
    {
        "dropzone" => DropZone,
        "opc" => ExpandOpcSectionAndReturnTarget(),
        "reserve" => ReserveVersionBtn,
        _ => null,
    };

    /// <summary>"ОПЦИИ ВЕРСИИ" — часть единого свёрнутого по умолчанию раздела "ДОП. ФАЙЛЫ И ДОП.
    /// НАСТРОЙКИ" (см. UploadView.xaml, ExtrasExpander — раньше был отдельный VersionOptionsExpander,
    /// объединены при переходе на плитки доп.файлов). OnboardingOverlay пропускает шаг, если у цели
    /// ActualWidth==0 (см. OnboardingOverlay.ShowStep), а свёрнутая панель именно так себя и ведёт.
    /// Разворачиваем перед показом шага и форсируем layout, чтобы ActualWidth уже был посчитан к
    /// моменту, когда overlay его прочитает.</summary>
    private FrameworkElement ExpandOpcSectionAndReturnTarget()
    {
        ExtrasExpander.IsExpanded = true;
        ExtrasExpander.UpdateLayout();
        return VersionOptionsPanel;
    }

    public UploadView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;

        _launchChecks = new LaunchTypeChecks(LaunchTypesPanel);

        _ioMapSlot = new FileSlotController(IoMapSlot, IoMapSlotText, IoMapSlotClear, IoMapInput, "+ файл",
            fileDialogTitle: "Выбрать файл — карта ВВ", folderDialogTitle: "Выбрать папку — карта ВВ");
        _modbusMapSlot = new FileSlotController(ModbusMapSlot, ModbusMapSlotText, ModbusMapSlotClear, ModbusMapInput, "+ файл",
            fileDialogTitle: "Выбрать файл — карта modbus", folderDialogTitle: "Выбрать папку — карта modbus");
        _instrSlot = new FileSlotController(InstructionsSlot, InstructionsSlotText, InstructionsSlotClear, InstructionsInput, "+ файл",
            fileDialogTitle: "Выбрать файл — инструкция", folderDialogTitle: "Выбрать папку — инструкция");
        _hmiPicker = new FilePickerRow(OnHmiPathPicked, ClearHmi,
            fileDialogTitle: "Выбрать файл HMI-проекта",
            fileDialogFilter: "HMI-проект (*.fsprj)|*.fsprj|Все файлы (*.*)|*.*",
            folderDialogTitle: "Выбрать папку HMI-проекта",
            combineMultiple: paths => StageMultiple(paths, "HMI-проекта"));

        Loaded += (_, _) => ReloadCombos();
        TagsEditor.Configure(System.Array.Empty<string>(), () => _services.Db.GetAllTags());

        // Стрелка/подсказка в заголовке ExtrasExpander (см. UploadView.xaml) должны отражать
        // текущее состояние — "▸ ...нажмите, чтобы развернуть" пока свёрнуто, "▾ ...нажмите, чтобы
        // свернуть" когда раскрыт. Обычный WPF Expander такое умеет через встроенный шеврон, но
        // здесь заголовок — наш собственный Border/StackPanel (шеврон SectionExpander слишком мелкий,
        // пользователь на живом прогоне не заметил, что заголовок кликабелен), поэтому переключаем
        // вручную по событиям Expanded/Collapsed.
        ExtrasExpander.Expanded += (_, _) => SetExtrasHeaderState(expanded: true);
        ExtrasExpander.Collapsed += (_, _) => SetExtrasHeaderState(expanded: false);
    }

    private void SetExtrasHeaderState(bool expanded)
    {
        ExtrasHeaderArrow.Text = expanded ? "▾" : "▸";
        ExtrasHeaderHint.Text = expanded ? "  —  нажмите, чтобы свернуть" : "  —  нажмите, чтобы развернуть";
    }

    // ── Combo population / reload ────────────────────────────────────────────

    /// <summary>Страница живёт в кэше между переходами (MainWindowViewModel._pageCache) — без явного
    /// перечитывания в комбобоксах остаются справочники на момент её первой отрисовки. Отсюда и
    /// «удалил мусорный тип шкафа в Настройках, а в Загрузке он всё ещё есть»: в Настройках список
    /// перечитывается после каждой правки, а здесь — нет.</summary>
    public void RefreshIfActive() => ReloadCombos();

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
        ApplyUnifiedZoneMode();
    }

    // ── Единая зона ПЛК+HMI (бета) ───────────────────────────────────────────

    /// <summary>Переключает видимость раздельных зон (ClassicZonesPanel) и единой зоны
    /// (UnifiedZonePanel) по текущему значению настройки. Вызывается из ReloadCombos — то есть при
    /// каждом заходе на страницу «Загрузка прошивки» (см. MainWindowViewModel.Navigate →
    /// UploadView.RefreshIfActive), а не только один раз при создании страницы — иначе переключение
    /// галочки в Настройках не подхватилось бы без перезапуска приложения.</summary>
    private void ApplyUnifiedZoneMode()
    {
        _unifiedZoneMode = _services.Cfg.UnifiedPlcHmiZoneEnabled();
        ClassicZonesPanel.Visibility = _unifiedZoneMode ? Visibility.Collapsed : Visibility.Visible;
        UnifiedZonePanel.Visibility = _unifiedZoneMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateUnifiedPreview();
    }

    /// <summary>Кого дальше пойдёт HmiSourcePath — эффективный признак «HMI включён», единый для
    /// обоих режимов. В раздельном режиме источник истины — галочка HmiCheck (как и раньше); в
    /// единой зоне отдельной галочки нет вовсе, HMI считается включённым, если единая зона что-то
    /// распознала как HMI-проект (_hmiPath не пуст). Используется и в BuildUploadRequest, и в
    /// Upload_Click (сообщение «HMI не грузится отдельно»), чтобы оба места не разъезжались.</summary>
    private bool HmiEnabledEffective => _unifiedZoneMode ? !string.IsNullOrEmpty(_hmiPath) : HmiCheck.IsChecked == true;

    /// <summary>Что показано в двух строках-превью единой зоны (распознано как ПЛК / распознано как
    /// HMI) — вызывается после каждого распределения файла и при переключении режима/сбросе формы.
    /// Не путать с UpdatePreview() выше — тот считает путь на сетевом диске для итоговой прошивки,
    /// этот — просто отражает текущее содержимое _srcPath/_hmiPath в самой единой зоне.</summary>
    private void UpdateUnifiedPreview()
    {
        bool hasPlc = !string.IsNullOrEmpty(_srcPath);
        UnifiedPlcPreviewText.Text = hasPlc
            ? $"ПЛК: {Path.GetFileName(_srcPath!.TrimEnd(Path.DirectorySeparatorChar))}"
            : "ПЛК: не выбрано";
        UnifiedPlcClearBtn.IsEnabled = hasPlc;

        bool hasHmi = !string.IsNullOrEmpty(_hmiPath);
        UnifiedHmiPreviewText.Text = hasHmi
            ? $"HMI: {Path.GetFileName(_hmiPath!.TrimEnd(Path.DirectorySeparatorChar))}"
            : "HMI: не выбрано (опционально)";
        UnifiedHmiClearBtn.IsEnabled = hasHmi;
    }

    private void UnifiedDropZone_Click(object sender, MouseButtonEventArgs e) => UnifiedBrowseFiles_Click(sender, e);

    private void UnifiedDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void UnifiedDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            ClassifyAndAssign(paths);
    }

    private void UnifiedBrowseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать файл(ы) прошивки ПЛК и/или HMI-проекта",
            Filter = "Прошивка ПЛК/HMI (*.psl;*.lfs;*.kpr;*.kpj;*.dpj;*.fsprj)|*.psl;*.lfs;*.kpr;*.kpj;*.dpj;*.fsprj|Все файлы (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) ClassifyAndAssign(dlg.FileNames);
    }

    private void UnifiedBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Выбрать папку прошивки ПЛК или HMI-проекта" };
        if (dlg.ShowDialog() == true) ClassifyAndAssign(new[] { dlg.FolderName });
    }

    private void UnifiedClearAll_Click(object sender, RoutedEventArgs e)
    {
        ClearUnifiedPlc();
        ClearHmi();
        UpdateUnifiedPreview();
    }

    private void UnifiedPlcClear_Click(object sender, RoutedEventArgs e) => ClearUnifiedPlc();

    private void UnifiedHmiClear_Click(object sender, RoutedEventArgs e)
    {
        ClearHmi();
        UpdateUnifiedPreview();
    }

    /// <summary>Очищает только ПЛК-часть единой зоны — узкий аналог ClearHmi() (та уже существовала
    /// для раздельного режима), нужен здесь отдельно потому что раздельный режим сбрасывает ПЛК-путь
    /// только целиком через ResetForm/ClearData_Click, а единая зона позволяет очистить ПЛК и HMI
    /// по отдельности, не трогая второе.</summary>
    private void ClearUnifiedPlc()
    {
        _srcPath = null;
        _autoDetectedModId = null;
        _executableHint = null;
        DropZoneLabel.Text = "Перетащите файл, папку или несколько файлов сюда\n\nили нажмите для выбора";
        StatusLabel.Text = "";
        UpdatePreview();
        UpdateUnifiedPreview();
    }

    /// <summary>Каждый элемент, брошенный/выбранный в единой зоне (файл ИЛИ папка — из drag&amp;drop
    /// может прийти сразу несколько верхнеуровневых путей, напр. .psl-файл + папка HMI, выбранные
    /// вместе в проводнике), распределяется НЕЗАВИСИМО от остальных: единая зона не пытается
    /// склеивать несколько отдельных файлов в один общий проект (в отличие от классической зоны +
    /// DropStagingService, см. FilePickerRow/StageMultiple) — если ПЛК-проект состоит из нескольких
    /// файлов без общей папки, его нужно либо сложить в папку заранее, либо воспользоваться
    /// раздельными зонами (там staging всё ещё работает). Компромисс сознательный: бета не пытается
    /// быть умнее эвристики "одно расширение — один тип", лучше спросить оператора, чем угадать
    /// неверно.</summary>
    private void ClassifyAndAssign(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
            ClassifyAndAssignOne(path);
        UpdateUnifiedPreview();
    }

    private void ClassifyAndAssignOne(string path)
    {
        var displayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

        if (Directory.Exists(path))
        {
            // Однозначно определяем по содержимому папки (см. ExecutableHintResolver.AutoDetect —
            // "ровно один файл с известным расширением во всём дереве"): если внутри есть ровно один
            // ПЛК-кандидат и ни одного HMI-кандидата (или наоборот) — папка явно того типа. Если
            // совпало и то, и другое (смешанная папка) или не совпало ни одно — не гадаем, спрашиваем.
            var plcHint = ExecutableHintResolver.AutoDetect(path, MainExecutableExts);
            var hmiHint = ExecutableHintResolver.AutoDetect(path, HmiExecutableExts);
            if (plcHint is not null && hmiHint is null) { AssignAsPlc(path); return; }
            if (hmiHint is not null && plcHint is null) { AssignAsHmi(path); return; }

            switch (AskPlcOrHmi(displayName))
            {
                case PlcHmiChoice.Plc: AssignAsPlc(path); break;
                case PlcHmiChoice.Hmi: AssignAsHmi(path); break;
                // Skip — оператор пропустил этот элемент, ничего не меняем.
            }
            return;
        }

        if (!File.Exists(path)) return; // путь исчез между drop и обработкой (сеть/флешка) — молча пропускаем

        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".fsprj", StringComparison.OrdinalIgnoreCase)) { AssignAsHmi(path); return; }
        if (MainExecutableExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) { AssignAsPlc(path); return; }

        switch (AskPlcOrHmi(displayName))
        {
            case PlcHmiChoice.Plc: AssignAsPlc(path); break;
            case PlcHmiChoice.Hmi: AssignAsHmi(path); break;
        }
    }

    /// <summary>Переиспользует существующую логику основной зоны как есть (автоопределение PSL/
    /// KINCO, сброс Карты in/out и т.п.) — единая зона не дублирует эту логику, а просто решает,
    /// КУДА какой путь отправить.</summary>
    private void AssignAsPlc(string path) => OnFileDropped(path);

    /// <summary>Аналогично — переиспользует существующую HMI-логику (подсказка исполняемого файла
    /// для папки и т.п.), см. OnHmiPathPicked.</summary>
    private void AssignAsHmi(string path) => OnHmiPathPicked(path);

    private enum PlcHmiChoice { Plc, Hmi, Skip }

    /// <summary>Небольшое модальное окно "это ПЛК или HMI?" — собрано в коде тем же приёмом, что и
    /// AppMessageBox (AntarusPoFinder.App/AppMessageBox.cs), а не через YesNo/OKCancel: у AppMessageBox
    /// только два готовых набора кнопок, ни один не даёт трёх смысловых вариантов с понятными
    /// подписями ("ПЛК"/"HMI"/"Пропустить") — Да/Нет здесь читались бы неоднозначно. Возвращает Skip
    /// при закрытии крестиком/Escape — как и остальные диалоги подтверждения в этом файле, закрытие
    /// без явного выбора не должно тайно засчитаться как один из вариантов.</summary>
    private PlcHmiChoice AskPlcOrHmi(string itemName)
    {
        var owner = Window.GetWindow(this);
        var result = PlcHmiChoice.Skip;

        var win = new Window
        {
            Title = "Единая зона (бета) — что это?",
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 360,
            MaxWidth = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        if (owner is not null && owner != win) win.Owner = owner;
        win.SetResourceReference(Window.BackgroundProperty, "BgBrush");
        win.SetResourceReference(Window.ForegroundProperty, "TextBrush");

        var root = new StackPanel { Margin = new Thickness(20) };
        var text = new TextBlock
        {
            Text = $"«{itemName}» — это прошивка ПЛК или HMI-проект?\n\nЕдиная зона не смогла определить это по расширению/содержимому автоматически. Укажите вручную.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        root.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        void AddBtn(string content, PlcHmiChoice choice, bool primary)
        {
            var btn = new Button { Content = content, Height = 34, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            if (!primary) btn.SetResourceReference(Button.StyleProperty, "SecondaryButton");
            btn.Click += (_, _) => { result = choice; win.Close(); };
            buttons.Children.Add(btn);
        }
        AddBtn("Пропустить", PlcHmiChoice.Skip, false);
        AddBtn("HMI", PlcHmiChoice.Hmi, false);
        AddBtn("ПЛК", PlcHmiChoice.Plc, true);
        root.Children.Add(buttons);

        win.Content = root;
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.ShowDialog();
        return result;
    }

    /// <summary>Наполняет единый чек-комбобокс подтипов (SubtypesSelect) под текущую группу —
    /// раньше это были два отдельных вызова (ComboBox SubCombo для основного + SetItems с
    /// исключённым основным для ExtraSubtypesSelect), теперь один SetItems на полный список
    /// подтипов группы. Текущая отметка (основной + дополнительные) сохраняется через SetItems'
    /// собственную фильтрацию по валидности ID — подтипы другой группы никогда не совпадут по ID
    /// с подтипами новой (ID глобально уникален), так что смена группы естественно вычищает
    /// то, что стало неприменимо, без отдельного явного сброса.</summary>
    private void PopulateSubtypes()
    {
        if (GroupCombo.SelectedItem is not EquipmentGroup group)
        {
            SubtypesSelect.SetItems(Enumerable.Empty<EquipmentSubType>());
            RefreshReservationPicker();
            UpdatePreview();
            return;
        }

        var subtypes = _services.Db.GetSubtypesForGroup(group.Id!.Value);
        SubtypesSelect.SetItems(subtypes);

        RefreshReservationPicker();
        UpdatePreview();
    }

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateSubtypes();

    /// <summary>Тот же каскад, что раньше висел на SubCombo_SelectionChanged: смена основного
    /// подтипа (в т.ч. когда пользователь снял старый основной и им автоматически стал следующий
    /// отмеченный — см. SubtypeMultiSelect.Check_Toggled) должна пересчитать резерв и превью пути,
    /// которые от основного подтипа и зависят.</summary>
    private void SubtypesSelect_SelectionChanged(object? sender, EventArgs e)
    {
        RefreshReservationPicker();
        UpdatePreview();
    }

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
        if (SubtypesSelect.MainSubtype is not EquipmentSubType subtype || CtrlCombo.SelectedItem is not ControllerModification mod)
        {
            ReservationPanel.Visibility = Visibility.Collapsed;
            ReservationCombo.ItemsSource = null;
            return;
        }

        var reservations = _services.Db.GetActiveReservations(subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
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
            SubtypesSelect.MainSubtype is not EquipmentSubType subtype ||
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
            subtype.Id!.Value, mod.ControllerId, mod.HwVersion,
            group.Prefix, subtype.Prefix, _services.CurrentUserName, IncludeDateCheck.IsChecked == true, ttlHours);
        var fwv = FwVersionNumber.Parse(reservation.VersionRaw)!;

        var subDisplay = subtype.Name == "—" ? "" : subtype.Name;
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
        if (SubtypesSelect.MainSubtype is not EquipmentSubType subtype) return;
        if (CtrlCombo.SelectedItem is not ControllerModification mod) return;

        var prev = _services.Db.GetLastActiveFwVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
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
            SubtypesSelect.MainSubtype is not EquipmentSubType subtype ||
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
            int swInt = _services.Db.GetNextSwVersion(subtype.Id!.Value, mod.ControllerId, hwInt);
            fwv = FwVersionNumber.Build(group.Prefix, subtype.Prefix, hwInt, swInt, includeDate: IncludeDateCheck.IsChecked == true);
        }

        bool isOpc = IsOpc;
        string ctrlFolder = isOpc ? "ОПЦ" : mod.ControllerName;
        string ext = string.IsNullOrEmpty(_srcPath) || Directory.Exists(_srcPath) ? ".psl" : Path.GetExtension(_srcPath);

        var subDisplay = subtype.Name == "—" ? "" : subtype.Name;
        var pathStr = string.Join(" / ", new[] { "ПО", group.Name, subDisplay, ctrlFolder, fwv.Raw }.Where(p => !string.IsNullOrEmpty(p)));

        string reqNum = OpcReqNumCheck.IsChecked == true ? FirmwareUploadService.Format5Digits(ReqNumInput.Text) : "";
        string cabinetSn = OpcSnCheck.IsChecked == true ? FirmwareUploadService.Format5Digits(CabinetSnInput.Text) : "";
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
            // Проект, у которого исполняемый файл и его файлы просто лежат рядом, а не в общей папке,
            // раньше нельзя было выбрать иначе как создав папку руками в проводнике.
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        OnFileDropped(dlg.FileNames.Length > 1 ? StageMultiple(dlg.FileNames, "прошивки") : dlg.FileName);
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
            OnFileDropped(paths.Length > 1 ? StageMultiple(paths, "прошивки") : paths[0]);
    }

    // ── Перетаскивание нескольких файлов сразу ────────────────────────────────

    /// <summary>Временные папки, собранные из наборов перетащенных файлов — удаляются после успешной
    /// загрузки и при очистке формы (см. DropStagingService).</summary>
    private readonly List<string> _stagedPaths = new();

    /// <summary>Несколько файлов складываются в одну временную папку, и дальше это обычная «папка»:
    /// те же проверки, тот же выбор исполняемого файла, то же копирование целиком. Раньше из набора
    /// молча брался первый файл, остальные пропадали без сообщения.</summary>
    private string StageMultiple(string[] paths, string contextLabel)
    {
        try
        {
            var staged = DropStagingService.Stage(paths);
            _stagedPaths.Add(staged);
            _host.ShowStatus($"Выбрано файлов для {contextLabel}: {paths.Length} — будут загружены вместе, одной папкой.",
                category: NotificationCategory.FirmwareAndParams);
            return staged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppMessageBox.Show($"Не удалось подготовить выбранные файлы: {ex.Message}", "Загрузка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return paths[0];
        }
    }

    private void CleanupStaged()
    {
        foreach (var path in _stagedPaths) DropStagingService.Cleanup(path);
        _stagedPaths.Clear();
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
            else if (FirmwareUploadService.KincoProjectExtensions.Contains(ext))
                TryKincoAutodetect();
        }
        else
        {
            _executableHint = PromptExecutableHint(path, MainExecutableExts, "прошивки");
            if (!string.IsNullOrEmpty(_executableHint))
            {
                StatusLabel.Text += $"  —  исполняемый: {_executableHint}";
                // Папка (в т.ч. собранная из набора перетащенных файлов) с .psl внутри — контроллер
                // определяется по содержимому ровно так же, как у одиночного файла; раньше выбор папки
                // отключал автоопределение целиком, хотя нужный файл уже был известен.
                var hintPath = Path.Combine(path, _executableHint);
                if (string.Equals(Path.GetExtension(hintPath), ".psl", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(hintPath))
                    TryPslAutodetect(hintPath);
            }
        }

        UpdatePreview();
    }

    /// <summary>When a picked FOLDER has no file matching a known extension, content-based/extension
    /// autodetect (TryPslAutodetect/TryKincoAutodetect above) has nothing to go on — this is the
    /// fallback: ask the operator directly which file in the folder is the one to run. Purely a
    /// display hint (FwVersionRecord.ExecutableHint/HmiExecutableHint); the whole folder is always
    /// copied regardless (support files/drivers alongside the executable are often required).
    /// Silent when exactly one candidate matches a known extension — только неоднозначные/пустые
    /// случаи отвлекают оператора.
    ///
    /// И поиск кандидата, и сам выбор теперь идут по ВСЕМУ дереву папки, а не только по верхнему
    /// уровню: проект, у которого .psl/.fsprj лежит во вложенной папке, раньше и не определялся
    /// автоматически, и не мог быть указан руками — в списке были только файлы корня.</summary>
    private string? PromptExecutableHint(string folderPath, string[] knownExts, string contextLabel)
    {
        var auto = ExecutableHintResolver.AutoDetect(folderPath, knownExts);
        if (auto is not null) return auto;
        if (ExecutableHintResolver.ListRelativeFiles(folderPath).Count == 0) return null;

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        return PickFileDialog.Pick(Window.GetWindow(this), "Выбрать исполняемый файл",
            $"В папке «{folderName}» не найден однозначный файл со стандартным расширением для {contextLabel}.\n" +
            "Укажите, какой файл является исполняемым (двойной клик по папке — зайти внутрь) — сама папка будет скопирована целиком в любом случае:",
            folderPath);
    }

    // ── PSL / KINCO auto-detection ────────────────────────────────────────────
    // Actual detection logic lives in FirmwareUploadService (Core, testable) — see
    // AutodetectFromPsl/AutodetectKinco/FindModificationByPslKey there.

    /// <summary>Unlike PSL (which has an inspectable internal format — see PslInspector), .dpj/
    /// .kpr/.kpj don't have a documented structure to parse a model out of, and KINCO has exactly
    /// one modification in the hierarchy (see HierarchyDefaultsData) — so just recognizing the
    /// extension is already an unambiguous match, no file content needs reading.</summary>
    private void TryKincoAutodetect()
    {
        var match = FirmwareUploadService.AutodetectKinco(_mods);
        if (match is null) return;
        ApplyAutoDetectedModification(match);
    }

    private void TryPslAutodetect(string path)
    {
        var result = FirmwareUploadService.AutodetectFromPsl(path, _mods);
        if (result is null || string.IsNullOrEmpty(result.DeviceKey)) return;

        if (result.Modification is null)
        {
            _host.ShowStatus($"Обнаружен контроллер «{result.DeviceKey}» — такой модификации нет в справочнике. Выберите вручную или добавьте её в Настройки → Иерархия.", category: NotificationCategory.Hierarchy);
            return;
        }

        ApplyAutoDetectedModification(result.Modification);
    }

    private void ApplyAutoDetectedModification(ControllerModification match)
    {
        _autoDetectedModId = match.Id;
        var idx = _mods.FindIndex(m => m.Id == match.Id);
        if (idx < 0) return;
        if (CtrlCombo.SelectedIndex == idx) OnCtrlChanged();
        else CtrlCombo.SelectedIndex = idx;
    }

    // ── Attachment fields ─────────────────────────────────────────────────────
    // Файл/Папка/Очистить for Карта ВВ/Карта modbus/Инструкция — see the FileSlotController fields
    // (_ioMapSlot/_modbusMapSlot/_instrSlot, constructed once above): each tile wires its own
    // dialogs/drag&drop/clear internally, no separate Click handlers needed here anymore.

    private const string HmiPathPlaceholder = "Перетащите файл, папку или несколько файлов HMI-проекта сюда\nили нажмите для выбора";

    private void HmiCheck_Toggled(object sender, RoutedEventArgs e)
    {
        bool isChecked = HmiCheck.IsChecked == true;
        HmiSection.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        if (!isChecked) ClearHmi();
    }

    /// <summary>Clicking the drop zone itself (outside a drag&amp;drop) is a shortcut for "Файл..." —
    /// same click-to-browse behavior as the main firmware DropZone above.</summary>
    private void HmiDropZone_Click(object sender, MouseButtonEventArgs e) => _hmiPicker.BrowseFile();

    private void HmiDropZone_DragOver(object sender, DragEventArgs e) => FilePickerRow.HandleDragOver(e);

    private void HmiDropZone_Drop(object sender, DragEventArgs e) => _hmiPicker.HandleDrop(e);

    private void HmiBrowseFile_Click(object sender, RoutedEventArgs e) => _hmiPicker.BrowseFile();
    private void HmiBrowseFolder_Click(object sender, RoutedEventArgs e) => _hmiPicker.BrowseFolder();
    private void HmiClear_Click(object sender, RoutedEventArgs e) => _hmiPicker.Clear();

    private void OnHmiPathPicked(string path)
    {
        _hmiPath = path;
        _hmiExecutableHint = Directory.Exists(path) ? PromptExecutableHint(path, HmiExecutableExts, "HMI-проекта") : null;
        HmiPathLabel.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
    }

    private void ClearHmi()
    {
        _hmiPath = null;
        _hmiExecutableHint = null;
        HmiPathLabel.Text = HmiPathPlaceholder;
    }

    private void ClearData_Click(object sender, RoutedEventArgs e) => ResetForm();

    // ── Upload ────────────────────────────────────────────────────────────────

    /// <summary>Асинхронная: копирование прошивки и вложений на сетевой диск (единственная долгая
    /// часть загрузки — гигабайты по общей шаре) идёт в фоновом потоке, обе БД-фазы остаются здесь,
    /// на потоке интерфейса (см. FirmwareUploadService.Prepare/CopyFiles/Register). Раньше окно
    /// стояло колом всё время копирования, без единого признака, что что-то происходит.</summary>
    private async void Upload_Click(object sender, RoutedEventArgs e)
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
            var noFwMsg = HmiEnabledEffective && !string.IsNullOrEmpty(_hmiPath)
                ? "HMI-проект не загружается отдельно — он прикладывается к версии прошивки ПЛК.\n\n" +
                  "Выберите файл или папку прошивки ПЛК в верхней области (даже если в этой версии изменился только HMI) — HMI-проект уйдёт вместе с ней."
                : "Выберите файл прошивки.";
            AppMessageBox.Show(noFwMsg, "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var request = BuildUploadRequest();
        var shortcuts = new Services.ShortcutCreator();

        // Подготовка (БД + проверки) — на потоке интерфейса; на диск здесь ещё ничего не пишется,
        // поэтому оба подтверждения ниже спрашиваются ДО начала копирования, как и раньше.
        var (plan, failure) = FirmwareUploadService.Prepare(_services.Db, _services.Hierarchy, request);

        // Two checks used to pop a Yes/No MessageBox mid-transaction and either continue in place or
        // abort — the service can't show UI, so it hands back NeedsConfirmation instead and expects
        // the SAME request re-submitted with the matching Confirm* flag once the user agrees (see
        // FirmwareUploadService's doc comment). Looping here reproduces the exact original sequence:
        // unknown-extension prompt first (if any), then the destination-exists prompt (if any).
        while (plan is null && failure!.Outcome == FirmwareUploadOutcome.NeedsConfirmation)
        {
            var title = failure.ConfirmationKind switch
            {
                FirmwareConfirmationKind.UnknownExtension => "Неизвестное расширение",
                FirmwareConfirmationKind.UnknownHmiExtension => "Неизвестное расширение HMI",
                _ => "Версия существует",
            };
            var reply = AppMessageBox.Show(failure.ConfirmationMessage ?? "", title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (reply != MessageBoxResult.Yes) return;

            if (failure.ConfirmationKind == FirmwareConfirmationKind.UnknownExtension)
                request.ConfirmUnknownExtension = true;
            else if (failure.ConfirmationKind == FirmwareConfirmationKind.UnknownHmiExtension)
                request.ConfirmUnknownHmiExtension = true;
            else
                request.ConfirmOverwriteExisting = true;

            (plan, failure) = FirmwareUploadService.Prepare(_services.Db, _services.Hierarchy, request);
        }

        FirmwareUploadResult result;
        if (plan is null)
        {
            result = failure!;
        }
        else
        {
            UploadBtn.IsEnabled = false;
            try
            {
                FirmwareUploadCopyResult copy;
                using (_host.BeginBusy($"Загрузка на диск: {plan.Version.Raw}"))
                    copy = await Task.Run(() => FirmwareUploadService.CopyFiles(plan));

                result = copy.IoErrorMessage is not null
                    ? FirmwareUploadResult.IoFailure(copy.IoErrorMessage)
                    : FirmwareUploadService.Register(_services.Db, _services.Hierarchy, plan, copy, shortcuts);
            }
            finally
            {
                UploadBtn.IsEnabled = true;
            }
        }

        switch (result.Outcome)
        {
            case FirmwareUploadOutcome.ValidationFailed:
                var error = result.Errors.FirstOrDefault() ?? "Не удалось загрузить прошивку.";
                AppMessageBox.Show(error, "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
                if (error == "Укажите описание изменений в этой версии.") DescInput.Focus();
                return;

            case FirmwareUploadOutcome.IoError:
                AppMessageBox.Show(result.IoErrorMessage ?? "", "Ошибка файла", MessageBoxButton.OK, MessageBoxImage.Error);
                return;

            case FirmwareUploadOutcome.Success:
                _host.ShowStatus($"Загружено: {result.Record!.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
                // We just wrote files to the disk — refresh the footer file count now instead of
                // letting it sit stale until the next periodic RunSync tick.
                _host.RefreshDiskStatus();
                // Выдача поиска кэшируется между заходами на вкладку — иначе оператор вернулся бы на
                // «Поиск» и не увидел только что загруженное; помечаем её устаревшей явно.
                _host.InvalidateSearchResults();

                var msg = $"Прошивка загружена:\n{result.DestinationFolder}";
                // Дополнительные подтипы легко потерять из виду: файлов у них на диске нет (только
                // ярлык), поэтому явно говорим, сколько записей завелось помимо основной.
                if (result.ExtraFwVersionIds.Count > 0)
                    msg += $"\n\nТа же версия добавлена ещё для {result.ExtraFwVersionIds.Count} подтип(ов) — ярлыком, без копирования файлов.";
                if (result.Warnings.Count > 0)
                    msg += "\n\nПредупреждения:\n" + string.Join("\n", result.Warnings);
                AppMessageBox.Show(msg, "Готово", MessageBoxButton.OK, MessageBoxImage.Information);

                ResetForm();
                return;
        }
    }

    private FirmwareUploadRequest BuildUploadRequest()
    {
        var group = GroupCombo.SelectedItem as EquipmentGroup;
        var mod = CtrlCombo.SelectedItem as ControllerModification;
        var launchTypes = _launchChecks.Selected;

        return new FirmwareUploadRequest
        {
            SourcePath = _srcPath ?? "",
            Group = group,
            Subtype = SubtypesSelect.MainSubtype,
            ExtraSubtypes = SubtypesSelect.ExtraSubtypes,
            Modification = mod,
            LaunchTypes = launchTypes,
            Description = DescInput.Text.Trim(),
            IncludeDateInVersion = IncludeDateCheck.IsChecked == true,
            OpcRequestEnabled = OpcReqNumCheck.IsChecked == true,
            RequestNumRaw = ReqNumInput.Text,
            OpcSnEnabled = OpcSnCheck.IsChecked == true,
            CabinetSnRaw = CabinetSnInput.Text,
            Reservation = GetSelectedReservation(),
            RootPath = _services.Cfg.RootPath(),
            IoMapSourcePath = IoMapInput.Text,
            InstructionsSourcePath = InstructionsInput.Text,
            ModbusMapSourcePath = ModbusMapInput.Text,
            HmiEnabled = HmiEnabledEffective,
            HmiSourcePath = _hmiPath ?? "",
            ExecutableHint = _executableHint ?? "",
            HmiExecutableHint = _hmiExecutableHint ?? "",
            Tags = TagsEditor.Tags.ToList(),
            AuthorUserName = _services.CurrentUserName,
        };
    }

    private void ResetForm()
    {
        // Файлы уже скопированы на диск (или оператор нажал «Очистить данные») — временные папки,
        // собранные из наборов перетащенных файлов, больше не нужны.
        CleanupStaged();
        _srcPath = null;
        _autoDetectedModId = null;
        _executableHint = null;
        _hmiExecutableHint = null;
        DropZoneLabel.Text = "Перетащите файл, папку или несколько файлов сюда\n\nили нажмите для выбора";
        SubtypesSelect.ClearAll();
        GroupCombo.SelectedIndex = -1;
        CtrlCombo.SelectedIndex = -1;
        OpcReqNumCheck.IsChecked = false;
        OpcSnCheck.IsChecked = false;
        ReqNumInput.Text = "";
        CabinetSnInput.Text = "";
        _launchChecks.ClearAll();
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
        UpdateUnifiedPreview();
    }

    // ── Rollback ──────────────────────────────────────────────────────────────

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (SubtypesSelect.MainSubtype is not EquipmentSubType subtype || CtrlCombo.SelectedItem is not ControllerModification mod)
        {
            AppMessageBox.Show("Выберите тип шкафа, подтип и контроллер.", "Откат", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var last = _services.Db.GetLastActiveFwVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
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
        _host.InvalidateSearchResults();
        _host.ShowStatus($"Откатано: {last.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
    }
}
