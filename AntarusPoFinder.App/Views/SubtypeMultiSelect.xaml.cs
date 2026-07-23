using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Мультивыбор подтипов чекбоксами прямо в комбобоксе — замена отдельному модальному
/// <c>PickSubtypesDialog</c> (тот же смысл: «каким ещё подтипам подходит этот же файл/прошивка»,
/// но без лишнего окна). По духу как <see cref="LaunchTypeChecks"/> — небольшой класс, программно
/// наполняющий список чекбоксами, с похожим API (SetItems/Selected/ClearAll), только тут ещё и сам
/// рисует поле-кнопку с выпадающим списком, а не наполняет готовую панель.</summary>
public partial class SubtypeMultiSelect : UserControl
{
    private readonly Dictionary<int, CheckBox> _checks = new();
    private List<EquipmentSubType> _candidates = new();

    /// <summary>Момент, когда Popup закрылся кликом СНАРУЖИ (см. ToggleMore в FirmwareCard.xaml.cs —
    /// тот же самый паттерн и та же причина: Popup с StaysOpen="False" перехватывает клик по самому
    /// полю, которым его открывали, и закрывается молча ДО того, как до поля дойдёт его собственный
    /// MouseLeftButtonUp — без этой защиты клик по полю с открытым списком закрывал бы его и тут же
    /// открывал заново.</summary>
    private DateTime _dismissedAt = DateTime.MinValue;

    /// <summary>Меняется при любом изменении отметок — родительская страница подписывается, чтобы
    /// узнавать о выборе без опроса по таймеру.</summary>
    public event EventHandler? SelectionChanged;

    public SubtypeMultiSelect()
    {
        InitializeComponent();
        ItemsPopup.Opened += ItemsPopup_Opened;
        ItemsPopup.Closed += ItemsPopup_Closed;
        UpdateSummary();
    }

    /// <summary>Отмеченные подтипы, в том же порядке, что переданы в SetItems.</summary>
    public List<EquipmentSubType> Selected =>
        _candidates.Where(s => s.Id is not null && _checks.TryGetValue(s.Id!.Value, out var cb) && cb.IsChecked == true).ToList();

    /// <summary>Наполняет список кандидатами заново. preselectedIds — какие из них должны прийти уже
    /// отмеченными (используется при пересборке списка, чтобы не терять выбор, который остаётся
    /// валидным — см. RefreshExtraSubtypeCandidates в UploadView/ParamsView). Пустой список кандидатов
    /// выключает поле целиком: выбирать не из чего.</summary>
    public void SetItems(IEnumerable<EquipmentSubType> candidates, IEnumerable<int>? preselectedIds = null)
    {
        _candidates = candidates.Where(s => s.Id is not null).ToList();
        var preselected = new HashSet<int>(preselectedIds ?? Enumerable.Empty<int>());

        ItemsPopup.IsOpen = false;
        ItemsPanel.Children.Clear();
        _checks.Clear();

        IsEnabled = _candidates.Count > 0;
        MainBorder.Opacity = IsEnabled ? 1.0 : 0.5;

        foreach (var subtype in _candidates)
        {
            var label = subtype.Name == "—" ? subtype.FolderName : $"{subtype.FolderName} ({subtype.Name})";
            var cb = new CheckBox
            {
                Content = label,
                Margin = new Thickness(4, 4, 4, 4),
                IsChecked = preselected.Contains(subtype.Id!.Value),
            };
            cb.Checked += Check_Toggled;
            cb.Unchecked += Check_Toggled;
            _checks[subtype.Id!.Value] = cb;
            ItemsPanel.Children.Add(cb);
        }

        UpdateSummary();
    }

    /// <summary>Снять все отметки, не трогая сам список кандидатов (кнопка «Очистить данные» и т.п.).</summary>
    public void ClearAll()
    {
        foreach (var cb in _checks.Values) cb.IsChecked = false;
        UpdateSummary();
    }

    private void Check_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSummary()
    {
        var names = _candidates
            .Where(s => s.Id is not null && _checks.TryGetValue(s.Id!.Value, out var cb) && cb.IsChecked == true)
            .Select(s => s.Name == "—" ? s.FolderName : s.Name)
            .ToList();

        SummaryText.Text = names.Count switch
        {
            0 when !IsEnabled => "нет других подтипов",
            0 => "не выбраны",
            <= 2 => string.Join(", ", names),
            _ => $"выбрано: {names.Count}",
        };
    }

    private void MainBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled) return;
        // Тот же порог и та же причина, что в FirmwareCard.ToggleMore: MouseUp по полю приходит уже
        // ПОСЛЕ того, как MouseDown по нему же закрыл Popup как «клик снаружи» — если не отличать этот
        // случай, повторное открытие происходило бы тем же кликом, которым список закрывали.
        var dismissedByThisClick = !ItemsPopup.IsOpen && (DateTime.Now - _dismissedAt).TotalMilliseconds < 250;
        if (dismissedByThisClick) return;
        ItemsPopup.IsOpen = !ItemsPopup.IsOpen;
    }

    private void ItemsPopup_Opened(object? sender, EventArgs e)
    {
        Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(ItemsPopup, DismissedByClickOutside);
        Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(ItemsPopup, DismissedByClickOutside);
    }

    private void ItemsPopup_Closed(object? sender, EventArgs e) =>
        Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(ItemsPopup, DismissedByClickOutside);

    private void DismissedByClickOutside(object sender, MouseButtonEventArgs e)
    {
        _dismissedAt = DateTime.Now;
        Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(ItemsPopup, DismissedByClickOutside);
    }
}
