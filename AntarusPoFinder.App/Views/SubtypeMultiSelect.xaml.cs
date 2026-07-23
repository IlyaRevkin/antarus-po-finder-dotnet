using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Единый чек-комбобокс для выбора подтипов шкафа — заменяет собой пару "обычный
/// ComboBox для основного подтипа + отдельный SubtypeMultiSelect для дополнительных", которая
/// раньше стояла и в UploadView, и в ParamsView. Смысл выбора не поменялся (одна и та же прошивка/
/// файл параметров часто подходит нескольким подтипам шкафа), поменялся только контрол: теперь
/// это ОДИН список чекбоксов на все подтипы группы, а не два разных виджета рядом.
///
/// ПЕРВЫЙ ОТМЕЧЕННЫЙ (по порядку клика, не по порядку в списке) подтип — основной: он определяет
/// папку на диске, номер версии и резерв (см. MainSubtype). Остальные отмеченные — дополнительные
/// (см. ExtraSubtypes): им заводится своя запись в БД, а на диск кладётся только ярлык. Если снять
/// отметку с основного — основным становится следующий по порядку отметки среди оставшихся
/// отмеченных (см. Check_Toggled/_checkOrder).
///
/// Popup вместо настоящего ComboBox — у ComboBox закрытие по выбору пункта встроено в шаблон и не
/// отключается без полной замены шаблона; Popup даёт то же самое (тень, позиционирование под
/// полем), но открытием/закрытием управляем сами.</summary>
public partial class SubtypeMultiSelect : UserControl
{
    private readonly Dictionary<int, CheckBox> _checks = new();
    private readonly Dictionary<int, string> _labels = new();
    private List<EquipmentSubType> _candidates = new();

    /// <summary>Порядок отметки чекбоксов — [0], если есть, это ID основного подтипа. Пополняется/
    /// уменьшается по мере кликов (см. Check_Toggled), переживает пересборку списка кандидатов
    /// (SetItems сохраняет то, что осталось валидным).</summary>
    private readonly List<int> _checkOrder = new();

    /// <summary>Момент, когда Popup закрылся кликом СНАРУЖИ (см. ToggleMore в FirmwareCard.xaml.cs —
    /// тот же самый паттерн и та же причина: Popup с StaysOpen="False" перехватывает клик по самому
    /// полю, которым его открывали, и закрывается молча ДО того, как до поля дойдёт его собственный
    /// MouseLeftButtonUp — без этой защиты клик по полю с открытым списком закрывал бы его и тут же
    /// открывал заново.</summary>
    private DateTime _dismissedAt = DateTime.MinValue;

    /// <summary>Меняется при любом изменении отметок (в т.ч. при смене основного) — родительская
    /// страница подписывается, чтобы узнавать о выборе без опроса по таймеру.</summary>
    public event EventHandler? SelectionChanged;

    public SubtypeMultiSelect()
    {
        InitializeComponent();
        ItemsPopup.Opened += ItemsPopup_Opened;
        ItemsPopup.Closed += ItemsPopup_Closed;
        UpdateSummary();
    }

    /// <summary>Все отмеченные подтипы в порядке отметки — [0] это MainSubtype.</summary>
    public List<EquipmentSubType> Selected =>
        _checkOrder
            .Select(id => _candidates.FirstOrDefault(s => s.Id == id))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

    /// <summary>Первый отмеченный подтип — определяет папку на диске, номер версии и резерв.
    /// Null, если не отмечено ни одного (валидируется вызывающей стороной как "выберите хотя бы
    /// один подтип").</summary>
    public EquipmentSubType? MainSubtype => Selected.Count > 0 ? Selected[0] : null;

    /// <summary>Все отмеченные, КРОМЕ основного — та же роль, что раньше играл отдельный
    /// SubtypeMultiSelect ("Ещё подтипы") целиком.</summary>
    public List<EquipmentSubType> ExtraSubtypes => Selected.Skip(1).ToList();

