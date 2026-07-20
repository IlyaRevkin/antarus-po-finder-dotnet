using System.Windows;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public partial class AddModificationDialog : Window
{
    public string ModName { get; private set; } = "";
    public int HwVersion { get; private set; }
    public string Description { get; private set; } = "";

    public AddModificationDialog(string controllerName)
    {
        InitializeComponent();
        Title = $"Добавить модификацию — {controllerName}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            AppMessageBox.Show("Укажите название модификации.", "Модификация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(HwVersionInput.Text.Trim(), out var hw))
        {
            AppMessageBox.Show("hw_version должен быть целым числом.", "Модификация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ModName = name;
        HwVersion = hw;
        Description = DescriptionInput.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
