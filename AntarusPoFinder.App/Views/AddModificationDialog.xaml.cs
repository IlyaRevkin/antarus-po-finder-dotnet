using System.Collections.Generic;
using System.Linq;
using System.Windows;

using AntarusPoFinder.App;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

public partial class AddModificationDialog : Window
{
    public string ModName { get; private set; } = "";
    public int HwVersion { get; private set; }
    public string Description { get; private set; } = "";

    /// <summary>Выбранный тип-контроллер — только в режиме правки (там доступен перенос модификации к
    /// другому типу). В режиме добавления null: тип фиксирован строкой, из которой открыли диалог.</summary>
    public int? SelectedControllerId { get; private set; }

    /// <summary>Режим добавления: тип фиксирован, поля пусты. Существующие вызовы не меняются.</summary>
    public AddModificationDialog(string controllerName)
    {
        InitializeComponent();
        Title = $"Добавить модификацию — {controllerName}";
    }

    /// <summary>Режим правки (двойной клик по модификации в Настройки → Иерархия): виден выбор типа,
    /// поля заполнены текущими значениями. hwHint — подсказка про переписывание уже загруженных
    /// прошивок при смене hw (её показывает вызывающий, если такие прошивки есть).</summary>
    public AddModificationDialog(IReadOnlyList<ControllerModel> controllers, int currentControllerId,
        string displayName, int hwVersion, string description, string? hwHint = null)
    {
        InitializeComponent();
        Title = "Изменить модификацию";
        OkButton.Content = "Сохранить";

        ControllerPanel.Visibility = Visibility.Visible;
        ControllerCombo.ItemsSource = controllers;
        ControllerCombo.SelectedItem = controllers.FirstOrDefault(c => c.Id == currentControllerId) ?? controllers.FirstOrDefault();

        NameInput.Text = displayName;
        HwVersionInput.Text = hwVersion.ToString();
        DescriptionInput.Text = description;

        if (!string.IsNullOrWhiteSpace(hwHint))
        {
            HwHint.Text = hwHint;
            HwHint.Visibility = Visibility.Visible;
        }
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
        if (hw < 0 || hw > 9999)
        {
            AppMessageBox.Show("hw_version должен быть от 0 до 9999 (не больше 4 цифр).", "Модификация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ControllerPanel.Visibility == Visibility.Visible && ControllerCombo.SelectedItem is not ControllerModel)
        {
            AppMessageBox.Show("Выберите тип контроллера.", "Модификация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ModName = name;
        HwVersion = hw;
        Description = DescriptionInput.Text.Trim();
        SelectedControllerId = (ControllerCombo.SelectedItem as ControllerModel)?.Id;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
