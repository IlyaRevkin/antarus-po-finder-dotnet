using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Правка набора подтипов у УЖЕ ЗАГРУЖЕННОГО файла параметров — то же, что блок «Подтипы
/// шкафов» в EditFirmwareDialog делает для прошивки, но с двумя отличиями, вытекающими из природы
/// параметров: подтипы предлагаются из ВСЕХ типов шкафа (файл параметров частотника подходит сразу
/// нескольким типам, прошивка ПЛК — нет), и список группируется по типу с фильтром по строке, иначе
/// на реальном справочнике это была бы стена из полусотни чекбоксов.</summary>
public partial class EditParamSubtypesDialog : Window
{
    private readonly AppServices _services;
    private readonly ParamFile _record;
    private readonly List<ParamFileLinkService.SubtypeTarget> _candidates;
    private readonly Dictionary<int, CheckBox> _checks = new();
    private readonly HashSet<int> _initiallyLinked;
    private readonly int _primarySubtypeId;

    public ParamFileLinkService.ApplyResult? Result { get; private set; }

    public EditParamSubtypesDialog(AppServices services, ParamFile record, string fileTitle)
    {
        InitializeComponent();
        _services = services;
        _record = record;
        _primarySubtypeId = record.SubtypeId ?? 0;
        TitleLabel.Text = $"Подтипы файла: {fileTitle}";

        var groups = _services.Db.GetAllEquipmentGroups().ToDictionary(g => g.Id ?? 0, g => g.Name);
        _candidates = _services.Db.GetAllEquipmentSubtypes()
            .Where(s => s.Id is not null)
            .Select(s => new ParamFileLinkService.SubtypeTarget(s,
                groups.TryGetValue(s.GroupId, out var name) ? name : ""))
            .ToList();

        _initiallyLinked = ParamFileLinkService.CurrentLinks(_services.Db, record)
            .Select(l => l.SubtypeId).ToHashSet();
        _initiallyLinked.Add(_primarySubtypeId);

        BuildChecks("");
    }

    /// <summary>Перестроение списка под фильтр. Отметки живут в _checks и переживают перестроение:
    /// чекбоксы, ушедшие из выдачи фильтра, не выбрасываются, а просто не показываются — иначе набор
    /// «отфильтровал → отметил → отфильтровал иначе» терял бы первую отметку.</summary>
    private void BuildChecks(string filter)
    {
        ChecksPanel.Children.Clear();
        filter = filter.Trim();

        foreach (var byGroup in _candidates.GroupBy(c => c.GroupName))
        {
            var matching = byGroup
                .Where(c => filter.Length == 0
                    || c.FullDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || c.Subtype.FolderName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matching.Count == 0) continue;

            ChecksPanel.Children.Add(new TextBlock
            {
                Text = byGroup.Key,
                Style = (Style)FindResource("MutedText"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, ChecksPanel.Children.Count == 0 ? 0 : 10, 2, 4),
            });

            foreach (var target in matching)
            {
                var id = target.Id;
                if (!_checks.TryGetValue(id, out var cb))
                {
                    var isPrimary = id == _primarySubtypeId;
                    var label = target.Subtype.Name == "—"
                        ? target.Subtype.FolderName
                        : $"{target.Subtype.FolderName} ({target.Subtype.Name})";
                    cb = new CheckBox
                    {
                        Tag = id,
                        Content = isPrimary ? $"{label}  —  основной" : label,
                        FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
                        IsChecked = _initiallyLinked.Contains(id),
                        IsEnabled = !isPrimary,
                        Margin = new Thickness(12, 3, 4, 3),
                        ToolTip = isPrimary ? "Файл лежит в папке этого подтипа — отвязать его нельзя" : null,
                    };
                    cb.Checked += (_, _) => UpdateCount();
                    cb.Unchecked += (_, _) => UpdateCount();
                    _checks[id] = cb;
                }
                // Родитель у чекбокса один, а панель перестраивается — прежнюю привязку снимаем сами,
                // иначе WPF бросит "Specified element is already the logical child of another element".
                if (cb.Parent is Panel old) old.Children.Remove(cb);
                ChecksPanel.Children.Add(cb);
            }
        }

        UpdateCount();
    }

    private void UpdateCount() =>
        CountLabel.Text = $"Отмечено подтипов: {_checks.Values.Count(c => c.IsChecked == true)}";

    private void Filter_TextChanged(object sender, TextChangedEventArgs e) => BuildChecks(FilterInput.Text);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root))
        {
            AppMessageBox.Show("Путь к диску не задан. Проверьте Настройки.", "Подтипы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var desired = _checks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        var result = ParamFileLinkService.Apply(_services.Db, _services.Hierarchy, root, _record,
            _candidates, desired, new Services.ShortcutCreator());
        if (result.Changed || result.Warnings.Count > 0) Result = result;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
