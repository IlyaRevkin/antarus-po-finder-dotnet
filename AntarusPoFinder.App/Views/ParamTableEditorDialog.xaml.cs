using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Правка таблицы параметров — и разбор только что выбранного txt, и правка уже
/// сохранённого документа. ОДНО окно на оба случая намеренно: и там и там человек делает ровно одно
/// и то же — смотрит на строки, поправляет, пишет «зачем» и сохраняет. Разница только в том, откуда
/// пришли строки, и ради неё второе такое же окно заводить незачем.
///
/// Импорт вслепую («выбрал файл — что-то записалось») бесполезен: разбор чужого текста ошибается, и
/// увидеть это надо ДО того, как документ уедет к коллегам общим конфигом.
///
/// Само окно ничего не решает и в базу не пишет — оно отдаёт наружу название, строки и «зачем»,
/// а записывает их ParamTableEditing (он под тестами).</summary>
public partial class ParamTableEditorDialog : Window
{
    /// <summary>Как показать «чем задано значение» человеку. Три состояния, и пустая ячейка без
    /// пометки врёт: наладчик не отличит «здесь ноль» от «здесь надо посмотреть на шильдик».</summary>
    private static readonly (string Label, string State)[] States =
    {
        ("Значение", ParamValueState.Set),
        ("Уточнить по ПЛК", ParamValueState.Ask),
        ("Снимается по месту", ParamValueState.OnSite),
    };

    public sealed class RowVm
    {
        public RowVm(IReadOnlyList<string> groups, IReadOnlyList<string> extraKeys)
        {
            Groups = groups;
            ExtraKeys = extraKeys;
            Extras = extraKeys.Select(_ => "").ToList();
        }

        /// <summary>Справочник групп — общий список на все строки, у каждой строки свой выпадающий
        /// список из него же.</summary>
        public IReadOnlyList<string> Groups { get; }

        /// <summary>Ключи своих столбцов документа — по одному на каждую ячейку в
        /// <see cref="Extras"/>, в том же порядке.</summary>
        public IReadOnlyList<string> ExtraKeys { get; }

        /// <summary>Значения своих столбцов ПО МЕСТУ, а не словарём: столбцы для них заводятся в
        /// коде (их число известно только на открытии окна), и привязка к «Extras[0]» ловит любой
        /// заголовок — в том числе с пробелами и скобками, на которых путь привязки со словарём
        /// разбирается неверно.</summary>
        public List<string> Extras { get; }

        public IReadOnlyList<string> StateChoices => States.Select(s => s.Label).ToList();

        public string GroupName { get; set; } = ParamGroupCatalog.Main;
        public string Code { get; set; } = "";
        public string Title { get; set; } = "";
        public string Value { get; set; } = "";
        public string Factory { get; set; } = "";
        public string Unit { get; set; } = "";
        public string Description { get; set; } = "";
        public string Applicability { get; set; } = "";
        public string AppliesWhen { get; set; } = "";

        public string StateLabel { get; set; } = States[0].Label;

        public ParamTableRow ToRow() => new()
        {
            GroupName = GroupName,
            Code = Code,
            Title = Title,
            Value = Value,
            ValueState = States.FirstOrDefault(s => s.Label == StateLabel).State ?? ParamValueState.Set,
            Factory = Factory,
            Unit = Unit,
            Description = Description,
            Applicability = Applicability,
            AppliesWhen = AppliesWhen,
            // Своё содержимое строки собирается заново из ячеек. Значения столбцов, которых в этом
            // окне не показывали (снятые), при этом теряются намеренно: сохранение заводит НОВУЮ
            // редакцию, и тащить в неё то, чего человек не видел, значит записать за него.
            Extra = ParamRowExtra.Format(BuildExtra()),
        };

