using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Группа чекбоксов «ТИП ПУСКА», общая для UploadView (загрузка новой версии) и
/// EditFirmwareDialog (модерация/редактирование уже загруженной). Раньше обе страницы строили этот
/// набор своим почти одинаковым циклом по <see cref="ConfigService.LaunchTypes"/>; с появлением
/// пятого, взаимоисключающего значения «Отсутствует» такое дублирование означало бы два независимых
/// набора правил блокировки, которые легко разъедутся.
///
/// Правило одно: пока отмечено «Отсутствует», остальные четыре сняты и заблокированы (шкаф без типа
/// пуска не может одновременно иметь тип пуска); как только галочку снимают — они снова доступны.
/// Обратной блокировки нет намеренно: оператор может передумать и переключиться на «Отсутствует» в
/// любой момент, не снимая сначала уже проставленные галочки вручную.</summary>
internal sealed class LaunchTypeChecks
{
    private readonly Dictionary<string, CheckBox> _checks = new();
    private readonly CheckBox? _noneCheck;

    public LaunchTypeChecks(Panel host, IEnumerable<string>? selected = null)
    {
        var preselected = new HashSet<string>(selected ?? Enumerable.Empty<string>());
        foreach (var lt in ConfigService.LaunchTypes)
        {
            var cb = new CheckBox
            {
                Content = lt,
                // Нижний отступ — чтобы при переносе на вторую строку (WrapPanel в UploadView на
                // узком окне) строки не слипались.
                Margin = new Thickness(0, 0, 16, 4),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = preselected.Contains(lt),
            };
            _checks[lt] = cb;
            host.Children.Add(cb);
        }

        _noneCheck = _checks.GetValueOrDefault(ConfigService.LaunchTypeNone);
        if (_noneCheck is not null)
        {
            _noneCheck.Checked += (_, _) => ApplyNoneState();
            _noneCheck.Unchecked += (_, _) => ApplyNoneState();
            ApplyNoneState();
        }
    }

    /// <summary>Отмеченные типы пуска в порядке объявления в ConfigService.LaunchTypes.</summary>
    public List<string> Selected =>
        ConfigService.LaunchTypes.Where(lt => _checks[lt].IsChecked == true).ToList();

    /// <summary>Сброс всей группы (кнопка «Очистить данные» / после успешной загрузки).</summary>
    public void ClearAll()
    {
        foreach (var cb in _checks.Values) cb.IsChecked = false;
        ApplyNoneState();
    }

    private void ApplyNoneState()
    {
        if (_noneCheck is null) return;
        bool none = _noneCheck.IsChecked == true;
        foreach (var (name, cb) in _checks)
        {
            if (name == ConfigService.LaunchTypeNone) continue;
            if (none) cb.IsChecked = false;
            cb.IsEnabled = !none;
        }
    }
}
