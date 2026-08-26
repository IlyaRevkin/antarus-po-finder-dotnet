using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Разовый перенос накопленных текстовых заданий в документы-таблицы.
///
/// Жалоба владельца: «Автоматически файлы не перенёс что были». Требование к решению было тут же:
/// «не молча и не вслепую: разбор может ошибиться, поэтому результат должен быть виден и обратим».
/// Отсюда устройство окна — сперва ПОКАЗАТЬ, что получится, дать снять отметки и поправить названия,
/// и только потом писать; а после записи предложить отменить ровно то, что записано.
///
/// Само окно ничего не решает: и разбор, и запись, и отмена живут в
/// Core/Services/ParamTableBulkImport.cs под тестами.</summary>
public partial class ParamTableBulkImportDialog : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<ItemVm> _items = new();
    private List<ParamTableBulkImport.ImportedDocument> _created = new();

    /// <summary>Завёл ли перенос хоть один документ — по этому вызывающая страница решает,
    /// перечитывать ли список.</summary>
    public bool Changed { get; private set; }

    /// <summary>Обёртка строки: сетке нужны уведомления об изменении (галочка и название правятся
    /// прямо в ней), а самой модели переноса они ни к чему.</summary>
    public sealed class ItemVm : INotifyPropertyChanged
    {
        public ItemVm(ParamTableBulkImport.Item source) => Source = source;

        public ParamTableBulkImport.Item Source { get; }

        public string FileName => Source.File.Filename;
        public string Subtypes => Source.Subtypes;
        public string SourceName => Source.SourceName;
        public string EncodingName => Source.EncodingName;
        public bool CanImport => Source.CanImport;

        public string DocumentName
        {
            get => Source.DocumentName;
            set { Source.DocumentName = value; Changed(nameof(DocumentName)); }
        }

        public bool Selected
        {
            get => Source.Selected;
            set { Source.Selected = value && Source.CanImport; Changed(nameof(Selected)); }
        }

        /// <summary>Перенесён ли уже. Красит строку в цвет успеха и запрещает переносить её второй
        /// раз: повторное нажатие иначе завело бы вторую копию того же документа.</summary>
        private bool _imported;
        public bool Imported
        {
            get => _imported;
            set { _imported = value; Changed(nameof(Imported)); Changed(nameof(Outcome)); }
        }

        public string Outcome => Imported ? "Заведён документ «" + DocumentName + "»" : Source.Outcome;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public ParamTableBulkImportDialog(AppServices services)
    {
        InitializeComponent();
        _services = services;
        ItemsGrid.ItemsSource = _items;
        Rescan();
    }

    private void Rescan()
    {
        _items.Clear();
        foreach (var item in ParamTableBulkImport.Scan(_services.Db))
            _items.Add(new ItemVm(item));

        var ready = _items.Count(i => i.CanImport);
        Intro.Text = ready == 0
            ? "Переносить нечего: у всех зарегистрированных файлов параметров документы уже есть либо рядом с ними нет текстового задания."
            : "Так разобрались накопленные текстовые задания. Ничего ещё не записано — снимите отметку там, где разбор промахнулся, "
              + "поправьте названия документов и только потом нажимайте «Перенести отмеченные». Отменить перенос можно тут же, не выходя из окна.";
        RunBtn.IsEnabled = ready > 0;
        RefreshCount();
    }

    private void RefreshCount()
    {
        var ready = _items.Count(i => i.CanImport);
        var chosen = _items.Count(i => i.Selected && !i.Imported);
        var done = _items.Count(i => i.Imported);
        CountLabel.Text = $"Всего файлов: {_items.Count}; разобралось: {ready}; отмечено: {chosen}"
                          + (done > 0 ? $"; перенесено: {done}" : "");
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items.Where(i => !i.Imported)) item.Selected = true;
        RefreshCount();
    }

    private void UncheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.Selected = false;
        RefreshCount();
    }

    private void Warnings_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join("\n", _items
            .Where(i => i.Source.Warnings.Count > 0)
            .Select(i => $"{i.SourceName}:\n" + string.Join("\n", i.Source.Warnings.Select(w => "  • " + w))));
        TextViewDialog.Show(this, "На что взглянуть",
            text.Length > 0 ? text : "Разбор ни на что не пожаловался.");
    }

    private void Report_Click(object sender, RoutedEventArgs e) =>
        TextViewDialog.Show(this, "Отчёт о переносе",
            ParamTableBulkImport.Report(_items.Select(i => i.Source)));

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        // Правка ячейки, из которой ещё не вышли: человек дописал название и сразу нажал кнопку.
        ItemsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        var chosen = _items.Where(i => i.Selected && i.CanImport && !i.Imported).ToList();
        if (chosen.Count == 0)
        {
            AppMessageBox.Show("Не отмечено ни одной строки.", "Перенос", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = AppMessageBox.Show(
            $"Завести документов: {chosen.Count}?\n\n"
            + "Файлы на диске не тронутся — заводятся только документы программы. "
            + "Они уедут к коллегам общим конфигом, поэтому перенос лучше сперва просмотреть.",
            "Перенос", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var result = ParamTableBulkImport.Import(_services.Db, chosen.Select(i => i.Source),
            _services.CurrentUserName);
        _created = result.Created;
        foreach (var item in chosen) item.Imported = true;

        Changed = result.Created.Count > 0;
        UndoBtn.Visibility = result.Created.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunBtn.IsEnabled = _items.Any(i => i.CanImport && !i.Imported);
        RefreshCount();

        if (result.Failed.Count > 0)
            AppMessageBox.Show(result.Describe() + "\n\n" + string.Join("\n", result.Failed),
                "Перенос", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            AppMessageBox.Show(result.Describe(), "Перенос", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var answer = AppMessageBox.Show(
            $"Убрать документы, заведённые этим переносом ({_created.Count})?\n\n"
            + "Файлы на диске останутся как есть.",
            "Отменить перенос", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var removed = ParamTableBulkImport.Undo(_services.Db, _created);
        _created = new();
        UndoBtn.Visibility = Visibility.Collapsed;
        Changed = true;
        Rescan();

        AppMessageBox.Show($"Убрано документов: {removed}.", "Отменить перенос",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = Changed;
}
