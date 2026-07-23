using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Выбор дополнительных подтипов шкафов, которым подходит та же самая прошивка/файл
/// параметров. Отдельный диалог, а не мультивыбор прямо в комбобоксе формы: основной подтип
/// определяет номер версии, папку на диске и резерв номера, а эти — только «куда ещё положить
/// ярлык и завести запись», их роль принципиально другая (см. FirmwareUploadRequest.ExtraSubtypes).</summary>
public partial class PickSubtypesDialog : Window
{
    private class Item : INotifyPropertyChanged
    {
        public required EquipmentSubType Subtype { get; init; }
        public required string Label { get; init; }
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<Item> _items;

    public List<EquipmentSubType> Selected { get; private set; } = new();

    public PickSubtypesDialog(string label, IEnumerable<EquipmentSubType> subtypes, IEnumerable<int> preselectedIds)
    {
        InitializeComponent();
        Title = "Дополнительные подтипы";
        LabelText.Text = label;

        var preselected = new HashSet<int>(preselectedIds);
        _items = subtypes
            .Where(s => s.Id is not null)
            .Select(s => new Item
            {
                Subtype = s,
                Label = s.Name == "—" ? s.FolderName : $"{s.FolderName} ({s.Name})",
                IsChecked = preselected.Contains(s.Id!.Value),
            })
            .ToList();
        SubtypesList.ItemsSource = _items;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Selected = _items.Where(i => i.IsChecked).Select(i => i.Subtype).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Возвращает null, если оператор отменил диалог (прежний выбор остаётся как был).</summary>
    public static List<EquipmentSubType>? Pick(Window? owner, string label,
        IEnumerable<EquipmentSubType> subtypes, IEnumerable<int> preselectedIds)
    {
        var dlg = new PickSubtypesDialog(label, subtypes, preselectedIds) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Selected : null;
    }
}
