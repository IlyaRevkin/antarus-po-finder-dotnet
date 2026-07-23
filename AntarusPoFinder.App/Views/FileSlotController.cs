using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AntarusPoFinder.App.Views;

/// <summary>Компактная плитка-слот для необязательного вложения на UploadView (Карта ВВ / Карта
/// modbus / Инструкция) — раньше эти три поля были тремя всегда-развёрнутыми рядами (подпись +
/// TextBox + три кнопки Файл/Папка/Очистить), теперь три небольшие плитки в один ряд внутри
/// свёрнутого раздела «ДОП. ФАЙЛЫ И ДОП. НАСТРОЙКИ» (см. UploadView.xaml). Пустая плитка приглушена
/// и предлагает «+ файл»; заполненная выделяется акцентом успеха (SuccessBrush) и показывает имя
/// файла/папки с крестиком «×» для быстрой очистки.
///
/// Контракт с FirmwareUploadService НЕ меняется: путь по-прежнему хранится в обычном TextBox
/// (IoMapInput/ModbusMapInput/InstructionsInput в UploadView.xaml — теперь просто
/// Visibility="Collapsed", носитель значения без своего UI). Плитка не хранит путь сама по себе —
/// она подписывается на TextChanged этого TextBox и просто отражает его текущее содержимое, поэтому
/// ЛЮБОЙ способ, которым путь туда попадает (диалог/drag&amp;drop через саму плитку, перенос из
/// предыдущей версии в UploadView.OfferCarryOver, сброс формы в ResetForm), автоматически обновляет
/// вид плитки — без отдельных ручных вызовов синхронизации по всему UploadView.xaml.cs.
///
/// Диалоги и drag&amp;drop внутри — переиспользованный FilePickerRow (тот же класс, что уже
/// обслуживал старые три ряда и продолжает обслуживать HMI-зону) — плитка не дублирует эту логику, а
/// только даёт ей компактный вид плюс маленькое контекстное меню "Файл.../Папка.../Очистить" по
/// клику (см. Slot_Click).</summary>
internal sealed class FileSlotController
{
    private readonly Border _slot;
    private readonly TextBlock _text;
    private readonly Button _clearButton;
    private readonly string _placeholder;
    private readonly FilePickerRow _picker;

    public FileSlotController(Border slot, TextBlock text, Button clearButton, TextBox valueField,
        string placeholder, string fileDialogTitle, string folderDialogTitle)
    {
        _slot = slot;
        _text = text;
        _clearButton = clearButton;
        _placeholder = placeholder;

        _picker = new FilePickerRow(p => valueField.Text = p, () => valueField.Text = "",
            fileDialogTitle: fileDialogTitle, folderDialogTitle: folderDialogTitle);

        // Единственный источник истины — сам скрытый TextBox; плитка ничего не хранит и не решает
        // сама, просто перерисовывается на любое изменение его .Text.
        valueField.TextChanged += (_, _) => Refresh(valueField.Text);

        _slot.AllowDrop = true;
        _slot.DragOver += (_, e) => FilePickerRow.HandleDragOver(e);
        _slot.Drop += (_, e) => _picker.HandleDrop(e);
        _slot.MouseLeftButtonUp += Slot_Click;
        // Клик по самому крестику не должен ЕЩЁ и открывать меню плитки поверх: ButtonBase помечает
        // свой MouseLeftButtonUp как Handled, когда он привёл к Click (стандартное поведение WPF), а
        // подписка на _slot.MouseLeftButtonUp сделана обычным "+=" (без handledEventsToo) — так что
        // до Slot_Click уже помеченное событие не долетает, отдельная защита не нужна.
        _clearButton.Click += (_, _) => _picker.Clear();

        Refresh(valueField.Text);
    }

    private void Refresh(string? path)
    {
        bool has = !string.IsNullOrEmpty(path);
        _slot.Tag = has ? "Filled" : null;
        _text.Text = has ? Path.GetFileName(path!.TrimEnd(Path.DirectorySeparatorChar)) : _placeholder;
        _text.ToolTip = has ? path : null;
        // Без хардкода цвета — та же тема, что и остальной текст, просто более контрастная (обычная
        // TextBrush вместо приглушённой TextMutedBrush), когда в плитке реально что-то выбрано.
        _text.SetResourceReference(TextBlock.ForegroundProperty, has ? "TextBrush" : "TextMutedBrush");
        _clearButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Клик по плитке (не по крестику — см. комментарий в конструкторе) открывает маленькое
    /// меню выбора: файл или папка, плюс "Очистить", если плитка уже заполнена. Тот же компромисс,
    /// что и в InspectionView (список файлов справки) — обычный WPF ContextMenu, без отдельной темной
    /// перерисовки чрома, см. отчёт по задаче.</summary>
    private void Slot_Click(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = _slot };

        var fileItem = new MenuItem { Header = "Файл..." };
        fileItem.Click += (_, _) => _picker.BrowseFile();
        menu.Items.Add(fileItem);

        var folderItem = new MenuItem { Header = "Папка..." };
        folderItem.Click += (_, _) => _picker.BrowseFolder();
        menu.Items.Add(folderItem);

        if (_clearButton.Visibility == Visibility.Visible)
        {
            menu.Items.Add(new Separator());
            var clearItem = new MenuItem { Header = "Очистить" };
            clearItem.Click += (_, _) => _picker.Clear();
            menu.Items.Add(clearItem);
        }

        _slot.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
