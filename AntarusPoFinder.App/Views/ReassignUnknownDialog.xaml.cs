using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Small "pick where this goes" record for the third-level combo — either a real controller
/// (ПО section) or a manufacturer (Параметры section), plus the special ОПЦ pseudo-controller (see
/// HierarchyFolders.Opc) that firmware without a specific controller lives under.</summary>
public record ReassignTarget(string Name, bool IsOpc = false);

/// <summary>Group/подтип/контроллер-или-производитель picker used by UnknownFilesDialog's
/// "Переместить выбранные в…" action (see Task 3 — reassign a formerly-unknown folder/file to an
/// existing place in the hierarchy instead of just parking it in «Неизвестное» or deleting it).</summary>
public partial class ReassignUnknownDialog : Window
{
    private readonly AppServices _services;
    private readonly string _section;

    public string? SelectedGroupName { get; private set; }
    public string SelectedSubtypeName { get; private set; } = "—";
    public string SelectedControllerOrManufacturer { get; private set; } = "";
    public bool SelectedIsOpc { get; private set; }

    public ReassignUnknownDialog(AppServices services, string section, int itemCount)
    {
        InitializeComponent();
        _services = services;
        _section = section;

        IntroText.Text = itemCount == 1
            ? "Куда перенести выбранный элемент:"
            : $"Куда перенести выбранные элементы ({itemCount}):";
        ThirdLevelLabel.Text = section == "ПО" ? "Контроллер:" : "Производитель:";

        GroupCombo.ItemsSource = _services.Db.GetAllEquipmentGroups();
        if (GroupCombo.Items.Count > 0) GroupCombo.SelectedIndex = 0;
    }

    private void GroupCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GroupCombo.SelectedItem is not EquipmentGroup group || group.Id is null)
        {
            SubtypeCombo.ItemsSource = null;
            return;
        }

        SubtypeCombo.ItemsSource = _services.Db.GetSubtypesForGroup(group.Id.Value);
        if (SubtypeCombo.Items.Count > 0) SubtypeCombo.SelectedIndex = 0;

        if (_section == "ПО")
        {
            var options = _services.Db.GetAllControllerModels()
                .Select(c => new ReassignTarget(c.Name))
                .Append(new ReassignTarget("ОПЦ (без привязки к контроллеру)", IsOpc: true))
                .ToList();
            ThirdLevelCombo.ItemsSource = options;
        }
        else
        {
            ThirdLevelCombo.ItemsSource = _services.Db.GetParamManufacturers().Select(m => new ReassignTarget(m)).ToList();
        }
        if (ThirdLevelCombo.Items.Count > 0) ThirdLevelCombo.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (GroupCombo.SelectedItem is not EquipmentGroup group)
        {
            ErrorText.Text = "Выберите тип шкафа.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (SubtypeCombo.SelectedItem is not EquipmentSubType subtype)
        {
            ErrorText.Text = "Выберите подтип.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (ThirdLevelCombo.SelectedItem is not ReassignTarget target)
        {
            ErrorText.Text = _section == "ПО" ? "Выберите контроллер (или ОПЦ)." : "Выберите производителя.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        SelectedGroupName = group.Name;
        SelectedSubtypeName = subtype.Name;
        SelectedControllerOrManufacturer = target.Name;
        SelectedIsOpc = target.IsOpc;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