        private Dictionary<string, string> BuildExtra()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < ExtraKeys.Count && i < Extras.Count; i++)
                values[ExtraKeys[i]] = Extras[i] ?? "";
            return values;
        }

        public static RowVm From(ParamTableRow row, IReadOnlyList<string> groups, IReadOnlyList<string> extraKeys)
        {
            var vm = new RowVm(groups, extraKeys)
            {
                GroupName = row.GroupName,
                Code = row.Code,
                Title = row.Title,
                Value = row.Value,
                Factory = row.Factory,
                Unit = row.Unit,
                Description = row.Description,
                Applicability = row.Applicability,
                AppliesWhen = row.AppliesWhen,
                StateLabel = States.First(s => s.State == ParamValueState.Normalize(row.ValueState)).Label,
            };

            var stored = ParamRowExtra.Parse(row.Extra);
            for (var i = 0; i < extraKeys.Count; i++)
                vm.Extras[i] = stored.TryGetValue(extraKeys[i], out var value) ? value : "";
            return vm;
        }
    }

    private readonly List<string> _groups;
    private readonly List<ParamTableColumn> _columns;
    private readonly List<string> _extraKeys;
    private readonly ObservableCollection<RowVm> _rows = new();

    /// <summary>Байты выбранного файла — их приходится держать при себе на всё время окна: смена
    /// кодировки перечитывает ТЕ ЖЕ байты заново, а не открывает файл ещё раз (файл мог лежать на
    /// шаре, которая успела отвалиться).</summary>
    private readonly byte[]? _bytes;
    private readonly string _fileName = "";
    private string _sourceText = "";

    public string ResultName => NameInput.Text.Trim();
    public string ResultReason => ReasonInput.Text.Trim();
    public List<ParamTableRow> ResultRows { get; private set; } = new();

    private ParamTableEditorDialog(IEnumerable<string> groups, IEnumerable<ParamTableColumn>? columns)
    {
        InitializeComponent();
        _groups = groups.ToList();
        _columns = (columns ?? Enumerable.Empty<ParamTableColumn>()).ToList();
        _extraKeys = _columns.Select(c => c.Key).ToList();
        AddOwnColumns();
        RowsGrid.ItemsSource = _rows;
        foreach (var choice in TextFileEncoding.Choices) EncodingCombo.Items.Add(choice);
        EncodingCombo.SelectedIndex = 0;
    }

    /// <summary>Свои столбцы документа — колонками справа от встроенных. В коде, а не в XAML, по
    /// единственной причине: сколько их и как они называются, известно только на открытии окна.
    /// Привязка идёт по МЕСТУ («Extras[0]»), а не по названию столбца: заголовок пишет человек, и
    /// в нём бывают пробелы, точки и скобки, на которых путь привязки разбирается не так.</summary>
    private void AddOwnColumns()
    {
        var cellStyle = (Style)FindResource("DataGridCellText");
        for (var i = 0; i < _columns.Count; i++)
            RowsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = _columns[i].Title,
                Binding = new System.Windows.Data.Binding($"Extras[{i}]")
                {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 110,
                ElementStyle = cellStyle,
            });
    }

    /// <summary>Импорт: разбор только что выбранного текстового файла.</summary>
    public ParamTableEditorDialog(IEnumerable<string> groups, byte[] bytes, string fileName)
        : this(groups, null)
    {
        _bytes = bytes;
        _fileName = fileName;
        Title = "Импорт таблицы параметров: " + fileName;
        Intro.Text = "Так разобрался выбранный файл. Поправьте, что разобралось не так, — и только после этого сохраняйте: "
                     + "документ уедет к коллегам общим конфигом, и вслепую его туда отправлять нечего.";
        ReasonInput.Text = "перенесено из " + fileName;
        Reparse();
    }

    /// <summary>Правка уже сохранённого документа: строки берутся из его последней ревизии.</summary>
    public ParamTableEditorDialog(IEnumerable<string> groups, IEnumerable<ParamTableColumn> columns,
        string documentName, IEnumerable<ParamTableRow> rows)
        : this(groups, columns)
    {
        Title = "Правка таблицы параметров: " + documentName;
        Intro.Text = "Правка сохранится НОВОЙ ревизией: прежняя останется как была, и переключаться между ними можно будет "
                     + "в окне документа. Что именно изменилось, программа посчитает сама — от вас нужно только «зачем».";
        NameInput.Text = documentName;
        EncodingLabel.Visibility = Visibility.Collapsed;
        EncodingCombo.Visibility = Visibility.Collapsed;
        ShowSourceBtn.Visibility = Visibility.Collapsed;
        Fill(rows);
    }

    private void Fill(IEnumerable<ParamTableRow> rows)
    {
        _rows.Clear();
        foreach (var row in rows) _rows.Add(RowVm.From(row, _groups, _extraKeys));
        RefreshCount();
    }

    private void RefreshCount() => CountLabel.Text = $"Строк: {_rows.Count}";

    /// <summary>Перечитать те же байты выбранной кодировкой и разобрать заново. Правки, сделанные
    /// руками, при этом теряются — но смена кодировки означает, что разбор был мусорным целиком, и
    /// сохранять из него нечего.</summary>
    private void Reparse()
    {
        if (_bytes is null) return;

        var choice = EncodingCombo.SelectedItem as string;
        var preview = ParamTableEditing.Preview(_bytes, _fileName, choice);
        _sourceText = preview.Text;
        if (NameInput.Text.Trim().Length == 0) NameInput.Text = preview.SuggestedName;
        Fill(preview.Rows);

        WarningsText.Text = string.Join("\n", preview.Warnings);
        WarningsBox.Visibility = preview.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Encoding_Changed(object sender, SelectionChangedEventArgs e) => Reparse();

    private void ShowSource_Click(object sender, RoutedEventArgs e)
    {
        // Исходный текст рядом с таблицей — то, с чем сверяют разбор. Отдельным окном, а не панелью
        // в этом же: сверяются один раз, а место на строки таблицы нужно всегда.
        TextViewDialog.Show(this, "Исходный текст: " + _fileName, _sourceText);
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var at = RowsGrid.SelectedIndex;
        var fresh = new RowVm(_groups, _extraKeys)
        {
            // Новая строка наследует группу и условия соседа: её заводят, чтобы дописать параметр
            // РЯДОМ с выделенным, а не в другой конец таблицы.
            GroupName = at >= 0 ? _rows[at].GroupName : ParamGroupCatalog.Main,
            Applicability = at >= 0 ? _rows[at].Applicability : "",
            AppliesWhen = at >= 0 ? _rows[at].AppliesWhen : "",
        };
        if (at >= 0) _rows.Insert(at + 1, fresh);
        else _rows.Add(fresh);
        RowsGrid.SelectedItem = fresh;
        RefreshCount();
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (RowsGrid.SelectedItem is not RowVm row) return;
        _rows.Remove(row);
        RefreshCount();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        var at = RowsGrid.SelectedIndex;
        var to = at + delta;
        if (at < 0 || to < 0 || to >= _rows.Count) return;
        _rows.Move(at, to);
        RowsGrid.SelectedIndex = to;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Правка ячейки, из которой ещё не вышли, иначе в объект не попадёт: человек дописал
        // значение и сразу нажал «Сохранить», а сохранилось бы прежнее.
        RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        if (ResultName.Length == 0)
        {
            AppMessageBox.Show("У документа должно быть название — по нему его находят в списке.",
                "Таблица параметров", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameInput.Focus();
            return;
        }

        var rows = ParamTableEditing.Tidy(_rows.Select(r => r.ToRow()), _groups);
        if (rows.Count == 0)
        {
            AppMessageBox.Show("В таблице не осталось ни одной строки — сохранять нечего.",
                "Таблица параметров", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultRows = rows;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