    /// <summary>Наполняет список кандидатами заново — вызывается при смене группы/типа шкафа.
    /// preselectedIdsInOrder задаёт, что должно прийти отмеченным и в каком порядке (первый — новый
    /// основной); null означает "сохранить текущую отметку как есть", отфильтровав то, что стало
    /// невалидным для новых кандидатов (напр. после смены группы старые ID из другой группы просто
    /// не совпадут ни с одним новым кандидатом и молча отсеются — ID подтипов уникальны глобально).
    /// Пустой список кандидатов выключает поле целиком: выбирать не из чего.</summary>
    public void SetItems(IEnumerable<EquipmentSubType> candidates, IEnumerable<int>? preselectedIdsInOrder = null)
    {
        _candidates = candidates.Where(s => s.Id is not null).ToList();
        var validIds = new HashSet<int>(_candidates.Select(s => s.Id!.Value));

        var preserved = (preselectedIdsInOrder ?? _checkOrder).Where(validIds.Contains).Distinct().ToList();
        _checkOrder.Clear();
        _checkOrder.AddRange(preserved);

        ItemsPopup.IsOpen = false;
        ItemsPanel.Children.Clear();
        _checks.Clear();
        _labels.Clear();

        IsEnabled = _candidates.Count > 0;
        MainBorder.Opacity = IsEnabled ? 1.0 : 0.5;

        foreach (var subtype in _candidates)
        {
            var id = subtype.Id!.Value;
            var label = subtype.Name == "—" ? subtype.FolderName : $"{subtype.FolderName} ({subtype.Name})";
            _labels[id] = label;

            var cb = new CheckBox
            {
                Tag = id,
                Margin = new Thickness(4, 4, 4, 4),
                // Присваивается ДО подписки на Checked/Unchecked ниже — событие тут не летит,
                // _checkOrder уже задан выше явным пересчётом, дублировать его через обработчик не нужно.
                IsChecked = _checkOrder.Contains(id),
            };
            cb.Checked += Check_Toggled;
            cb.Unchecked += Check_Toggled;
            _checks[id] = cb;
            ItemsPanel.Children.Add(cb);
        }

        RefreshLabels();
        UpdateSummary();
    }

    /// <summary>Снять ВСЕ отметки, не трогая сам список кандидатов (кнопка «Очистить данные» и
    /// сброс страницы после успешной загрузки в UploadView).</summary>
    public void ClearAll()
    {
        foreach (var cb in _checks.Values) cb.IsChecked = false;
        _checkOrder.Clear();
        RefreshLabels();
        UpdateSummary();
    }

    /// <summary>Снять отметки со всех, КРОМЕ текущего основного — используется там, где после
    /// каждой загрузки удобнее оставить основной подтип как есть (обычно грузят несколько файлов
    /// подряд в один и тот же шкаф/подтип), а дополнительные у следующего файла почти всегда другие
    /// или их вовсе нет (см. ParamsView.Upload_Click).</summary>
    public void ClearExtras()
    {
        var mainId = _checkOrder.Count > 0 ? _checkOrder[0] : (int?)null;
        foreach (var (id, cb) in _checks)
        {
            if (id == mainId) continue;
            cb.IsChecked = false;
        }
    }

    private void Check_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: int id } cb)
        {
            if (cb.IsChecked == true)
            {
                if (!_checkOrder.Contains(id)) _checkOrder.Add(id);
            }
            else
            {
                // Если сняли основной (id == _checkOrder[0]) — следующий по порядку отметки
                // автоматически становится [0], т.е. новым основным. Больше ничего для этого
                // делать не нужно: MainSubtype/Selected и так читают _checkOrder[0].
                _checkOrder.Remove(id);
            }
        }
        RefreshLabels();
        UpdateSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Помечает текущего основного прямо в списке чекбоксов ("(основной)" в подписи,
    /// полужирный) — при открытом попапе видно не только по сводке в закрытом поле, кто именно
    /// основной, особенно важно сразу после смены (пользователь снял старый основной).</summary>
    private void RefreshLabels()
    {
        var mainId = _checkOrder.Count > 0 ? _checkOrder[0] : (int?)null;
        foreach (var (id, cb) in _checks)
        {
            var baseLabel = _labels.TryGetValue(id, out var l) ? l : "";
            cb.Content = id == mainId ? $"{baseLabel}  —  основной" : baseLabel;
            cb.FontWeight = id == mainId ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void UpdateSummary()
    {
        var selected = Selected;
        if (selected.Count == 0)
        {
            SummaryText.Text = IsEnabled ? "не выбраны" : "нет подтипов";
            return;
        }

        static string DisplayName(EquipmentSubType s) => s.Name == "—" ? s.FolderName : s.Name;
        var mainText = $"{DisplayName(selected[0])} (основной)";
        SummaryText.Text = selected.Count switch
        {
            1 => mainText,
            2 => $"{mainText} + {DisplayName(selected[1])}",
            _ => $"{mainText} + ещё {selected.Count - 1}",
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
